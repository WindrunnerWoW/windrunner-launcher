using System.Text.Json;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Mods;

public sealed class ModManifestSource
{
    public string Origin { get; init; } = "none";
    public bool Signed { get; init; }
    public string? KeyId { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// Owns the signed client manifest, the <c>mods/</c> store and the live client state.
///
/// Optional assets are downloaded into <c>mods/</c> separately from enabling them. Disabling moves the
/// payload back into <c>mods/</c> instead of deleting it, which is also how per-realm mod state
/// works: <see cref="MaterializeAsync"/> shuffles assets between the store and the client until the
/// selected realm's desired state is satisfied.
/// </summary>
public sealed class ModManager : IManagedAssetRepairer
{
    public const string DisabledMpqFolderName = "DisabledMPQs";
    public const string CachedEnvelopeFileName = "client.manifest.json";

    /// <summary>
    /// Single source of truth for client content links (mods catalog and the clean-client
    /// bootstrap download). Hosted next to the site the launcher already pulls news from, so one
    /// publish updates both without a launcher release.
    /// </summary>
    public const string RemoteManifestUrl = "https://windrunnerwow.github.io/client.manifest.json";

    private readonly LauncherPaths _paths;
    private readonly StateStore _state;
    private readonly DownloadManager _downloads;
    private readonly ReleaseCatalog? _releases;

    private ClientManifest _manifest = new();

    public event Action? Changed;

    public ClientManifest Manifest => _manifest;
    public ModManifestSource ManifestSource { get; private set; } = new();
    public IReadOnlyList<ManagedAsset> Assets => _manifest.Assets;

    public ModManager(LauncherPaths paths, StateStore state, DownloadManager downloads, ReleaseCatalog? releases = null)
    {
        _paths = paths;
        _state = state;
        _downloads = downloads;
        _releases = releases;
        ReloadManifest();
    }


    /// <summary>
    /// Loads the last cached copy of the signed website manifest. It is trusted only after its
    /// Ed25519 signature verifies against a compiled-in client trust anchor.
    /// </summary>
    public void ReloadManifest()
    {
        _manifest = new ClientManifest();
        ManifestSource = new ModManifestSource();
        var envelopePath = Path.Combine(_paths.ManifestCache, CachedEnvelopeFileName);
        if (File.Exists(envelopePath))
        {
            var envelope = TryRead(envelopePath, LauncherJsonContext.Default.SignedManifestEnvelope);
            if (envelope is null || !Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Client))
            {
                ManifestSource = new ModManifestSource
                {
                    Origin = "none",
                    Warning = "The cached client manifest is invalid or its signature could not be verified."
                };
            }
            else if (TryParse(envelope.PayloadJson) is { } signed)
            {
                _manifest = signed;
                ManifestSource = new ModManifestSource
                {
                    Origin = Path.GetFileName(envelopePath),
                    Signed = true,
                    KeyId = envelope.KeyId
                };
            }
            else
            {
                ManifestSource = new ModManifestSource
                {
                    Origin = "none",
                    Warning = "The cached client manifest payload could not be parsed."
                };
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Best-effort refresh of the signed manifest from <see cref="RemoteManifestUrl"/>. Downloaded
    /// to a temp file and signature-verified before it ever touches the trusted cache, so a bad or
    /// unreachable host just leaves the previous verified manifest cache in place.
    /// </summary>
    public async Task RefreshFromRemoteAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.ManifestCache);
        var dest = Path.Combine(_paths.ManifestCache, CachedEnvelopeFileName);
        var tmp = dest + ".tmp";
        try
        {
            await _downloads.DownloadAsync(new DownloadRequest
            {
                Id = "client-manifest",
                DisplayName = CachedEnvelopeFileName,
                Url = RemoteManifestUrl,
                DestinationPath = tmp,
                MaxAttempts = 2,
                RetryDelay = TimeSpan.FromSeconds(5)
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(tmp);
            return;
        }

        var envelope = TryRead(tmp, LauncherJsonContext.Default.SignedManifestEnvelope);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.SignatureB64)
            || !Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Client)
            || TryParse(envelope.PayloadJson) is null)
        {
            TryDelete(tmp);
            return;
        }

        File.Copy(tmp, dest, overwrite: true);
        TryDelete(tmp);
        ReloadManifest();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public ManagedAsset? Find(string assetId) =>
        _manifest.Assets.FirstOrDefault(a => string.Equals(a.Id, assetId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ManagedAsset> RequiredAssets => _manifest.Assets.Where(a => a.Required);


    public string ClientDirFor(RealmEntry realm)
    {
        if (!string.IsNullOrWhiteSpace(realm.ClientDirectoryOverride))
            return Path.GetFullPath(realm.ClientDirectoryOverride);
        var configured = _state.Settings.ClientPath;
        return string.IsNullOrWhiteSpace(configured) ? _paths.Client : Path.GetFullPath(configured);
    }

    /// <summary>Required assets are always on; otherwise the realm overrides the manifest default.</summary>
    public bool DesiredState(RealmEntry realm, ManagedAsset asset)
    {
        if (asset.Required)
            return true;
        if (realm.ManagedModState.TryGetValue(asset.Id, out var wanted))
            return wanted;
        return asset.DefaultEnabled;
    }

    public bool IsApplied(RealmEntry realm, ManagedAsset asset) =>
        ModHandlers.For(asset.Kind).IsApplied(BuildContext(realm, asset));

    public bool IsInstalled(RealmEntry realm, ManagedAsset asset) =>
        ModHandlers.For(asset.Kind).IsInstalled(BuildContext(realm, asset));

    /// <summary>True when the catalog asset with this id is actually applied to the realm's client.</summary>
    public bool IsApplied(RealmEntry realm, string assetId)
    {
        var asset = _manifest.Assets.FirstOrDefault(a => a.Id == assetId);
        return asset is not null && ModHandlers.IsSupported(asset.Kind) && IsApplied(realm, asset);
    }

    public int EnabledCountFor(RealmEntry realm) =>
        _manifest.Assets.Count(a => ModHandlers.IsSupported(a.Kind) && DesiredState(realm, a));

    public List<ModListItem> ListItems(RealmEntry realm)
    {
        var items = new List<ModListItem>(_manifest.Assets.Count);
        foreach (var asset in _manifest.Assets)
        {
            var ctx = BuildContext(realm, asset);
            var handler = ModHandlers.For(asset.Kind);
            var applied = handler.IsApplied(ctx);
            items.Add(new ModListItem
            {
                Asset = asset,
                Enabled = DesiredState(realm, asset),
                Installed = applied || handler.IsInstalled(ctx),
                UpdateAvailable = applied && !ChecksumMatches(ctx, asset)
            });
        }

        foreach (var unmanaged in DetectUnmanaged(realm))
        {
            items.Add(new ModListItem
            {
                Asset = new ManagedAsset
                {
                    Id = "unmanaged:" + unmanaged.Name,
                    DisplayName = unmanaged.Name,
                    Kind = unmanaged.Kind,
                    Destination = unmanaged.Path,
                    Optional = true,
                    Category = "Unmanaged"
                },
                Enabled = true,
                Installed = true,
                Unmanaged = true
            });
        }

        return items;
    }

    /// <summary>
    /// Third-party artifacts that show up in a client folder without being a mod: Discord drops
    /// these DLLs next to any game it detects running, purely for its own overlay/rich presence.
    /// </summary>
    private static readonly HashSet<string> KnownNoiseDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "DiscordOverlay", "discord_game_sdk", "twdiscord"
    };

    /// <summary>Stock UI addons that ship inside every client install, not something the user added.</summary>
    private static bool IsStockAddonFolder(string name) =>
        name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DLLs and addons the launcher did not install. Informational only: they are never
    /// toggled, updated or realm-managed.
    /// </summary>
    public List<UnmanagedItem> DetectUnmanaged(RealmEntry realm)
    {
        var found = new List<UnmanagedItem>();
        var clientDir = ClientDirFor(realm);
        if (!Directory.Exists(clientDir))
            return found;

        var managedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in _manifest.Assets)
        {
            var leaf = ModPaths.LeafName(asset.Destination);
            if (leaf.Length > 0)
                managedFiles.Add(leaf);
            foreach (var dll in asset.DllsTxtAdd)
                managedFiles.Add(dll);
        }

        foreach (var dll in Directory.EnumerateFiles(clientDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(dll);
            if (DllsTxt.IsStock(name) || managedFiles.Contains(name)
                || KnownNoiseDlls.Contains(Path.GetFileNameWithoutExtension(name)))
                continue;
            found.Add(new UnmanagedItem { Name = name, Path = dll, Kind = ModKind.Dll });
        }

        var addons = ClientPaths.Resolve(clientDir, "Interface", "AddOns");
        if (Directory.Exists(addons))
        {
            foreach (var dir in Directory.EnumerateDirectories(addons))
            {
                var name = Path.GetFileName(dir);
                if (managedFiles.Contains(name) || IsStockAddonFolder(name))
                    continue;
                found.Add(new UnmanagedItem { Name = name, Path = dir, Kind = ModKind.AddOn });
            }
        }

        return found;
    }

    /// <summary>Lists base archives and other Data MPQs for the selected realm.</summary>
    public List<ClientMpqItem> ListClientMpqs(RealmEntry realm)
    {
        var data = ClientPaths.Resolve(ClientDirFor(realm), "Data");
        var disabled = ClientPaths.Resolve(data, DisabledMpqFolderName);
        return EnumerateClientMpqNames(data, disabled)
            .Select(name => new ClientMpqItem
            {
                FileName = name,
                Required = ClientManager.IsRequiredFor(realm, name),
                Enabled = ClientManager.IsRequiredFor(realm, name) || !realm.MpqFileState.TryGetValue(name, out var enabled) || enabled,
                Active = FindMpqPath(data, name) is not null
            })
            .OrderByDescending(item => item.Required)
            .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Saves one realm's MPQ choice and moves the file immediately.</summary>
    public Task SetClientMpqEnabledAsync(RealmEntry realm, string fileName, bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateClientMpqName(fileName);
        if (ClientManager.IsRequiredFor(realm, fileName))
            throw new InvalidOperationException($"{fileName} is required for {realm.DisplayName} and cannot be disabled.");

        var data = ClientPaths.Resolve(ClientDirFor(realm), "Data");
        var disabled = ClientPaths.Resolve(data, DisabledMpqFolderName);
        var actualName = EnumerateClientMpqNames(data, disabled)
            .FirstOrDefault(name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));
        if (actualName is null)
            throw new FileNotFoundException($"MPQ file not found in Data or {DisabledMpqFolderName}: {fileName}");

        MoveClientMpq(data, disabled, actualName, enabled);
        realm.MpqFileState[actualName] = enabled;
        _state.SaveRealms();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Applies the selected realm's MPQ choices before launching the client.</summary>
    public IReadOnlyList<string> MaterializeClientMpqs(RealmEntry realm, CancellationToken ct = default)
    {
        var data = ClientPaths.Resolve(ClientDirFor(realm), "Data");
        var disabled = ClientPaths.Resolve(data, DisabledMpqFolderName);
        var log = new List<string>();
        foreach (var name in EnumerateClientMpqNames(data, disabled))
        {
            ct.ThrowIfCancellationRequested();
            var enabled = ClientManager.IsRequiredFor(realm, name)
                || !realm.MpqFileState.TryGetValue(name, out var desired) || desired;
            if (MoveClientMpq(data, disabled, name, enabled))
                log.Add($"{(enabled ? "Enabled" : "Disabled")} {name} for {realm.DisplayName}.");
        }

        return log;
    }

    private List<string> EnumerateClientMpqNames(string data, string disabled)
    {
        var managed = _manifest.Assets
            .Where(asset => asset.Kind == ModKind.Mpq)
            .Select(asset => ModPaths.LeafName(asset.Destination))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { data, disabled })
        {
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (Path.GetExtension(name).Equals(".mpq", StringComparison.OrdinalIgnoreCase) && !managed.Contains(name))
                    names.Add(name);
            }
        }
        return [.. names];
    }

    private static bool MoveClientMpq(string data, string disabled, string name, bool enabled)
    {
        var activePath = FindMpqPath(data, name) ?? Path.Combine(data, name);
        var disabledPath = FindMpqPath(disabled, name) ?? Path.Combine(disabled, name);
        var active = File.Exists(activePath);
        var parked = File.Exists(disabledPath);
        if (active && parked)
            throw new InvalidOperationException($"Both active and disabled copies of {name} exist. Resolve the duplicate before switching realms.");
        if (!active && !parked)
            throw new FileNotFoundException($"MPQ file disappeared while switching realms: {name}");
        if (enabled && parked)
        {
            Directory.CreateDirectory(data);
            File.Move(disabledPath, Path.Combine(data, name));
            return true;
        }
        if (!enabled && active)
        {
            Directory.CreateDirectory(disabled);
            File.Move(activePath, Path.Combine(disabled, name));
            return true;
        }
        return false;
    }

    private static string? FindMpqPath(string directory, string fileName) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
            : null;

    private static void ValidateClientMpqName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(':')
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !Path.GetExtension(fileName).Equals(".mpq", StringComparison.OrdinalIgnoreCase)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Expected a Data MPQ file name.", nameof(fileName));
    }


    /// <summary>Downloads an asset for later use without changing its enabled state.</summary>
    public async Task DownloadAssetAsync(RealmEntry realm, string assetId, CancellationToken ct = default)
    {
        var asset = Find(assetId) ?? throw new KeyNotFoundException($"Unknown managed asset: {assetId}");
        if (!ModHandlers.IsSupported(asset.Kind))
            throw new InvalidOperationException($"{Display(asset)} is not supported by this launcher.");
        if (IsInstalled(realm, asset))
            return;
        if (string.IsNullOrWhiteSpace(asset.DownloadUrl) && string.IsNullOrWhiteSpace(asset.GitHubRepo))
            throw new InvalidOperationException($"{Display(asset)} has no download source. Add its files to mods/{asset.Id}.");

        if (await EnsurePayloadAsync(asset, ct).ConfigureAwait(false) is null || !IsInstalled(realm, asset))
            throw new InvalidDataException($"Downloaded {Display(asset)}, but its expected files were not found in mods/{asset.Id}.");
        Changed?.Invoke();
    }

    /// <summary>
    /// Records the desired state for a realm and immediately reconciles the client:
    /// enable applies files already available offline; disable moves them into <c>mods/</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> SetEnabledAsync(
        RealmEntry realm,
        string assetId,
        bool enabled,
        CancellationToken ct = default)
    {
        var asset = Find(assetId) ?? throw new KeyNotFoundException($"Unknown managed asset: {assetId}");
        if (!enabled && asset.Required)
            throw new InvalidOperationException($"{asset.DisplayName} is required and cannot be disabled.");
        if (enabled)
            AssertAvailableOffline(realm, asset);

        var previous = realm.ManagedModState.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        realm.ManagedModState[asset.Id] = enabled;

        if (enabled)
        {
            foreach (var dependency in asset.Dependencies)
            {
                if (Find(dependency) is { } dep && !DesiredState(realm, dep))
                    realm.ManagedModState[dep.Id] = true;
            }

            foreach (var conflict in asset.Conflicts)
            {
                if (SuppressesConflict(asset.Id, conflict))
                    continue;
                if (Find(conflict) is { Required: false } other)
                    realm.ManagedModState[other.Id] = false;
            }
        }
        else
        {
            // Anything that depends on this asset can no longer stay enabled.
            foreach (var dependent in _manifest.Assets.Where(a =>
                         !a.Required && a.Dependencies.Contains(asset.Id, StringComparer.OrdinalIgnoreCase)))
            {
                realm.ManagedModState[dependent.Id] = false;
            }
        }

        _state.SaveRealms();
        try
        {
            var log = await MaterializeAsync(realm, downloadMissing: false, ct).ConfigureAwait(false);
            Changed?.Invoke();
            return log;
        }
        catch
        {
            realm.ManagedModState.Clear();
            foreach (var pair in previous)
                realm.ManagedModState[pair.Key] = pair.Value;
            _state.SaveRealms();
            throw;
        }
    }

