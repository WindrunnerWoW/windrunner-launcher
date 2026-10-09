using System.Text.Json;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Mods;

/// <summary>
/// Resolves a mod's download URL from the latest GitHub or Codeberg release when the
/// manifest has a repository but no direct <c>downloadUrl</c>.
/// </summary>
public sealed class ReleaseCatalog
{
    private static readonly HttpClient Shared = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    private readonly HttpClient _http;
    private readonly GitHubReleases _github;

    public ReleaseCatalog(HttpClient? http = null)
    {
        _http = http ?? Shared;
        _github = new GitHubReleases(_http);
    }

    public async Task<string> ResolveAsync(ManagedAsset asset, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(asset.GitHubRepo))
            throw new InvalidOperationException($"{Display(asset)} has no repository to download from.");

        if (!TryParse(asset.GitHubRepo, out var host, out var ownerRepo))
            throw new InvalidOperationException($"{Display(asset)} has an invalid repository '{asset.GitHubRepo}'.");

        var release = await LatestAsync(host, ownerRepo, ct).ConfigureAwait(false);
        if (release is null)
            throw new InvalidOperationException($"Could not read the latest release of {asset.GitHubRepo}.");

        var picked = ModReleaseSelector.Select(release, asset);
        if (picked is null || string.IsNullOrWhiteSpace(picked.BrowserDownloadUrl))
            throw new InvalidOperationException(
                $"The latest release of {asset.GitHubRepo} has no matching Windows download for {Display(asset)}.");

        return picked.BrowserDownloadUrl;
    }

    public Task<GitHubRelease?> FetchLatestAsync(string host, string ownerRepo, CancellationToken ct = default) =>
        LatestAsync(host, ownerRepo, ct);

    public Task<GitHubRepository?> FetchGitHubRepositoryAsync(string ownerRepo, CancellationToken ct = default) =>
        _github.RepositoryAsync(ownerRepo, ct);

    public static bool TryParse(string spec, out string host, out string ownerRepo)
    {
        host = "";
        ownerRepo = "";
        var text = spec.Trim().Trim('/');
        if (text.Length == 0)
            return false;

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                return false;
            host = uri.Host;
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return false;
            ownerRepo = parts[0] + "/" + parts[1];
            return true;
        }

        if (text.StartsWith("codeberg.org/", StringComparison.OrdinalIgnoreCase))
        {
            host = "codeberg.org";
            ownerRepo = text["codeberg.org/".Length..].Trim('/');
            return ownerRepo.Count(c => c == '/') == 1;
        }

        if (text.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            host = "github.com";
            ownerRepo = text["github.com/".Length..].Trim('/');
            return ownerRepo.Count(c => c == '/') == 1;
        }

        if (text.Count(c => c == '/') == 1)
        {
            host = "github.com";
            ownerRepo = text;
            return true;
        }

        return false;
    }

    private async Task<GitHubRelease?> LatestAsync(string host, string ownerRepo, CancellationToken ct)
    {
        if (host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase))
            return await LatestCodebergAsync(ownerRepo, ct).ConfigureAwait(false);

        return await _github.LatestAsync(ownerRepo, ct).ConfigureAwait(false);
    }

    private async Task<GitHubRelease?> LatestCodebergAsync(string ownerRepo, CancellationToken ct)
    {
        var url = $"https://codeberg.org/api/v1/repos/{ownerRepo}/releases?limit=10";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            if (item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                continue;
            return ParseCodeberg(item);
        }

        return null;
    }

    private static GitHubRelease ParseCodeberg(JsonElement item)
    {
        var release = new GitHubRelease
        {
            TagName = item.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "",
            Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Body = item.TryGetProperty("body", out var body) ? body.GetString() ?? "" : ""
        };
        if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                release.Assets.Add(new GitHubReleaseAsset
                {
                    Name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    BrowserDownloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                    Size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0
                });
            }
        }

        return release;
    }

    private static string Display(ManagedAsset asset) =>
        string.IsNullOrWhiteSpace(asset.DisplayName) ? asset.Id : asset.DisplayName;
}
