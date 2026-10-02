using System.Diagnostics;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Platform;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Realms;
using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Client;

/// <summary>Raised instead of silently downloading when required client content is out of date.</summary>
public sealed class ClientUpdateRequiredException : InvalidOperationException
{
    public IReadOnlyList<ManagedAsset> OutdatedAssets { get; }

    public ClientUpdateRequiredException(IReadOnlyList<ManagedAsset> outdated)
        : base(BuildMessage(outdated))
    {
        OutdatedAssets = outdated;
    }

    private static string BuildMessage(IReadOnlyList<ManagedAsset> outdated)
    {
        var names = string.Join(", ", outdated.Select(a => a.DisplayName.Length > 0 ? a.DisplayName : a.Id));
        return $"Required client content is missing or out of date ({names}). " +
               "Run Update before playing; the launcher never downloads required content behind your back.";
    }
}

public sealed class LaunchedGame
{
    public required int ProcessId { get; init; }
    public required string Executable { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public bool ThroughLoader { get; init; }
}

public sealed class PlayResult
{
    public LaunchedGame? Game { get; init; }
    public bool ServerStartedByLauncher { get; init; }
    public bool ServerReadinessConfirmed { get; init; }
    public IReadOnlyList<string> Log { get; init; } = [];
}

/// <summary>
/// The single place where a realm becomes a running game. Realm selection and mod toggles only
/// record intent; this pipeline is what touches the client directory.
///
/// No WoW credentials are handled or stored anywhere: login happens inside the game.
/// </summary>
public sealed class PlayPipeline
{
    /// <summary>Matches the server subsystem's readiness budget for ordinary launches.</summary>
    public static readonly TimeSpan ServerReadyTimeout = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan ServerPollInterval = TimeSpan.FromSeconds(1);

    public const string LoaderExecutable = "VanillaFixes.exe";

    private readonly LauncherPaths _paths;
    private readonly StateStore _state;
    private readonly ClientManager _client;
    private readonly ModManager _mods;
    private readonly ServerManager _server;
    private readonly RealmManager _realms;
    private readonly LinuxRunner? _runner;

    /// <summary>Progress messages for the UI: one line per pipeline step.</summary>
    public event Action<string>? Stage;

    /// <summary>Raised when a tracked game process exits.</summary>
    public event Action? GameExited;

    public int? TrackedProcessId { get; private set; }

    public PlayPipeline(
        LauncherPaths paths,
        StateStore state,
        ClientManager client,
        ModManager mods,
        ServerManager server,
        RealmManager realms,
        LinuxRunner? runner = null)
    {
        _paths = paths;
        _state = state;
        _client = client;
        _mods = mods;
        _server = server;
        _realms = realms;
        _runner = runner;
    }

    /// <summary>
    /// Resolves the primary action. Button labels are localization keys; the runtime resolves them.
    ///
    /// Required client integrity blocks Play. A pending server update never does. Restart Required
    /// blocks Play only while the Local Server realm is selected.
    /// </summary>
    public PlayContext CanPlay(RealmEntry realm, bool clientUpdateRequired)
    {
        if (!_state.Settings.OnboardingCompleted)
            return new PlayContext { Action = PlayActionKind.Onboarding, ButtonLabel = "onboarding.continue" };

        if (!_client.IsValid(_client.ClientForRealm(realm)))
            return new PlayContext
            {
                Action = PlayActionKind.Onboarding,
                ButtonLabel = "onboarding.locate",
                BlockReason = "status.missing"
            };

        if (clientUpdateRequired || _mods.RequiredOutOfDate(realm).Count > 0)
            return new PlayContext
            {
                Action = PlayActionKind.Update,
                ButtonLabel = "update",
                BlockReason = "status.outdated"
            };

        if (RealmManager.IsLocal(realm))
        {
            if (!_server.IsInstalled)
                return new PlayContext { Action = PlayActionKind.InstallServer, ButtonLabel = "install.server" };

            if (_server.State == ServerLifecycleState.RestartRequired)
                return new PlayContext
                {
                    Action = PlayActionKind.RestartRequired,
                    ButtonLabel = "restart.required",
                    BlockReason = "server.restart"
                };
        }

        return new PlayContext { Action = PlayActionKind.Play, ButtonLabel = "play" };
    }

