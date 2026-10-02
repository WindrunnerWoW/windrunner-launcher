using System.Text.Json;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Mods;

public sealed record AddonReleaseCandidate(string Tag, GitHubReleaseAsset Asset);
public sealed record AddonReleaseSet(string Tag, IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>
/// Owns the user-facing addon catalog at <see cref="LauncherPaths.AddonsFile"/>. The file is seeded
/// once from the bundled default list and from then on is a plain, hand-editable JSON file: the
/// launcher only ever appends to or removes entries from it, so a user can open it directly and add,
/// edit or delete addons themselves.
///
/// An entry with a <see cref="AddonEntry.GitHubRepo"/> resolves its download from that repository's
/// latest release every time it is installed, updated or checked, instead of a fixed URL. Because a
/// release can carry several files (different game-version builds, debug symbols, checksums...), an
/// optional <see cref="AddonEntry.AssetFilter"/> narrows the release's assets down to the intended
/// payload. Entries with <see cref="AddonEntry.InstallAllMatchingAssets"/> can consume multiple ZIPs
/// from one release, including numbered byte segments of a single split ZIP archive.
/// </summary>
public sealed class AddonManager
{
    public const string EmbeddedDefaultCatalogName = "default-addons.json";
    private const int MaxCandidateNamesInError = 8;

    private readonly LauncherPaths _paths;
    private readonly DownloadManager _downloads;
    private readonly ReleaseCatalog _releases;
    private AddonCatalog _catalog = new();

    public event Action? Changed;

    public IReadOnlyList<AddonEntry> Addons => _catalog.Addons;

    public AddonManager(LauncherPaths paths, DownloadManager downloads, ReleaseCatalog releases)
    {
        _paths = paths;
        _downloads = downloads;
        _releases = releases;
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_paths.AddonsFile))
            EmbeddedResources.ExtractTo(EmbeddedDefaultCatalogName, _paths.AddonsFile);

        _catalog = JsonStore.LoadOrNew(_paths.AddonsFile, LoadEmbeddedDefault);
    }


    /// <summary>
    /// Adds an entry from a pasted URL. A github.com/codeberg.org link is treated as "track this
    /// repository's latest release" and is resolved immediately so a bad repo or filter is caught
    /// now instead of at the next install. Anything else is stored as a fixed direct download.
    /// </summary>
    public async Task<AddonEntry> AddFromUrlAsync(string url, string? assetFilter, CancellationToken ct = default)
    {
        url = url.Trim();
        assetFilter = assetFilter?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Enter an addon URL first.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("That doesn't look like a valid web address.");
        if (_catalog.Addons.Any(a => string.Equals(a.Url, url, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That addon is already in the list.");

        var isRepoHost = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                          || uri.Host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase);

        AddonEntry entry;
        if (isRepoHost && ReleaseCatalog.TryParse(url, out var host, out var ownerRepo))
        {
            var (name, author) = GuessNameAndAuthor(uri);
            entry = new AddonEntry
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = name,
                Author = author,
                Category = "Custom",
                Version = "-",
                Url = url,
                GitHubRepo = host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase) ? $"codeberg.org/{ownerRepo}" : ownerRepo,
                AssetFilter = assetFilter
            };

            var candidate = await ResolveReleaseAsync(entry, ct).ConfigureAwait(false);
            entry.Version = candidate.Tag;
            entry.Description = $"Latest release: {candidate.Asset.Name}";
        }
        else
        {
            var (name, _) = GuessNameAndAuthor(uri);
            entry = new AddonEntry
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = name,
                Category = "Custom",
                Description = url,
                Version = "-",
                Url = url
            };
        }

        _catalog.Addons.Add(entry);
        Save();
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Removes an entry and, when it was installed, cleans up the AddOns folder(s) it owns.</summary>
    public void Remove(string id, string? clientDir)
    {
        var entry = _catalog.Addons.FirstOrDefault(a => a.Id == id);
        if (entry is null)
            return;

        if (clientDir is not null)
        {
            var addOnsDir = ClientPaths.Resolve(clientDir, "Interface", "AddOns");
            var disabledDir = DisabledStore(entry.Id);
            foreach (var folder in entry.FolderNames)
            {
                TryDeleteDir(Path.Combine(addOnsDir, folder));
                TryDeleteDir(Path.Combine(disabledDir, folder));
            }
        }

        _catalog.Addons.Remove(entry);
        Save();
        Changed?.Invoke();
    }

    public void Save() => JsonStore.Save(_paths.AddonsFile, _catalog);


    /// <summary>Resolves the one release asset an entry's repo + filter currently point at. Never downloads.</summary>
    public async Task<AddonReleaseCandidate> ResolveReleaseAsync(AddonEntry entry, CancellationToken ct = default)
    {
        var set = await ResolveReleaseAssetsAsync(entry, ct).ConfigureAwait(false);
        if (set.Assets.Count != 1)
        {
            var names = string.Join(", ", set.Assets.Take(MaxCandidateNamesInError).Select(a => a.Name));
            throw new InvalidOperationException(
                $"The latest release of {entry.GitHubRepo} has {set.Assets.Count} matching files; " +
                $"enable installAllMatchingAssets for a multi-file addon. Files: {names}");
        }

        return new AddonReleaseCandidate(set.Tag, set.Assets[0]);
    }

    /// <summary>Resolves all release assets an entry should install. Never downloads.</summary>
    public async Task<AddonReleaseSet> ResolveReleaseAssetsAsync(AddonEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.GitHubRepo))
            throw new InvalidOperationException($"{Display(entry)} has no repository configured.");
        if (!ReleaseCatalog.TryParse(entry.GitHubRepo, out var host, out var ownerRepo))
            throw new InvalidOperationException($"{Display(entry)} has an invalid repository '{entry.GitHubRepo}'.");

        var release = await _releases.FetchLatestAsync(host, ownerRepo, ct).ConfigureAwait(false);
        if (release is null)
            throw new InvalidOperationException($"Could not read the latest release of {entry.GitHubRepo}.");

        var pool = release.Assets
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .Where(a => !ModReleaseSelector.IsChecksum(a.Name))
            .ToList();

        if (!string.IsNullOrWhiteSpace(entry.AssetFilter))
            pool = pool.Where(a => GitHubReleases.NameMatches(a.Name, entry.AssetFilter)
                // Keep existing *.zip catalog filters working for data.zip.001, .002, etc.
                || (entry.InstallAllMatchingAssets
                    && AddonPayload.TryGetSplitZipPart(a.Name, out var archiveName, out _)
                    && GitHubReleases.NameMatches(archiveName, entry.AssetFilter))).ToList();

        if (entry.InstallAllMatchingAssets)
        {
            pool = pool
                .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || AddonPayload.TryGetSplitZipPart(a.Name, out _, out _))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pool.Count == 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(entry.AssetFilter)
                    ? $"The latest release of {entry.GitHubRepo} ({release.TagName}) has no downloadable ZIP files or split ZIP parts."
                    : $"No ZIP file or split ZIP part in the latest release of {entry.GitHubRepo} ({release.TagName}) matches the filter '{entry.AssetFilter}'.");

            // Validate all parts before downloading any large files, and order parts numerically.
            return new AddonReleaseSet(release.TagName, GroupArchives(pool).SelectMany(g => g).ToList());
        }

        if (pool.Count == 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(entry.AssetFilter)
                ? $"The latest release of {entry.GitHubRepo} ({release.TagName}) has no downloadable files."
                : $"No file in the latest release of {entry.GitHubRepo} ({release.TagName}) matches the filter '{entry.AssetFilter}'.");

        if (pool.Count > 1)
        {
            var names = string.Join(", ", pool.Take(MaxCandidateNamesInError).Select(a => a.Name));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(entry.AssetFilter)
                ? $"The latest release of {entry.GitHubRepo} has {pool.Count} files - add a filter to pick one, e.g. a version fragment. Files: {names}"
                : $"{pool.Count} files still match '{entry.AssetFilter}': {names}. Narrow the filter until only one is left.");
        }

        return new AddonReleaseSet(release.TagName, [pool[0]]);
    }

    /// <summary>Best-effort refresh of the "latest known" version without downloading anything.</summary>
    public async Task<string> RefreshLatestVersionAsync(AddonEntry entry, CancellationToken ct = default)
    {
        var candidates = await ResolveReleaseAssetsAsync(entry, ct).ConfigureAwait(false);
        if (!string.Equals(entry.Version, candidates.Tag, StringComparison.Ordinal))
        {
            entry.Version = candidates.Tag;
            Save();
            Changed?.Invoke();
        }

        return candidates.Tag;
    }


    /// <summary>Downloads the resolved asset(s) and extracts their addon folder(s) into the client.</summary>
    public async Task InstallOrUpdateAsync(AddonEntry entry, string clientDir, CancellationToken ct = default)
    {
        var assets = new List<GitHubReleaseAsset>();
        string version;
        if (!string.IsNullOrWhiteSpace(entry.GitHubRepo))
        {
            var candidates = await ResolveReleaseAssetsAsync(entry, ct).ConfigureAwait(false);
            assets.AddRange(candidates.Assets);
            version = candidates.Tag;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(entry.Url))
                throw new InvalidOperationException($"{Display(entry)} has no download source.");
            assets.Add(new GitHubReleaseAsset { Name = "payload.zip", BrowserDownloadUrl = entry.Url });
            version = string.IsNullOrWhiteSpace(entry.Version) || entry.Version == "-" ? "" : entry.Version;
        }

        var store = Store(entry.Id);
        Directory.CreateDirectory(store);
        var staging = Path.Combine(store, ".staging");
        var merged = Path.Combine(staging, "merged");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(merged);

        var groups = GroupArchives(assets);
        var downloadIndex = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var payload = Path.Combine(store, $"payload-{i + 1:00}.zip");
            if (AddonPayload.TryGetSplitZipPart(group[0].Name, out _, out _))
            {
                // .zip.001/.002 are byte segments, not independently extractable ZIPs.
                // Stream them into one archive rather than loading gigabytes into memory.
                await using var output = new FileStream(payload, FileMode.Create, FileAccess.Write,
                    FileShare.None, 81920, useAsync: true);
                foreach (var asset in group)
                {
                    var part = Path.Combine(store, $"payload-part-{downloadIndex + 1:00}");
                    await DownloadAssetAsync(asset, part).ConfigureAwait(false);
                    await using (var input = new FileStream(part, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, useAsync: true))
                    {
                        await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    }
                    File.Delete(part);
                }
            }
            else
            {
                await DownloadAssetAsync(group[0], payload).ConfigureAwait(false);
            }

            try
            {
                var partStaging = Path.Combine(staging, $"part-{i + 1:00}");
                AddonPayload.ExtractArchive(payload, partStaging);
                // Merge the extracted trees before looking for .toc files. A later data ZIP may
                // contain only complementary files and omit the small .toc file entirely.
                ModFileOps.CopyTree(partStaging, merged);
                Directory.Delete(partStaging, recursive: true);
                File.Delete(payload);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"{Display(entry)}'s download was not a valid ZIP archive after assembling its parts. {ex.Message}", ex);
            }
        }

        ct.ThrowIfCancellationRequested();
        var folders = AddonPayload.FindAddonFolders(merged);

        if (folders.Count == 0)
            throw new InvalidDataException($"{Display(entry)}'s download did not contain a recognisable addon (no .toc file found).");

        var addOnsDir = ClientPaths.Resolve(clientDir, "Interface", "AddOns");
        Directory.CreateDirectory(addOnsDir);

        var newNames = folders.Select(f => f.Name).ToList();
        foreach (var stale in entry.FolderNames.Except(newNames, StringComparer.OrdinalIgnoreCase))
        {
            TryDeleteDir(Path.Combine(addOnsDir, stale));
            TryDeleteDir(Path.Combine(DisabledStore(entry.Id), stale));
        }

        foreach (var folder in folders)
        {
            var dest = Path.Combine(addOnsDir, folder.Name);
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
            ModFileOps.MoveDirectory(folder.SourcePath, dest);
        }

        TryDeleteDir(staging);
        entry.FolderNames = newNames;
        entry.Version = version;
        entry.InstalledVersion = version;
        entry.Installed = true;
        entry.Enabled = true;
        Save();
        Changed?.Invoke();

        async Task DownloadAssetAsync(GitHubReleaseAsset asset, string destination)
        {
            downloadIndex++;
            await _downloads.DownloadAsync(new DownloadRequest
            {
                Id = $"addon:{entry.Id}:{downloadIndex}",
                DisplayName = assets.Count == 1 ? Display(entry) : $"{Display(entry)} ({downloadIndex}/{assets.Count})",
                Url = asset.BrowserDownloadUrl,
                DestinationPath = destination,
                MaxAttempts = 3,
                RetryDelay = TimeSpan.FromSeconds(2)
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Moves this entry's folders between the live AddOns directory and its disabled store.</summary>
    public void SetEnabled(AddonEntry entry, string clientDir, bool enabled)
    {
        if (!entry.Installed || entry.FolderNames.Count == 0)
        {
            entry.Enabled = enabled;
            Save();
            Changed?.Invoke();
            return;
        }

        var addOnsDir = ClientPaths.Resolve(clientDir, "Interface", "AddOns");
        var disabledDir = DisabledStore(entry.Id);
        Directory.CreateDirectory(enabled ? addOnsDir : disabledDir);

        foreach (var folder in entry.FolderNames)
        {
            var live = Path.Combine(addOnsDir, folder);
            var parked = Path.Combine(disabledDir, folder);
            if (enabled && Directory.Exists(parked) && !Directory.Exists(live))
                ModFileOps.MoveDirectory(parked, live);
            else if (!enabled && Directory.Exists(live) && !Directory.Exists(parked))
                ModFileOps.MoveDirectory(live, parked);
        }

        entry.Enabled = enabled;
        Save();
        Changed?.Invoke();
    }

    private string Store(string id) => Path.Combine(_paths.Mods, "addons", id);
    private string DisabledStore(string id) => Path.Combine(Store(id), "disabled");


    private static List<IReadOnlyList<GitHubReleaseAsset>> GroupArchives(IReadOnlyList<GitHubReleaseAsset> assets)
    {
        var result = new List<IReadOnlyList<GitHubReleaseAsset>>();
        foreach (var group in assets.GroupBy(a => AddonPayload.TryGetSplitZipPart(a.Name, out var name, out _)
                     ? name : a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var split = group.Where(a => AddonPayload.TryGetSplitZipPart(a.Name, out _, out _)).ToList();
            if (split.Count == 0)
            {
                foreach (var asset in group)
                    result.Add([asset]);
                continue;
            }
            if (split.Count != group.Count())
                throw new InvalidOperationException($"The release contains both {group.Key} and its split parts. Narrow the asset filter to choose one archive format.");
            split = split.OrderBy(a =>
            {
                AddonPayload.TryGetSplitZipPart(a.Name, out _, out var part);
                return part;
            }).ToList();
            for (var i = 0; i < split.Count; i++)
            {
                AddonPayload.TryGetSplitZipPart(split[i].Name, out _, out var part);
                if (part != i + 1)
                    throw new InvalidOperationException($"Split ZIP {group.Key} needs consecutive parts starting at .001; expected .{i + 1:000}, found {split[i].Name}. Check the release and asset filter.");
            }
            result.Add(split);
        }
        return result;
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { }
    }

    private static AddonCatalog LoadEmbeddedDefault()
    {
        try
        {
            var json = EmbeddedResources.ReadText(EmbeddedDefaultCatalogName);
            return JsonSerializer.Deserialize(json, LauncherJsonContext.Default.AddonCatalog) ?? new AddonCatalog();
        }
        catch (FileNotFoundException)
        {
            return new AddonCatalog();
        }
    }

    private static (string Name, string Author) GuessNameAndAuthor(Uri uri)
    {
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
            return (CleanName(segments[1]), segments[0]);
        if (uri.Host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
            return (CleanName(segments[1]), segments[0]);

        var leaf = segments.Length > 0 ? segments[^1] : uri.Host;
        return (CleanName(leaf), "");
    }

    private static string CleanName(string raw)
    {
        var name = Path.GetFileNameWithoutExtension(raw).Replace('-', ' ').Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(name) ? raw : name;
    }

    private static string Display(AddonEntry entry) => string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name;
}
