using System.IO.Compression;
using System.Net;
using System.Text.Json;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Security;
using WindrunnerLauncher.Core.Server;
using WindrunnerLauncher.Core.Updates;

namespace WindrunnerLauncher.Core.Tests;

public class ServerUpdateTests
{
    [Fact]
    public async Task Check_PicksHighestServerRelease_IgnoringClientPrereleaseAndDrafts()
    {
        using var env = new Env();
        env.InstallServer("1.18.1");
        env.Releases =
        [
            Release("Client", serverAsset: false),
            Release("1.18.2"),
            Release("1.19.0", prerelease: true),
            Release("1.20.0", draft: true),
            Release("1.18.3"),
            Release("1.18.0")
        ];

        var problems = await env.Updates.CheckServerAsync();

        Assert.Empty(problems);
        Assert.Equal("1.18.3", env.Updates.LatestServerRelease);
        Assert.Equal("Notes for 1.18.3", env.Updates.LatestServerNotes);
        Assert.True(env.Updates.ServerUpdateAvailable);
        Assert.Equal(300, env.Updates.LatestServerDownloadBytes);
    }

    [Fact]
    public async Task Check_FreshInstallWithOnlyMarker_IsNotOutdated()
    {
        using var env = new Env();
        env.InstallServer(buildInfoVersion: null);
        File.WriteAllText(env.Paths.ServerReleaseMarker, "1.18.2\n");
        env.Releases = [Release("1.18.2")];

        await env.Updates.CheckServerAsync();

        Assert.Equal("1.18.2", env.Updates.InstalledServerVersion);
        Assert.False(env.Updates.ServerUpdateAvailable);
    }

    [Fact]
    public async Task Check_WithoutInstalledServer_OffersNothing()
    {
        using var env = new Env();
        env.Releases = [Release("1.18.3")];

        await env.Updates.CheckServerAsync();

        Assert.Equal("1.18.3", env.Updates.LatestServerRelease);
        Assert.False(env.Updates.ServerUpdateAvailable);
    }

    [Fact]
    public async Task Skip_HidesThatRelease_UntilANewerOneShips()
    {
        using var env = new Env();
        env.InstallServer("1.18.1");
        env.Releases = [Release("1.18.2")];
        await env.Updates.CheckServerAsync();
        Assert.True(env.Updates.ServerUpdateAvailable);

        env.Updates.IgnoreServerVersion();
        Assert.Equal("1.18.2", env.State.Settings.IgnoredServerRelease);
        await env.Updates.CheckServerAsync();
        Assert.False(env.Updates.ServerUpdateAvailable);

        env.Releases = [Release("1.18.2"), Release("1.18.3")];
        await env.Updates.CheckServerAsync();
        Assert.True(env.Updates.ServerUpdateAvailable);
    }

