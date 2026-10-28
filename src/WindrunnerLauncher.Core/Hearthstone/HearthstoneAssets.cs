using System.Text.Json;
using System.Text.Json.Serialization;
using WindrunnerLauncher.Core.Client;
using WindrunnerLauncher.Core.Persistence;

namespace WindrunnerLauncher.Core.Hearthstone;

public enum HearthstoneAssetState
{
    NotInstalled,
    Installed,
    UpdateAvailable
}

public sealed record HearthstoneAssetProgress(string Text, double? Fraction);

/// <summary>What was installed into a client's Interface\Hearthstone, kept in that folder.</summary>
public sealed class HearthstoneInstallRecord
{
    public string Repository { get; set; } = "";
    public string Commit { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
    public int Textures { get; set; }
    public int MatchingReference { get; set; }
    public int WithReference { get; set; }
    public List<string> Warnings { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HearthstoneInstallRecord))]
internal partial class HearthstoneJsonContext : JsonSerializerContext;

/// <summary>
/// Sets up the Hearthstone module's textures in a game client: Interface\Hearthstone holds the card art
/// (rebuilt locally from HearthstoneJSON, because Blizzard's art may not be redistributed) and the module's own
/// committed art. The addon itself ships in the client patch and reads every texture from that folder.
///
/// Downloads are cached under cache/hearthstone, so updates and repairs only fetch what changed.
/// </summary>
public sealed class HearthstoneAssets
{
    /// <summary>Raise when the builder's output changes, so installed clients rebuild.</summary>
    public const int BuilderVersion = 1;

    public const string FolderName = "Hearthstone";
    public const string RecordFileName = ".windrunner-hearthstone.json";

    private static readonly HttpClient SharedHttp = CreateHttp();

    private readonly LauncherPaths _paths;
    private readonly StateStore? _state;
    private readonly HttpClient _http;
    private readonly HearthstoneJsonFetcher _fetcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _remoteFingerprint;
    private long _lastReport;

    public event Action? Changed;

    public bool IsBusy { get; private set; }
    public HearthstoneAssetProgress? Progress { get; private set; }

    /// <param name="state">Settings with the module repository and branch; the defaults without one.</param>
    public HearthstoneAssets(LauncherPaths paths, StateStore? state = null, HttpClient? http = null)
    {
        _paths = paths;
        _state = state;
        _http = http ?? SharedHttp;
        _fetcher = new HearthstoneJsonFetcher(_http);
    }

    /// <summary>The module repository of the current settings.</summary>
    private HearthstoneModuleSource Module()
    {
        var repo = _state?.Settings.HearthstoneRepository;
        var branch = _state?.Settings.HearthstoneBranch;
        return new HearthstoneModuleSource(_http, Path.Combine(CacheRoot, "module"),
            string.IsNullOrWhiteSpace(repo) ? HearthstoneModuleSource.DefaultRepository : repo.Trim(),
            string.IsNullOrWhiteSpace(branch) ? HearthstoneModuleSource.DefaultBranch : branch.Trim());
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WindrunnerLauncher/0.1 (Hearthstone art, private local cache)");
        return http;
    }

    public string CacheRoot => Path.Combine(_paths.Cache, "hearthstone");
    public string RenderCache => Path.Combine(CacheRoot, "renders");

    public static string TargetFor(string clientDir) => ClientPaths.Resolve(clientDir, "Interface", FolderName);

