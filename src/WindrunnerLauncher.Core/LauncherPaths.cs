namespace WindrunnerLauncher.Core;

/// <summary>
/// Portable layout beside the launcher executable:
/// Launcher.exe / client / server / mods / cache / backups / config
/// </summary>
public sealed class LauncherPaths
{
    public string Root { get; }
    public string Client => Path.Combine(Root, "client");
    public string ServerRoot => Path.Combine(Root, "server");
    public string Mods => Path.Combine(Root, "mods");
    public string Cache => Path.Combine(Root, "cache");
    public string Backups => Path.Combine(Root, "backups");
    public string Config => Path.Combine(Root, "config");
    public string SettingsFile => Path.Combine(Config, "settings.json");
    public string RealmsFile => Path.Combine(Config, "realms.json");
    public string RollbackFile => Path.Combine(Config, "rollback.json");
    public string AddonsFile => Path.Combine(Config, "addons.json");
    public string DownloadCache => Path.Combine(Cache, "downloads");
    public string ManifestCache => Path.Combine(Cache, "manifests");

    public string ServerBinaries => Path.Combine(ServerRoot, "server");
    public string MariaDb => Path.Combine(ServerRoot, "mariadb");
    public string Sql => Path.Combine(ServerRoot, "sql");
    public string Maps => Path.Combine(ServerRoot, "maps");
    public string Logs => Path.Combine(ServerRoot, "logs");
    public string Conf => Path.Combine(ServerRoot, "conf");
    public string ServerData => Path.Combine(ServerRoot, "data");
    public string PortableEnv => Path.Combine(ServerRoot, "portable.env");
    public string PortableLocalEnv => Path.Combine(ServerRoot, "portable.local.env");
    public string MyIni => Path.Combine(Conf, "my.ini");
    public string MyIniTemplate => Path.Combine(Conf, "my.ini.template");
    public string SetupCompleteMarker => Path.Combine(ServerData, ".setup-complete");
    public string SetupIncompleteMarker => Path.Combine(ServerData, ".setup-incomplete");
    public string ServerReleaseMarker => Path.Combine(ServerData, ".server-release");
    public string SqlReleaseMarker => Path.Combine(ServerData, ".sql-release");
    public string WinePrefix => Path.Combine(Root, "wineprefix");

    /// <summary>Proton cannot use a win32 Wine prefix, so it gets its own.</summary>
    public string ProtonPrefix => Path.Combine(Root, "protonprefix");

    public string Tools => Path.Combine(Root, "tools");
    public string UmuDir => Path.Combine(Tools, "umu");
    public string GitHubTokenFile => Path.Combine(Config, "github.token");

    public LauncherPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>Directory of the running executable. Language packs may live here beside the binary.</summary>
    public static string ExecutableDirectory()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            exe = AppContext.BaseDirectory;
        return Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
    }

    public static bool IsDevelopmentDirectory(string dir) =>
        dir.Contains("go-build", StringComparison.OrdinalIgnoreCase)
        || dir.Contains(Path.Combine("bin", "Debug"), StringComparison.Ordinal)
        || dir.Contains(Path.Combine("bin", "Release"), StringComparison.Ordinal);

    /// <summary>
    /// Windows keeps the portable tree beside the executable. Linux uses
    /// <c>$XDG_DATA_HOME/windrunner-launcher</c>, or <c>~/.local/share/windrunner-launcher</c>.
    /// A development build under <c>bin/Debug</c> or <c>bin/Release</c> stays in the working
    /// directory on every OS, so tests do not write into the home folder.
    /// </summary>
    public static LauncherPaths FromExecutable()
    {
        var dir = ExecutableDirectory();
        if (IsDevelopmentDirectory(dir))
            return new LauncherPaths(Directory.GetCurrentDirectory());

        if (OperatingSystem.IsLinux())
            return new LauncherPaths(LinuxDataHome());

        return new LauncherPaths(dir);
    }

    public static string LinuxDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg, "windrunner-launcher");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        return Path.Combine(home, ".local", "share", "windrunner-launcher");
    }

    public void EnsureLayout()
    {
        foreach (var dir in new[] { Client, ServerRoot, Mods, Cache, Backups, Config, DownloadCache, ManifestCache, Logs, Conf, ServerData, Maps, Sql, ServerBinaries })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string ClientForRealm(Models.RealmEntry realm)
    {
        if (!string.IsNullOrWhiteSpace(realm.ClientDirectoryOverride))
            return Path.GetFullPath(realm.ClientDirectoryOverride);
        return Client;
    }

    public string ModStore(string assetId) => Path.Combine(Mods, assetId);
    public string ServerBackupDir => Path.Combine(Backups, "server-previous");
}