    [Fact]
    public async Task Update_ReplacesFiles_KeepsRuntimeFiles_BacksUpAndRecordsVersion()
    {
        using var env = new Env();
        env.InstallServer("1.18.1");
        File.WriteAllText(Path.Combine(env.Paths.ServerBinaries, "removed.dll"), "stale");
        File.WriteAllText(Path.Combine(env.Paths.ServerBinaries, "honorupdate.txt"), "runtime");
        File.WriteAllText(Path.Combine(env.Paths.Sql, "create_databases.sql"), "old");
        File.WriteAllText(Path.Combine(env.Paths.Sql, "old-only.sql"), "old");
        env.Releases = [Release("1.18.2")];
        await env.Updates.CheckServerAsync();

        var stages = new List<SetupStage>();
        await env.Updates.UpdateServerAsync(new SyncProgress(stages.Add));

        Assert.Equal("new mangosd", File.ReadAllText(Path.Combine(env.Paths.ServerBinaries, "mangosd.exe")));
        Assert.True(File.Exists(Path.Combine(env.Paths.ServerBinaries, "lua_scripts", "init.lua")));
        Assert.False(File.Exists(Path.Combine(env.Paths.ServerBinaries, "removed.dll")), "stale binaries are removed");
        Assert.Equal("runtime", File.ReadAllText(Path.Combine(env.Paths.ServerBinaries, "honorupdate.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(env.Paths.Sql, "create_databases.sql")));
        Assert.False(File.Exists(Path.Combine(env.Paths.Sql, "old-only.sql")), "sql/ is replaced wholesale");

        Assert.Equal("1.18.2", env.State.Settings.InstalledServerVersion);
        Assert.Equal("1.18.2", File.ReadAllText(env.Paths.ServerReleaseMarker).Trim());
        Assert.Equal("1.18.2", File.ReadAllText(env.Paths.SqlReleaseMarker).Trim());
        Assert.Equal("1.18.2", env.Updates.InstalledServerVersion);
        Assert.False(env.Updates.ServerUpdateAvailable);

        Assert.True(env.Updates.HasServerRollback);
        Assert.Equal("1.18.1", env.Updates.ServerRollback!.FromVersion);
        Assert.Equal("old mangosd", File.ReadAllText(Path.Combine(env.Paths.ServerBackupDir, "server", "mangosd.exe")));
        Assert.Equal(Path.Combine(env.Paths.ServerBackupDir, "sqldump"), Assert.Single(env.Database.Backups));
        Assert.Empty(Directory.GetFiles(env.Paths.DownloadCache, "*.zip"));

        Assert.Contains(stages, s => s.Id == "update-backup-db" && s.Completed);
        Assert.Contains(stages, s => s.Id == "update-start" && s.Completed);
        Assert.DoesNotContain(stages, s => s.Failed);
        Assert.Equal(ServerLifecycleState.Stopped, env.Server.State);
    }

    [Fact]
    public async Task Update_DigestMismatch_AbortsBeforeTouchingTheServer()
    {
        using var env = new Env();
        env.InstallServer("1.18.1");
        env.Releases = [Release("1.18.2", corruptDigest: true)];
        await env.Updates.CheckServerAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => env.Updates.UpdateServerAsync());

        Assert.Equal("old mangosd", File.ReadAllText(Path.Combine(env.Paths.ServerBinaries, "mangosd.exe")));
        Assert.False(env.Updates.HasServerRollback);
        Assert.Empty(env.Database.Backups);
        Assert.True(env.Updates.ServerUpdateAvailable);
    }

    [Fact]
    public async Task Update_BrokenArchive_RestoresPreviousFiles()
    {
        using var env = new Env();
        env.InstallServer("1.18.1");
        File.WriteAllText(Path.Combine(env.Paths.Sql, "create_databases.sql"), "old");
        env.ServerZip = Zip(("readme.txt", "no binaries in here"));
        env.Releases = [Release("1.18.2")];
        await env.Updates.CheckServerAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => env.Updates.UpdateServerAsync());

        Assert.Contains("restored", ex.Message);
        Assert.Equal("old mangosd", File.ReadAllText(Path.Combine(env.Paths.ServerBinaries, "mangosd.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(env.Paths.Sql, "create_databases.sql")));
        Assert.Equal("1.18.1", env.Updates.InstalledServerVersion);
        Assert.False(env.Updates.HasServerRollback);
        Assert.Empty(env.Database.Restores);
        Assert.True(env.Updates.ServerUpdateAvailable);
    }

    [Fact]
    public async Task Rollback_RestoresFilesAndDatabases_AndOffersTheUpdateAgain()
    {
        using var env = new Env();
        env.InstallServer("1.18.1");
        env.Releases = [Release("1.18.2")];
        await env.Updates.CheckServerAsync();
        await env.Updates.UpdateServerAsync();
        var dumpDir = env.Updates.ServerRollback!.Directory;

        await env.Updates.RollbackServerAsync();

        Assert.Equal("old mangosd", File.ReadAllText(Path.Combine(env.Paths.ServerBinaries, "mangosd.exe")));
        Assert.Equal(Path.Combine(dumpDir, "sqldump"), Assert.Single(env.Database.Restores));
        Assert.Equal("1.18.1", env.State.Settings.InstalledServerVersion);
        Assert.Equal("1.18.1", env.Updates.InstalledServerVersion);
        Assert.False(env.Updates.HasServerRollback);
        Assert.True(env.Updates.ServerUpdateAvailable);
    }

    [Fact]
    public async Task ManualFullBackup_CopiesFilesAndDumpsEveryDatabase()
    {
        using var env = new Env();
        env.InstallServer("1.18.2");
        File.WriteAllText(Path.Combine(env.Paths.ServerBinaries, "ACE.dll"), "dll");
        File.WriteAllText(Path.Combine(env.Paths.ServerBinaries, "mangosd.conf"), "conf");
        File.WriteAllText(Path.Combine(env.Paths.Conf, "my.ini"), "ini");
        var backups = env.Backups();

        var info = await backups.CreateAsync(ServerBackupKind.Full);

        Assert.StartsWith(Path.Combine(env.Paths.Backups, "manual"), info.Directory);
        Assert.EndsWith("-full", info.Directory);
        foreach (var file in new[] { "mangosd.exe", "realmd.exe", "ACE.dll", "mangosd.conf" })
            Assert.True(File.Exists(Path.Combine(info.Directory, "server", file)), file);
        Assert.Equal("ini", File.ReadAllText(Path.Combine(info.Directory, "conf", "my.ini")));
        Assert.Null(Assert.Single(env.Database.BackupDatabases));
        Assert.True(File.Exists(Path.Combine(info.Directory, "sqldump", "tw_char.sql")));
        Assert.Contains("server_version=1.18.2", File.ReadAllText(Path.Combine(info.Directory, ServerBackupService.InfoFileName)));
        Assert.Equal(info.Directory, backups.Latest?.Directory);
    }

    [Fact]
    public async Task ManualCharacterBackup_DumpsOnlyAccountsAndCharacters()
    {
        using var env = new Env();
        env.InstallServer("1.18.2");
        var backups = env.Backups();

        var info = await backups.CreateAsync(ServerBackupKind.Characters);

        Assert.EndsWith("-characters", info.Directory);
        Assert.False(Directory.Exists(Path.Combine(info.Directory, "server")));
        Assert.Equal(["tw_logon", "tw_char"], Assert.Single(env.Database.BackupDatabases)!);
        Assert.Equal(ServerBackupKind.Characters, backups.Latest?.Kind);
    }

    [Fact]
    public async Task ManualBackup_Failure_LeavesNoHalfBackup()
    {
        using var env = new Env();
        env.InstallServer("1.18.2");
        env.Database.Fail = true;
        var backups = env.Backups();

        await Assert.ThrowsAsync<InvalidOperationException>(() => backups.CreateAsync(ServerBackupKind.Full));

        Assert.Empty(Directory.EnumerateDirectories(backups.Root));
        Assert.Null(backups.Latest);
    }

    [Fact]
    public async Task Preview_ShowsAnUpdate_WithoutTouchingSettingsOrFiles()
    {
        using var env = new Env();
        env.InstallServer("1.18.2");

        env.Updates.BeginServerUpdatePreview();
        Assert.True(env.Updates.ServerUpdateAvailable);
        Assert.Equal("1.18.3", env.Updates.LatestServerRelease);

        await env.Updates.CheckServerAsync(); // real checks are suppressed
        Assert.Equal("1.18.3", env.Updates.LatestServerRelease);

        env.Updates.IgnoreServerVersion();
        Assert.False(env.Updates.ServerUpdateAvailable);
        Assert.True(env.Updates.IsLatestServerReleaseSkipped);
        Assert.Null(env.State.Settings.IgnoredServerRelease);
        env.Updates.ClearIgnoredServerVersion();

        await env.Updates.UpdateServerAsync();
        Assert.False(env.Updates.ServerUpdateAvailable);
        Assert.Equal("old mangosd", File.ReadAllText(Path.Combine(env.Paths.ServerBinaries, "mangosd.exe")));
        Assert.Null(env.State.Settings.InstalledServerVersion);
        Assert.Empty(env.Database.Backups);
    }

    [Fact]
    public void DumpArguments_KeepThePasswordOffTheCommandLine()
    {
        var args = MariaDbManager.BuildDumpArguments("tw_char", "root", 3307, "C:/tmp/cred.cnf", "C:/srv/my.ini", null);

        Assert.Equal("--defaults-extra-file=C:/tmp/cred.cnf", args[0]);
        Assert.DoesNotContain(args, a => a.StartsWith("--defaults-file", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("-p", StringComparison.Ordinal) || a.StartsWith("--password", StringComparison.Ordinal));
        Assert.Contains("-P3307", args);
        Assert.Contains("--single-transaction", args);
        Assert.Contains("--add-drop-database", args);
        Assert.Equal(["--databases", "tw_char"], args.TakeLast(2));
    }

    // ---------------------------------------------------------------- fixture

    private static ReleaseSpec Release(
        string tag, bool serverAsset = true, bool prerelease = false, bool draft = false, bool corruptDigest = false) =>
        new(tag, serverAsset, prerelease, draft, corruptDigest);

    private sealed record ReleaseSpec(string Tag, bool ServerAsset, bool Prerelease, bool Draft, bool CorruptDigest);

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private sealed class Env : IDisposable
    {
        private readonly TempDir _tmp = new("srvupd");
        private readonly LoopbackHttpServer _assets;
        private readonly HttpClient _api;
        private readonly DownloadManager _downloads;
        private readonly MariaDbManager _maria;

        public LauncherPaths Paths { get; }
        public StateStore State { get; }
        public ServerManager Server { get; }
        public UpdateManager Updates { get; }
        public FakeDatabaseBackup Database { get; } = new();
        public List<ReleaseSpec> Releases { get; set; } = [];

        public byte[] ServerZip { get; set; } = Zip(
            ("windrunner-wow-windows-server/mangosd.exe", "new mangosd"),
            ("windrunner-wow-windows-server/realmd.exe", "new realmd"),
            ("windrunner-wow-windows-server/BUILD_INFO.txt", "version=NEW\n"),
            ("windrunner-wow-windows-server/lua_scripts/init.lua", "print('hi')"));

        public byte[] SqlZip { get; set; } = Zip(
            ("sql/create_databases.sql", "new"),
            ("sql/base/world.sql", "new world"));

        public Env()
        {
            Paths = _tmp.Paths();
            Paths.EnsureLayout();
            State = new StateStore(Paths);
            _assets = new LoopbackHttpServer(async (ctx, _) =>
            {
                var body = ctx.Request.Url!.AbsolutePath.Contains("sql") ? SqlZip : ServerZip;
                await LoopbackHttpServer.WriteAsync(ctx, body);
            });
            _api = new HttpClient(new ApiHandler(this));
            _downloads = new DownloadManager();
            _maria = new MariaDbManager(Paths, State);
            Server = new ServerManager(Paths, State, _maria, downloads: _downloads);
            var mods = new ModManager(Paths, State, _downloads);
            Updates = new UpdateManager(
                Paths, State, _downloads, Server, mods, new BackupRollbackManager(Paths, State),
                new GitHubReleases(_api), Database)
            {
                ServerDownloadRetryDelay = TimeSpan.Zero,
                PreviewStepDelay = TimeSpan.Zero
            };
        }

        public ServerBackupService Backups() =>
            new(Paths, Server, Database, () => Updates.InstalledServerVersion);

        /// <summary>An installed realm at <paramref name="buildInfoVersion"/>, with maps.</summary>
        public void InstallServer(string? buildInfoVersion)
        {
            File.WriteAllText(Path.Combine(Paths.ServerBinaries, "mangosd.exe"), "old mangosd");
            File.WriteAllText(Path.Combine(Paths.ServerBinaries, "realmd.exe"), "old realmd");
            if (buildInfoVersion is not null)
                File.WriteAllText(Path.Combine(Paths.ServerBinaries, "BUILD_INFO.txt"), $"version={buildInfoVersion}\ncommit=abc\n");
            foreach (var dir in new[] { "dbc", "maps", "vmaps", "mmaps" })
                _tmp.File(Path.Combine("server", "maps", dir, "x.bin"), "x");
            Assert.True(Server.IsInstalled);
        }

        private string ReleasesJson()
        {
            var serverHash = Checksums.Sha256Hex(ServerZip);
            var sqlHash = Checksums.Sha256Hex(SqlZip);
            return JsonSerializer.Serialize(Releases.Select(r => new
            {
                tag_name = r.Tag,
                name = r.Tag,
                body = "Notes for " + r.Tag,
                draft = r.Draft,
                prerelease = r.Prerelease,
                assets = r.ServerAsset
                    ? new object[]
                    {
                        new
                        {
                            name = OperatingSystem.IsLinux()
                                ? $"windrunner-wow-linux-server-{r.Tag}.zip"
                                : $"windrunner-wow-windows-server-{r.Tag}.zip",
                            browser_download_url = _assets.Url($"server-{r.Tag}.zip"),
                            size = 200,
                            digest = "sha256:" + (r.CorruptDigest ? new string('0', 64) : serverHash)
                        },
                        new
                        {
                            name = $"windrunner-wow-sql-{r.Tag}.zip",
                            browser_download_url = _assets.Url($"sql-{r.Tag}.zip"),
                            size = 100,
                            digest = "sha256:" + sqlHash
                        }
                    }
                    : new object[]
                    {
                        new { name = "patch-W.mpq", browser_download_url = _assets.Url("patch-W.mpq"), size = 1 }
                    }
            }));
        }

        public void Dispose()
        {
            Server.Dispose();
            _maria.Dispose();
            _downloads.Dispose();
            _api.Dispose();
            _assets.Dispose();
            _tmp.Dispose();
        }

        private sealed class ApiHandler(Env env) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(env.ReleasesJson(), Encoding.UTF8, "application/json")
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeDatabaseBackup : IServerDatabaseBackup
    {
        public List<string> Backups { get; } = [];
        public List<IReadOnlyList<string>?> BackupDatabases { get; } = [];
        public List<string> Restores { get; } = [];
        public bool Fail { get; set; }

        public Task BackupAsync(string directory, IReadOnlyList<string>? databases, CancellationToken ct)
        {
            if (Fail)
                throw new InvalidOperationException("mariadb-dump failed");
            Directory.CreateDirectory(directory);
            BackupDatabases.Add(databases);
            File.WriteAllText(Path.Combine(directory, "tw_char.sql"), "-- dump");
            Backups.Add(directory);
            return Task.CompletedTask;
        }

        public Task RestoreAsync(string directory, CancellationToken ct)
        {
            Assert.True(File.Exists(Path.Combine(directory, "tw_char.sql")), "the dump must still exist when restoring");
            Restores.Add(directory);
            return Task.CompletedTask;
        }
    }

    /// <summary><see cref="Progress{T}"/> posts asynchronously; tests need every report in order.</summary>
    private sealed class SyncProgress(Action<SetupStage> report) : IProgress<SetupStage>
    {
        public void Report(SetupStage value) => report(value);
    }
}
