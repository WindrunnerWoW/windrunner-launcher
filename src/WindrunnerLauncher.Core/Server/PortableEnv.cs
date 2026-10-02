using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Loads <c>portable.env</c> then <c>portable.local.env</c> (later file wins).
/// Blank lines and <c>#</c> comments are skipped; keys split on the first <c>=</c>.
/// </summary>
public sealed class PortableEnv
{
    public const string DefaultMariadbVersion = "10.11.11";
    public const string DefaultMariadbZipUrl = "https://archive.mariadb.org/mariadb-10.11.11/winx64-packages/mariadb-10.11.11-winx64.zip";
    public const string DefaultMariadbLinuxTarballUrl = "https://archive.mariadb.org/mariadb-10.11.11/bintar-linux-systemd-x86_64/mariadb-10.11.11-linux-systemd-x86_64.tar.gz";
    public const int DefaultMysqlPort = 3307;
    public const string DefaultMysqlRootUser = "root";
    public const string DefaultMysqlUser = "mangos";
    public const string DefaultMysqlPassword = "mangos";
    public const string DefaultRealmName = "Windrunner";
    public const string DefaultRealmAddress = "127.0.0.1";
    public const int DefaultRealmPort = 3724;
    public const int DefaultWorldPort = 8090;
    public const int DefaultMinRandomBots = 20;
    public const int DefaultMaxRandomBots = 20;
    public const string DefaultTortoiseWowRepo = "WindrunnerWoW/windrunner-wow";

    public const string DefaultTortoiseWowRelease = "latest";

    private static readonly IReadOnlyDictionary<string, string> BuiltInDefaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MARIADB_VERSION"] = DefaultMariadbVersion,
        ["MARIADB_ZIP_URL"] = DefaultMariadbZipUrl,
        ["MYSQL_PORT"] = DefaultMysqlPort.ToString(),
        ["MYSQL_ROOT_USER"] = DefaultMysqlRootUser,
        ["MYSQL_ROOT_PASSWORD"] = "",
        ["MYSQL_USER"] = DefaultMysqlUser,
        ["MYSQL_PASSWORD"] = DefaultMysqlPassword,
        ["REALM_NAME"] = DefaultRealmName,
        ["REALM_ADDRESS"] = DefaultRealmAddress,
        ["REALM_PORT"] = DefaultRealmPort.ToString(),
        ["WORLD_PORT"] = DefaultWorldPort.ToString(),
        ["MIN_RANDOM_BOTS"] = DefaultMinRandomBots.ToString(),
        ["MAX_RANDOM_BOTS"] = DefaultMaxRandomBots.ToString(),
        ["TORTOISE_WOW_REPO"] = DefaultTortoiseWowRepo,
        ["TORTOISE_WOW_RELEASE"] = DefaultTortoiseWowRelease
    };

    private readonly LauncherPaths _paths;
    private readonly object _gate = new();
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public PortableEnv(LauncherPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Reload();
    }

    /// <summary>Re-reads <c>portable.env</c> and <c>portable.local.env</c>.</summary>
    public void Reload()
    {
        var merged = new Dictionary<string, string>(BuiltInDefaults, StringComparer.Ordinal);
        MergeFile(merged, _paths.PortableEnv);
        MergeFile(merged, _paths.PortableLocalEnv);
        lock (_gate)
            _values = merged;
    }

    /// <summary>
    /// Windows downloads the winx64 zip. Linux downloads the official bintar unless
    /// <c>portable.local.env</c> sets <c>MARIADB_TARBALL_URL</c> or <c>MARIADB_ZIP_URL</c>.
    /// The bundled <c>portable.env</c> still names the Windows zip and is not used as the Linux URL.
    /// </summary>
    public string MariaDbDownloadUrl()
    {
        var localTarball = LocalValue("MARIADB_TARBALL_URL");
        if (!string.IsNullOrWhiteSpace(localTarball))
            return localTarball;
        var localZip = LocalValue("MARIADB_ZIP_URL");
        if (!string.IsNullOrWhiteSpace(localZip))
            return localZip;
        if (OperatingSystem.IsLinux())
            return DefaultMariadbLinuxTarballUrl;
        return Get("MARIADB_ZIP_URL", DefaultMariadbZipUrl);
    }

    public static string MariaDbArchiveFileName(string version, string url) =>
        url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            ? $"mariadb-{version}-linux-systemd-x86_64.tar.gz"
            : $"mariadb-{version}-winx64.zip";

    private string? LocalValue(string name)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeFile(values, _paths.PortableLocalEnv);
        return values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    public string Get(string name, string defaultValue = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (_values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return defaultValue;
    }

    public int GetInt(string name, int defaultValue)
    {
        var raw = Get(name, defaultValue.ToString());
        if (!int.TryParse(raw, out var n))
            throw new InvalidOperationException($"{name} must be an integer (got {raw}).");
        return n;
    }

    /// <summary>
    /// Overwrites keys in <c>portable.local.env</c> only, preserving comments and unrelated keys.
    /// Empty values remove a previously written override.
    /// </summary>
    public void WriteLocal(IReadOnlyDictionary<string, string> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var path = _paths.PortableLocalEnv;
        List<string> lines;
        if (File.Exists(path))
        {
            var data = File.ReadAllText(path);
            lines = data.Length == 0
                ? []
                : [.. data.TrimEnd('\r', '\n').Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];
        }
        else
        {
            lines = ["# Local overrides for Windrunner Launcher. Gitignored."];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>(lines.Count + updates.Count);
        foreach (var line in lines)
        {
            var trim = line.Trim();
            if (trim.Length == 0 || trim.StartsWith('#'))
            {
                output.Add(line.TrimEnd('\r'));
                continue;
            }

            var eq = trim.IndexOf('=');
            if (eq < 0)
            {
                output.Add(line.TrimEnd('\r'));
                continue;
            }

            var key = trim[..eq].Trim();
            if (updates.TryGetValue(key, out var replacement))
            {
                if (string.IsNullOrWhiteSpace(replacement))
                    continue;
                output.Add(key + "=" + replacement.Trim());
                seen.Add(key);
                continue;
            }

            output.Add(line.TrimEnd('\r'));
        }

        var missing = updates
            .Where(kv => !seen.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            if (output.Count > 0 && output[^1].Length != 0)
                output.Add("");
            foreach (var key in missing)
                output.Add(key + "=" + updates[key].Trim());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", output) + "\n");
        Reload();
    }

    /// <summary>
    /// Writes the friendly launcher settings that belong in <c>portable.local.env</c>:
    /// realm name/address/ports, MySQL port, and random-bot counts.
    /// </summary>
    public void WriteFriendlySettings(ServerFriendlySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WriteLocal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REALM_NAME"] = settings.RealmName,
            ["REALM_ADDRESS"] = settings.RealmAddress,
            ["REALM_PORT"] = settings.AuthPort.ToString(),
            ["WORLD_PORT"] = settings.WorldPort.ToString(),
            ["MYSQL_PORT"] = settings.MysqlPort.ToString(),
            ["MIN_RANDOM_BOTS"] = settings.MinRandomBots.ToString(),
            ["MAX_RANDOM_BOTS"] = settings.MaxRandomBots.ToString()
        });
    }

    private static void MergeFile(Dictionary<string, string> target, string path)
    {
        if (!File.Exists(path))
            return;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0)
                continue;
            target[key] = value;
        }
    }
}
