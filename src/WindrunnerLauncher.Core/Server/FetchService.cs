using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Server;

/// <summary>
/// Downloads server binaries and SQL from GitHub releases and maps from the signed client manifest.
/// </summary>
public sealed class FetchService : IDisposable
{
    private readonly LauncherPaths _paths;
    private readonly PortableEnv _env;
    private readonly DownloadManager _downloads;
    private readonly GitHubReleases _github;
    private readonly HttpClient _githubHttp;
    private readonly ModManager? _mods;
    private readonly bool _ownsDownloads;

    public FetchService(LauncherPaths paths, PortableEnv env, DownloadManager? downloads = null, ModManager? mods = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _mods = mods;
        if (downloads is null)
        {
            _downloads = new DownloadManager();
            _ownsDownloads = true;
        }
        else
        {
            _downloads = downloads;
        }

        _githubHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _github = new GitHubReleases(_githubHttp);
    }

    /// <summary>
    /// Fetches <c>windrunner-wow-windows-server-*.zip</c> and extracts it (with subfolders) requiring mangosd + realmd.
    /// </summary>
    public async Task FetchServerAsync(bool force = false, CancellationToken ct = default, Action<string>? log = null)
    {
        _env.Reload();
        Directory.CreateDirectory(_paths.ServerBinaries);
        Directory.CreateDirectory(_paths.DownloadCache);
        if (!force && ServerPresent())
        {
            log?.Invoke("Server already at " + _paths.ServerBinaries);
            return;
        }

        var asset = await ResolveServerAssetAsync(log, ct).ConfigureAwait(false);
        var zipPath = Path.Combine(_paths.DownloadCache, asset.Name);
        if (force || !File.Exists(zipPath))
        {
            log?.Invoke("Downloading " + asset.Url);
            await _downloads.DownloadAsync(new DownloadRequest
            {
                Id = "server-zip",
                DisplayName = asset.Name,
                Url = asset.Url,
                DestinationPath = zipPath
            }, ct).ConfigureAwait(false);
        }

        var tag = string.IsNullOrWhiteSpace(asset.TagName) ? asset.Name : asset.TagName;
        await InstallServerArchiveAsync(zipPath, tag, ct, log).ConfigureAwait(false);
    }

    /// <summary>
    /// Unpacks a server release zip over <c>server/</c>. Files shipped by the release replace
    /// their old copies; executables and libraries the new release no longer ships are removed so a
    /// stale DLL cannot shadow a renamed one. Runtime files the server writes itself are kept.
    /// </summary>
    public async Task InstallServerArchiveAsync(string zipPath, string tag, CancellationToken ct = default, Action<string>? log = null)
    {
        Directory.CreateDirectory(_paths.ServerBinaries);
        log?.Invoke("Unpacking into server/...");
        var extract = Path.Combine(_paths.DownloadCache, "server-extract");
        RecreateDir(extract);
        try
        {
            await Task.Run(() =>
            {
                if (zipPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                    || zipPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                    || (ArchiveUtil.LooksLikeGzip(zipPath) && !ServerUtil.LooksLikeZip(zipPath)))
                    ArchiveUtil.ExtractTarGz(zipPath, extract);
                else
                    ArchiveUtil.ExtractZip(zipPath, extract);
                var root = FindServerRoot(extract)
                           ?? throw BadZip(zipPath, "unpack finished but the archive has no mangosd/realmd at its top level");

                var shipped = new HashSet<string>(
                    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var stale in Directory.EnumerateFiles(_paths.ServerBinaries, "*", SearchOption.AllDirectories)
                             .Where(f => IsBinary(f) && !shipped.Contains(Path.GetRelativePath(_paths.ServerBinaries, f)))
                             .ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    File.Delete(stale);
                    log?.Invoke("Removed stale " + Path.GetRelativePath(_paths.ServerBinaries, stale));
                }

                // Recurse so subfolders the core loads relative to mangosd.exe (modules/*.conf, lua_scripts/) survive.
                foreach (var relative in shipped)
                {
                    ct.ThrowIfCancellationRequested();
                    ServerUtil.MoveFileReplace(Path.Combine(root, relative), Path.Combine(_paths.ServerBinaries, relative));
                }
            }, ct).ConfigureAwait(false);

            if (!ServerPresent())
                throw BadZip(zipPath, "unpack finished but server binaries are missing");

            foreach (var name in new[] { "mangosd", "realmd" })
            {
                var binary = ServerUtil.FindExisting(_paths.ServerBinaries, name);
                if (binary is not null)
                    ServerUtil.MakeUserExecutable(binary);
            }

            ServerUtil.WriteMarker(_paths.ServerReleaseMarker, tag);
            log?.Invoke($"OK: {_paths.ServerBinaries} ({tag})");
        }
        finally
        {
            if (Directory.Exists(extract))
                Directory.Delete(extract, recursive: true);
        }
    }

