namespace WindrunnerLauncher.Core.Tests;

public class LauncherPathsTests
{
    [Fact]
    public void Root_IsFullPath()
    {
        using var tmp = new TempDir();
        var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), tmp.Path);
        var paths = new LauncherPaths(rel);
        Assert.Equal(Path.GetFullPath(tmp.Path), paths.Root);
    }

    [Fact]
    public void PortableLayout_LivesBesideRoot()
    {
        using var tmp = new TempDir();
        var p = tmp.Paths();
        var root = p.Root;

        Assert.Equal(Path.Combine(root, "client"), p.Client);
        Assert.Equal(Path.Combine(root, "server"), p.ServerRoot);
        Assert.Equal(Path.Combine(root, "mods"), p.Mods);
        Assert.Equal(Path.Combine(root, "cache"), p.Cache);
        Assert.Equal(Path.Combine(root, "backups"), p.Backups);
        Assert.Equal(Path.Combine(root, "config"), p.Config);

        Assert.Equal(Path.Combine(root, "config", "settings.json"), p.SettingsFile);
        Assert.Equal(Path.Combine(root, "config", "realms.json"), p.RealmsFile);
        Assert.Equal(Path.Combine(root, "config", "rollback.json"), p.RollbackFile);
        Assert.Equal(Path.Combine(root, "cache", "downloads"), p.DownloadCache);
        Assert.Equal(Path.Combine(root, "cache", "manifests"), p.ManifestCache);
        Assert.Equal(Path.Combine(root, "backups", "server-previous"), p.ServerBackupDir);
    }

    [Fact]
    public void ServerLayout_UsesPortableDirectoryStructure()
    {
        using var tmp = new TempDir();
        var p = tmp.Paths();
        var server = p.ServerRoot;

        Assert.Equal(Path.Combine(server, "server"), p.ServerBinaries);
        Assert.Equal(Path.Combine(server, "mariadb"), p.MariaDb);
        Assert.Equal(Path.Combine(server, "sql"), p.Sql);
        Assert.Equal(Path.Combine(server, "maps"), p.Maps);
        Assert.Equal(Path.Combine(server, "logs"), p.Logs);
        Assert.Equal(Path.Combine(server, "conf"), p.Conf);
        Assert.Equal(Path.Combine(server, "data"), p.ServerData);
        Assert.Equal(Path.Combine(server, "portable.env"), p.PortableEnv);
        Assert.Equal(Path.Combine(server, "portable.local.env"), p.PortableLocalEnv);
        Assert.Equal(Path.Combine(server, "conf", "my.ini"), p.MyIni);
        Assert.Equal(Path.Combine(server, "conf", "my.ini.template"), p.MyIniTemplate);
        Assert.Equal(Path.Combine(server, "data", ".setup-complete"), p.SetupCompleteMarker);
        Assert.Equal(Path.Combine(server, "data", ".setup-incomplete"), p.SetupIncompleteMarker);
        Assert.Equal(Path.Combine(server, "data", ".server-release"), p.ServerReleaseMarker);
        Assert.Equal(Path.Combine(server, "data", ".sql-release"), p.SqlReleaseMarker);
    }

    [Fact]
    public void EnsureLayout_CreatesAllDirectories_AndIsIdempotent()
    {
        using var tmp = new TempDir();
        var p = tmp.Paths();
        p.EnsureLayout();
        p.EnsureLayout();

        foreach (var dir in new[]
                 {
                     p.Client, p.ServerRoot, p.Mods, p.Cache, p.Backups, p.Config, p.DownloadCache, p.ManifestCache,
                     p.Logs, p.Conf, p.ServerData, p.Maps, p.Sql, p.ServerBinaries
                 })
        {
            Assert.True(Directory.Exists(dir), dir);
        }

        // Nothing outside the root must be touched, and no files are created by layout alone.
        Assert.Empty(Directory.EnumerateFiles(p.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ClientForRealm_UsesOverrideWhenSet()
    {
        using var tmp = new TempDir();
        var p = tmp.Paths();
        var custom = tmp.Dir("elsewhere");

        Assert.Equal(p.Client, p.ClientForRealm(new RealmEntry()));
        Assert.Equal(p.Client, p.ClientForRealm(new RealmEntry { ClientDirectoryOverride = "   " }));
        Assert.Equal(Path.GetFullPath(custom), p.ClientForRealm(new RealmEntry { ClientDirectoryOverride = custom }));
    }

    [Fact]
    public void ModStore_IsPerAssetUnderMods()
    {
        using var tmp = new TempDir();
        var p = tmp.Paths();
        Assert.Equal(Path.Combine(p.Mods, "vanillafixes"), p.ModStore("vanillafixes"));
    }

    [Fact]
    public void FromExecutable_ResolvesPlatformDataDirectory()
    {
        var p = LauncherPaths.FromExecutable();
        var executableDirectory = LauncherPaths.ExecutableDirectory();
        var expected = LauncherPaths.IsDevelopmentDirectory(executableDirectory)
            ? Directory.GetCurrentDirectory()
            : OperatingSystem.IsLinux()
                ? LauncherPaths.LinuxDataHome()
                : executableDirectory;

        // Resolving paths does not create the Linux data directory on a fresh install.
        Assert.Equal(Path.GetFullPath(expected), p.Root);
        Assert.True(Path.IsPathRooted(p.Root));
    }
}
