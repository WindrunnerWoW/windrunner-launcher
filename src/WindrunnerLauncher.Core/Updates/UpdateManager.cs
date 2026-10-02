using System.Text;
using System.Text.Json;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Security;
using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Updates;

public sealed class UpdateCheckResult
{
    public bool ClientUpdateRequired { get; init; }
    public bool ServerUpdateAvailable { get; init; }
    public bool LauncherUpdateAvailable { get; init; }
    public string? LatestServerRelease { get; init; }
    public string? PatchNotes { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>
/// The three independent update systems: client content, the optional local server, and the
/// launcher itself. Each uses its own trust domain.
///
/// Only required client integrity blocks Play. A newer server release never does — the user may
/// stay on an old server version indefinitely, or suppress a release with
/// <see cref="IgnoreServerVersion"/>.
/// </summary>
public sealed class UpdateManager
{
    public const string ServerRepository = PortableEnv.DefaultTortoiseWowRepo;

    /// <summary>
    /// The launcher's own GitHub repository, whose Releases carry the signed self-update payload
    /// (<see cref="LauncherEnvelopeFileName"/>) published by .github/workflows/release.yml. Update
    /// this if the repository is renamed or moved.
    /// </summary>
    public const string LauncherRepository = "WindrunnerWoW/windrunner-launcher";

    public const string LauncherEnvelopeFileName = "launcher.manifest.json";
    public const string ServerReleaseNotesFileName = "server-release-notes.txt";

    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private readonly LauncherPaths _paths;
    private readonly StateStore _state;
    private readonly DownloadManager _downloads;
    private readonly ServerManager _server;
    private readonly ModManager _mods;
    private readonly BackupRollbackManager _rollback;
    private readonly GitHubReleases _releases;
    private readonly IServerDatabaseBackup _databaseBackup;
    private readonly LauncherSelfUpdate _selfUpdate;
    private GitHubRelease? _latestServer;
    private bool _preview;
    private bool _previewSkipped;
    private string? _previewInstalled;

    public event Action? Changed;

    public bool ClientUpdateRequired { get; private set; }
    public bool ServerUpdateAvailable { get; private set; }
    public bool LauncherUpdateAvailable { get; private set; }

    /// <summary>Notes gathered by the last check: server release body plus client changelog.</summary>
    public string? PatchNotes { get; private set; }

    public string? LatestServerRelease { get; private set; }
    public string? LatestServerNotes { get; private set; }
    public LauncherUpdateManifest? LatestLauncher { get; private set; }
    public DateTime? LastCheckUtc { get; private set; }
    public IReadOnlyList<string> LastCheckProblems { get; private set; } = [];

    public LauncherSelfUpdate SelfUpdate => _selfUpdate;
    public bool HasServerRollback => _rollback.HasBackup;
    public RollbackMetadata? ServerRollback => _rollback.Current;

    /// <summary>True once a check this session found a server release; <see cref="LatestServerRelease"/> is then live.</summary>
    public bool ServerReleaseChecked => _latestServer is not null;

    /// <summary>Combined size of the server and SQL archives of <see cref="LatestServerRelease"/>, when known.</summary>
    public long? LatestServerDownloadBytes
    {
        get
        {
            if (_latestServer is null)
                return null;
            var total = (ServerReleaseNames.Find(_latestServer)?.Size ?? 0)
                        + (GitHubReleases.FindAsset(_latestServer, SqlAssetGlob)?.Size ?? 0);
            return total > 0 ? total : null;
        }
    }

    /// <summary>
    /// The server release on disk. Three sources can know it: the build's own <c>BUILD_INFO.txt</c>
    /// (travels with the binaries), the version the launcher recorded after its last update, and
    /// the release marker written by first install. The first install never recorded a version, so
    /// the highest of whichever are present wins; a rollback rewinds all three together.
    /// </summary>
    public string? InstalledServerVersion
    {
        get
        {
            if (_previewInstalled is not null)
                return _previewInstalled;
            string?[] sources =
            [
                ReadBuildInfoVersion(Path.Combine(_paths.ServerBinaries, "BUILD_INFO.txt")),
                _state.Settings.InstalledServerVersion,
                ServerUtil.ReadTrimmed(_paths.ServerReleaseMarker)
            ];
            string? best = null;
            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source) || !TryParseVersion(source, out _))
                    continue;
                if (best is null || IsNewer(source, best))
                    best = source;
            }

            // A marker can hold an asset name when the zip came from a URL override.
            return best ?? sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        }
    }
    public string CurrentLauncherVersion { get; }

    public UpdateManager(
        LauncherPaths paths,
        StateStore state,
        DownloadManager downloads,
        ServerManager server,
        ModManager mods,
        BackupRollbackManager rollback,
        GitHubReleases? releases = null,
        IServerDatabaseBackup? databaseBackup = null)
    {
        _paths = paths;
        _state = state;
        _downloads = downloads;
        _server = server;
        _mods = mods;
        _rollback = rollback;
        _releases = releases ?? new GitHubReleases(SharedHttp);
        _databaseBackup = databaseBackup ?? new MariaDbDatabaseBackup(server.MariaDb);
        _selfUpdate = new LauncherSelfUpdate(state);
        CurrentLauncherVersion = typeof(UpdateManager).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        LatestServerRelease = _state.Settings.InstalledServerVersion;
        LoadCachedNotes();
        RefreshClientStatus();
    }


    /// <summary>Startup and manual "Check for updates". Never mutates the installation.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var problems = new List<string>();

        await CheckLauncherAsync(problems, ct).ConfigureAwait(false);
        await _mods.RefreshFromRemoteAsync(ct).ConfigureAwait(false);
        RefreshClientStatus();
        await CheckServerAsync(problems, ct).ConfigureAwait(false);

        LastCheckUtc = DateTime.UtcNow;
        LastCheckProblems = problems;
        PatchNotes = ComposePatchNotes();
        Changed?.Invoke();

        return new UpdateCheckResult
        {
            ClientUpdateRequired = ClientUpdateRequired,
            ServerUpdateAvailable = ServerUpdateAvailable,
            LauncherUpdateAvailable = LauncherUpdateAvailable,
            LatestServerRelease = LatestServerRelease,
            PatchNotes = PatchNotes,
            Problems = problems
        };
    }

    /// <summary>Recomputes client integrity from the manifest; cheap enough to call on any change.</summary>
    public void RefreshClientStatus()
    {
        var realm = _state.SelectedRealm();
        ClientUpdateRequired = _mods.RequiredOutOfDate(realm).Count > 0;
        Changed?.Invoke();
    }

    /// <summary>Checks only the server release; used by the Server tab's "Check now".</summary>
    public async Task<IReadOnlyList<string>> CheckServerAsync(CancellationToken ct = default)
    {
        var problems = new List<string>();
        await CheckServerAsync(problems, ct).ConfigureAwait(false);
        PatchNotes = ComposePatchNotes();
        Changed?.Invoke();
        return problems;
    }

    private async Task CheckServerAsync(List<string> problems, CancellationToken ct)
    {
        if (_preview)
            return;
        var repo = ServerRepositoryFromEnv();
        try
        {
            var release = await FindLatestServerReleaseAsync(repo, ct).ConfigureAwait(false);
            if (release is null)
            {
                problems.Add($"Could not read the latest release of {repo}.");
                return;
            }

            _latestServer = release;
            LatestServerRelease = release.TagName;
            LatestServerNotes = release.Body;
            SaveCachedNotes(release.Body);
            ServerUpdateAvailable = ComputeServerUpdateAvailable();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            problems.Add($"Server update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The highest-versioned published release that actually carries a server build. The same
    /// repository also hosts non-server releases (the rolling <c>Client</c> patch release), and
    /// "latest" follows publish order, so it cannot be trusted on its own.
    /// </summary>
    private async Task<GitHubRelease?> FindLatestServerReleaseAsync(string repo, CancellationToken ct)
    {
        var candidates = (await _releases.ListAsync(repo, ct: ct).ConfigureAwait(false) ?? [])
            .Where(r => !r.Draft && !r.Prerelease && !string.IsNullOrWhiteSpace(r.TagName)
                        && ServerReleaseNames.Find(r) is not null)
            .ToList();

        GitHubRelease? best = null;
        foreach (var release in candidates.Where(r => TryParseVersion(r.TagName, out _)))
        {
            if (best is null || IsNewer(release.TagName, best.TagName))
                best = release;
        }

        if (best is not null || candidates.Count > 0)
            return best ?? candidates[0];

        // The list endpoint failed or was empty; fall back to GitHub's notion of "latest".
        var latest = await _releases.LatestAsync(repo, ct).ConfigureAwait(false);
        return ServerReleaseNames.Find(latest) is not null ? latest : null;
    }

    /// <summary>True when the newest known release is newer than the install but was skipped.</summary>
    public bool IsLatestServerReleaseSkipped
    {
        get
        {
            var latest = LatestServerRelease;
            if (_latestServer is null || string.IsNullOrWhiteSpace(latest))
                return false;
            var skipped = _preview
                ? _previewSkipped
                : string.Equals(latest, _state.Settings.IgnoredServerRelease, StringComparison.OrdinalIgnoreCase);
            return skipped && IsNewer(latest, InstalledServerVersion ?? "");
        }
    }

    private bool ComputeServerUpdateAvailable()
    {
        if (_preview)
            return _latestServer is not null && !_previewSkipped && _previewInstalled is null;
        var latest = LatestServerRelease;
        var installed = InstalledServerVersion;
        if (_latestServer is null || string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(installed))
            return false;
        if (!_server.IsInstalled)
            return false;
        if (string.Equals(latest, _state.Settings.IgnoredServerRelease, StringComparison.OrdinalIgnoreCase))
            return false;
        return IsNewer(latest, installed);
    }

    private string ServerRepositoryFromEnv()
    {
        var repo = new PortableEnv(_paths).Get("TORTOISE_WOW_REPO", ServerRepository);
        return string.IsNullOrWhiteSpace(repo) ? ServerRepository : repo;
    }

    private static string? ReadBuildInfoVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("version=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed["version=".Length..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }
        catch (IOException)
        {
            // Fall through to the recorded version.
        }

        return null;
    }

    private async Task CheckLauncherAsync(List<string> problems, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await RefreshLauncherManifestFromRemoteAsync(ct).ConfigureAwait(false);

        var envelopePath = Path.Combine(_paths.ManifestCache, LauncherEnvelopeFileName);

        LauncherUpdateManifest? manifest = null;

        if (File.Exists(envelopePath))
        {
            var envelope = TryRead(envelopePath, LauncherJsonContext.Default.SignedManifestEnvelope);
            if (envelope is null)
            {
                problems.Add("The cached launcher manifest envelope could not be parsed.");
            }
            else if (!Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Launcher))
            {
                problems.Add("The cached launcher manifest failed signature verification and was ignored.");
            }
            else
            {
                manifest = TryParse(envelope.PayloadJson, LauncherJsonContext.Default.LauncherUpdateManifest);
            }
        }

        LatestLauncher = manifest;
        LauncherUpdateAvailable = manifest is not null
                                  && manifest.HasDownloadForCurrentOs()
                                  && IsNewer(manifest.Version, CurrentLauncherVersion);
    }

    /// <summary>
    /// Best-effort refresh of the signed launcher manifest from the latest GitHub release of
    /// <see cref="LauncherRepository"/>. Downloaded to a temp file and signature-verified before it
    /// ever touches the trusted cache, so a bad or unreachable host just leaves the previously
    /// cached manifest (if any) in place, exactly like <see cref="ModManager.RefreshFromRemoteAsync"/>
    /// does for the client manifest.
    /// </summary>
    private async Task RefreshLauncherManifestFromRemoteAsync(CancellationToken ct)
    {
        try
        {
            var release = await _releases.LatestAsync(LauncherRepository, ct).ConfigureAwait(false);
            var asset = release?.Assets.FirstOrDefault(a =>
                a.Name.Equals(LauncherEnvelopeFileName, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                return;

            Directory.CreateDirectory(_paths.ManifestCache);
            var dest = Path.Combine(_paths.ManifestCache, LauncherEnvelopeFileName);
            var tmp = dest + ".tmp";
            await _downloads.DownloadAsync(new DownloadRequest
            {
                Id = "launcher-manifest",
                DisplayName = LauncherEnvelopeFileName,
                Url = asset.BrowserDownloadUrl,
                DestinationPath = tmp,
                MaxAttempts = 2,
                RetryDelay = TimeSpan.FromSeconds(5)
            }, ct).ConfigureAwait(false);

            var envelope = TryRead(tmp, LauncherJsonContext.Default.SignedManifestEnvelope);
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.SignatureB64)
                || !Ed25519Signer.VerifyEnvelope(envelope, BuiltInTrust.Keys, TrustDomain.Launcher)
                || TryParse(envelope.PayloadJson, LauncherJsonContext.Default.LauncherUpdateManifest) is null)
            {
                TryDeleteFile(tmp);
                return;
            }

            File.Copy(tmp, dest, overwrite: true);
            TryDeleteFile(tmp);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // Best effort: the previously cached manifest (if any) is left in place.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }


    /// <summary>
    /// Downloads, verifies and applies every required asset that is missing or stale, plus any
    /// optional asset whose live payload no longer matches the manifest.
    /// </summary>
    public async Task<IReadOnlyList<string>> UpdateClientAsync(CancellationToken ct = default)
    {
        var realm = _state.SelectedRealm();
        var log = new List<string>();

        var required = _mods.RequiredOutOfDate(realm);
        var optional = _mods.OptionalOutOfDate(realm);
        if (required.Count == 0 && optional.Count == 0)
        {
            log.Add("Client content is already up to date.");
            RefreshClientStatus();
            return log;
        }

        foreach (var asset in required.Concat(optional))
        {
            ct.ThrowIfCancellationRequested();
            if (await _mods.EnsurePayloadAsync(asset, ct).ConfigureAwait(false) is { } payload)
                log.Add($"Downloaded and verified {Path.GetFileName(payload)}.");
        }

        await _mods.ApplyRequiredAsync(realm, ct).ConfigureAwait(false);
        log.AddRange(await _mods.MaterializeAsync(realm, downloadMissing: true, ct).ConfigureAwait(false));

        _state.Settings.InstalledClientManifestVersion = _mods.Manifest.Version;
        _state.SaveSettings();
        RefreshClientStatus();
        return log;
    }


    /// <summary>Windows server archive. Selection for the running OS is <see cref="ServerReleaseNames"/>.</summary>
    public const string ServerAssetGlob = ServerReleaseNames.WindowsZipGlob;
    public const string SqlAssetGlob = "windrunner-wow-sql-*.zip";

    /// <summary>Pause between download attempts of the server archives.</summary>
    internal TimeSpan ServerDownloadRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Hides the update hint until a release newer than <paramref name="tag"/> is published.</summary>
    public void IgnoreServerVersion(string? tag = null)
    {
        var version = tag ?? LatestServerRelease;
        if (string.IsNullOrWhiteSpace(version))
            return;
        if (_preview)
        {
            // A preview release is made up; never persist it into the real settings.
            _previewSkipped = true;
            ServerUpdateAvailable = false;
            Changed?.Invoke();
            return;
        }

        _state.Settings.IgnoredServerRelease = version;
        _state.SaveSettings();
        ServerUpdateAvailable = false;
        Changed?.Invoke();
    }

    public void ClearIgnoredServerVersion()
    {
        if (_preview)
        {
            _previewSkipped = false;
            ServerUpdateAvailable = ComputeServerUpdateAvailable();
            Changed?.Invoke();
            return;
        }
        _state.Settings.IgnoredServerRelease = null;
        _state.SaveSettings();
        ServerUpdateAvailable = ComputeServerUpdateAvailable();
        Changed?.Invoke();
    }

    /// <summary>
    /// Installs the newest server release: download and verify both archives while the realm keeps
    /// running → stop gracefully → back up files and dump the databases → replace <c>server/</c>
    /// and <c>sql/</c> → re-patch configuration → start again if it was running. mangosd applies
    /// the new <c>sql/database_updates</c> itself on that start.
    ///
    /// A failure while files are being replaced restores the previous files automatically. A
    /// failure after that (for example the new build refusing to start) keeps the backup so
    /// <see cref="RollbackServerAsync"/> can bring back both the files and the databases.
    /// </summary>
    public async Task<IReadOnlyList<string>> UpdateServerAsync(
        IProgress<SetupStage>? progress = null,
        CancellationToken ct = default)
    {
        if (_preview)
            return await SimulateServerUpdateAsync(progress, ct).ConfigureAwait(false);
        var release = _latestServer
                      ?? throw new InvalidOperationException("No server release has been discovered yet. Check for updates first.");
        var tag = release.TagName;
        var serverAsset = ServerReleaseNames.Find(release)
                          ?? throw new InvalidOperationException(ServerReleaseNames.MissingMessage(tag));
        var sqlAsset = GitHubReleases.FindAsset(release, SqlAssetGlob)
                       ?? throw new InvalidOperationException($"Release {tag} has no {SqlAssetGlob} asset.");
        var from = InstalledServerVersion ?? "unknown";
        var log = new List<string>();
        var stages = ServerUpdateStages.Update;

        string serverZip = "", sqlZip = "";
        await StageAsync(progress, stages, "update-download", async () =>
        {
            serverZip = await DownloadServerAssetAsync(serverAsset, "server-update", ct).ConfigureAwait(false);
            sqlZip = await DownloadServerAssetAsync(sqlAsset, "server-update-sql", ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        log.Add($"Downloaded and verified {serverAsset.Name} and {sqlAsset.Name}.");

        progress?.Report(ServerUpdateStages.Create(stages, "update-stop", completed: false));
        bool wasRunning;
        try
        {
            wasRunning = await _server.RunOfflineAsync(ServerLifecycleState.Updating, "Updating server…", async running =>
            {
                progress?.Report(ServerUpdateStages.Create(stages, "update-stop", completed: true));
                if (running)
                    log.Add("Stopped the server gracefully.");

                await StageAsync(progress, stages, "update-backup-files",
                    () => _rollback.CreatePreUpdateBackupAsync(from, tag, ct)).ConfigureAwait(false);
                await StageAsync(progress, stages, "update-backup-db",
                    () => _databaseBackup.BackupAsync(_rollback.DumpDirectory, null, ct)).ConfigureAwait(false);
                log.Add($"Backed up server {from} and its databases.");

                try
                {
                    await StageAsync(progress, stages, "update-replace", async () =>
                    {
                        await _server.Fetch.InstallServerArchiveAsync(serverZip, tag, ct, _server.Log).ConfigureAwait(false);
                        await _server.Fetch.InstallSqlArchiveAsync(sqlZip, tag, ct, _server.Log).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    await StageAsync(progress, stages, "update-configure", () =>
                    {
                        _server.Conf.Apply();
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Nothing has touched the databases yet, so the files are all there is to undo.
                    await _rollback.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Updating to {tag} failed, so server {from} was restored: {ex.Message}", ex);
                }

                _state.Settings.InstalledServerVersion = tag;
                if (string.Equals(_state.Settings.IgnoredServerRelease, tag, StringComparison.OrdinalIgnoreCase))
                    _state.Settings.IgnoredServerRelease = null;
                _state.SaveSettings();
                log.Add($"Installed server {tag}.");
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            ServerUpdateAvailable = ComputeServerUpdateAvailable();
            Changed?.Invoke();
        }

        TryDeleteFile(serverZip);
        TryDeleteFile(sqlZip);

        if (wasRunning)
        {
            try
            {
                await StageAsync(progress, stages, "update-start", () => _server.StartAsync(ct)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Server {tag} is installed but did not start: {ex.Message} " +
                    $"Use \"Restore previous version\" on the Server tab to go back to {from}.", ex);
            }

            log.Add("Started the server again.");
        }
        else
        {
            progress?.Report(ServerUpdateStages.Create(stages, "update-start", completed: true));
        }

        return log;
    }

    /// <summary>
    /// One-step rollback to the server version that was installed before the last update: restores
    /// the files and re-imports the database dumps taken just before that update, then starts the
    /// realm again if it was running. Progress made on the realm since that update is lost.
    /// </summary>
    public async Task<IReadOnlyList<string>> RollbackServerAsync(
        IProgress<SetupStage>? progress = null,
        CancellationToken ct = default)
    {
        var backup = _rollback.Current
                     ?? throw new InvalidOperationException("There is no pre-update backup to roll back to.");
        var target = backup.FromVersion;
        var log = new List<string>();
        var stages = ServerUpdateStages.Rollback;

        progress?.Report(ServerUpdateStages.Create(stages, "rollback-stop", completed: false));
        bool wasRunning;
        try
        {
            wasRunning = await _server.RunOfflineAsync(ServerLifecycleState.RollingBack, "Rolling back…", async running =>
            {
                progress?.Report(ServerUpdateStages.Create(stages, "rollback-stop", completed: true));

                await StageAsync(progress, stages, "rollback-files",
                    () => _rollback.RestoreFilesAsync(ct)).ConfigureAwait(false);
                await StageAsync(progress, stages, "rollback-db",
                    () => _databaseBackup.RestoreAsync(_rollback.DumpDirectory, ct)).ConfigureAwait(false);
                _server.Conf.Apply();

                _state.Settings.InstalledServerVersion =
                    string.IsNullOrWhiteSpace(target) || string.Equals(target, "unknown", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : target;
                _state.SaveSettings();
                _rollback.DeleteBackup();
                log.Add($"Restored server {target} and its databases. The backup was removed.");
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            ServerUpdateAvailable = ComputeServerUpdateAvailable();
            Changed?.Invoke();
        }

        if (wasRunning)
        {
            await StageAsync(progress, stages, "rollback-start", () => _server.StartAsync(ct)).ConfigureAwait(false);
            log.Add("Started the server again.");
        }
        else
        {
            progress?.Report(ServerUpdateStages.Create(stages, "rollback-start", completed: true));
        }

        return log;
    }


    /// <summary>How long each simulated stage of a preview update takes.</summary>
    internal TimeSpan PreviewStepDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>True while a made-up release is shown via <see cref="BeginServerUpdatePreview"/>.</summary>
    public bool IsServerUpdatePreview => _preview;

    /// <summary>
    /// Debug aid: announces a made-up server release one patch above the installed one so the
    /// update hint, dialog, and progress can be looked at. Real update checks are suppressed,
    /// skipping is not persisted, and "updating" only walks the stages; nothing is downloaded,
    /// stopped, or written.
    /// </summary>
    public void BeginServerUpdatePreview()
    {
        _preview = true;
        _previewSkipped = false;
        _previewInstalled = null;
        var installed = InstalledServerVersion;
        var tag = installed is not null && TryParseVersion(installed, out var current)
            ? $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0) + 1}"
            : "1.18.3";
        _latestServer = new GitHubRelease
        {
            TagName = tag,
            Name = tag,
            Body = $"""
                ## Windrunner {tag} (preview)

                This is a made-up release used to preview the update flow. Nothing is downloaded.

                ### Highlights
                - Crafting Orders: fixed orders expiring early after a server restart.
                - Material Storage now stacks reagents from the bank.
                - Playerbots: smarter dungeon role selection.

                ### Fixes
                - Several quest and loot table corrections.
                - Auction house search no longer times out on large result sets.
                """,
            Assets =
            [
                new GitHubReleaseAsset { Name = "windrunner-wow-windows-server-preview.zip", Size = 87L * 1024 * 1024 },
                new GitHubReleaseAsset { Name = "windrunner-wow-sql-preview.zip", Size = 51L * 1024 * 1024 }
            ]
        };
        LatestServerRelease = tag;
        LatestServerNotes = _latestServer.Body;
        ServerUpdateAvailable = true;
        PatchNotes = ComposePatchNotes();
        Changed?.Invoke();
    }

    private async Task<IReadOnlyList<string>> SimulateServerUpdateAsync(IProgress<SetupStage>? progress, CancellationToken ct)
    {
        var stages = ServerUpdateStages.Update;
        foreach (var stage in stages)
        {
            progress?.Report(ServerUpdateStages.Create(stages, stage.Id, completed: false));
            var slow = stage.Id is "update-download" or "update-backup-db";
            await Task.Delay(slow ? PreviewStepDelay * 2.5 : PreviewStepDelay, ct).ConfigureAwait(false);
            progress?.Report(ServerUpdateStages.Create(stages, stage.Id, completed: true));
        }

        _previewInstalled = LatestServerRelease;
        ServerUpdateAvailable = false;
        Changed?.Invoke();
        return ["Preview only: nothing was downloaded or changed."];
    }

    private async Task<string> DownloadServerAssetAsync(GitHubReleaseAsset asset, string id, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.DownloadCache);
        var destination = Path.Combine(_paths.DownloadCache, asset.Name);
        if (!string.IsNullOrWhiteSpace(asset.Sha256) && File.Exists(destination)
            && Checksums.VerifyFile(destination, asset.Sha256))
            return destination;

        await _downloads.DownloadAsync(new DownloadRequest
        {
            Id = id,
            DisplayName = asset.Name,
            Url = asset.BrowserDownloadUrl,
            DestinationPath = destination,
            ExpectedSha256 = asset.Sha256,
            RetryDelay = ServerDownloadRetryDelay
        }, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(asset.Sha256) && !Checksums.VerifyFile(destination, asset.Sha256))
        {
            TryDeleteFile(destination);
            throw new InvalidDataException($"{asset.Name} failed SHA-256 verification.");
        }

        return destination;
    }

    private static async Task StageAsync(
        IProgress<SetupStage>? progress,
        IReadOnlyList<SetupStageInfo> stages,
        string id,
        Func<Task> work)
    {
        progress?.Report(ServerUpdateStages.Create(stages, id, completed: false));
        try
        {
            await work().ConfigureAwait(false);
            progress?.Report(ServerUpdateStages.Create(stages, id, completed: true));
        }
        catch (Exception ex)
        {
            progress?.Report(ServerUpdateStages.Create(stages, id, completed: false, detail: ex.Message));
            throw;
        }
    }


    /// <summary>
    /// Downloads the announced launcher build, verifies it against the signed manifest checksum and
    /// stages it beside the running executable. The caller restarts; the new build reports itself
    /// healthy through <see cref="LauncherSelfUpdate.MarkHealthy"/>.
    /// </summary>
    public async Task<string> UpdateLauncherAsync(CancellationToken ct = default)
    {
        var manifest = LatestLauncher
                       ?? throw new InvalidOperationException("No launcher update has been announced.");
        var (url, sha256) = manifest.DownloadForCurrentOs();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("The launcher manifest has no download URL for this operating system.");
        if (string.IsNullOrWhiteSpace(sha256))
            throw new InvalidOperationException("The launcher manifest has no checksum; refusing to install it.");

        var staged = Path.Combine(_paths.DownloadCache, $"launcher-{manifest.Version}.bin");
        await _downloads.DownloadAsync(new DownloadRequest
        {
            Id = "launcher-update",
            DisplayName = $"Launcher {manifest.Version}",
            Url = url,
            DestinationPath = staged,
            ExpectedSha256 = sha256
        }, ct).ConfigureAwait(false);

        if (!Checksums.VerifyFile(staged, sha256))
        {
            File.Delete(staged);
            throw new InvalidDataException("The downloaded launcher failed SHA-256 verification.");
        }

        var previous = _selfUpdate.Stage(staged);
        try { File.Delete(staged); } catch { }

        LauncherUpdateAvailable = false;
        Changed?.Invoke();
        return previous;
    }


    private string ComposePatchNotes()
    {
        var notes = new StringBuilder();

        if (LauncherUpdateAvailable && LatestLauncher is { } launcher)
        {
            notes.AppendLine($"Launcher {launcher.Version}");
            if (!string.IsNullOrWhiteSpace(launcher.Notes))
                notes.AppendLine(launcher.Notes.Trim());
            notes.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(_mods.Manifest.Changelog))
        {
            notes.AppendLine($"Client content {_mods.Manifest.Version}");
            notes.AppendLine(_mods.Manifest.Changelog.Trim());
            notes.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(LatestServerNotes))
        {
            notes.AppendLine($"Server {LatestServerRelease}");
            notes.AppendLine(LatestServerNotes.Trim());
        }

        var text = notes.ToString().Trim();
        return text.Length > 0 ? text : "";
    }

    private void LoadCachedNotes()
    {
        var path = Path.Combine(_paths.ManifestCache, ServerReleaseNotesFileName);
        if (File.Exists(path))
            LatestServerNotes = File.ReadAllText(path);
        PatchNotes = ComposePatchNotes();
    }

    private void SaveCachedNotes(string body)
    {
        try
        {
            Directory.CreateDirectory(_paths.ManifestCache);
            File.WriteAllText(Path.Combine(_paths.ManifestCache, ServerReleaseNotesFileName), body ?? "");
        }
        catch (IOException)
        {
            // Patch notes are a convenience; failing to cache them changes nothing.
        }
    }

    /// <summary>Semantic-ish comparison that tolerates tags such as <c>v1.4.2</c>.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        if (string.IsNullOrWhiteSpace(current))
            return true;

        if (TryParseVersion(candidate, out var left) && TryParseVersion(current, out var right))
            return left > right;

        return !string.Equals(candidate.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        var cleaned = text.Trim().TrimStart('v', 'V');
        var cut = cleaned.IndexOfAny(['-', '+', ' ']);
        if (cut > 0)
            cleaned = cleaned[..cut];
        return Version.TryParse(cleaned, out version!);
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

    private static T? TryParse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
