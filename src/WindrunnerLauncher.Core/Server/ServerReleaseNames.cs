using WindrunnerLauncher.Core.Downloads;

namespace WindrunnerLauncher.Core.Server;

/// <summary>Release asset names for the portable server. Linux must not install the Windows zip.</summary>
public static class ServerReleaseNames
{
    public const string WindowsZipGlob = "windrunner-wow-windows-server-*.zip";
    public const string LinuxTarGlob = "windrunner-wow-linux-server-*.tar.gz";
    public const string LinuxZipGlob = "windrunner-wow-linux-server-*.zip";

    public static IReadOnlyList<string> GlobsForCurrentOs() =>
        OperatingSystem.IsLinux()
            ? [LinuxTarGlob, LinuxZipGlob]
            : [WindowsZipGlob];

    public static GitHubReleaseAsset? Find(GitHubRelease? release)
    {
        if (release is null)
            return null;
        foreach (var glob in GlobsForCurrentOs())
        {
            var asset = GitHubReleases.FindAsset(release, glob);
            if (asset is not null)
                return asset;
        }

        return null;
    }

    public static string MissingMessage(string tag) =>
        OperatingSystem.IsLinux()
            ? $"Release {tag} has no Linux server archive ({LinuxTarGlob} or {LinuxZipGlob}). " +
              "Windrunner currently publishes only the Windows server zip. " +
              "Set TORTOISE_WOW_SERVER_ZIP_URL to a Linux build to override."
            : $"Release {tag} has no {WindowsZipGlob} asset.";
}
