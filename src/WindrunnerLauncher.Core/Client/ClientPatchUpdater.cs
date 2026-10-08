using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Client;

/// <summary>A server MPQ whose local copy is missing or differs from the published release asset.</summary>
public sealed class ClientPatchUpdate
{
    public required GitHubReleaseAsset Asset { get; init; }

    /// <summary>Where the new file goes: the existing copy (active or parked) or <c>Data/</c> when missing.</summary>
    public required string TargetPath { get; init; }

    /// <summary>True when no copy exists yet, as opposed to an outdated one.</summary>
    public bool Missing { get; init; }
}

/// <summary>
/// Tracks the server's client MPQ patches (every <c>.mpq</c> on the <c>Client</c> release of the
/// Windrunner repository) against the local client.
///
/// Checking never changes the installation; the download only happens through
/// <see cref="ApplyAsync"/>, which the Play button offers as "Update". Patches follow the per-realm
/// MPQ choices: a patch the realm has disabled is never reported or updated, so realms that share a
/// client directory with a different server are left alone.
/// </summary>
public sealed class ClientPatchUpdater
{
    public const string Repository = "WindrunnerWoW/windrunner-wow";
    public const string ReleaseTag = "Client";

    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly LauncherPaths _paths;
    private readonly ClientManager _client;
    private readonly DownloadManager _downloads;
    private readonly GitHubReleases _releases;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GitHubRelease? _release;
    private string? _plannedClientDir;
    private IReadOnlyList<ClientPatchUpdate> _pending = [];

    public event Action? Changed;

    public ClientPatchUpdater(LauncherPaths paths, ClientManager client, DownloadManager downloads, GitHubReleases? releases = null)
    {
        _paths = paths;
        _client = client;
        _downloads = downloads;
        _releases = releases ?? new GitHubReleases(SharedHttp);
    }

    /// <summary>Outdated or missing patches the realm wants. Empty until a check covered the realm's client.</summary>
    public IReadOnlyList<ClientPatchUpdate> PendingFor(RealmEntry realm)
    {
        var pending = _pending;
        if (pending.Count == 0 || !SameDirectory(_plannedClientDir, _client.ClientForRealm(realm)))
            return [];
        return pending.Where(update => IsWanted(realm, update.Asset.Name)).ToList();
    }

    /// <summary>
    /// Compares the realm's client against the release. With <paramref name="fetch"/> false the
    /// release from the previous check is reused, which is enough after a realm switch.
    /// </summary>
    public async Task CheckAsync(RealmEntry realm, bool fetch = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (fetch || _release is null)
                _release = await _releases.TagAsync(Repository, ReleaseTag, ct).ConfigureAwait(false)
                           ?? throw new InvalidOperationException($"Could not read the {ReleaseTag} release of {Repository}.");

            var clientDir = _client.ClientForRealm(realm);
            var release = _release;
            _pending = _client.IsValid(clientDir)
                ? await Task.Run(() => Plan(clientDir, release), ct).ConfigureAwait(false)
                : [];
            _plannedClientDir = clientDir;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Downloads and installs every pending patch the realm wants.</summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(RealmEntry realm, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var log = new List<string>();
        try
        {
            // If the planned client dir doesn't match the realm (e.g., realm switched after check),
            // re-check to ensure we have patches for the current realm.
            var pending = PendingFor(realm);
            if (pending.Count == 0 && _release is not null)
            {
                var clientDir = _client.ClientForRealm(realm);
                if (_client.IsValid(clientDir))
                {
                    _pending = await Task.Run(() => Plan(clientDir, _release), ct).ConfigureAwait(false);
                    _plannedClientDir = clientDir;
                    pending = PendingFor(realm);
                }
            }

            foreach (var update in pending)
            {
                ct.ThrowIfCancellationRequested();
                await DownloadAndReplaceAsync(update, ct).ConfigureAwait(false);
                _pending = _pending.Where(p => !ReferenceEquals(p, update)).ToList();
                log.Add($"Updated {update.Asset.Name}.");
            }
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }

        return log;
    }

    /// <summary>Lists release assets whose local copy in <paramref name="clientDir"/> is missing or different.</summary>
    public static List<ClientPatchUpdate> Plan(string clientDir, GitHubRelease release)
    {
        var data = ClientPaths.Resolve(clientDir, "Data");
        var parked = ClientPaths.Resolve(data, ModManager.DisabledMpqFolderName);
        var updates = new List<ClientPatchUpdate>();

        foreach (var asset in release.Assets)
        {
            if (!IsPatchName(asset.Name))
                continue;

            var existing = FindFile(data, asset.Name) ?? FindFile(parked, asset.Name);
            if (existing is not null && IsCurrent(existing, asset))
                continue;

            updates.Add(new ClientPatchUpdate
            {
                Asset = asset,
                TargetPath = existing ?? Path.Combine(data, asset.Name),
                Missing = existing is null
            });
        }

        return updates;
    }

    /// <summary>An explicit "off" for the realm means the server it talks to does not use the patch.</summary>
    public static bool IsWanted(RealmEntry realm, string fileName) =>
        ClientManager.IsRequiredFor(realm, fileName)
        || !realm.MpqFileState.TryGetValue(fileName, out var enabled) || enabled;

    private async Task DownloadAndReplaceAsync(ClientPatchUpdate update, CancellationToken ct)
    {
        var asset = update.Asset;
        var staged = Path.Combine(_paths.DownloadCache, "client-patches", asset.Name);
        await _downloads.DownloadAsync(new DownloadRequest
        {
            Id = $"client-patch-{asset.Name}",
            DisplayName = asset.Name,
            Url = asset.BrowserDownloadUrl,
            DestinationPath = staged,
            ExpectedSha256 = asset.Sha256,
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromSeconds(10)
        }, ct).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(update.TargetPath)!);
        try
        {
            File.Move(staged, update.TargetPath, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new IOException($"Could not replace {asset.Name}. Close the game and try again.", ex);
        }
    }

    private static bool IsCurrent(string path, GitHubReleaseAsset asset)
    {
        var length = new FileInfo(path).Length;
        if (asset.Size > 0 && length != asset.Size)
            return false;
        // Without a published digest the size match is the best signal available.
        return string.IsNullOrWhiteSpace(asset.Sha256) || Checksums.VerifyFile(path, asset.Sha256);
    }

    private static bool IsPatchName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && Path.GetExtension(name).Equals(".mpq", StringComparison.OrdinalIgnoreCase)
        && !ClientManager.IsOfficialMpq(name);

    private static bool SameDirectory(string? left, string right) =>
        left is not null
        && string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string? FindFile(string directory, string fileName) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
            : null;
}
