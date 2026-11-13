using System.Text;

namespace WindrunnerLauncher.Core.Client;

/// <summary>
/// Points a client installation at a realm. The 1.12 client reads the realm address from up to
/// three places depending on build and locale, so all of them are kept in sync.
/// </summary>
public static class RealmlistWriter
{
    public const int DefaultAuthPort = 3724;

    /// <summary>Locales that always get a realmlist.wtf when their Data folder exists.</summary>
    public static readonly IReadOnlyList<string> KnownLocales = ["enUS", "enGB", "deDE", "frFR"];

    /// <summary>
    /// Splits an explicit <c>host:port</c> suffix off an address. Anything that is not a single
    /// trailing <c>:port</c> (IPv6 literals, malformed ports) is returned unchanged with no port.
    /// </summary>
    public static (string Host, int? Port) SplitHostAndPort(string? address)
    {
        var text = (address ?? "").Trim();
        var lastColon = text.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == text.Length - 1 || text.IndexOf(':') != lastColon)
            return (text, null);
        if (!int.TryParse(text[(lastColon + 1)..], out var port) || port is <= 0 or > 65535)
            return (text, null);
        return (text[..lastColon].Trim(), port);
    }

    /// <summary>Vanilla realmlist entries omit the port when it is the default 3724.</summary>
    public static string FormatHost(string address, int authPort)
    {
        var host = (address ?? "").Trim();
        if (host.Length == 0)
            host = "127.0.0.1";
        if (host.Contains(':'))
            return host;
        return authPort is DefaultAuthPort or 0 ? host : $"{host}:{authPort}";
    }

    /// <summary>Writes every realmlist location and returns the files that were touched.</summary>
    public static IReadOnlyList<string> Write(string clientDir, string host, string? patchHost = null)
    {
        if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
            throw new DirectoryNotFoundException($"Client directory not found: {clientDir}");

        var written = new List<string>();

        var root = ClientPaths.Child(clientDir, "realmlist.wtf");
        WriteRealmlist(root, host);
        written.Add(root);

        foreach (var localeDir in LocaleDirectories(clientDir))
        {
            var file = ClientPaths.Child(localeDir, "realmlist.wtf");
            WriteRealmlist(file, host);
            written.Add(file);
        }

        var config = ClientPaths.Resolve(clientDir, "WTF", "Config.wtf");
        ConfigWtf.SetValues(config, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["realmList"] = host,
            ["patchList"] = string.IsNullOrWhiteSpace(patchHost) ? host : patchHost
        });
        written.Add(config);

        return written;
    }

    /// <summary>Existing <c>Data/&lt;locale&gt;</c> directories, including the well-known locales.</summary>
    public static IEnumerable<string> LocaleDirectories(string clientDir)
    {
        var data = ClientPaths.Resolve(clientDir, "Data");
        if (!Directory.Exists(data))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var locale in KnownLocales)
        {
            var dir = ClientPaths.Child(data, locale);
            if (Directory.Exists(dir) && seen.Add(Path.GetFullPath(dir)))
                yield return dir;
        }

        foreach (var dir in Directory.EnumerateDirectories(data))
        {
            if (!LooksLikeLocale(Path.GetFileName(dir)))
                continue;
            if (seen.Add(Path.GetFullPath(dir)))
                yield return dir;
        }
    }

    private static bool LooksLikeLocale(string name) =>
        name.Length == 4 && name.All(char.IsAsciiLetter);

    private static void WriteRealmlist(string path, string host)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"set realmlist \"{host}\"\n", new UTF8Encoding(false));
    }
}

/// <summary>
/// Minimal <c>WTF/Config.wtf</c> reader/writer. Unknown lines are preserved verbatim so user CVars
/// survive every launcher write, and the file keeps the line endings it already uses: the 1.12
/// client writes and parses <c>Config.wtf</c> with bare LF, so a CRLF rewrite made the client fall
/// back to its defaults and drop every CVar it had not registered itself.
/// </summary>
public static class ConfigWtf
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return values;

        foreach (var line in File.ReadAllLines(path))
        {
            if (!TryParse(line, out var key, out var value))
                continue;
            values[key] = value;
        }

        return values;
    }

    /// <summary>
    /// Sets or replaces <c>SET key "value"</c> lines, keeping everything else intact. The file is
    /// only rewritten when a value actually changes, and it keeps its existing line endings.
    /// </summary>
    public static void SetValues(string path, IReadOnlyDictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var existing = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : [];
        var newline = DetectNewline(path);

        var remaining = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var output = new List<string>(existing.Count + remaining.Count);

        foreach (var line in existing)
        {
            if (TryParse(line, out var key, out _) && remaining.TryGetValue(key, out var replacement))
            {
                output.Add(Format(key, replacement));
                remaining.Remove(key);
                continue;
            }

            output.Add(line);
        }

        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
            output.RemoveAt(output.Count - 1);

        foreach (var pair in remaining)
            output.Add(Format(pair.Key, pair.Value));

        WriteIfChanged(path, string.Join(newline, output) + newline);
    }

    public static void Remove(string path, IEnumerable<string> keys)
    {
        if (!File.Exists(path))
            return;

        var drop = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var kept = File.ReadAllLines(path)
            .Where(line => !(TryParse(line, out var key, out _) && drop.Contains(key)))
            .ToList();
        var newline = DetectNewline(path);
        WriteIfChanged(path, string.Join(newline, kept) + newline);
    }

    /// <summary>
    /// Line ending the file already uses. A new file gets bare LF, which is what the client writes.
    /// </summary>
    private static string DetectNewline(string path)
    {
        if (!File.Exists(path))
            return "\n";

        var text = File.ReadAllText(path);
        var firstBreak = text.IndexOf('\n');
        return firstBreak > 0 && text[firstBreak - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>Leaves the file untouched when the content is already what it should be.</summary>
    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Format(string key, string value) => $"SET {key} \"{value}\"";

    private static bool TryParse(string line, out string key, out string value)
    {
        key = "";
        value = "";
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = trimmed[4..].TrimStart();
        var space = rest.IndexOf(' ');
        if (space <= 0)
            return false;

        key = rest[..space];
        value = rest[(space + 1)..].Trim().Trim('"');
        return key.Length > 0;
    }
}