    public Task<PlayResult> ExecuteAsync(CancellationToken ct = default) =>
        ExecuteAsync(_realms.Selected, ct);

    public async Task<PlayResult> ExecuteAsync(RealmEntry realm, CancellationToken ct = default)
    {
        var log = new List<string>();

        void Step(string message)
        {
            log.Add(message);
            Stage?.Invoke(message);
        }

        ValidateRealm(realm);
        Step($"Realm '{Describe(realm)}' validated.");

        var clientDir = _client.ClientForRealm(realm);
        if (!_client.IsValid(clientDir))
            throw new DirectoryNotFoundException($"No playable client in {clientDir}.");
        _client.EnsureCleanExecutableBackup(clientDir);
        Step($"Client at {clientDir} validated.");

        // Required assets must be updated before Play; launching never downloads them.
        var outdated = _mods.RequiredOutOfDate(realm);
        if (outdated.Count > 0)
            throw new ClientUpdateRequiredException(outdated);

        // Fetch the Linux runner now, so mods that run through Wine (VanillaTweaks) can use it too.
        if (OperatingSystem.IsLinux() && _runner is not null)
            await _runner.EnsureAsync(_state.Settings, Step, ct).ConfigureAwait(false);

        var moves = await _mods.MaterializeAsync(realm, ct).ConfigureAwait(false);
        foreach (var move in moves)
            Step(move);
        Step($"Mod state materialized for '{Describe(realm)}'.");

        var host = RealmlistWriter.FormatHost(realm.Address, realm.AuthPort);
        RealmlistWriter.Write(clientDir, host);
        Step($"realmlist set to {host}.");

        if (realm.ClearWdb)
        {
            var cleared = Wdb.ClearWdb(clientDir);
            Step(cleared.Count > 0 ? $"Cleared {cleared.Count} WDB cache directory(ies)." : "No WDB cache to clear.");
        }

        var startedServer = false;
        var readinessConfirmed = true;
        if (RealmManager.IsLocal(realm) && _state.Settings.Server.StartServerWithClient && _server.IsInstalled)
        {
            if (_server.State != ServerLifecycleState.Ready)
            {
                Step("Starting the local server…");
                await _server.StartAsync(ct).ConfigureAwait(false);
                startedServer = true;
            }

            readinessConfirmed = await WaitForServerAsync(ct).ConfigureAwait(false);
            Step(readinessConfirmed
                ? "Local server is ready."
                : "Server is running, but readiness could not be confirmed.");
        }

        var game = await LaunchWoWAsync(realm, clientDir, ct).ConfigureAwait(false);
        Step($"Launched {Path.GetFileName(game.Executable)} (pid {game.ProcessId}).");

        // The local server stays running after the game closes.
        TrackProcess(game.ProcessId);

        return new PlayResult
        {
            Game = game,
            ServerStartedByLauncher = startedServer,
            ServerReadinessConfirmed = readinessConfirmed,
            Log = log
        };
    }

