using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Mods;
using WindrunnerLauncher.Core.Persistence;
using WindrunnerLauncher.Core.Security;
using WindrunnerLauncher.Core.Server;

namespace WindrunnerLauncher.Core.Tests;

public class FuckingFastTests
{
    /// <summary>
    /// The download row as the page renders it, including the "Copy download link" value. The page
    /// also loads Cloudflare Turnstile, which must not be mistaken for the challenge interstitial.
    /// </summary>
    private const string PageHtml = """
        <script src="https://challenges.cloudflare.com/turnstile/v0/api.js" defer></script>
        <div class="download-row">
            <a class="download-btn gay-button" hx-get="/kjtphrfhvb83/download?t=MTc5NDc1OTU2MTU0NQ.FND5qzUhJ8FtBkDl1wgL0pClzjm2akISrz1gp6YfBQc" rel="noopener">Download File</a>
            <a class="link-button copy" onclick="copyDownloadLink('\/kjtphrfhvb83\/download?t=MTc5NDc1OTU2MTU0NQ.FND5qzUhJ8FtBkDl1wgL0pClzjm2akISrz1gp6YfBQc')">Copy download link</a>
        </div>
        """;

    [Theory]
    [InlineData("https://fuckingfast.net/kjtphrfhvb83", "kjtphrfhvb83")]
    [InlineData("https://fuckingfast.net/kjtphrfhvb83/", "kjtphrfhvb83")]
    [InlineData("http://fuckingfast.net/abc123", "abc123")]
    [InlineData("https://www.fuckingfast.net/abc123", "abc123")]
    public void FileId_RecognisesSharePages(string url, string expected) =>
        Assert.Equal(expected, FuckingFast.FileId(url));

    [Theory]
    [InlineData("https://ts.fuckingfast.net/d/kjtphrfhvb83?v=token")]
    [InlineData("https://fuckingfast.net/kjtphrfhvb83/download?t=token")]
    [InlineData("https://github.com/org/repo/releases/download/v1/client.zip")]
    [InlineData("https://example.com/kjtphrfhvb83")]
    public void FileId_IgnoresDirectLinksAndOtherHosts(string url) => Assert.Null(FuckingFast.FileId(url));

    [Fact]
    public void DownloadPath_ReadsTheCopyDownloadLinkValue()
    {
        var path = FuckingFast.DownloadPath(PageHtml);
        Assert.Equal("/kjtphrfhvb83/download?t=MTc5NDc1OTU2MTU0NQ.FND5qzUhJ8FtBkDl1wgL0pClzjm2akISrz1gp6YfBQc", path);
        Assert.Equal(
            "https://fuckingfast.net/kjtphrfhvb83/download?t=MTc5NDc1OTU2MTU0NQ.FND5qzUhJ8FtBkDl1wgL0pClzjm2akISrz1gp6YfBQc",
            FuckingFast.DownloadUrl(path!));
    }

    [Fact]
    public void DownloadPath_IsNullWhenThePageHasNoLink() =>
        Assert.Null(FuckingFast.DownloadPath("<p>This file has expired.</p>"));

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>Just a moment...</title></head></html>")]
    [InlineData("<html><body>Enable JavaScript and cookies to continue</body></html>")]
    public void IsChallenge_DetectsTheCloudflareInterstitial(string body) => Assert.True(FuckingFast.IsChallenge(body));

    [Fact]
    public void IsChallenge_AcceptsTheFilePage() => Assert.False(FuckingFast.IsChallenge(PageHtml));

    [Fact]
    public async Task DownloadManager_ResolvesSharePage_AndVerifiesChecksum()
    {
        var payload = Encoding.ASCII.GetBytes("PK\u0003\u0004fake-client-zip");
        const string direct = "https://ts.fuckingfast.net/d/kjtphrfhvb83?v=fresh-token";
        var requested = new List<string>();

        var handler = new RouteHandler(request =>
        {
            requested.Add(request.RequestUri!.ToString());
            return Binary(payload);
        });

        using var tmp = new TempDir();
        using var manager = new DownloadManager(new HttpClient(handler), (_, _) => Task.FromResult(direct));
        var dest = tmp.Combine("client.zip");
        await manager.DownloadAsync(new DownloadRequest
        {
            Id = "client-bootstrap",
            DisplayName = "Client",
            Url = "https://fuckingfast.net/kjtphrfhvb83",
            DestinationPath = dest,
            ExpectedSha256 = Checksums.Sha256Hex(payload),
            MaxAttempts = 1
        });

        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.Equal([direct], requested);
    }

