using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Platform;

/// <summary>The runner to start Windows programs with, and the prefix that goes with it.</summary>
public sealed record LinuxRunnerChoice(string Runner, string Prefix, WineRunnerKind Kind);

/// <summary>
/// Turns the Settings choice (Auto, Wine, Proton, Custom) into a runner. Proton goes through
/// umu-run, which downloads GE-Proton and the Steam runtime on first use. When umu-run is not
/// installed, the launcher fetches the umu zipapp from GitHub into <c>tools/umu</c>.
/// </summary>
public sealed class LinuxRunner
{
    public const string UmuRepo = "Open-Wine-Components/umu-launcher";

    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly LauncherPaths _paths;
    private readonly DownloadManager _downloads;
    private readonly GitHubReleases _releases;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LinuxRunner(LauncherPaths paths, DownloadManager downloads, GitHubReleases? releases = null)
    {
        _paths = paths;
        _downloads = downloads;
        _releases = releases ?? new GitHubReleases(SharedHttp);
    }

    /// <summary>The runner for this mode if it is already on disk. Never downloads.</summary>
    public static LinuxRunnerChoice? ResolveInstalled(LauncherSettings settings, LauncherPaths paths) =>
        settings.LinuxRunner switch
        {
            LinuxRunnerMode.Wine => Wine(paths),
            LinuxRunnerMode.Proton => Umu(paths),
            LinuxRunnerMode.Custom => Custom(settings.WineRunnerPath, paths),
            _ => Wine(paths) ?? Umu(paths)
        };

    /// <summary>
    /// The runner for the current settings. Fetches umu-run for Proton, and for Auto when no
    /// Wine is installed.
    /// </summary>
    public async Task<LinuxRunnerChoice> EnsureAsync(
        LauncherSettings settings, Action<string>? stage = null, CancellationToken ct = default)
    {
        if (ResolveInstalled(settings, _paths) is { } installed)
            return installed;

        switch (settings.LinuxRunner)
        {
            case LinuxRunnerMode.Wine:
                throw new InvalidOperationException(
                    "Wine is not installed. Install Wine from your distribution, or choose Proton in Settings.");
            case LinuxRunnerMode.Custom:
                throw new InvalidOperationException(
                    "No custom runner is set. Enter the path to wine, proton, or umu-run in Settings, or choose Auto.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Umu(_paths) is { } fetched)
                return fetched;
            stage?.Invoke("Downloading umu-launcher to run Proton…");
            await DownloadUmuAsync(ct).ConfigureAwait(false);
            return Umu(_paths)
                   ?? throw new InvalidOperationException("umu-launcher was downloaded, but umu-run is missing from it.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadUmuAsync(CancellationToken ct)
    {
        // The zipapp is a Python program: #!/usr/bin/env python3.
        if (WineHost.Which("python3") is null)
            throw new InvalidOperationException(
                "Proton needs umu-launcher. Install the umu-launcher package from your distribution, "
                + "or install python3 so the launcher can fetch umu-launcher itself.");

        var release = await _releases.LatestAsync(UmuRepo, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException(
                          "Could not reach GitHub to download umu-launcher. Check your connection, or install umu-launcher from your distribution.");
        var asset = release.Assets.FirstOrDefault(a =>
                        a.Name.EndsWith("zipapp.tar", StringComparison.OrdinalIgnoreCase)
                        || a.Name.EndsWith("zipapp.tar.gz", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"umu-launcher {release.TagName} has no zipapp download. Install umu-launcher from your distribution.");

        var archive = Path.Combine(_paths.DownloadCache, asset.Name);
        await _downloads.DownloadAsync(new DownloadRequest
        {
            Id = "umu-launcher",
            DisplayName = asset.Name,
            Url = asset.BrowserDownloadUrl,
            DestinationPath = archive,
            ExpectedSha256 = asset.Sha256
        }, ct).ConfigureAwait(false);

        var staging = _paths.UmuDir + ".tmp";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        ArchiveUtil.ExtractTar(archive, staging);
        var umu = FindUmu(staging)
                  ?? throw new InvalidDataException($"{asset.Name} does not contain umu-run.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(umu, File.GetUnixFileMode(umu)
                                      | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        if (Directory.Exists(_paths.UmuDir))
            Directory.Delete(_paths.UmuDir, recursive: true);
        Directory.Move(staging, _paths.UmuDir);
        File.Delete(archive);
    }

    private static LinuxRunnerChoice? Wine(LauncherPaths paths) =>
        WineHost.ResolveRunner(null) is { } wine
            ? new LinuxRunnerChoice(wine, paths.WinePrefix, WineRunnerKind.Wine)
            : null;

    private static LinuxRunnerChoice? Umu(LauncherPaths paths)
    {
        var umu = WineHost.Which("umu-run") ?? FindUmu(paths.UmuDir);
        return umu is null ? null : new LinuxRunnerChoice(umu, paths.ProtonPrefix, WineRunnerKind.Umu);
    }

    private static LinuxRunnerChoice? Custom(string? path, LauncherPaths paths)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var runner = WineHost.ResolveRunner(path)!;
        var kind = WineHost.KindOf(runner);
        return new LinuxRunnerChoice(runner, kind == WineRunnerKind.Wine ? paths.WinePrefix : paths.ProtonPrefix, kind);
    }

    internal static string? FindUmu(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "umu-run", SearchOption.AllDirectories).FirstOrDefault()
            : null;
}
