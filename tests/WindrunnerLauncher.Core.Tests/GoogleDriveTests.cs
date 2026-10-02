using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Tests;

public class GoogleDriveTests
{
    [Theory]
    [InlineData("https://drive.google.com/file/d/1-cG8r_Kypd3g7Hsf-GISLticA_y02B3E/view?usp=sharing", "1-cG8r_Kypd3g7Hsf-GISLticA_y02B3E")]
    [InlineData("https://drive.google.com/open?id=abc_DEF-123", "abc_DEF-123")]
    [InlineData("https://drive.usercontent.google.com/download?id=abc123&export=download&confirm=t", "abc123")]
    [InlineData("https://drive.google.com/uc?export=download&id=xyz789", "xyz789")]
    public void FileId_RecognisesShareAndDirectLinks(string url, string expected) =>
        Assert.Equal(expected, GoogleDrive.FileId(url));

    [Theory]
    [InlineData("https://github.com/org/repo/releases/download/v1/maps.zip")]
    [InlineData("https://example.com/get?id=not-a-drive-file")]
    public void FileId_IgnoresNonDriveUrls(string url) => Assert.Null(GoogleDrive.FileId(url));

    [Fact]
    public void ConfirmedUrl_UsesFormTokenAndUuid()
    {
        const string html = """<form><input type="hidden" name="confirm" value="AbC1"><input type="hidden" name="uuid" value="u-42"></form>""";
        Assert.Equal(
            "https://drive.usercontent.google.com/download?id=F1&export=download&confirm=AbC1&uuid=u-42",
            GoogleDrive.ConfirmedUrl("F1", html));
    }

    [Fact]
    public async Task DownloadManager_FollowsDriveConfirmPage_AndVerifiesChecksum()
    {
        var payload = Encoding.ASCII.GetBytes("PK\u0003\u0004fake-zip-body");
        var requested = new List<string>();
        var handler = new RouteHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            requested.Add(url);
            if (url.Contains("uuid=u-1", StringComparison.Ordinal))
                return Binary(payload);
            if (url.Contains("confirm=t", StringComparison.Ordinal))
                return Html("<p>still a page</p>");
            return Html("""<form><input name="confirm" value="tok9"><input name="uuid" value="u-1"></form>""");
        });

        using var tmp = new TempDir();
        using var manager = new DownloadManager(new HttpClient(handler));
        var dest = tmp.Combine("client.zip");
        await manager.DownloadAsync(new DownloadRequest
        {
            Id = "drive",
            DisplayName = "Drive file",
            Url = "https://drive.google.com/file/d/FILE1/view?usp=sharing",
            DestinationPath = dest,
            ExpectedSha256 = Checksums.Sha256Hex(payload),
            MaxAttempts = 1
        });

        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.Contains(requested, u => u.Contains("confirm=tok9", StringComparison.Ordinal));
        Assert.All(requested, u => Assert.Contains("FILE1", u));
    }

    [Fact]
    public async Task DownloadManager_FailsWhenDriveOnlyServesPages()
    {
        using var tmp = new TempDir();
        using var manager = new DownloadManager(new HttpClient(new RouteHandler(_ => Html("<p>sign in</p>"))));
        var dest = tmp.Combine("x.zip");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DownloadAsync(new DownloadRequest
        {
            Id = "drive",
            DisplayName = "Drive file",
            Url = "https://drive.google.com/file/d/FILE2/view",
            DestinationPath = dest,
            MaxAttempts = 1
        }));
        Assert.False(File.Exists(dest));
    }

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

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