    /// <summary>
    /// Starts the game. When <c>dlls.txt</c> lists something to inject and VanillaFixes is present,
    /// the loader is launched instead so the DLLs are actually loaded.
    /// </summary>
    public async Task<LaunchedGame> LaunchWoWAsync(RealmEntry realm, string clientDir, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var target = ResolveGameExecutable(realm, clientDir);
        var loader = ClientPaths.Child(clientDir, LoaderExecutable);
        var useLoader = DllsTxt.HasEntries(clientDir) && File.Exists(loader);

        var fileName = useLoader ? loader : target;
        var arguments = useLoader ? new List<string> { Path.GetFileName(target) } : new List<string>();

        Process process;
        if (!OperatingSystem.IsWindows() && WineHost.IsWindowsImage(fileName))
        {
            var choice = _runner is not null
                ? await _runner.EnsureAsync(_state.Settings, Stage, ct).ConfigureAwait(false)
                : LinuxRunner.ResolveInstalled(_state.Settings, _paths)
                  ?? throw new InvalidOperationException(
                      "This client is a Windows executable. Install Wine, or choose Proton in Settings.");
            var wine = choice.Runner;

            if (!WineHost.PrefixIsReady(wine, choice.Prefix))
                Stage?.Invoke(choice.Kind == WineRunnerKind.Wine
                    ? "Preparing Wine (the first launch can take a minute)…"
                    : "Preparing Proton. The first launch downloads Proton and the Steam runtime and can take several minutes…");
            await WineHost.EnsurePrefixAsync(wine, choice.Prefix, ct).ConfigureAwait(false);

            var dxvk = File.Exists(ClientPaths.Child(clientDir, "d3d9.dll"));
            var psi = WineHost.CreateStartInfo(wine, choice.Prefix, fileName, clientDir, arguments, dxvk);
            process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start {fileName} with {wine}.");
        }
        else
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = clientDir,
                UseShellExecute = false
            };
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        using (process)
        {
            return new LaunchedGame
            {
                ProcessId = process.Id,
                Executable = fileName,
                WorkingDirectory = clientDir,
                Arguments = arguments,
                ThroughLoader = useLoader
            };
        }
    }

    public string ResolveGameExecutable(RealmEntry realm, string clientDir)
    {
        if (!string.IsNullOrWhiteSpace(realm.ClientExecutable))
        {
            var chosen = ClientPaths.Child(clientDir, realm.ClientExecutable);
            if (File.Exists(chosen))
                return chosen;
        }

        return ClientManager.FindExecutable(clientDir)
               ?? throw new FileNotFoundException(
                   $"Neither '{realm.ClientExecutable}' nor a known WoW executable exists in {clientDir}.");
    }

    /// <summary>
    /// Waits for the server to report readiness. Returns false when the wait ends in the
    /// "running, but readiness could not be confirmed" state rather than a hard failure.
    /// </summary>
    public async Task<bool> WaitForServerAsync(CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + ServerReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            switch (_server.State)
            {
                case ServerLifecycleState.Ready:
                    return true;
                case ServerLifecycleState.Error:
                    throw new InvalidOperationException("The local server entered the Error state while starting.");
                case ServerLifecycleState.NotInstalled:
                    throw new InvalidOperationException("The local server is not installed.");
                case ServerLifecycleState.Stopped:
                case ServerLifecycleState.Stopping:
                    return false;
            }

            if (_server.ReadinessUnconfirmed)
                return false;

            await Task.Delay(ServerPollInterval, ct).ConfigureAwait(false);
        }

        return false;
    }

    private void TrackProcess(int processId)
    {
        TrackedProcessId = processId;

        _ = Task.Run(async () =>
        {
            try
            {
                await Platform.ProcessHost.WaitGoneAsync(processId, TimeSpan.FromDays(30)).ConfigureAwait(false);
            }
            catch
            {
                // Losing the watcher must never take the launcher down.
            }

            TrackedProcessId = null;
            GameExited?.Invoke();
        });
    }

    private static void ValidateRealm(RealmEntry realm)
    {
        if (string.IsNullOrWhiteSpace(realm.Address))
            throw new InvalidOperationException($"Realm '{Describe(realm)}' has no address.");
        if (realm.AuthPort is <= 0 or > 65535)
            throw new InvalidOperationException($"Realm '{Describe(realm)}' has an invalid auth port {realm.AuthPort}.");
        if (string.IsNullOrWhiteSpace(realm.ClientExecutable))
            throw new InvalidOperationException($"Realm '{Describe(realm)}' has no client executable selected.");
    }

    private static string Describe(RealmEntry realm) =>
        realm.DisplayName.Length > 0 ? realm.DisplayName : realm.Id;
}
