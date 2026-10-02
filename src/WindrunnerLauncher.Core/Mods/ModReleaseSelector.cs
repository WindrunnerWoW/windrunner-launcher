using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Mods;

/// <summary>
/// Picks one file from a release. Windows builds win over macOS and Linux, and extra
/// bundles (DXVK, debug symbols, checksum sidecars) lose when a plain payload is present.
/// </summary>
public static class ModReleaseSelector
{
    public static GitHubReleaseAsset? Select(GitHubRelease release, ManagedAsset asset)
    {
        var pool = release.Assets
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .ToList();
        if (pool.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(asset.AssetContains))
        {
            pool = pool.Where(a => Matches(a.Name, asset.AssetContains)).ToList();
            if (pool.Count == 0)
                return null;
        }

        pool = Prefer(pool, a => IsPayload(a.Name));
        pool = Prefer(pool, a => !IsDebugBuild(a.Name));
        if (asset.Kind != ModKind.Dxvk)
            pool = Prefer(pool, a => !a.Name.Contains("dxvk", StringComparison.OrdinalIgnoreCase));
        pool = Prefer(pool, a => !IsChecksum(a.Name));

        var windows = pool.Where(a => IsWindows(a.Name)).ToList();
        if (windows.Count > 0)
            pool = windows;
        else if (pool.All(a => IsOtherOperatingSystem(a.Name)))
            return null;

        var leaf = ModPaths.LeafName(asset.Destination);
        if (leaf.Length > 0)
        {
            var exact = pool.FirstOrDefault(a => a.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
        }

        return pool.FirstOrDefault();
    }

    private static List<GitHubReleaseAsset> Prefer(List<GitHubReleaseAsset> pool, Func<GitHubReleaseAsset, bool> keep)
    {
        var preferred = pool.Where(keep).ToList();
        return preferred.Count > 0 ? preferred : pool;
    }

    private static bool Matches(string name, string glob) => GitHubReleases.NameMatches(name, glob);

    private static bool IsPayload(string name)
    {
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = Path.GetExtension(name);
        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mpq", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDebugBuild(string name) =>
        name.Contains("-debug", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".debug", StringComparison.OrdinalIgnoreCase);

    internal static bool IsChecksum(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.EndsWith(".sha256", StringComparison.Ordinal)
               || lower.EndsWith(".sha256sum", StringComparison.Ordinal)
               || lower.EndsWith(".sig", StringComparison.Ordinal)
               || lower.EndsWith(".asc", StringComparison.Ordinal)
               || lower.Contains("sha256sum", StringComparison.Ordinal);
    }

    private static bool IsWindows(string name) =>
        Contains(name, "windows") || Contains(name, "win64") || Contains(name, "win32") || Contains(name, "win-x64");

    private static bool IsOtherOperatingSystem(string name) =>
        !IsWindows(name) && (
            Contains(name, "linux") || Contains(name, "darwin") || Contains(name, "apple")
            || Contains(name, "osx") || Contains(name, "macos"));

    private static bool Contains(string name, string part) =>
        name.Contains(part, StringComparison.OrdinalIgnoreCase);
}
