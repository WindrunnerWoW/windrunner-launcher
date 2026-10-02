using System.Diagnostics;
using System.Text;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Platform;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Owns the portable MariaDB/MySQL datadir under the launcher server root.
/// Never issues SHUTDOWN to a mysqld that this launcher did not start or verify.
/// Binds to 127.0.0.1 only.
/// </summary>
public sealed class MariaDbManager : IDisposable
{
    private readonly LauncherPaths _paths;
    private readonly StateStore _state;
    private readonly PortableEnv _env;
    private readonly object _gate = new();
    private OwnedProcess? _process;
    private DownloadManager? _downloads;
    private bool _startedHere;
    private bool _disposed;

    public MariaDbManager(LauncherPaths paths, StateStore state)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _env = new PortableEnv(paths);
        Log = new ConsoleCapture();
    }

    /// <summary>Captured mysqld/mysql client output.</summary>
    public ConsoleCapture Log { get; }

    /// <summary>Raised for each captured output line.</summary>
    public event Action<string>? Output;

    /// <summary>True when mariadbd/mysqld exists under <c>server/mariadb</c>.</summary>
    public bool IsInstalled => FindBinDir() is not null && FindDaemonPath() is not null;

    /// <summary>True when <c>SELECT 1</c> succeeds against the configured port.</summary>
    public bool IsReady
    {
        get
        {
            try
            {
                return MysqlReady();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when this manager started the current mysqld process.</summary>
    public bool StartedHere
    {
        get
        {
            lock (_gate)
                return _startedHere && _process is { Process.HasExited: false };
        }
    }

    /// <summary>Downloads the MariaDB zip into cache and flattens one nested folder into <c>mariadb/</c>.</summary>
    public async Task FetchAsync(CancellationToken ct = default, Action<string>? log = null)
    {
        ThrowIfDisposed();
        _env.Reload();
        if (IsInstalled)
        {
            log?.Invoke("MariaDB already at " + FindBinDir());
            return;
        }

        var version = _env.Get("MARIADB_VERSION", PortableEnv.DefaultMariadbVersion);
        var url = _env.MariaDbDownloadUrl();
        var cache = Path.Combine(_paths.DownloadCache, "mariadb");
        Directory.CreateDirectory(cache);
        var zipPath = Path.Combine(cache, PortableEnv.MariaDbArchiveFileName(version, url));
        if (!File.Exists(zipPath))
        {
            log?.Invoke($"Downloading MariaDB {version} from {url}");
            var downloads = Downloads();
            await downloads.DownloadAsync(new DownloadRequest
            {
                Id = "mariadb",
                DisplayName = $"MariaDB {version}",
                Url = url,
                DestinationPath = zipPath
            }, ct).ConfigureAwait(false);
        }

        log?.Invoke("Unpacking MariaDB...");
        var extract = Path.Combine(cache, "extract-" + version);
        if (Directory.Exists(extract))
            Directory.Delete(extract, recursive: true);
        Directory.CreateDirectory(extract);
        try
        {
            if (ArchiveUtil.LooksLikeGzip(zipPath) && !ServerUtil.LooksLikeZip(zipPath))
                ArchiveUtil.ExtractTarGz(zipPath, extract);
            else
                ArchiveUtil.ExtractZip(zipPath, extract);
            var inner = Directory.EnumerateDirectories(extract).FirstOrDefault();
            if (inner is null)
            {
                File.Delete(zipPath);
                throw new InvalidDataException($"ZIP had no top-level folder under {extract}.");
            }

            var dest = _paths.MariaDb;
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(dest);
            foreach (var child in Directory.EnumerateFileSystemEntries(inner))
            {
                var name = Path.GetFileName(child);
                ServerUtil.MoveReplace(child, Path.Combine(dest, name));
            }

            if (!IsInstalled)
            {
                File.Delete(zipPath);
                throw new InvalidOperationException("Unpack finished but mysqld/mariadbd is missing.");
            }

            MarkBinariesExecutable(dest);

            log?.Invoke("OK: " + FindBinDir());
        }
        finally
        {
            if (Directory.Exists(extract))
                Directory.Delete(extract, recursive: true);
        }
    }

    /// <summary>
    /// Writes <c>conf/my.ini</c> from the template, substituting port, basedir, datadir, and tmpdir.
    /// Ensures <c>bind-address=127.0.0.1</c>.
    /// </summary>
    public void GenerateMyIni()
    {
        ThrowIfDisposed();
        _env.Reload();
        var template = _paths.MyIniTemplate;
        if (!File.Exists(template))
            throw new FileNotFoundException("Missing my.ini.template. Rerun the launcher so bundled templates are extracted.", template);

        var bin = FindBinDir() ?? throw new InvalidOperationException("MariaDB bin directory not found. Fetch MariaDB first.");
        var basedir = Directory.GetParent(bin)?.FullName ?? bin;
        var dataDir = Path.Combine(_paths.ServerData, "mysql");
        var tmpDir = Path.Combine(_paths.ServerData, "mysql-tmp");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(_paths.Conf);

        var port = MysqlPort();
        var text = File.ReadAllText(template);
        text = text
            .Replace("@MYSQL_PORT@", port.ToString(), StringComparison.Ordinal)
            .Replace("@BASEDIR@", ServerUtil.IniPath(basedir), StringComparison.Ordinal)
            .Replace("@DATADIR@", ServerUtil.IniPath(dataDir), StringComparison.Ordinal)
            .Replace("@TMPDIR@", ServerUtil.IniPath(tmpDir), StringComparison.Ordinal);
        if (!text.Contains("bind-address", StringComparison.OrdinalIgnoreCase))
            text += "\nbind-address=127.0.0.1\n";
        File.WriteAllText(_paths.MyIni, text);
    }

    /// <summary>
    /// Initializes <c>data/mysql</c> when <c>data/mysql/mysql</c> is missing,
    /// using <c>mariadb-install-db</c> or <c>--initialize-insecure</c>.
    /// </summary>
    public async Task InitDatadirAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _env.Reload();
        var dataDir = Path.Combine(_paths.ServerData, "mysql");
        if (DatadirInitialized(dataDir))
            return;
        if (!File.Exists(_paths.MyIni))
            GenerateMyIni();

        var bin = RequireBinDir();
        var daemon = RequireDaemon();
        // Marks the datadir as unfinished until every step below succeeds, so an interrupted or
        // killed initialisation is wiped and redone instead of being mistaken for a working one.
        var incomplete = IncompleteMarker(dataDir);
        ServerUtil.WriteMarker(incomplete, DateTime.UtcNow.ToString("O"));
        await StopStrayInitProcessesAsync(bin, daemon, ct).ConfigureAwait(false);
        Directory.CreateDirectory(dataDir);
        ClearUninitializedDatadir(dataDir);

        var installDb = ServerUtil.FindExisting(bin, "mariadb-install-db", "mysql_install_db");
        var initialized = false;
        if (installDb is not null)
        {
            try
            {
                await RunLoggedAsync(installDb, _paths.ServerRoot,
                    ["--datadir=" + dataDir, $"--port={MysqlPort()}"], ct).ConfigureAwait(false);
                // The unfinished marker is still present here, so check the system tables directly.
                initialized = HasSystemTables(dataDir);
            }
            catch (Exception ex)
            {
                Emit($"mariadb-install-db failed ({ex.Message}), falling back to --initialize-insecure");
                ClearUninitializedDatadir(dataDir);
            }
        }
        else
        {
            Emit("no mariadb-install-db, falling back to --initialize-insecure");
        }

        if (!initialized)
        {
            await RunLoggedAsync(daemon, _paths.ServerRoot,
                ["--defaults-file=" + _paths.MyIni, "--initialize-insecure"], ct).ConfigureAwait(false);
        }

        var rootPass = _env.Get("MYSQL_ROOT_PASSWORD", "");
        if (string.IsNullOrEmpty(rootPass))
        {
            File.Delete(incomplete);
            return;
        }

        Emit("setting the initial root password");
        await StartDaemonAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitReadyAsync(TimeSpan.FromSeconds(90), ct, rootUser: "root", rootPass: "").ConfigureAwait(false);
            var passSql = ServerUtil.SqlLiteral(rootPass);
            var sql =
                "ALTER USER IF EXISTS 'root'@'localhost' IDENTIFIED BY " + passSql + ";\n" +
                "ALTER USER IF EXISTS 'root'@'127.0.0.1' IDENTIFIED BY " + passSql + ";\nFLUSH PRIVILEGES;";
            await InvokeSqlAsync(sql, null, false, ct, user: "root", password: "").ConfigureAwait(false);
            if (!MysqlReady("root", rootPass))
                throw new InvalidOperationException("Root password was set but could not be verified over the portable MySQL connection.");
            File.Delete(incomplete);
        }
        finally
        {
            await StopOwnedAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Starts mysqld if it is not already answering <c>SELECT 1</c>. Waits up to 90 seconds.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _env.Reload();
        if (!File.Exists(_paths.MyIni))
            throw new FileNotFoundException($"no {_paths.MyIni} - run Full setup first", _paths.MyIni);

        var daemon = RequireDaemon();
        var port = MysqlPort();
        AssertPort(port, daemon);
        if (MysqlReady())
        {
            Emit($"mysqld already up on {port}");
            return;
        }

        var recorded = PidFiles.Read(_paths.ServerData, "mysqld");
        if (recorded > 0 && ServerUtil.ProcessMatchesImage(recorded, daemon))
            throw new InvalidOperationException($"portable mysqld is already listening on {port} but rejected the configured credentials; check MYSQL_ROOT_USER/MYSQL_ROOT_PASSWORD");

        Emit("starting mysqld");
        await StartDaemonAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitReadyAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        }
        catch
        {
            await StopOwnedAsync(ct).ConfigureAwait(false);
            throw;
        }

        Emit($"mysqld pid {_process?.Id}");
    }

    /// <summary>Gracefully SHUTDOWN only if this launcher owns the process. Waits up to 30 seconds.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _env.Reload();
        var daemon = FindDaemonPath();
        if (daemon is null)
        {
            Emit("no mariadb folder");
            PidFiles.Remove(_paths.ServerData, "mysqld");
            return;
        }

        if (!OwnsRunningServer(daemon))
        {
            var recorded = PidFiles.Read(_paths.ServerData, "mysqld");
            if (recorded > 0 && ServerUtil.ProcessMatchesImage(recorded, daemon))
            {
                Emit($"stopping portable mysqld pid {recorded}");
                KillPid(recorded);
                PidFiles.Remove(_paths.ServerData, "mysqld");
                ReleaseProcess();
                return;
            }

            PidFiles.Remove(_paths.ServerData, "mysqld");
            Emit($"could not verify ownership of the process listening on {MysqlPort()}; nothing was stopped");
            return;
        }

        if (!MysqlReady())
        {
            Emit($"portable mysqld owns port {MysqlPort()} but rejected the configured credentials; stopping the verified process");
            KillOwned(daemon);
            PidFiles.Remove(_paths.ServerData, "mysqld");
            return;
        }

        Emit($"SHUTDOWN on {MysqlPort()}");
        try
        {
            await InvokeSqlAsync("SHUTDOWN;", null, false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Emit("SHUTDOWN failed - killing the verified portable process: " + ex.Message);
            KillOwned(daemon);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!MysqlReady())
            {
                Emit("stopped");
                PidFiles.Remove(_paths.ServerData, "mysqld");
                ReleaseProcess();
                return;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        Emit("still up after 30s");
    }

    public Task<string> InvokeSqlAsync(string sql, CancellationToken ct) =>
        InvokeSqlAsync(sql, null, false, ct);

    public Task<string> InvokeSqlAsync(string sql, string? database, CancellationToken ct) =>
        InvokeSqlAsync(sql, database, false, ct);

    /// <summary>
    /// Runs SQL via the mysql/mariadb client. The password is written to a 0600 temp <c>.cnf</c>, never argv.
    /// SQL is fed on stdin.
    /// </summary>
    public Task<string> InvokeSqlAsync(string sql, string? database = null, bool force = false, CancellationToken ct = default) =>
        InvokeSqlAsync(sql, database, force, ct, user: null, password: null);

    /// <summary>Imports a <c>.sql</c> file into an optional database.</summary>
    public async Task<string> InvokeSqlFileAsync(string file, string? database = null, bool force = false, CancellationToken ct = default)
    {
        if (!File.Exists(file))
            throw new FileNotFoundException("SQL file not found.", file);
        return await InvokeClientAsync(database, force, sql: null, inputFile: file, ct, null, null).ConfigureAwait(false);
    }

    internal async Task<string> InvokeSqlAsync(string sql, string? database, bool force, CancellationToken ct, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("mysql requires SQL or an input file.", nameof(sql));
        return await InvokeClientAsync(database, force, sql, null, ct, user, password).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a full logical dump of <paramref name="database"/> (schema, data, routines, triggers)
    /// to <paramref name="outFile"/> with mariadb-dump. The dump recreates the database on import,
    /// so it can be restored with <see cref="InvokeSqlFileAsync"/> without naming a database.
    /// </summary>
    public async Task DumpDatabaseAsync(string database, string outFile, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _env.Reload();
        var bin = RequireBinDir();
        var dump = ServerUtil.FindExisting(bin, "mariadb-dump", "mysqldump")
                   ?? throw new FileNotFoundException("no mariadb-dump/mysqldump under " + bin);
        var user = _env.Get("MYSQL_ROOT_USER", PortableEnv.DefaultMysqlRootUser);
        var password = _env.Get("MYSQL_ROOT_PASSWORD", "");
        var port = MysqlPort();
        string? credFile = null;
        var partial = outFile + ".partial";
        try
        {
            if (!string.IsNullOrEmpty(password))
                credFile = WriteClientCnf(user, password, port);
            var plugin = Path.Combine(Directory.GetParent(bin)?.FullName ?? bin, "lib", "plugin");
            var args = BuildDumpArguments(database, user, port, credFile,
                File.Exists(_paths.MyIni) ? _paths.MyIni : null,
                Directory.Exists(plugin) ? plugin : null);

            var psi = new ProcessStartInfo
            {
                FileName = dump,
                WorkingDirectory = _paths.ServerRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start {dump}");

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await using (var output = File.Create(partial))
                await process.StandardOutput.BaseStream.CopyToAsync(output, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"exit {process.ExitCode}" : stderr.Trim();
                throw new InvalidOperationException($"Backing up {database} failed: {msg}");
            }

            File.Move(partial, outFile, overwrite: true);
            Emit($"dumped {database} to {outFile}");
        }
        finally
        {
            if (credFile is not null)
            {
                try { File.Delete(credFile); } catch { }
            }

            try { File.Delete(partial); } catch { }
        }
    }

    /// <summary>Command line for <see cref="DumpDatabaseAsync"/>. The password never appears on argv.</summary>
    internal static IReadOnlyList<string> BuildDumpArguments(
        string database, string user, int port, string? credFile, string? myIni, string? pluginDir)
    {
        var args = new List<string>();
        // --defaults-(extra-)file must come first or the client ignores it.
        if (credFile is not null)
            args.Add("--defaults-extra-file=" + credFile);
        else if (myIni is not null)
            args.Add("--defaults-file=" + myIni);
        if (pluginDir is not null)
            args.Add("--plugin-dir=" + pluginDir);
        args.Add("-u" + user);
        args.Add("-h127.0.0.1");
        args.Add($"-P{port}");
        args.Add("--single-transaction");
        args.Add("--routines");
        args.Add("--triggers");
        args.Add("--events");
        args.Add("--hex-blob");
        args.Add("--default-character-set=utf8mb4");
        args.Add("--add-drop-database");
        args.Add("--databases");
        args.Add(database);
        return args;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            var daemon = FindDaemonPath();
            if (daemon is not null && OwnsRunningServer(daemon))
                KillOwned(daemon);
        }
        catch
        {
        }

        ReleaseProcess();
        _downloads?.Dispose();
    }

    private async Task<string> InvokeClientAsync(
        string? database,
        bool force,
        string? sql,
        string? inputFile,
        CancellationToken ct,
        string? userOverride,
        string? passwordOverride)
    {
        ThrowIfDisposed();
        _env.Reload();
        var bin = RequireBinDir();
        var client = RequireClient();
        var user = userOverride ?? _env.Get("MYSQL_ROOT_USER", PortableEnv.DefaultMysqlRootUser);
        var password = passwordOverride ?? _env.Get("MYSQL_ROOT_PASSWORD", "");
        var port = MysqlPort();
        var args = new List<string>();
        string? credFile = null;
        try
        {
            if (!string.IsNullOrEmpty(password))
            {
                credFile = WriteClientCnf(user, password, port);
                args.Add("--defaults-extra-file=" + credFile);
            }
            else if (File.Exists(_paths.MyIni))
            {
                args.Add("--defaults-file=" + _paths.MyIni);
            }

            var plugin = Path.Combine(Directory.GetParent(bin)?.FullName ?? bin, "lib", "plugin");
            if (Directory.Exists(plugin))
                args.Add("--plugin-dir=" + plugin);

            args.Add("-u" + user);
            args.Add("-h127.0.0.1");
            args.Add($"-P{port}");
            args.Add("--batch");
            args.Add("--raw");
            args.Add("--skip-column-names");
            if (force)
                args.Add("--force");
            if (!string.IsNullOrWhiteSpace(database))
                args.Add(database);

            var psi = new ProcessStartInfo
            {
                FileName = client,
                WorkingDirectory = _paths.ServerRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start {client}");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            if (sql is not null)
                await process.StandardInput.WriteAsync(sql.AsMemory(), ct).ConfigureAwait(false);
            else if (inputFile is not null)
            {
                await using var input = File.OpenRead(inputFile);
                await input.CopyToAsync(process.StandardInput.BaseStream, ct).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdout))
                Emit(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
                Emit(stderr.TrimEnd());
            if (process.ExitCode != 0 && !force)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"mysql failed with exit {process.ExitCode}" : stderr.Trim();
                throw new InvalidOperationException("mysql failed: " + msg);
            }

            return stdout;
        }
        finally
        {
            if (credFile is not null)
            {
                try { File.Delete(credFile); } catch { }
            }
        }
    }

    private string WriteClientCnf(string user, string password, int port)
    {
        var dir = Path.Combine(_paths.ServerData, "mysql-tmp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".mysql-client-" + Guid.NewGuid().ToString("N")[..16] + ".cnf");
        var text = $"[client]\r\nuser=\"{ServerUtil.QuoteCnf(user)}\"\r\npassword=\"{ServerUtil.QuoteCnf(password)}\"\r\nhost=\"127.0.0.1\"\r\nport={port}\r\n";
        File.WriteAllText(path, text, Encoding.UTF8);
        ServerUtil.SetOwnerOnlyFileMode(path);
        return path;
    }

    private async Task StartDaemonAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var daemon = RequireDaemon();
        DataReceivedEventHandler handler = (_, e) =>
        {
            if (e.Data is not null)
                Emit(e.Data);
        };
        var owned = ProcessHost.StartHidden(
            daemon,
            _paths.ServerRoot,
            ["--defaults-file=" + _paths.MyIni],
            captureOutput: true,
            onOutput: handler,
            holdStdin: false);
        lock (_gate)
        {
            _process = owned;
            _startedHere = true;
        }

        PidFiles.Write(_paths.ServerData, "mysqld", owned.Id);
        await Task.Delay(200, ct).ConfigureAwait(false);
        if (owned.Process.HasExited)
        {
            ReleaseProcess();
            var hint = OperatingSystem.IsLinux()
                ? " On Linux the official MariaDB build needs libaio and ncurses (libncurses) installed."
                : "";
            throw new InvalidOperationException("mysqld exited during startup." + hint);
        }
    }

    private async Task WaitReadyAsync(TimeSpan timeout, CancellationToken ct, string? rootUser = null, string? rootPass = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (MysqlReady(rootUser, rootPass))
                return;
            lock (_gate)
            {
                if (_process?.Process.HasExited == true)
                    throw new InvalidOperationException("mysqld exited before becoming ready.");
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"MySQL did not become ready on port {MysqlPort()} within {(int)timeout.TotalSeconds}s.");
    }

    private bool MysqlReady(string? user = null, string? password = null)
    {
        try
        {
            InvokeSqlAsync("SELECT 1;", null, false, CancellationToken.None, user, password).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AssertPort(int port, string daemon)
    {
        if (!ServerUtil.TcpPortInUse(port))
            return;
        if (OwnsRunningServer(daemon))
            return;
        var hint = $"Port {port} is already used by another MySQL/MariaDB server.";
        if (port == 3306)
            hint += " A local MySQL 8 install often owns 3306 (and 33060).";
        hint += " Stop that service or set MYSQL_PORT=3307 in portable.local.env, then run setup again.";
        throw new InvalidOperationException(hint);
    }

    private bool OwnsRunningServer(string daemon)
    {
        lock (_gate)
        {
            if (_process is { Process.HasExited: false })
                return true;
        }

        var recorded = PidFiles.Read(_paths.ServerData, "mysqld");
        return recorded > 0 && ProcessHost.IsAlive(recorded) && ServerUtil.ProcessMatchesImage(recorded, daemon);
    }

    private void KillOwned(string daemon)
    {
        lock (_gate)
        {
            _process?.Kill();
        }

        var recorded = PidFiles.Read(_paths.ServerData, "mysqld");
        if (recorded > 0 && ServerUtil.ProcessMatchesImage(recorded, daemon))
            KillPid(recorded);
        foreach (var pid in ServerUtil.ProcessesByImage(daemon))
            KillPid(pid);
        ReleaseProcess();
    }

    private async Task StopOwnedAsync(CancellationToken ct)
    {
        try
        {
            await StopAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            var daemon = FindDaemonPath();
            if (daemon is not null)
                KillOwned(daemon);
        }
    }

    private void ReleaseProcess()
    {
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
            _startedHere = false;
        }
    }

    /// <summary>
    /// An interrupted initialisation (launcher killed or crashed) leaves the bootstrap daemon that
    /// mariadb-install-db spawned running without a pid file, holding the datadir open. The datadir
    /// is unfinished, so nothing of value runs there; stop every portable copy before wiping it.
    /// </summary>
    private async Task StopStrayInitProcessesAsync(string bin, string daemon, CancellationToken ct)
    {
        var images = new List<string> { daemon };
        if (ServerUtil.FindExisting(bin, "mariadb-install-db", "mysql_install_db") is { } installDb)
            images.Add(installDb);

        foreach (var image in images)
        {
            foreach (var pid in ServerUtil.ProcessesByImage(image))
            {
                Emit($"stopping leftover {Path.GetFileName(image)} pid {pid} from an interrupted setup");
                KillPid(pid);
                await ProcessHost.WaitGoneAsync(pid, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
        }

        PidFiles.Remove(_paths.ServerData, "mysqld");
        ReleaseProcess();
    }

    private static void KillPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static async Task RunLoggedAsync(string file, string cwd, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start " + file);
        // Both pipes must drain concurrently: a child that fills an unread stdout buffer blocks
        // forever, and so would a ReadToEnd on stderr waiting for it.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(stderr) ? $"exit {process.ExitCode}" : stderr.Trim();
            throw new InvalidOperationException($"{Path.GetFileName(file)} failed: {msg}");
        }
    }

    private static bool DatadirInitialized(string dataDir) =>
        HasSystemTables(dataDir) && !File.Exists(IncompleteMarker(dataDir));

    private static bool HasSystemTables(string dataDir) =>
        Directory.Exists(Path.Combine(dataDir, "mysql"));

    /// <summary>Lives beside the datadir so wiping the datadir leaves it in place.</summary>
    private static string IncompleteMarker(string dataDir) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataDir))!, ".datadir-incomplete");

    private static void ClearUninitializedDatadir(string dataDir)
    {
        if (DatadirInitialized(dataDir))
            throw new InvalidOperationException("refusing to wipe initialized datadir: " + dataDir);
        if (Directory.Exists(dataDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dataDir))
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
        }
        else
        {
            Directory.CreateDirectory(dataDir);
        }
    }

    private int MysqlPort()
    {
        try
        {
            return _env.GetInt("MYSQL_PORT", _state.Settings.Server.MysqlPort);
        }
        catch
        {
            return _state.Settings.Server.MysqlPort;
        }
    }

    private string RequireBinDir() =>
        FindBinDir() ?? throw new InvalidOperationException("MariaDB not found. Run Full setup first.");

    private string RequireDaemon() =>
        FindDaemonPath() ?? throw new FileNotFoundException("no mariadbd/mysqld under " + (FindBinDir() ?? _paths.MariaDb));

    private string RequireClient() =>
        FindClientPath() ?? throw new FileNotFoundException("no mariadb/mysql client under " + (FindBinDir() ?? _paths.MariaDb));

    private string? FindBinDir()
    {
        var root = _paths.MariaDb;
        if (!Directory.Exists(root))
            return null;
        var direct = Path.Combine(root, "bin");
        if (IsDaemonDir(direct))
            return direct;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var bin = Path.Combine(dir, "bin");
            if (IsDaemonDir(bin))
                return bin;
        }

        if (IsDaemonDir(root))
            return root;
        return null;
    }

    private string? FindDaemonPath()
    {
        var bin = FindBinDir();
        return bin is null ? null : ServerUtil.FindExisting(bin, "mariadbd", "mysqld");
    }

    private string? FindClientPath()
    {
        var bin = FindBinDir();
        return bin is null ? null : ServerUtil.FindExisting(bin, "mariadb", "mysql");
    }

    private static bool IsDaemonDir(string dir) =>
        ServerUtil.FindExisting(dir, "mariadbd", "mysqld") is not null;

    private static void MarkBinariesExecutable(string root)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(root))
            return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var inBin = file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
            var nativeLib = name.Contains(".so", StringComparison.OrdinalIgnoreCase);
            if (!inBin && !nativeLib)
                continue;
            ServerUtil.MakeUserExecutable(file);
        }
    }

    private DownloadManager Downloads()
    {
        lock (_gate)
            return _downloads ??= new DownloadManager();
    }

    private void Emit(string message)
    {
        Log.Append(message.EndsWith('\n') ? message : message + "\n");
        Output?.Invoke(message);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
