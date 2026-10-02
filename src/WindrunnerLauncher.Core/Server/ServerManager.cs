using System.Diagnostics;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Platform;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Portable realm lifecycle: MariaDB → realmd → mangosd, semantic readiness,
/// graceful stdin shutdown, and first-install setup.
/// </summary>
public sealed class ServerManager : IDisposable
{
    public const string UnconfirmedReadyMessage = "Server is running, but readiness could not be confirmed.";
    public const string SavingStatusMessage = "Saving & stopping server…";

    private static readonly string[] ReadySignals =
    [
        "World initialized",
        "WORLD: World initialized",
        "server is ready",
        "mangosd ready",
        "Worldserver started",
        "World server is up and running"
    ];

    private readonly LauncherPaths _paths;
    private readonly StateStore _state;
    private readonly MariaDbManager _maria;
    private readonly PortableEnv _env;
    private readonly FetchService _fetch;
    private readonly ConfPatcher _conf;
    private readonly SqlImporter _sql;
    private readonly AccountService _accounts;
    private readonly SetupOrchestrator _setup;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stdinGate = new();

    private OwnedProcess? _realmd;
    private OwnedProcess? _mangosd;
    private ServerLifecycleState _stateValue;
    private string _statusMessage = "";
    private bool _startedByLauncher;
    private bool _readinessUnconfirmed;
    private bool _stopping;
    private bool _disposed;
    private DateTime? _mangosdHaltedAt;

    private static readonly TimeSpan HaltGrace = TimeSpan.FromSeconds(5);

    public ServerManager(
        LauncherPaths paths,
        StateStore state,
        MariaDbManager maria,
        ModManager? mods = null,
        DownloadManager? downloads = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _maria = maria ?? throw new ArgumentNullException(nameof(maria));
        _env = new PortableEnv(paths);
        // Sharing the launcher's DownloadManager puts server downloads on the footer progress bar.
        _fetch = new FetchService(paths, _env, downloads, mods);
        _conf = new ConfPatcher(paths, _env);
        _sql = new SqlImporter(paths, _env, maria);
        _accounts = new AccountService(paths, _env, maria);
        _setup = new SetupOrchestrator(paths, _env, state, maria, _fetch, _conf, _sql);
        MangosLog = new ConsoleCapture();
        RealmLog = new ConsoleCapture();
        MysqlLog = maria.Log;
        maria.Output += line =>
        {
            OutputReceived?.Invoke(line);
        };
        _stateValue = IsInstalled ? ServerLifecycleState.Stopped : ServerLifecycleState.NotInstalled;
    }

    /// <summary>Fired when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Fired for captured daemon output lines.</summary>
    public event Action<string>? OutputReceived;

    public ConsoleCapture MangosLog { get; }
    public ConsoleCapture RealmLog { get; }
    public ConsoleCapture MysqlLog { get; }

    public ServerLifecycleState State => _stateValue;
    public bool StartedByLauncher => _startedByLauncher;
    public bool ReadinessUnconfirmed => _readinessUnconfirmed;
    public string StatusMessage => _statusMessage;

    /// <summary>mangosd + realmd exist (with or without .exe) and maps are present.</summary>
    public bool IsInstalled =>
        ServerUtil.FileExistsInsensitive(_paths.ServerBinaries, "mangosd")
        && ServerUtil.FileExistsInsensitive(_paths.ServerBinaries, "realmd")
        && ServerUtil.MapsPresent(_paths.Maps);

    /// <summary>Starts MariaDB, realmd, then mangosd, waiting up to 15 minutes for a readiness line.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsInstalled)
                throw new InvalidOperationException("Server is not installed. Run setup first.");
            if (IsBusyState(_stateValue) && _stateValue is not ServerLifecycleState.RestartRequired)
                throw new InvalidOperationException($"Cannot start while state is {_stateValue}.");
            if (MangosdAlive && RealmdAlive && _stateValue is ServerLifecycleState.Ready or ServerLifecycleState.RestartRequired)
                return;

            _stopping = false;
            _readinessUnconfirmed = false;
            _env.WriteFriendlySettings(_state.Settings.Server);
            _conf.Apply();
            ServerUtil.AssertMapsPresent(_paths.Maps);

