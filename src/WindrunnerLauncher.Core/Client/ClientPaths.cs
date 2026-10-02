namespace WindrunnerLauncher.Core.Client;

/// <summary>
/// Resolves a path under a game client. Linux filesystems are case-sensitive, and a client
/// copied from Windows or Lutris may already use <c>data</c> or <c>Wtf</c>. Reusing that
/// directory keeps the launcher from creating a second folder the game will not read.
/// </summary>
public static class ClientPaths
{
    public static string Resolve(string root, params string[] segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment))
                continue;
            foreach (var part in segment.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
                current = Child(current, part);
        }

        return current;
    }

    /// <summary>
    /// An existing child of <paramref name="parent"/> whose name matches <paramref name="name"/>
    /// ignoring case, or <c>parent/name</c> when nothing is there yet.
    /// </summary>
    public static string Child(string parent, string name)
    {
        if (Directory.Exists(parent))
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
                {
                    if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                        return entry;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to the canonical name; the caller sees the original error on use.
            }
        }

        return Path.Combine(parent, name);
    }
}