    [Fact]
    public async Task DownloadManager_ResumesAcrossTokenRotation()
    {
        var head = Encoding.ASCII.GetBytes("PK\u0003\u0004first-half-");
        var tail = Encoding.ASCII.GetBytes("second-half");
        var payload = head.Concat(tail).ToArray();
        const string pageUrl = "https://fuckingfast.net/kjtphrfhvb83";

        using var tmp = new TempDir();
        var dest = tmp.Combine("client.zip");
        var partial = dest + ".partial";

        // A previous attempt got as far as the first half, then the token rotated.
        await File.WriteAllBytesAsync(partial, head);
        await File.WriteAllLinesAsync(partial + ".resume", [pageUrl, "\"kjtphrfhvb83\""]);

        RangeHeaderValue? sentRange = null;
        var handler = new RouteHandler(request =>
        {
            sentRange = request.Headers.Range;
            var content = new ByteArrayContent(tail);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentRange = new ContentRangeHeaderValue(head.Length, payload.Length - 1, payload.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        });

        using var manager = new DownloadManager(new HttpClient(handler),
            (_, _) => Task.FromResult("https://ts.fuckingfast.net/d/kjtphrfhvb83?v=rotated-token"));
        await manager.DownloadAsync(new DownloadRequest
        {
            Id = "client-bootstrap",
            DisplayName = "Client",
            Url = pageUrl,
            DestinationPath = dest,
            ExpectedSha256 = Checksums.Sha256Hex(payload),
            MaxAttempts = 1
        });

        Assert.NotNull(sentRange);
        Assert.Equal(head.Length, sentRange!.Ranges.First().From);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task DownloadManager_FailsWhenTheSharePageYieldsNoLink()
    {
        using var tmp = new TempDir();
        using var manager = new DownloadManager(
            new HttpClient(new RouteHandler(_ => Binary([]))),
            (_, _) => throw new InvalidDataException("fuckingfast.net did not return a download link."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DownloadAsync(new DownloadRequest
        {
            Id = "client-bootstrap",
            DisplayName = "Client",
            Url = "https://fuckingfast.net/expired123",
            DestinationPath = tmp.Combine("client.zip"),
            MaxAttempts = 1
        }));
        Assert.False(File.Exists(tmp.Combine("client.zip")));
    }

    /// <summary>
    /// The maps entry in the signed manifest may point at a share page too; the maps fetch resolves
    /// it the same way the client bootstrap does.
    /// </summary>
    [Fact]
    public async Task FetchService_ResolvesMapsFromSharePage()
    {
        using var tmp = new TempDir();
        var paths = tmp.Paths();
        paths.EnsureLayout();

        var zip = BuildMapsZip();
        ManifestTestData.SaveClient(paths, new ClientManifest
        {
            Version = "1.0.0",
            Maps = new ClientBootstrap
            {
                DisplayName = "Maps",
                Url = "https://fuckingfast.net/maps123",
                Sha256 = Checksums.Sha256Hex(zip),
                Size = zip.Length
            }
        });

        var state = new StateStore(paths);
        var downloads = new DownloadManager(
            new HttpClient(new RouteHandler(_ => Binary(zip))),
            (_, _) => Task.FromResult("https://ts.fuckingfast.net/d/maps123?v=fresh-token"));
        var mods = new ModManager(paths, state, downloads);
        using var service = new FetchService(paths, new PortableEnv(paths), downloads, mods);

        await service.FetchMapsAsync();

        Assert.True(ServerUtil.MapsPresent(paths.Maps));
    }

    /// <summary>A maps zip whose root already holds the four client-data directories.</summary>
    private static byte[] BuildMapsZip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var dir in ServerUtil.MapDirs)
            {
                using var entry = archive.CreateEntry($"{dir}/data.bin").Open();
                entry.WriteByte(1);
            }
        }

        return buffer.ToArray();
    }

    private static HttpResponseMessage Binary(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(route(request));
    }
}