    private static bool IsBinary(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".exe" or ".dll" or ".pdb" or ".so")
            return true;
        return Path.GetFileName(path).Contains(".so.", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ResolvedAsset> ResolveServerAssetAsync(Action<string>? log, CancellationToken ct)
    {
        var overrideUrl = _env.Get("TORTOISE_WOW_SERVER_ZIP_URL", "");
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            var name = Path.GetFileName(overrideUrl.Split('?')[0]);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "/")
                name = OperatingSystem.IsLinux() ? "windrunner-wow-linux-server.tar.gz" : "windrunner-wow-windows-server.zip";
            return new ResolvedAsset(overrideUrl, name, "");
        }

        var repo = _env.Get("TORTOISE_WOW_REPO", PortableEnv.DefaultTortoiseWowRepo);
        var releaseName = _env.Get("TORTOISE_WOW_RELEASE", PortableEnv.DefaultTortoiseWowRelease);
        log?.Invoke($"Resolving server archive from {repo} release {releaseName}");
        var info = string.Equals(releaseName, "latest", StringComparison.OrdinalIgnoreCase)
            ? await _github.LatestAsync(repo, ct).ConfigureAwait(false)
            : await _github.TagAsync(repo, releaseName, ct).ConfigureAwait(false);
        if (info is null)
            throw new InvalidOperationException($"could not fetch release info for {repo} ({releaseName}). Set TORTOISE_WOW_SERVER_ZIP_URL to override.");

