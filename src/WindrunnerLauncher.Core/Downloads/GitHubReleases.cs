using System.Net;
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

/// <summary>
/// Why a GitHub API call returned nothing. Callers report this instead of treating every failure as
/// "GitHub is unreachable", which is what hid an expired sign-in behind a network error.
/// </summary>
public sealed record GitHubApiFailure(HttpStatusCode StatusCode, string Message);

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

    public async Task<GitHubRelease?> LatestAsync(string ownerRepo, CancellationToken ct = default) =>
        (await LatestWithFailureAsync(ownerRepo, ct).ConfigureAwait(false)).Release;

    /// <summary>As <see cref="LatestAsync"/>, but also says why nothing came back.</summary>
    public Task<(GitHubRelease? Release, GitHubApiFailure? Failure)> LatestWithFailureAsync(
        string ownerRepo, CancellationToken ct = default) =>
        FetchAsync($"https://api.github.com/repos/{ownerRepo}/releases/latest", ct);

    public async Task<GitHubRelease?> TagAsync(string ownerRepo, string tag, CancellationToken ct = default) =>
        (await FetchAsync($"https://api.github.com/repos/{ownerRepo}/releases/tags/{tag}", ct).ConfigureAwait(false)).Release;

    /// <summary>Returns the default branch and last push time for a public GitHub repository.</summary>
    public async Task<GitHubRepository?> RepositoryAsync(string ownerRepo, CancellationToken ct = default)
    {
        var (doc, _) = await FetchDocumentAsync($"https://api.github.com/repos/{ownerRepo}", ct).ConfigureAwait(false);
        using var document = doc;
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("default_branch", out var branchElement))
            return null;

        var branch = branchElement.GetString();
        if (string.IsNullOrWhiteSpace(branch))
            return null;

        DateTimeOffset? pushedAt = document.RootElement.TryGetProperty("pushed_at", out var pushedElement)
            && pushedElement.ValueKind == JsonValueKind.String
            && pushedElement.TryGetDateTimeOffset(out var parsedPushedAt)
                ? parsedPushedAt
                : null;

        return new GitHubRepository(branch, pushedAt);
    }

    /// <summary>The most recent releases, newest first. Null when the API cannot be reached.</summary>
    public async Task<IReadOnlyList<GitHubRelease>?> ListAsync(string ownerRepo, int perPage = 30, CancellationToken ct = default) =>
        (await ListWithFailureAsync(ownerRepo, perPage, ct).ConfigureAwait(false)).Releases;

    /// <summary>As <see cref="ListAsync"/>, but also says why nothing came back.</summary>
    public async Task<(IReadOnlyList<GitHubRelease>? Releases, GitHubApiFailure? Failure)> ListWithFailureAsync(
        string ownerRepo, int perPage = 30, CancellationToken ct = default)
    {
        var (doc, failure) = await FetchDocumentAsync(
            $"https://api.github.com/repos/{ownerRepo}/releases?per_page={perPage}", ct).ConfigureAwait(false);
        using var document = doc;
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
            return (null, failure);
        return (document.RootElement.EnumerateArray().Select(Parse).ToList(), null);
    }

    private async Task<(GitHubRelease? Release, GitHubApiFailure? Failure)> FetchAsync(string url, CancellationToken ct)
    {
        var (doc, failure) = await FetchDocumentAsync(url, ct).ConfigureAwait(false);
        using var document = doc;
        return document is null || document.RootElement.ValueKind != JsonValueKind.Object
            ? (null, failure)
            : (Parse(document.RootElement), null);
    }

    /// <summary>
    /// Signed-in requests get GitHub's authenticated rate limit (5,000/hour) instead of the
    /// anonymous 60/hour-per-IP limit that unauthenticated callers share.
    ///
    /// A 401 means the stored token is dead. It is dropped and the request retried anonymously,
    /// because a token GitHub no longer accepts must not fail every later request until the user
    /// signs out by hand.
    /// </summary>
    private async Task<(JsonDocument? Document, GitHubApiFailure? Failure)> FetchDocumentAsync(
        string url, CancellationToken ct)
    {
        if (GitHubSession.IsSignedIn && GitHubSession.RefreshHook is { } refresh)
            await refresh(ct).ConfigureAwait(false);

        var sentToken = GitHubSession.IsSignedIn;
        var resp = await SendAsync(url, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized && sentToken)
        {
            resp.Dispose();
            // Drop the token here as well as through the hook, so the retry goes out anonymously
            // even when no auth service is listening.
            GitHubSession.ClearToken();
            GitHubSession.TokenRejectedHook?.Invoke();
            resp = await SendAsync(url, ct).ConfigureAwait(false);
        }

        using (resp)
            return await ReadAsync(resp, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (GitHubSession.AccessToken is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<(JsonDocument? Document, GitHubApiFailure? Failure)> ReadAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
            return (null, DescribeFailure(resp));

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return (await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false), null);
    }

    private static GitHubApiFailure DescribeFailure(HttpResponseMessage resp)
    {
        var status = resp.StatusCode;
        var code = (int)status;

        if (status == HttpStatusCode.Unauthorized)
            return new GitHubApiFailure(status,
                "GitHub rejected the stored sign-in (401). Sign in again to raise the API limit.");

        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var reset = resp.Headers.TryGetValues("X-RateLimit-Reset", out var values)
                        && long.TryParse(values.FirstOrDefault(), out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime()
                : (DateTimeOffset?)null;
            var when = reset is { } at ? $" Retry after {at:t}." : "";
            return new GitHubApiFailure(status,
                $"GitHub rate limit reached ({code}).{when} Signing in raises the limit from 60 to 5,000 requests per hour.");
        }

        if (status == HttpStatusCode.NotFound)
            return new GitHubApiFailure(status,
                "GitHub returned 404: the repository or release does not exist, or is private.");

        return new GitHubApiFailure(status, $"GitHub returned {code} {resp.ReasonPhrase}.");
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
