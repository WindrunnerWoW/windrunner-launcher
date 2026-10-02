using System.Text;
using WindrunnerLauncher.Core.Client;

namespace WindrunnerLauncher.Core.Mods;

/// <summary>
/// Manages <c>dlls.txt</c>, the injection list VanillaFixes reads at launch, plus the
/// <c>dlls.txt.cache</c> companion file other launchers expect (absolute Windows-style paths,
/// CRLF separated).
/// </summary>
public static class DllsTxt
{
    public const string FileName = "dlls.txt";
    public const string CacheFileName = "dlls.txt.cache";

    /// <summary>
    /// Libraries that ship with the client or with the loader itself. They are already loaded by
    /// the game and must never be injected a second time.
    /// </summary>
    public static readonly IReadOnlyList<string> StockDlls =
    [
        "ace", "divxdecoder", "dbghelp", "fmod", "ijl15",
        "sdl", "scan", "unicows", "zlib1", "twloader"
    ];

    private static readonly HashSet<string> StockSet = new(StockDlls, StringComparer.OrdinalIgnoreCase);

    public static string PathFor(string clientDir) => ClientPaths.Child(clientDir, FileName);
    public static string CachePathFor(string clientDir) => ClientPaths.Child(clientDir, CacheFileName);

    public static bool IsStock(string dllName) =>
        StockSet.Contains(Path.GetFileNameWithoutExtension(dllName.Trim()));

    /// <summary>Entries in file order, without comments or blank lines.</summary>
    public static List<string> Read(string clientDir)
    {
        var path = PathFor(clientDir);
        var entries = new List<string>();
        if (!File.Exists(path))
            return entries;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (!entries.Contains(line, StringComparer.OrdinalIgnoreCase))
                entries.Add(line);
        }

        return entries;
    }

    /// <summary>True when the loader would actually inject something.</summary>
    public static bool HasEntries(string clientDir) => Read(clientDir).Count > 0;

    /// <summary>
    /// Rewrites both files. Stock libraries are filtered out, and an empty list deletes the files
    /// so VanillaFixes does not try to inject nothing.
    /// </summary>
    public static void Write(string clientDir, IEnumerable<string> dlls)
    {
        var path = PathFor(clientDir);
        var cachePath = CachePathFor(clientDir);

        var entries = new List<string>();
        foreach (var raw in dlls)
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0 || name.StartsWith('#') || IsStock(name))
                continue;
            if (!entries.Contains(name, StringComparer.OrdinalIgnoreCase))
                entries.Add(name);
        }

        if (entries.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(cachePath)) File.Delete(cachePath);
            return;
        }

        Directory.CreateDirectory(clientDir);
        var encoding = new UTF8Encoding(false);
        File.WriteAllText(path, string.Join('\n', entries) + "\n", encoding);

        var root = Path.GetFullPath(clientDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cacheLines = entries.Select(e => $"{root}\\{e}".Replace('/', '\\'));
        File.WriteAllText(cachePath, string.Join("\r\n", cacheLines), encoding);
    }

    public static bool Add(string clientDir, string dll)
    {
        if (string.IsNullOrWhiteSpace(dll) || IsStock(dll))
            return false;

        var entries = Read(clientDir);
        if (entries.Contains(dll.Trim(), StringComparer.OrdinalIgnoreCase))
            return false;

        entries.Add(dll.Trim());
        Write(clientDir, entries);
        return true;
    }

    public static bool Remove(string clientDir, string dll)
    {
        var entries = Read(clientDir);
        var removed = entries.RemoveAll(e => string.Equals(e, dll.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            Write(clientDir, entries);
        return removed;
    }

    public static bool Contains(string clientDir, string dll) =>
        Read(clientDir).Contains(dll.Trim(), StringComparer.OrdinalIgnoreCase);
}
