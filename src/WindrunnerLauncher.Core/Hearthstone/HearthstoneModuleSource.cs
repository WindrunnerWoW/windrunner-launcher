using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindrunnerLauncher.Core.Downloads;

namespace WindrunnerLauncher.Core.Hearthstone;

/// <summary>One file of the module repository at a commit: its path and git blob id.</summary>
public sealed record ModuleFile(string Path, string BlobSha);

/// <summary>The module files the art build needs at one commit of the module repository.</summary>
public sealed class ModuleSnapshot
{
    public required string Commit { get; init; }

    /// <summary>The input files (<see cref="HearthstoneArtInputs.Files"/>).</summary>
    public required IReadOnlyList<ModuleFile> Inputs { get; init; }

    /// <summary>The committed textures, with the path they get under Interface\Hearthstone.</summary>
    public required IReadOnlyList<(ModuleFile File, string Target)> Textures { get; init; }

    /// <summary>
    /// Identifies what an install built from this snapshot contains: the blobs of every input and committed
    /// texture plus the builder version. A new commit that touches none of them keeps the fingerprint.
    /// </summary>
    public string Fingerprint(int builderVersion)
    {
        var sb = new StringBuilder();
        sb.Append("builder ").Append(builderVersion).Append('\n');
        foreach (var f in Inputs.OrderBy(f => f.Path, StringComparer.Ordinal))
            sb.Append(f.Path).Append(' ').Append(f.BlobSha).Append('\n');
        foreach (var (f, target) in Textures.OrderBy(t => t.Target, StringComparer.Ordinal))
            sb.Append(target).Append(' ').Append(f.BlobSha).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>
/// Reads the Hearthstone module repository on GitHub: the head commit of a branch, its file tree, and raw files.
/// Files are cached by git blob id, so an update only downloads what changed.
/// </summary>
public sealed class HearthstoneModuleSource
{
    public const string DefaultRepository = "WindrunnerWoW/windrunner-hearthstone";
    public const string DefaultBranch = "main";

    private readonly HttpClient _http;
    private readonly string _blobCache;

    public string Repository { get; }
    public string Branch { get; }

    public HearthstoneModuleSource(HttpClient http, string blobCache, string repository = DefaultRepository, string branch = DefaultBranch)
    {
        _http = http;
        _blobCache = blobCache;
        Repository = repository;
        Branch = branch;
    }

    /// <summary>The head commit of the branch and the module files the art build uses at it.</summary>
    public async Task<ModuleSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        using var branch = await ApiAsync($"https://api.github.com/repos/{Repository}/branches/{Uri.EscapeDataString(Branch)}", ct)
            .ConfigureAwait(false);
        var commit = branch.RootElement.GetProperty("commit").GetProperty("sha").GetString()
                     ?? throw new InvalidDataException("GitHub returned no commit for the module branch.");
        using var tree = await ApiAsync($"https://api.github.com/repos/{Repository}/git/trees/{commit}?recursive=1", ct)
            .ConfigureAwait(false);
        if (tree.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
            throw new InvalidDataException("The module repository tree is too large for one GitHub listing.");
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in tree.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (e.GetProperty("type").GetString() == "blob")
                blobs[e.GetProperty("path").GetString()!] = e.GetProperty("sha").GetString()!;
        }

        var inputs = new List<ModuleFile>();
        foreach (var path in HearthstoneArtInputs.Files)
        {
            if (!blobs.TryGetValue(path, out var sha))
                throw new InvalidDataException($"{Repository}@{commit[..7]} has no {path}; the launcher cannot build the card art from it.");
            inputs.Add(new ModuleFile(path, sha));
        }

        var textures = new List<(ModuleFile, string)>();
        foreach (var (folder, target) in HearthstoneArtInputs.CommittedTextureFolders)
        {
            foreach (var (path, sha) in blobs)
            {
                if (!path.StartsWith(folder, StringComparison.Ordinal) || !path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rest = path[folder.Length..];
                if (rest.Contains('/'))
                    continue;
                textures.Add((new ModuleFile(path, sha), target + "/" + rest));
            }
        }

        return new ModuleSnapshot { Commit = commit, Inputs = inputs, Textures = textures };
    }

    /// <summary>The cached file of a blob, downloading and checking it first when needed.</summary>
    public async Task<string> FetchAsync(string commit, ModuleFile file, CancellationToken ct = default)
    {
        var path = Path.Combine(_blobCache, file.BlobSha[..2], file.BlobSha);
        if (File.Exists(path))
            return path;
        var url = $"https://raw.githubusercontent.com/{Repository}/{commit}/{string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString))}";
        byte[] data;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // a private module repository answers raw downloads to a signed-in account that can read it
                if (GitHubSession.AccessToken is { Length: > 0 } token)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    throw new InvalidOperationException(
                        $"{file.Path} of {Repository} could not be downloaded. If the repository is private, sign in to GitHub "
                        + "in Settings with an account that can read it.");
                resp.EnsureSuccessStatusCode();
                data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (HttpRequestException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }

        if (GitBlobSha(data) != file.BlobSha)
            throw new InvalidDataException($"{file.Path} from {Repository}@{commit[..7]} does not match its git blob id.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".partial";
        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>The git object id of a blob: SHA-1 of "blob &lt;size&gt;\0" and the content.</summary>
    public static string GitBlobSha(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(header);
        sha.AppendData(content);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private async Task<JsonDocument> ApiAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (GitHubSession.AccessToken is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException(
                "GitHub refused the request (rate limit). Sign in to GitHub in Settings, or try again in an hour.");
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"GitHub does not show {Repository} (branch {Branch}). If the repository is private, sign in to GitHub in Settings "
                + "with an account that can read it.");
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not read {Repository} on GitHub ({(int)resp.StatusCode} {resp.ReasonPhrase}).");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