    private void AssertAvailableOffline(RealmEntry realm, ManagedAsset asset)
    {
        var pending = new Stack<ManagedAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(asset);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current.Id))
                continue;
            if (!ModHandlers.IsSupported(current.Kind) || !IsInstalled(realm, current))
                throw new InvalidOperationException($"Download {Display(current)} before enabling {Display(asset)}. If it has no download source, add its files to mods/{current.Id}.");
            foreach (var dependencyId in current.Dependencies)
            {
                if (Find(dependencyId) is { } dependency)
                    pending.Push(dependency);
            }
        }
    }

    /// <summary>
    /// Moves managed assets between <c>mods/</c> and the client until the realm's desired state is
    /// satisfied. Called by the Play pipeline, so by default nothing is downloaded here.
    /// </summary>
    public Task<IReadOnlyList<string>> MaterializeAsync(RealmEntry realm, CancellationToken ct = default) =>
        MaterializeAsync(realm, downloadMissing: false, ct);

    public async Task<IReadOnlyList<string>> MaterializeAsync(
        RealmEntry realm,
        bool downloadMissing,
        CancellationToken ct = default)
    {
        var log = new List<string>();
        var clientDir = ClientDirFor(realm);
        var desired = ResolveDesiredState(realm);
        if (!Directory.Exists(clientDir))
        {
            if (downloadMissing)
            {
                foreach (var asset in _manifest.Assets)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ModHandlers.IsSupported(asset.Kind) || !desired[asset.Id])
                        continue;
                    if (ModHandlers.For(asset.Kind).IsInstalled(BuildContext(realm, asset)))
                        continue;
                    if (await EnsurePayloadAsync(asset, ct).ConfigureAwait(false) is null)
                        continue;
                    log.Add($"Downloaded {Display(asset)}. It will be applied once a client is available.");
                }
            }

            log.Add(log.Count == 0
                ? $"Client directory {clientDir} does not exist; nothing to materialize."
                : $"Client directory {clientDir} does not exist; downloaded payloads were stored for later.");
            return log;
        }

        log.AddRange(MaterializeClientMpqs(realm, ct));

        // Disable first so conflicting payloads never occupy the same destination.
        foreach (var asset in Enumerable.Reverse(Order(_manifest.Assets)))
        {
            ct.ThrowIfCancellationRequested();
            if (!ModHandlers.IsSupported(asset.Kind) || desired[asset.Id])
                continue;

            var handler = ModHandlers.For(asset.Kind);
            var ctx = BuildContext(realm, asset);
            if (!handler.IsApplied(ctx))
                continue;

            var outcome = await handler.DisableAsync(ctx, ct).ConfigureAwait(false);
            if (outcome.Changed || outcome.Skipped)
                log.Add(outcome.Message);
        }

        foreach (var asset in Order(_manifest.Assets))
        {
            ct.ThrowIfCancellationRequested();
            if (!ModHandlers.IsSupported(asset.Kind) || !desired[asset.Id])
                continue;

            var handler = ModHandlers.For(asset.Kind);
            var ctx = BuildContext(realm, asset);
            var vanillaSettingsChanged = asset.Kind == ModKind.ExecutablePatch
                && ctx.CleanExecutableBackup is { } backup
                && !VanillaTweaks.IsCurrent(ctx.ClientDir, backup, _state.Settings.VanillaTweaks);
            if (handler.IsApplied(ctx) && ChecksumMatches(ctx, asset) && !vanillaSettingsChanged)
                continue;

            string? downloaded = null;
            if (downloadMissing && !handler.IsInstalled(ctx))
                downloaded = await EnsurePayloadAsync(asset, ct).ConfigureAwait(false);

            var outcome = await handler.EnableAsync(ctx, ct).ConfigureAwait(false);
            if (outcome.Changed || outcome.Skipped)
                log.Add(outcome.Message);

            // A file was fetched but never landed in the client. Surface that instead of
            // leaving the toggle looking enabled.
            if (downloaded is not null && !outcome.Changed && !handler.IsApplied(ctx))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(outcome.Message)
                    ? $"Could not enable {Display(asset)}."
                    : outcome.Message);
        }

        Changed?.Invoke();
        return log;
    }

    /// <summary>Downloads a payload into <c>mods/&lt;assetId&gt;</c> and verifies its checksum.</summary>
    public async Task<string?> EnsurePayloadAsync(ManagedAsset asset, CancellationToken ct = default)
    {
        var url = asset.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            if (_releases is null || string.IsNullOrWhiteSpace(asset.GitHubRepo))
                return null;
            url = await _releases.ResolveAsync(asset, ct).ConfigureAwait(false);
        }

        var store = _paths.ModStore(asset.Id);
        Directory.CreateDirectory(store);
        var destination = Path.Combine(store, PayloadFileName(asset, url));

        if (File.Exists(destination))
        {
            if (string.IsNullOrWhiteSpace(asset.Sha256) || Checksums.VerifyFile(destination, asset.Sha256))
                return destination;
            File.Delete(destination);
        }

        await _downloads.DownloadAsync(new DownloadRequest
        {
            Id = "mod:" + asset.Id,
            DisplayName = Display(asset),
            Url = url,
            DestinationPath = destination,
            ExpectedSha256 = string.IsNullOrWhiteSpace(asset.Sha256) ? null : asset.Sha256,
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromSeconds(2)
        }, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(asset.Sha256) && !Checksums.VerifyFile(destination, asset.Sha256))
        {
            File.Delete(destination);
            throw new InvalidDataException($"{asset.DisplayName} failed SHA-256 verification.");
        }

        // Force a re-extract on the next apply now that a new payload arrived.
        foreach (var marker in Directory.EnumerateFiles(store, "*.extracted", SearchOption.TopDirectoryOnly))
            File.Delete(marker);

        return destination;
    }


    /// <summary>Required assets that are missing or whose checksum no longer matches the manifest.</summary>
    public List<ManagedAsset> RequiredOutOfDate(RealmEntry realm)
    {
        var stale = new List<ManagedAsset>();
        var clientDir = ClientDirFor(realm);
        if (!Directory.Exists(clientDir))
            return stale;

        foreach (var asset in _manifest.Assets.Where(a => a.Required && ModHandlers.IsSupported(a.Kind)))
        {
            var ctx = BuildContext(realm, asset);
            var handler = ModHandlers.For(asset.Kind);
            if (!handler.IsApplied(ctx) || !ChecksumMatches(ctx, asset))
                stale.Add(asset);
        }

        return stale;
    }

    /// <summary>Optional assets that are live but no longer match the manifest checksum.</summary>
    public List<ManagedAsset> OptionalOutOfDate(RealmEntry realm)
    {
        var stale = new List<ManagedAsset>();
        foreach (var asset in _manifest.Assets.Where(a => !a.Required && ModHandlers.IsSupported(a.Kind)))
        {
            var ctx = BuildContext(realm, asset);
            if (ModHandlers.For(asset.Kind).IsApplied(ctx) && !ChecksumMatches(ctx, asset))
                stale.Add(asset);
        }

        return stale;
    }

    public async Task ApplyRequiredAsync(RealmEntry realm, CancellationToken ct = default)
    {
        foreach (var asset in _manifest.Assets.Where(a => a.Required && ModHandlers.IsSupported(a.Kind)))
        {
            ct.ThrowIfCancellationRequested();
            var handler = ModHandlers.For(asset.Kind);
            var ctx = BuildContext(realm, asset);
            if (handler.IsApplied(ctx) && ChecksumMatches(ctx, asset))
                continue;

            if (!handler.IsInstalled(ctx) || !ChecksumMatches(ctx, asset))
                await EnsurePayloadAsync(asset, ct).ConfigureAwait(false);
            await handler.EnableAsync(ctx, ct).ConfigureAwait(false);
        }

        Changed?.Invoke();
    }

    public async Task RestoreManagedStateAsync(RealmEntry realm, bool strict, CancellationToken ct = default)
    {
        await MaterializeAsync(realm, downloadMissing: strict, ct).ConfigureAwait(false);
    }


    private ModApplyContext BuildContext(RealmEntry realm, ManagedAsset asset)
    {
        var clientDir = ClientDirFor(realm);
        return new ModApplyContext
        {
            Paths = _paths,
            State = _state,
            Asset = asset,
            ClientDir = clientDir,
            StoreDir = _paths.ModStore(asset.Id),
            CleanExecutableBackup = ResolveCleanBackup(clientDir)
        };
    }

    private string? ResolveCleanBackup(string clientDir)
    {
        var local = ClientPaths.Child(clientDir, ClientManager.CleanBackupFileName);
        if (File.Exists(local))
            return local;

        var configured = _state.Settings.CleanWowExeBackupPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            && string.Equals(Path.GetFullPath(Path.GetDirectoryName(configured) ?? "")
                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(clientDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return configured;
        return null;
    }


    /// <summary>
    /// Expands the realm's stored state into a decision for every asset: dependencies are pulled
    /// in, and conflicts lose against required assets and against explicit user choices.
    /// </summary>
    public Dictionary<string, bool> ResolveDesiredState(RealmEntry realm)
    {
        var desired = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in _manifest.Assets)
            desired[asset.Id] = DesiredState(realm, asset);

        // Dependencies of an enabled asset must also be enabled.
        for (var pass = 0; pass < _manifest.Assets.Count + 1; pass++)
        {
            var changed = false;
            foreach (var asset in _manifest.Assets.Where(a => desired[a.Id]))
            {
                foreach (var dependency in asset.Dependencies)
                {
                    if (!desired.TryGetValue(dependency, out var on) || on)
                        continue;
                    desired[dependency] = true;
                    changed = true;
                }
            }

            if (!changed)
                break;
        }

        foreach (var asset in _manifest.Assets)
        {
            if (!desired[asset.Id])
                continue;

            foreach (var conflict in asset.Conflicts)
            {
                if (SuppressesConflict(asset.Id, conflict))
                    continue;
                if (!desired.TryGetValue(conflict, out var on) || !on)
                    continue;
                var other = Find(conflict);
                if (other is null)
                    continue;

                // Required assets and explicit realm choices win; otherwise drop the conflicting one.
                if (other.Required)
                    desired[asset.Id] = false;
                else if (realm.ManagedModState.TryGetValue(other.Id, out var chosen) && chosen
                         && !realm.ManagedModState.ContainsKey(asset.Id))
                    desired[asset.Id] = false;
                else
                    desired[conflict] = false;
            }
        }

        return desired;
    }

    /// <summary>
    /// VanillaFixes and DXVK conflict on Windows drivers. Under Wine both can be enabled:
    /// DXVK is a DLL beside the executable, and VanillaFixes stays the loader.
    /// </summary>
    private static bool SuppressesConflict(string assetId, string otherId) =>
        OperatingSystem.IsLinux()
        && ((assetId.Equals("dxvk", StringComparison.OrdinalIgnoreCase) && otherId.Equals("vanillafixes", StringComparison.OrdinalIgnoreCase))
            || (assetId.Equals("vanillafixes", StringComparison.OrdinalIgnoreCase) && otherId.Equals("dxvk", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Dependencies before dependents so payloads land in a usable order.</summary>
    private List<ManagedAsset> Order(IEnumerable<ManagedAsset> assets)
    {
        var remaining = assets.ToList();
        var ordered = new List<ManagedAsset>(remaining.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(a => a.Dependencies.All(d => placed.Contains(d) || Find(d) is null))
                .ToList();

            if (ready.Count == 0)
            {
                // Dependency cycle or unresolvable graph: keep manifest order for the rest.
                ordered.AddRange(remaining);
                break;
            }

            foreach (var asset in ready)
            {
                ordered.Add(asset);
                placed.Add(asset.Id);
                remaining.Remove(asset);
            }
        }

        return ordered;
    }

    /// <summary>
    /// Compares the live single-file payload against the manifest checksum. Assets without a
    /// checksum (local-only entries) are treated as current.
    /// </summary>
    private static bool ChecksumMatches(ModApplyContext ctx, ManagedAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Sha256))
            return true;
        if (asset.Kind is not (ModKind.Mpq or ModKind.Dll))
            return true;
        var live = ctx.LivePath;
        return File.Exists(live) && Checksums.VerifyFile(live, asset.Sha256);
    }

    public static string PayloadFileName(ManagedAsset asset) => PayloadFileName(asset, asset.DownloadUrl);

    public static string PayloadFileName(ManagedAsset asset, string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var fromUrl = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
            if (!string.IsNullOrWhiteSpace(fromUrl)
                && Path.HasExtension(fromUrl)
                && fromUrl.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                return fromUrl;
        }

        var leaf = ModPaths.LeafName(asset.Destination);
        if (leaf.Length > 0 && Path.HasExtension(leaf))
            return leaf;

        return asset.Id + ".zip";
    }

    private static string Display(ManagedAsset asset) =>
        string.IsNullOrWhiteSpace(asset.DisplayName) ? asset.Id : asset.DisplayName;

    private static ClientManifest? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, LauncherJsonContext.Default.ClientManifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? TryRead<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
