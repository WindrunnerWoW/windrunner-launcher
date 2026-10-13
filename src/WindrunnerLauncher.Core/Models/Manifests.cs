namespace WindrunnerLauncher.Core.Models;

public sealed class SignedManifestEnvelope
{
    public string KeyId { get; set; } = "";
    public string Algorithm { get; set; } = "Ed25519";
    public string SignatureB64 { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

public sealed class ClientManifest
{
    public int Schema { get; set; } = 1;
    public string Channel { get; set; } = "stable";
    public string Version { get; set; } = "0.0.0";
    public string Product { get; set; } = "turtle-wow-client";
    public string? Changelog { get; set; }
    public ClientBootstrap? Bootstrap { get; set; }
    public ClientBootstrap? Maps { get; set; }
    public List<ManagedAsset> Assets { get; set; } = [];
}

public sealed class ClientBootstrap
{
    public string DisplayName { get; set; } = "Vanilla 1.12 client";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string NestedRoot { get; set; } = "";
}

public sealed class ManagedAsset
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public ModKind Kind { get; set; }
    public string Destination { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public bool Required { get; set; }
    public bool Optional { get; set; } = true;
    public bool DefaultEnabled { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? DownloadUrl { get; set; }
    public string? AssetContains { get; set; }
    public string? GitHubRepo { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public List<string> Conflicts { get; set; } = [];
    public List<string> DllsTxtAdd { get; set; } = [];
    public bool Recommended { get; set; }
}

public sealed class ServerManifest
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public string ServerZipUrl { get; set; } = "";
    public string ServerZipSha256 { get; set; } = "";
    public string SqlZipUrl { get; set; } = "";
    public string SqlZipSha256 { get; set; } = "";
    public string? Notes { get; set; }
}

public sealed class LauncherUpdateManifest
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = "";
    public string ExeUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }

    /// <summary>Linux binary URL. Lives in the signed payload beside <see cref="ExeUrl"/>.</summary>
    public string? LinuxUrl { get; set; }
    public string? LinuxSha256 { get; set; }
    public long LinuxSize { get; set; }
    public string? Notes { get; set; }

    public bool HasDownloadForCurrentOs() =>
        OperatingSystem.IsLinux()
            ? !string.IsNullOrWhiteSpace(LinuxUrl)
            : !string.IsNullOrWhiteSpace(ExeUrl);

    public (string Url, string Sha256) DownloadForCurrentOs() =>
        OperatingSystem.IsLinux()
            ? (LinuxUrl ?? "", LinuxSha256 ?? "")
            : (ExeUrl, Sha256);
}

/// <summary>
/// Seeded once from the bundled defaults; subsequent edits and install state live in
/// <see cref="LauncherPaths.AddonsFile"/>.
/// </summary>
public sealed class AddonCatalog
{
    public List<AddonEntry> Addons { get; set; } = [];
}

public sealed class AddonEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Latest known release tag or source archive revision.</summary>
    public string Version { get; set; } = "";

    /// <summary>Version/tag that is actually on disk right now, or "" if never installed.</summary>
    public string InstalledVersion { get; set; } = "";

    /// <summary>Direct download link. Used as-is when <see cref="GitHubRepo"/> is empty.</summary>
    public string Url { get; set; } = "";

    /// <summary>"owner/repo", or "codeberg.org/owner/repo". When set, installs/updates resolve a release instead of using <see cref="Url"/> directly. GitHub repos without a release use the default-branch source archive.</summary>
    public string GitHubRepo { get; set; } = "";

    /// <summary>
    /// Substring or <c>*</c> glob used to select release assets. Multiple matches require
    /// <see cref="InstallAllMatchingAssets"/>.
    /// </summary>
    public string AssetFilter { get; set; } = "";

    /// <summary>
    /// When true, install every matching ZIP asset from the latest release into one addon entry.
    /// Supports complementary ZIPs and numbered split ZIP parts (.zip.001, .zip.002, etc.).
    /// </summary>
    public bool InstallAllMatchingAssets { get; set; }

    /// <summary>Top-level Interface/AddOns folder name(s) this entry last installed, so updates and removal touch exactly those.</summary>
    public List<string> FolderNames { get; set; } = [];

    public bool Installed { get; set; }
    public bool Enabled { get; set; }
}

public sealed class RollbackMetadata
{
    public string BackupId { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string Directory { get; set; } = "";
}
