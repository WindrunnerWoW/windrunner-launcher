namespace WindrunnerLauncher.Core.Client;

/// <summary>
/// The client caches server-provided data (quests, creatures, items, gossip) under WDB. Stale
/// caches show up as wrong quest text or missing NPC data after a realm changes its database, so
/// realms can opt into clearing the cache before every launch.
/// </summary>
public static class Wdb
{
    /// <summary>Cache locations used by 1.12 clients and by VanillaFixes-relocated caches.</summary>
    public static IEnumerable<string> CacheDirectories(string clientDir)
    {
        yield return ClientPaths.Resolve(clientDir, "WDB");
        yield return ClientPaths.Resolve(clientDir, "Cache", "WDB");
    }

    /// <summary>Deletes the WDB caches that exist and returns the directories that were removed.</summary>
    public static IReadOnlyList<string> ClearWdb(string clientDir)
    {
        var removed = new List<string>();
        if (string.IsNullOrWhiteSpace(clientDir) || !Directory.Exists(clientDir))
            return removed;

        foreach (var dir in CacheDirectories(clientDir))
        {
            if (!Directory.Exists(dir))
                continue;
            Directory.Delete(dir, recursive: true);
            removed.Add(dir);
        }

        return removed;
    }

    public static bool HasCache(string clientDir) =>
        !string.IsNullOrWhiteSpace(clientDir) && CacheDirectories(clientDir).Any(Directory.Exists);
}
