using System.Net.Http.Headers;
using System.Text.Json;

namespace WindrunnerLauncher.Core.Downloads;

public sealed class GitHubReleaseAsset
{
    public string Name { get; set; } = "";
    public string BrowserDownloadUrl { get; set; } = "";
    public long Size { get; set; }

    /// <summary>Lower-case hex SHA-256 from the API's <c>digest</c> field, when GitHub provides one.</summary>
    public string? Sha256 { get; set; }
}

public sealed class GitHubRelease
{
    public string TagName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    public List<GitHubReleaseAsset> Assets { get; set; } = [];
}

public sealed record GitHubRepository(string DefaultBranch, DateTimeOffset? PushedAt);

public sealed class GitHubReleases
{
    private readonly HttpClient _http;

    public GitHubReleases(HttpClient http)
    {
        _http = http;
        // Default headers are shared by every request on this client. Adding the same
        // value again, especially while a request is being written, corrupts the collection.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WindrunnerLauncher/0.1");
        if (!_http.DefaultRequestHeaders.Accept.Any(a => a.MediaType == "application/vnd.github+json"))
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Task<GitHubRelease?> LatestAsync(string ownerRepo, CancellationToken ct = default) =>
        FetchAsync($"https://api.github.com/repos/{ownerRepo}/releases/latest", ct);

    public Task<GitHubRelease?> TagAsync(string ownerRepo, string tag, CancellationToken ct = default) =>
        FetchAsync($"https://api.github.com/repos/{ownerRepo}/releases/tags/{tag}", ct);

    /// <summary>Returns the default branch and last push time for a public GitHub repository.</summary>
    public async Task<GitHubRepository?> RepositoryAsync(string ownerRepo, CancellationToken ct = default)
    {
        using var doc = await FetchDocumentAsync($"https://api.github.com/repos/{ownerRepo}", ct).ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("default_branch", out var branchElement))
            return null;

        var branch = branchElement.GetString();
        if (string.IsNullOrWhiteSpace(branch))
            return null;

        DateTimeOffset? pushedAt = doc.RootElement.TryGetProperty("pushed_at", out var pushedElement)
            && pushedElement.ValueKind == JsonValueKind.String
            && pushedElement.TryGetDateTimeOffset(out var parsedPushedAt)
                ? parsedPushedAt
                : null;

        return new GitHubRepository(branch, pushedAt);
    }

    /// <summary>The most recent releases, newest first. Null when the API cannot be reached.</summary>
    public async Task<IReadOnlyList<GitHubRelease>?> ListAsync(string ownerRepo, int perPage = 30, CancellationToken ct = default)
    {
        using var doc = await FetchDocumentAsync(
            $"https://api.github.com/repos/{ownerRepo}/releases?per_page={perPage}", ct).ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;
        return doc.RootElement.EnumerateArray().Select(Parse).ToList();
    }

    private async Task<GitHubRelease?> FetchAsync(string url, CancellationToken ct)
    {
        using var doc = await FetchDocumentAsync(url, ct).ConfigureAwait(false);
        return doc is null || doc.RootElement.ValueKind != JsonValueKind.Object ? null : Parse(doc.RootElement);
    }

    /// <summary>
    /// Signed-in requests get GitHub's authenticated rate limit (5,000/hour) instead of the
    /// anonymous 60/hour-per-IP limit that unauthenticated callers share.
    /// </summary>
    private async Task<JsonDocument?> FetchDocumentAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (GitHubSession.AccessToken is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public static GitHubReleaseAsset? FindAsset(GitHubRelease release, string globContains) =>
        release.Assets.FirstOrDefault(a => NameMatches(a.Name, globContains));

    /// <summary>Substring match, or a `*`-glob (e.g. <c>*1.12*</c>, <c>vanillafixes*.zip</c>) if the filter contains one.</summary>
    public static bool NameMatches(string name, string filter) =>
        name.Contains(filter, StringComparison.OrdinalIgnoreCase) || MatchesSimpleGlob(name, filter);

    /// <summary>Any number of `*` wildcards; each non-empty segment between them must appear in order.</summary>
    private static bool MatchesSimpleGlob(string name, string glob)
    {
        if (!glob.Contains('*'))
            return name.Equals(glob, StringComparison.OrdinalIgnoreCase);

        var parts = glob.Split('*');
        var pos = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
                continue;

            if (i == 0)
            {
                if (!name.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                    return false;
                pos = part.Length;
                continue;
            }

            if (i == parts.Length - 1)
                return name.Length - part.Length >= pos
                       && name.EndsWith(part, StringComparison.OrdinalIgnoreCase);

            var idx = name.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;
            pos = idx + part.Length;
        }

        return true;
    }

    private static GitHubRelease Parse(JsonElement root)
    {
        var release = new GitHubRelease
        {
            TagName = root.GetProperty("tag_name").GetString() ?? "",
            Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            Draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
            Prerelease = root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True
        };
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                release.Assets.Add(new GitHubReleaseAsset
                {
                    Name = a.GetProperty("name").GetString() ?? "",
                    BrowserDownloadUrl = a.GetProperty("browser_download_url").GetString() ?? "",
                    Size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    Sha256 = ParseSha256Digest(a)
                });
            }
        }

        return release;
    }

    private static string? ParseSha256Digest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var d) || d.ValueKind != JsonValueKind.String)
            return null;
        const string prefix = "sha256:";
        var digest = d.GetString();
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }
}