            var mangosd = RequireBinary("mangosd");
            var realmd = RequireBinary("realmd");
            AssertConf("mangosd.conf");
            AssertConf("realmd.conf");
            AssertNotAlreadyRunning(mangosd, "mangosd");
            AssertNotAlreadyRunning(realmd, "realmd");

            var mysqlWasReady = _maria.IsReady;
            try
            {
                SetState(ServerLifecycleState.StartingDatabase, "Starting Database");
                await _maria.StartAsync(ct).ConfigureAwait(false);

                if (_maria.IsReady)
                {
                    try { await _sql.SyncRealmlistAsync(ct).ConfigureAwait(false); }
                    catch { /* realmlist sync is best-effort at start */ }
                }

                SetState(ServerLifecycleState.StartingAuth, "Starting Auth");
                _realmd = StartDaemon(realmd, RealmLog);
                PidFiles.Write(_paths.ServerData, "realmd", _realmd.Id);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                if (_realmd.Process.HasExited || !ProcessHost.IsAlive(_realmd.Id))
                    throw new InvalidOperationException("realmd exited during startup.");

                SetState(ServerLifecycleState.InitializingWorld, "Initializing World");
                // Readiness belongs to this mangosd process. MangosLog intentionally retains
                // previous runs for diagnostics, so it must not be consulted as a readiness
                // source for a new start.
                var readiness = new ReadinessSession();
                _mangosd = StartDaemon(mangosd, MangosLog, line =>
                {
                    readiness.Observe(line);
                    if (line.Contains("Halting process", StringComparison.OrdinalIgnoreCase))
                        _mangosdHaltedAt ??= DateTime.UtcNow;
                });
                PidFiles.Write(_paths.ServerData, "mangosd", _mangosd.Id);
                HookUnexpectedExit(_mangosd, readiness);
                if (_mangosd.Process.HasExited)
                    readiness.ProcessExited();

                try
                {
                    var completed = await readiness.Task.WaitAsync(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
                    if (!completed)
                        throw new InvalidOperationException("mangosd exited during startup.");
                    _startedByLauncher = true;
                    _readinessUnconfirmed = false;
                    SetState(ServerLifecycleState.Ready, "");
                }
                catch (TimeoutException)
                {
                    if (!MangosdAlive)
                        throw new InvalidOperationException("mangosd exited during startup.");
                    _startedByLauncher = true;
                    _readinessUnconfirmed = true;
                    SetState(ServerLifecycleState.Ready, UnconfirmedReadyMessage);
                }
            }
            catch (Exception ex)
            {
                var cancel = CancellationToken.None;
                await FailStartAsync(mysqlWasReady, ex, cancel).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Graceful stop: <c>saveall</c> then <c>server shutdown 0</c>, wait up to 60 seconds.
    /// Does not force-kill mangosd unless <paramref name="force"/> is true.
    /// </summary>
    public Task StopAsync(CancellationToken ct) => StopAsync(force: false, ct);

    /// <inheritdoc cref="StopAsync(CancellationToken)"/>
    public async Task StopAsync(bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(force, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Kills mangosd and realmd, then stops MariaDB. UI confirmation is the caller's job.</summary>
    public async Task ForceStopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(force: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Graceful stop then start.</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(force: false, ct).ConfigureAwait(false);
        if (MangosdAlive)
            throw new InvalidOperationException(
                "The server did not stop gracefully within 60 seconds. " +
                "Confirm Force Stop before restarting.");
        await StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes a line to mangosd stdin.</summary>
    public void SendCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_stdinGate)
        {
            var stdin = _mangosd?.Stdin ?? throw new InvalidOperationException("mangosd is not running.");
            stdin.WriteLine(command);
            stdin.Flush();
        }
    }

    /// <summary>Marks that live-incompatible friendly settings changed.</summary>
    public void MarkRestartRequired()
    {
        if (_stateValue is ServerLifecycleState.NotInstalled)
            return;
        SetState(ServerLifecycleState.RestartRequired, "Restart Required");
    }

    /// <summary>Writes friendly settings to env, patches confs, syncs realmlist. Marks restart if the realm is running.</summary>
    public async Task ApplyFriendlySettingsAsync(CancellationToken ct = default)
    {
        ApplyFriendlySettings();
        if (_maria.IsReady)
            await _sql.SyncRealmlistAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Synchronous apply used when the caller will persist settings itself.</summary>
    public void ApplyFriendlySettings()
    {
        ThrowIfDisposed();
        var before = SnapshotLiveSettings();
        var running = ProcessesRunning || _stateValue is ServerLifecycleState.Ready
            or ServerLifecycleState.InitializingWorld
            or ServerLifecycleState.StartingAuth
            or ServerLifecycleState.StartingDatabase
            or ServerLifecycleState.RestartRequired;
        _env.WriteFriendlySettings(_state.Settings.Server);
        _conf.Apply();
        var after = SnapshotLiveSettings();
        if (running && !before.Equals(after))
            MarkRestartRequired();
    }

    public Task CreateAccountAsync(string username, string password, CancellationToken ct) =>
        _accounts.CreateAccountAsync(username, password, 0, ct);

    public Task CreateAccountAsync(string username, string password, int gmLevel = 0, CancellationToken ct = default) =>
        _accounts.CreateAccountAsync(username, password, gmLevel, ct);

    public Task ChangePasswordAsync(string username, string password, CancellationToken ct = default) =>
        _accounts.ChangePasswordAsync(username, password, ct);

    public Task SetGmLevelAsync(string username, int gmLevel, CancellationToken ct = default) =>
        _accounts.SetGmLevelAsync(username, gmLevel, ct);

    /// <summary>First-install stages (fetch, MariaDB, import, confs). Does not use the 15-minute ready timeout.</summary>
    public Task SetupAsync(CancellationToken ct) => SetupAsync(null, ct);

    /// <inheritdoc cref="SetupAsync(CancellationToken)"/>
    public async Task SetupAsync(IProgress<SetupStage>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ProcessesRunning)
                await StopCoreAsync(force: true, ct).ConfigureAwait(false);
            SetState(ServerLifecycleState.Updating, "Installing server…");
            try
            {
                await _setup.RunAsync(progress, ct, AppendSetupLog).ConfigureAwait(false);
                SetState(IsInstalled ? ServerLifecycleState.Stopped : ServerLifecycleState.NotInstalled, "");
            }
            catch (Exception ex)
            {
                SetState(ServerLifecycleState.Error, ex.Message);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops the realm gracefully, wipes <c>tw_world</c> and re-imports it from
    /// the bundled SQL (see <see cref="SqlImporter.ReimportWorldAsync"/>), then starts the realm again if it
    /// was running. Accounts and characters are preserved.
    /// </summary>
    public async Task RepairWorldDatabaseAsync(CancellationToken ct = default)
    {
        bool restart;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsInstalled)
                throw new InvalidOperationException("Server is not installed. Run setup first.");
            if (IsBusyState(_stateValue))
                throw new InvalidOperationException($"Cannot repair while state is {_stateValue}.");

            restart = ProcessesRunning;
            if (restart)
            {
                await StopCoreAsync(force: false, ct).ConfigureAwait(false);
                if (MangosdAlive)
                    throw new InvalidOperationException(
                        "The server did not stop gracefully within 60 seconds. " +
                        "Force Stop it before repairing the world database.");
            }

            SetState(ServerLifecycleState.Updating, "Repairing world database…");
            var mysqlWasReady = _maria.IsReady;
            try
            {
                await _maria.StartAsync(ct).ConfigureAwait(false);
                await _sql.ReimportWorldAsync(ct, AppendSetupLog).ConfigureAwait(false);
                SetState(ServerLifecycleState.Stopped, "");
            }
            catch (Exception ex)
            {
                SetState(ServerLifecycleState.Error, ex.Message);
                throw;
            }
            finally
            {
                if (!mysqlWasReady)
                {
                    try { await _maria.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (restart)
            await StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs offline maintenance (server update, rollback) under the lifecycle gate: stops the realm
    /// gracefully if it is running, holds <paramref name="busyState"/> while <paramref name="work"/>
    /// runs, and leaves the realm stopped. The work receives whether the realm had been running.
    /// Returns that flag so the caller can start it again once the gate is released.
    /// </summary>
    internal async Task<bool> RunOfflineAsync(
        ServerLifecycleState busyState,
        string status,
        Func<bool, Task> work,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsBusyState(_stateValue))
                throw new InvalidOperationException($"Cannot start {busyState} while state is {_stateValue}.");

            var wasRunning = ProcessesRunning;
            if (wasRunning)
            {
                await StopCoreAsync(force: false, ct).ConfigureAwait(false);
                if (MangosdAlive)
                {
                    SetState(ServerLifecycleState.Ready, "");
                    throw new InvalidOperationException(
                        "The server did not stop gracefully within 60 seconds. " +
                        "Force Stop it, then try again.");
                }
            }

            SetState(busyState, status);
            try
            {
                await work(wasRunning).ConfigureAwait(false);
                SetState(IsInstalled ? ServerLifecycleState.Stopped : ServerLifecycleState.NotInstalled, "");
            }
            catch (Exception ex)
            {
                SetState(ServerLifecycleState.Error, ex.Message);
                throw;
            }

            return wasRunning;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the lifecycle gate without changing the realm's state, so
    /// nothing can start, stop, or update it meanwhile (a live backup while it keeps running).
    /// </summary>
    public async Task RunExclusiveAsync(Func<Task> work, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsBusyState(_stateValue))
                throw new InvalidOperationException($"The server is busy ({_stateValue}). Try again once it has settled.");
            await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal FetchService Fetch => _fetch;
    internal ConfPatcher Conf => _conf;
    internal MariaDbManager MariaDb => _maria;
    internal void Log(string line) => AppendSetupLog(line);

    private void AppendSetupLog(string line)
    {
        MysqlLog.Append(line.EndsWith('\n') ? line : line + "\n");
        OutputReceived?.Invoke(line);
    }

    public void MarkUpdating() => SetState(ServerLifecycleState.Updating, "Updating server…");
    public void MarkRollingBack() => SetState(ServerLifecycleState.RollingBack, "Rolling back…");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stopping = true;
        try { _mangosd?.Kill(); } catch { }
        try { _realmd?.Kill(); } catch { }
        _mangosd?.Dispose();
        _realmd?.Dispose();
        _fetch.Dispose();
        _gate.Dispose();
    }

    private async Task StopCoreAsync(bool force, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_stateValue is ServerLifecycleState.NotInstalled)
            return;
        _stopping = true;
        SetState(ServerLifecycleState.Stopping, SavingStatusMessage);

        if (MangosdAlive)
        {
            _mangosdHaltedAt = null;
            TrySend("saveall");
            TrySend("server shutdown 0");
            // mangosd's console thread blocks reading stdin and never returns during
            // shutdown unless it sees EOF; the queued commands are still read first.
            lock (_stdinGate)
                _mangosd?.CloseStdin();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline && MangosdAlive)
            {
                ct.ThrowIfCancellationRequested();
                // "Halting process..." is logged after the world is saved and the DB closed;
                // anything left afterwards is a stuck thread, so finish it off.
                if (_mangosdHaltedAt is { } halted && DateTime.UtcNow - halted > HaltGrace)
                {
                    _mangosd?.Kill();
                    await ProcessHost.WaitGoneAsync(_mangosd?.Id ?? 0, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    break;
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            if (MangosdAlive && !force)
                return;

            if (MangosdAlive)
                _mangosd?.Kill();
        }

        if (RealmdAlive)
            _realmd?.Kill();
        await ProcessHost.WaitGoneAsync(_realmd?.Id ?? 0, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        PidFiles.Remove(_paths.ServerData, "mangosd");
        PidFiles.Remove(_paths.ServerData, "realmd");
        ReleaseDaemon(ref _mangosd);
        ReleaseDaemon(ref _realmd);
        await _maria.StopAsync(ct).ConfigureAwait(false);
        _startedByLauncher = false;
        _readinessUnconfirmed = false;
        _stopping = false;
        SetState(IsInstalled ? ServerLifecycleState.Stopped : ServerLifecycleState.NotInstalled, "");
    }

    private async Task FailStartAsync(bool mysqlWasReady, Exception ex, CancellationToken ct)
    {
        _stopping = true;
        try { _mangosd?.Kill(); } catch { }
        try { _realmd?.Kill(); } catch { }
        ReleaseDaemon(ref _mangosd);
        ReleaseDaemon(ref _realmd);
        PidFiles.Remove(_paths.ServerData, "mangosd");
        PidFiles.Remove(_paths.ServerData, "realmd");
        if (!mysqlWasReady)
        {
            try { await _maria.StopAsync(ct).ConfigureAwait(false); }
            catch { }
        }

        _startedByLauncher = false;
        _stopping = false;
        SetState(ServerLifecycleState.Error, ex.Message);
    }

    private OwnedProcess StartDaemon(string exe, ConsoleCapture capture, Action<string>? onLine = null)
    {
        DataReceivedEventHandler handler = (_, e) =>
        {
            if (e.Data is null)
                return;
            capture.Append(e.Data + "\n");
            OutputReceived?.Invoke(e.Data);
            onLine?.Invoke(e.Data);
        };
        return ProcessHost.StartHidden(exe, _paths.ServerBinaries, arguments: null, captureOutput: true, onOutput: handler, holdStdin: true);
    }

    private void HookUnexpectedExit(OwnedProcess process, ReadinessSession readiness)
    {
        process.Process.Exited += (_, _) =>
        {
            readiness.ProcessExited();
            if (_stopping)
                return;
            _startedByLauncher = false;
            SetState(ServerLifecycleState.Error, "mangosd exited unexpectedly.");
        };
    }

    private void TrySend(string command)
    {
        try { SendCommand(command); }
        catch { /* process may already be gone */ }
    }

    private string RequireBinary(string name) =>
        ServerUtil.FindExisting(_paths.ServerBinaries, name)
        ?? throw new FileNotFoundException($"Missing {name} in {_paths.ServerBinaries}");

    private void AssertConf(string name)
    {
        var path = Path.Combine(_paths.ServerBinaries, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"no server/{name} - run Full setup", path);
    }

    private void AssertNotAlreadyRunning(string exe, string name)
    {
        var running = ServerUtil.ProcessesByImage(exe);
        if (running.Count > 0)
            throw new InvalidOperationException($"{name} is already running for this install (pid {running[0]})");
        var recorded = PidFiles.Read(_paths.ServerData, name);
        if (recorded > 0 && ServerUtil.ProcessMatchesImage(recorded, exe))
            throw new InvalidOperationException($"{name} is already running for this install (pid {recorded})");
    }

    private bool MangosdAlive => _mangosd is { Process.HasExited: false };
    private bool RealmdAlive => _realmd is { Process.HasExited: false };
    private bool ProcessesRunning => MangosdAlive || RealmdAlive;

    private static bool IsBusyState(ServerLifecycleState state) =>
        state is ServerLifecycleState.StartingDatabase
            or ServerLifecycleState.StartingAuth
            or ServerLifecycleState.InitializingWorld
            or ServerLifecycleState.Stopping
            or ServerLifecycleState.Updating
            or ServerLifecycleState.RollingBack;

    private static bool IsReadySignal(string line) =>
        ReadySignals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private LiveSettings SnapshotLiveSettings()
    {
        _env.Reload();
        return new LiveSettings(
            _env.Get("REALM_NAME", PortableEnv.DefaultRealmName),
            _env.Get("REALM_ADDRESS", PortableEnv.DefaultRealmAddress),
            _env.GetInt("REALM_PORT", PortableEnv.DefaultRealmPort),
            _env.GetInt("WORLD_PORT", PortableEnv.DefaultWorldPort),
            _env.GetInt("MYSQL_PORT", PortableEnv.DefaultMysqlPort),
            _env.GetInt("MIN_RANDOM_BOTS", PortableEnv.DefaultMinRandomBots),
            _env.GetInt("MAX_RANDOM_BOTS", PortableEnv.DefaultMaxRandomBots));
    }

    private void SetState(ServerLifecycleState state, string status)
    {
        var changed = _stateValue != state || !string.Equals(_statusMessage, status, StringComparison.Ordinal);
        _stateValue = state;
        _statusMessage = status;
        if (changed)
            StateChanged?.Invoke();
    }

    private static void ReleaseDaemon(ref OwnedProcess? process)
    {
        try { process?.Dispose(); } catch { }
        process = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct LiveSettings(string Name, string Address, int Auth, int World, int Mysql, int MinBots, int MaxBots);

    /// <summary>Tracks readiness output from one mangosd start.</summary>
    internal sealed class ReadinessSession
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Task => _completion.Task;

        public void Observe(string line)
        {
            if (IsReadySignal(line))
                _completion.TrySetResult(true);
        }

        public void ProcessExited() => _completion.TrySetResult(false);
    }
}