    public HearthstoneInstallRecord? Installed(string clientDir)
    {
        var path = Path.Combine(TargetFor(clientDir), RecordFileName);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), HearthstoneJsonContext.Default.HearthstoneInstallRecord)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Installed state against the last <see cref="CheckAsync"/>; never touches the network.</summary>
    public HearthstoneAssetState StateFor(string clientDir)
    {
        var record = Installed(clientDir);
        if (record is null)
            return HearthstoneAssetState.NotInstalled;
        return _remoteFingerprint is not null && _remoteFingerprint != record.Fingerprint
            ? HearthstoneAssetState.UpdateAvailable
            : HearthstoneAssetState.Installed;
    }

    /// <summary>Reads the module's current art inputs on GitHub, so <see cref="StateFor"/> can report updates.</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        var snapshot = await Module().SnapshotAsync(ct).ConfigureAwait(false);
        _remoteFingerprint = snapshot.Fingerprint(BuilderVersion);
        Changed?.Invoke();
    }

    /// <summary>
    /// Downloads what is missing, rebuilds every texture and replaces the client's Interface\Hearthstone with the
    /// result. Nothing in the client changes until the new set is complete.
    /// </summary>
    public async Task<HearthstoneInstallRecord> InstallAsync(string clientDir, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("The Hearthstone art is already being set up.");
        IsBusy = true;
        try
        {
            return await Task.Run(() => InstallCoreAsync(clientDir, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            Progress = null;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Progress arrives per file from several threads; listeners hear about it at most every 150 ms.</summary>
    private void Report(string text, double? fraction = null)
    {
        Progress = new HearthstoneAssetProgress(text, fraction);
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastReport);
        if (now - last < 150 || Interlocked.CompareExchange(ref _lastReport, now, last) != last)
            return;
        Changed?.Invoke();
    }

    private async Task<HearthstoneInstallRecord> InstallCoreAsync(string clientDir, CancellationToken ct)
    {
        if (!Directory.Exists(clientDir))
            throw new DirectoryNotFoundException($"No game client in {clientDir}.");

        Report("Reading the Hearthstone module on GitHub…");
        var module = Module();
        var snapshot = await module.SnapshotAsync(ct).ConfigureAwait(false);
        var fingerprint = snapshot.Fingerprint(BuilderVersion);
        _remoteFingerprint = fingerprint;

        // the module files, laid out like the repository
        var moduleDir = Path.Combine(CacheRoot, "inputs");
        var files = snapshot.Inputs.Concat(snapshot.Textures.Select(t => t.File)).ToList();
        var fetched = 0;
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (f, token) =>
        {
            var blob = await module.FetchAsync(snapshot.Commit, f, token).ConfigureAwait(false);
            lock (blobs)
                blobs[f.Path] = blob;
            var n = Interlocked.Increment(ref fetched);
            Report($"Downloading module files ({n}/{files.Count})", (double)n / files.Count);
        }).ConfigureAwait(false);
        if (Directory.Exists(moduleDir))
            Directory.Delete(moduleDir, recursive: true);
        foreach (var f in snapshot.Inputs)
        {
            var path = Path.Combine(moduleDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(blobs[f.Path], path);
        }

        var builder = new HearthstoneArtBuilder(new HearthstoneArtInputs(moduleDir), RenderCache);

        // HearthstoneJSON downloads (cached across runs)
        var plan = builder.PlanDownloads();
        var pending = plan.Where(d => !(File.Exists(d.CachePath) && new FileInfo(d.CachePath).Length > 0)).ToList();
        var done = 0;
        var missing = new List<string>();
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (item, token) =>
        {
            if (!await _fetcher.FetchAsync(item, token).ConfigureAwait(false))
            {
                lock (missing)
                    missing.Add(item.Urls[0]);
                if (item.Required)
                    throw new InvalidDataException($"HearthstoneJSON has no {item.Urls[0]}.");
            }

            var n = Interlocked.Increment(ref done);
            Report($"Downloading card art from HearthstoneJSON ({n}/{pending.Count})", (double)n / pending.Count);
        }).ConfigureAwait(false);

        // build into a staging folder beside the target, then swap
        var target = TargetFor(clientDir);
        var staging = target + ".partial";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var result = builder.Build(staging, new SyncProgress<ArtBuildProgress>(p =>
            Report($"{p.Stage} ({p.Done}/{p.Total})", p.Total > 0 ? (double)p.Done / p.Total : null)), ct);
        foreach (var (file, rel) in snapshot.Textures)
        {
            var path = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(blobs[file.Path], path, overwrite: true);
        }

        var record = new HearthstoneInstallRecord
        {
            Repository = module.Repository,
            Commit = snapshot.Commit,
            Fingerprint = fingerprint,
            InstalledAt = DateTimeOffset.UtcNow,
            Textures = result.Files.Count + snapshot.Textures.Count,
            MatchingReference = result.MatchingReference,
            WithReference = result.WithReference,
            Warnings = missing.Select(u => $"not published by HearthstoneJSON: {u}").Concat(result.Warnings).ToList()
        };
        File.WriteAllText(Path.Combine(staging, RecordFileName),
            JsonSerializer.Serialize(record, HearthstoneJsonContext.Default.HearthstoneInstallRecord));

        Report("Installing into the client…");
        Swap(staging, target);
        return record;
    }

    /// <summary>Replaces <paramref name="target"/> with <paramref name="staging"/>; the old folder survives a failure.</summary>
    private static void Swap(string staging, string target)
    {
        var old = target + ".old";
        if (Directory.Exists(old))
            Directory.Delete(old, recursive: true);
        try
        {
            if (Directory.Exists(target))
                Directory.Move(target, old);
        }
        catch (IOException ex)
        {
            throw new IOException(
                "Interface\\Hearthstone is in use. Close the game and try again.", ex);
        }

        Directory.Move(staging, target);
        if (Directory.Exists(old))
            Directory.Delete(old, recursive: true);
    }

    /// <summary>An IProgress that reports on the caller's thread (Progress&lt;T&gt; posts to a captured context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
