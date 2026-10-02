using WindrunnerLauncher.Core.Downloads;

namespace WindrunnerLauncher.Core.Tests;

public class GitHubReleasesTests
{
    private static readonly GitHubRelease Release = new()
    {
        TagName = "v0.3.1",
        Assets =
        [
            new GitHubReleaseAsset { Name = "windrunner-wow-windows-server-1.18.2.zip", BrowserDownloadUrl = "https://example.invalid/server.zip", Size = 10 },
            new GitHubReleaseAsset { Name = "windrunner-wow-sql-1.18.2.zip", BrowserDownloadUrl = "https://example.invalid/sql.zip", Size = 20 },
            new GitHubReleaseAsset { Name = "SHA256SUMS.txt", BrowserDownloadUrl = "https://example.invalid/sums", Size = 1 }
        ]
    };

    [Fact]
    public void FindAsset_ByContains_CaseInsensitive()
    {
        Assert.Equal("windrunner-wow-sql-1.18.2.zip", GitHubReleases.FindAsset(Release, "SQL")!.Name);
        Assert.Equal("windrunner-wow-windows-server-1.18.2.zip", GitHubReleases.FindAsset(Release, "windows-server")!.Name);
    }

    [Fact]
    public void FindAsset_BySimpleGlob()
    {
        Assert.Equal("windrunner-wow-windows-server-1.18.2.zip", GitHubReleases.FindAsset(Release, "windrunner-wow-windows-server-*.zip")!.Name);
        Assert.Equal("SHA256SUMS.txt", GitHubReleases.FindAsset(Release, "*.txt")!.Name);
    }

    [Fact]
    public void FindAsset_NoMatch_ReturnsNull()
    {
        Assert.Null(GitHubReleases.FindAsset(Release, "launcher"));
        Assert.Null(GitHubReleases.FindAsset(Release, "*.exe"));
    }

    [Theory]
    [InlineData("abc", "abc*abc", false)]
    [InlineData("abcabc", "abc*abc", true)]
    [InlineData("abcbc", "a*bc*bc", true)]
    [InlineData("abc", "a*bc*bc", false)]
    [InlineData("addon.zip", "*zip*zip", false)]
    [InlineData("addon.zip", "*.zip", true)]
    public void NameMatches_GlobSegmentsDoNotOverlap(string name, string filter, bool expected)
    {
        Assert.Equal(expected, GitHubReleases.NameMatches(name, filter));
    }

    [Fact]
    public async Task LatestAsync_ParsesGitHubJson_FromLoopbackServer()
    {
        // GitHubReleases hardcodes api.github.com, so parsing is exercised through a handler that
        // rewrites the request to the loopback server. No real network is touched.
        const string json = """
            {
              "tag_name": "v1.2.3",
              "name": "Release 1.2.3",
              "body": "notes",
              "assets": [
                { "name": "a.zip", "browser_download_url": "https://example.invalid/a.zip", "size": 42 },
                { "name": "b.zip", "browser_download_url": "https://example.invalid/b.zip" }
              ]
            }
            """;
        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            if (!ctx.Request.Url!.AbsolutePath.EndsWith("/repos/owner/repo/releases/latest", StringComparison.Ordinal))
                return LoopbackHttpServer.WriteAsync(ctx, [], status: 404);
            ctx.Response.ContentType = "application/json";
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.UTF8.GetBytes(json));
        });

        using var http = new HttpClient(new RewriteHostHandler(server.BaseUrl));
        var releases = new GitHubReleases(http);

        var release = await releases.LatestAsync("owner/repo");
        Assert.NotNull(release);
        Assert.Equal("v1.2.3", release!.TagName);
        Assert.Equal("Release 1.2.3", release.Name);
        Assert.Equal("notes", release.Body);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(42, release.Assets[0].Size);
        Assert.Equal(0, release.Assets[1].Size);
        Assert.Equal("https://example.invalid/b.zip", release.Assets[1].BrowserDownloadUrl);

        Assert.Null(await releases.LatestAsync("owner/other"));
        Assert.Null(await releases.TagAsync("owner/repo", "v0.0.0"));
    }

    [Fact]
    public void Constructor_SetsUserAgentAndAccept()
    {
        using var http = new HttpClient();
        _ = new GitHubReleases(http);
        Assert.Contains(http.DefaultRequestHeaders.UserAgent, ua => ua.Product?.Name == "WindrunnerLauncher");
        Assert.Contains(http.DefaultRequestHeaders.Accept, a => a.MediaType == "application/vnd.github+json");
    }

    private sealed class RewriteHostHandler(string baseUrl) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var original = request.RequestUri!;
            request.RequestUri = new Uri(new Uri(baseUrl), original.PathAndQuery.TrimStart('/'));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