        var match = ServerReleaseNames.Find(info)
                    ?? throw new InvalidOperationException(ServerReleaseNames.MissingMessage(info.TagName));
        log?.Invoke($"Using {info.TagName}: {match.Name}");
        return new ResolvedAsset(match.BrowserDownloadUrl, match.Name, info.TagName);
    }

    /// <summary>Fetches the SQL zip requiring <c>sql/create_databases.sql</c>.</summary>
    public async Task FetchSqlAsync(bool force = false, CancellationToken ct = default, Action<string>? log = null)
    {
        _env.Reload();
        Directory.CreateDirectory(_paths.DownloadCache);
        if (!force && File.Exists(Path.Combine(_paths.Sql, "create_databases.sql")))
        {
            log?.Invoke("SQL already at " + _paths.Sql);
            return;
        }

        var asset = await ResolveReleaseAssetAsync(
            "windrunner-wow-sql-*.zip",
            "TORTOISE_WOW_SQL_ZIP_URL",
            "windrunner-wow-sql.zip",
            "SQL zip",
            log, ct).ConfigureAwait(false);
        var zipPath = Path.Combine(_paths.DownloadCache, asset.Name);
        if (force || !File.Exists(zipPath))
        {
            log?.Invoke("Downloading " + asset.Url);
            await _downloads.DownloadAsync(new DownloadRequest
            {
                Id = "sql-zip",
                DisplayName = asset.Name,
                Url = asset.Url,
                DestinationPath = zipPath
            }, ct).ConfigureAwait(false);
        }

        var tag = string.IsNullOrWhiteSpace(asset.TagName) ? asset.Name : asset.TagName;
        await InstallSqlArchiveAsync(zipPath, tag, ct, log).ConfigureAwait(false);
    }

    /// <summary>Replaces <c>sql/</c> wholesale with the contents of a SQL release zip.</summary>
    public async Task InstallSqlArchiveAsync(string zipPath, string tag, CancellationToken ct = default, Action<string>? log = null)
    {
        log?.Invoke("Unpacking into sql/...");
        var extract = Path.Combine(_paths.DownloadCache, "sql-extract");
        RecreateDir(extract);
        try
        {
            var sqlSrc = await Task.Run(() =>
            {
                ArchiveUtil.ExtractZip(zipPath, extract);
                var src = ArchiveUtil.FindNestedRoot(extract, Path.Combine("sql", "create_databases.sql"))
                          ?? ArchiveUtil.FindNestedRoot(extract, "create_databases.sql")
                          ?? throw BadZip(zipPath, "unpack finished but sql/create_databases.sql is missing - bad zip layout?");
                return File.Exists(Path.Combine(src, "create_databases.sql")) ? src : Path.Combine(src, "sql");
            }, ct).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(sqlSrc, "create_databases.sql")))
                throw BadZip(zipPath, "unpack finished but sql/create_databases.sql is missing - bad zip layout?");

            if (Directory.Exists(_paths.Sql))
                Directory.Delete(_paths.Sql, recursive: true);
            ServerUtil.MoveReplace(sqlSrc, _paths.Sql);
            ServerUtil.WriteMarker(_paths.SqlReleaseMarker, tag);
            log?.Invoke($"OK: {_paths.Sql} ({tag})");
        }
        finally
        {
            if (Directory.Exists(extract))
                Directory.Delete(extract, recursive: true);
        }
    }

    /// <summary>
    /// Fetches maps so <c>dbc</c>, <c>maps</c>, <c>vmaps</c>, and <c>mmaps</c> are non-empty.
    /// URL and checksum come from the signed client manifest's <c>maps</c> entry.
    /// </summary>
    public async Task FetchMapsAsync(bool force = false, CancellationToken ct = default, Action<string>? log = null)
    {
        Directory.CreateDirectory(_paths.Maps);
        Directory.CreateDirectory(_paths.DownloadCache);
        var (raw, expectedSha) = ResolveMapsZipUrl();

        var sourceHash = ServerUtil.Sha256Hex(raw + "\n" + expectedSha);
        var zipPath = Path.Combine(_paths.DownloadCache, "maps-" + sourceHash + ".zip");
        var sourceMarker = Path.Combine(_paths.ServerData, ".maps-source-sha256");
        var installedHash = ServerUtil.ReadTrimmed(sourceMarker);
        if (ServerUtil.MapsPresent(_paths.Maps) && !force && installedHash == sourceHash)
        {
            log?.Invoke($"Maps already at {_paths.Maps} (source {sourceHash})");
            return;
        }

        if (force || !File.Exists(zipPath) || !ServerUtil.LooksLikeZip(zipPath)
            || !Checksums.VerifyFile(zipPath, expectedSha))
        {
            log?.Invoke("Downloading maps zip");
            log?.Invoke(raw);
            await SaveMapsZipAsync(raw, zipPath, expectedSha, log, ct).ConfigureAwait(false);
        }

        log?.Invoke("Unpacking into maps/...");
        var extract = Path.Combine(_paths.DownloadCache, "maps-extract");
        RecreateDir(extract);
        try
        {
            ArchiveUtil.ExtractZip(zipPath, extract);
            var src = FindMapRoot(extract)
                      ?? throw BadZip(zipPath, "zip unpacked but dbc/maps/vmaps/mmaps were not found");
            foreach (var name in ServerUtil.MapDirs)
            {
                ct.ThrowIfCancellationRequested();
                var from = Path.Combine(src, name);
                var to = Path.Combine(_paths.Maps, name);
                ServerUtil.MoveReplace(from, to);
            }

            if (!ServerUtil.MapsPresent(_paths.Maps))
                throw new InvalidOperationException("unpack finished but maps/ is still incomplete");
            ServerUtil.WriteMarker(sourceMarker, sourceHash);
            log?.Invoke("OK: " + _paths.Maps);
        }
        finally
        {
            if (Directory.Exists(extract))
                Directory.Delete(extract, recursive: true);
        }
    }

    public bool ServerPresent() =>
        ServerUtil.FileExistsInsensitive(_paths.ServerBinaries, "mangosd")
        && ServerUtil.FileExistsInsensitive(_paths.ServerBinaries, "realmd");

    public void Dispose()
    {
        if (_ownsDownloads)
            _downloads.Dispose();
        _githubHttp.Dispose();
    }

    private async Task<ResolvedAsset> ResolveReleaseAssetAsync(
        string glob, string overrideKey, string fallbackName, string kind, Action<string>? log, CancellationToken ct)
    {
        var overrideUrl = _env.Get(overrideKey, "");
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            var name = Path.GetFileName(overrideUrl.Split('?')[0]);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "/")
                name = fallbackName;
            return new ResolvedAsset(overrideUrl, name, "");
        }

        var repo = _env.Get("TORTOISE_WOW_REPO", PortableEnv.DefaultTortoiseWowRepo);
        var release = _env.Get("TORTOISE_WOW_RELEASE", PortableEnv.DefaultTortoiseWowRelease);
        log?.Invoke($"Resolving {kind} from {repo} release {release}");
        var info = string.Equals(release, "latest", StringComparison.OrdinalIgnoreCase)
            ? await _github.LatestAsync(repo, ct).ConfigureAwait(false)
            : await _github.TagAsync(repo, release, ct).ConfigureAwait(false);
        if (info is null)
            throw new InvalidOperationException($"could not fetch release info for {repo} ({release}). Set {overrideKey} to override.");

        var match = GitHubReleases.FindAsset(info, glob)
                    ?? throw new InvalidOperationException($"release {info.TagName} has no {glob} asset");
        log?.Invoke($"Using {info.TagName}: {match.Name}");
        return new ResolvedAsset(match.BrowserDownloadUrl, match.Name, info.TagName);
    }

    /// <summary>
    /// Resolves the maps zip from the signed client manifest, the single source of client content metadata.
    /// </summary>
    private (string Url, string Sha256) ResolveMapsZipUrl()
    {
        var maps = _mods?.Manifest.Maps;
        if (maps is null || string.IsNullOrWhiteSpace(maps.Url) || string.IsNullOrWhiteSpace(maps.Sha256))
            throw new InvalidOperationException("The signed client manifest does not contain a maps URL and SHA-256 checksum.");

        return (maps.Url, maps.Sha256);
    }

    /// <summary>
    /// True when maps are installed but came from an older signed manifest entry than the current one.
    /// Missing maps are not reported here; setup fetches those.
    /// </summary>
    public bool MapsOutOfDate()
    {
        var maps = _mods?.Manifest.Maps;
        if (maps is null || string.IsNullOrWhiteSpace(maps.Url) || string.IsNullOrWhiteSpace(maps.Sha256))
            return false;
        if (!ServerUtil.MapsPresent(_paths.Maps))
            return false;

        var sourceHash = ServerUtil.Sha256Hex(maps.Url + "\n" + maps.Sha256);
        var installedHash = ServerUtil.ReadTrimmed(Path.Combine(_paths.ServerData, ".maps-source-sha256"));
        return installedHash != sourceHash;
    }

    private async Task SaveMapsZipAsync(string raw, string outFile, string expectedSha, Action<string>? log, CancellationToken ct)
    {
        if (GoogleDrive.FileId(raw) is { } driveId)
            log?.Invoke("Google Drive file id " + driveId);

        await _downloads.DownloadAsync(new DownloadRequest
        {
            Id = "maps-zip",
            DisplayName = "Maps",
            Url = raw,
            DestinationPath = outFile,
            ExpectedSha256 = expectedSha
        }, ct).ConfigureAwait(false);
        if (!ServerUtil.LooksLikeZip(outFile))
        {
            File.Delete(outFile);
            throw new InvalidDataException("downloaded file is not a zip (got a webpage). Need a direct zip URL or a public Google Drive share link.");
        }
    }

    private static string? FindServerRoot(string extracted)
    {
        var nested = ArchiveUtil.FindNestedRoot(extracted, "mangosd.exe", "realmd.exe")
                     ?? ArchiveUtil.FindNestedRoot(extracted, "mangosd", "realmd");
        if (nested is not null)
            return nested;
        if (ServerUtil.FileExistsInsensitive(extracted, "mangosd") && ServerUtil.FileExistsInsensitive(extracted, "realmd"))
            return extracted;
        return null;
    }

    private static string? FindMapRoot(string extract)
    {
        bool Ok(string d) => ServerUtil.MapDirs.All(n => Directory.Exists(Path.Combine(d, n)));
        if (Ok(extract))
            return extract;
        var nested = Path.Combine(extract, "maps");
        if (Ok(nested))
            return nested;
        if (!Directory.Exists(extract))
            return null;
        foreach (var dir in Directory.EnumerateDirectories(extract))
        {
            if (Ok(dir))
                return dir;
            var inner = Path.Combine(dir, "maps");
            if (Ok(inner))
                return inner;
        }

        return null;
    }

    private static void RecreateDir(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    private static InvalidDataException BadZip(string zipPath, string message)
    {
        try { File.Delete(zipPath); } catch { }
        return new InvalidDataException(message);
    }

    private readonly record struct ResolvedAsset(string Url, string Name, string TagName);
}
