using System.Collections.Concurrent;
using System.Net;
using WindrunnerLauncher.Core.Downloads;
using WindrunnerLauncher.Core.Security;

namespace WindrunnerLauncher.Core.Tests;

public class DownloadManagerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static DownloadRequest Request(string url, string dest, string? sha = null, int maxAttempts = 3, int retrySeconds = 1) => new()
    {
        Id = "test-" + Guid.NewGuid().ToString("N")[..8],
        DisplayName = "Test download",
        Url = url,
        DestinationPath = dest,
        ExpectedSha256 = sha,
        MaxAttempts = maxAttempts,
        RetryDelay = TimeSpan.FromSeconds(retrySeconds)
    };

    private static (DownloadManager Manager, ConcurrentQueue<DownloadProgress> Events) NewManager()
    {
        var manager = new DownloadManager();
        var events = new ConcurrentQueue<DownloadProgress>();
        manager.Progress += events.Enqueue;
        return (manager, events);
    }

    [Fact]
    public async Task Download_CancelDuringRetry_ReportsCancelled()
    {
        using var tmp = new TempDir();
        using var http = new HttpClient(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        using var manager = new DownloadManager(http);
        var events = new List<DownloadProgress>();
        var request = Request("https://example.invalid/file", tmp.Combine("file.bin"));
        manager.Progress += progress =>
        {
            events.Add(progress);
            if (progress.Status == DownloadStatus.RetryWait)
                manager.Cancel(request.Id);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DownloadAsync(request));

        Assert.Equal(DownloadStatus.Cancelled, events.Last().Status);
        Assert.False(File.Exists(request.DestinationPath + ".partial"));
    }

    [Fact]
    public async Task Download_DuplicateId_DoesNotReplaceActiveCancellation()
    {
        using var tmp = new TempDir();
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        using var manager = new DownloadManager(http);
        var request = Request("https://example.invalid/file", tmp.Combine("file.bin"));
        var running = manager.DownloadAsync(request);
        await handler.Started.Task.WaitAsync(TestTimeout);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DownloadAsync(request));
        manager.Cancel(request.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TestTimeout));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DownloadAsync(request, cancelled.Token));
    }

    [Fact]
    public async Task Dispose_CancelsActiveDownload_AndRejectsNewDownloads()
    {
        using var tmp = new TempDir();
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        using var manager = new DownloadManager(http);
        var request = Request("https://example.invalid/file", tmp.Combine("file.bin"));
        var running = manager.DownloadAsync(request);
        await handler.Started.Task.WaitAsync(TestTimeout);

        manager.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TestTimeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.DownloadAsync(request));
    }

    [Fact]
    public async Task Dispose_LeavesBorrowedHttpClientUsable()
    {
        using var http = new HttpClient(new StatusHandler(HttpStatusCode.OK));
        var manager = new DownloadManager(http);

        manager.Dispose();

        using var response = await http.GetAsync("https://example.invalid/file");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent([]) });
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Download_Success_WritesFileAndVerifiesChecksum()
    {
        var payload = TestData.Bytes(300_000);
        using var server = new LoopbackHttpServer((ctx, _) => LoopbackHttpServer.WriteAsync(ctx, payload));
        using var tmp = new TempDir();
        var (manager, events) = NewManager();
        using var _ = manager;

        var dest = tmp.Combine("cache", "downloads", "payload.bin");
        var request = Request(server.Url(), dest, Checksums.Sha256Hex(payload));

        await manager.DownloadAsync(request).WaitAsync(TestTimeout);

        Assert.True(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.Equal(1, server.RequestCount);

        var list = events.ToList();
        Assert.Contains(list, e => e.Status == DownloadStatus.Running && e.Id == request.Id);
        var done = list.Last();
        Assert.Equal(DownloadStatus.Succeeded, done.Status);
        Assert.Equal(payload.Length, done.BytesReceived);
        Assert.Equal(payload.Length, done.TotalBytes);
        Assert.Equal(1, done.Attempt);
        Assert.Contains(list, e => e.Status == DownloadStatus.Running && e.BytesReceived > 0 && e.TotalBytes == payload.Length);
    }

    [Fact]
    public async Task Download_WithoutExpectedHash_Succeeds()
    {
        var payload = Encoding.ASCII.GetBytes("hello");
        using var server = new LoopbackHttpServer((ctx, _) => LoopbackHttpServer.WriteAsync(ctx, payload));
        using var tmp = new TempDir();
        using var manager = new DownloadManager();
        var dest = tmp.Combine("x.bin");
        await manager.DownloadAsync(Request(server.Url(), dest)).WaitAsync(TestTimeout);
        Assert.Equal("hello", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task Download_ReplacesExistingDestination()
    {
        var payload = Encoding.ASCII.GetBytes("new content");
        using var server = new LoopbackHttpServer((ctx, _) => LoopbackHttpServer.WriteAsync(ctx, payload));
        using var tmp = new TempDir();
        using var manager = new DownloadManager();
        var dest = tmp.File("x.bin", "old content that is longer");
        await manager.DownloadAsync(Request(server.Url(), dest)).WaitAsync(TestTimeout);
        Assert.Equal("new content", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task Download_ServerErrorThenSuccess_RetriesWithCountdown()
    {
        var payload = TestData.Bytes(2048, seed: 7);
        var hits = 0;
        using var server = new LoopbackHttpServer(async (ctx, _) =>
        {
            var n = Interlocked.Increment(ref hits);
            if (n == 1)
                await LoopbackHttpServer.WriteAsync(ctx, Encoding.ASCII.GetBytes("boom"), status: 503);
            else
                await LoopbackHttpServer.WriteAsync(ctx, payload);
        });
        using var tmp = new TempDir();
        var (manager, events) = NewManager();
        using var _ = manager;

        var dest = tmp.Combine("retry.bin");
        var request = Request(server.Url(), dest, Checksums.Sha256Hex(payload), maxAttempts: 3, retrySeconds: 1);
        await manager.DownloadAsync(request).WaitAsync(TestTimeout);

        Assert.Equal(2, hits);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));

        var list = events.ToList();
        var waits = list.Where(e => e.Status == DownloadStatus.RetryWait).ToList();
        Assert.NotEmpty(waits);
        Assert.All(waits, w =>
        {
            Assert.Equal(2, w.Attempt);
            Assert.Equal(3, w.MaxAttempts);
            Assert.True(w.RetryInSeconds >= 1);
        });
        var done = list.Last();
        Assert.Equal(DownloadStatus.Succeeded, done.Status);
        Assert.Equal(2, done.Attempt);
    }

    [Fact]
    public async Task Download_AlwaysFailing_GivesUpAfterMaxAttempts()
    {
        using var server = new LoopbackHttpServer((ctx, _) => LoopbackHttpServer.WriteAsync(ctx, [], status: 500));
        using var tmp = new TempDir();
        var (manager, events) = NewManager();
        using var _ = manager;

        var dest = tmp.Combine("fail.bin");
        var request = Request(server.Url(), dest, maxAttempts: 2, retrySeconds: 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DownloadAsync(request).WaitAsync(TestTimeout));

        Assert.Contains("2 attempts", ex.Message);
        Assert.IsAssignableFrom<HttpRequestException>(ex.InnerException);
        Assert.Equal(2, server.RequestCount);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));

        var last = events.ToList().Last();
        Assert.Equal(DownloadStatus.Failed, last.Status);
        Assert.Equal(2, last.Attempt);
        Assert.Equal(2, last.MaxAttempts);
        Assert.False(string.IsNullOrWhiteSpace(last.Error));
    }

    [Fact]
    public async Task Download_ChecksumMismatch_IsRetriedThenFails()
    {
        var payload = Encoding.ASCII.GetBytes("this is not what you expected");
        using var server = new LoopbackHttpServer((ctx, _) => LoopbackHttpServer.WriteAsync(ctx, payload));
        using var tmp = new TempDir();
        using var manager = new DownloadManager();

        var dest = tmp.Combine("bad.bin");
        var request = Request(server.Url(), dest, sha: new string('0', 64), maxAttempts: 2, retrySeconds: 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DownloadAsync(request).WaitAsync(TestTimeout));

        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Contains("SHA-256", ex.InnerException!.Message);
        Assert.Equal(2, server.RequestCount);
        Assert.False(File.Exists(dest), "a file failing verification must never land at the destination");
        Assert.False(File.Exists(dest + ".partial"));
    }

    [Fact]
    public async Task Download_Cancel_DuringTransfer_StopsAndCleansUp()
    {
        var chunk = new byte[16 * 1024];
        var serverSawCancel = new TaskCompletionSource();
        using var server = new LoopbackHttpServer(async (ctx, ct) =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = chunk.Length * 1000L;
            try
            {
                for (var i = 0; i < 1000; i++)
                {
                    await ctx.Response.OutputStream.WriteAsync(chunk, ct);
                    await ctx.Response.OutputStream.FlushAsync(ct);
                    await Task.Delay(20, ct);
                }
            }
            catch
            {
                // client went away
            }
            finally
            {
                serverSawCancel.TrySetResult();
            }
        });
        using var tmp = new TempDir();
        var (manager, events) = NewManager();
        using var _ = manager;

        var dest = tmp.Combine("cancel.bin");
        var request = Request(server.Url(), dest, maxAttempts: 5, retrySeconds: 60);

        var gotData = new TaskCompletionSource();
        manager.Progress += p =>
        {
            if (p.Id == request.Id && p.Status == DownloadStatus.Running && p.BytesReceived > 0)
                gotData.TrySetResult();
        };

        var download = manager.DownloadAsync(request);
        await gotData.Task.WaitAsync(TestTimeout);
        manager.Cancel(request.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TestTimeout));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));
        var list = events.ToList();
        Assert.Equal(DownloadStatus.Cancelled, list.Last().Status);
        Assert.DoesNotContain(list, e => e.Status == DownloadStatus.RetryWait);
        Assert.DoesNotContain(list, e => e.Status == DownloadStatus.Failed);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Download_Cancel_DuringRetryWait_AbortsWithoutRetrying()
    {
        using var server = new LoopbackHttpServer((ctx, _) => LoopbackHttpServer.WriteAsync(ctx, [], status: 500));
        using var tmp = new TempDir();
        var (manager, events) = NewManager();
        using var _ = manager;

        var dest = tmp.Combine("wait.bin");
        var request = Request(server.Url(), dest, maxAttempts: 5, retrySeconds: 60);

        var waiting = new TaskCompletionSource();
        manager.Progress += p =>
        {
            if (p.Id == request.Id && p.Status == DownloadStatus.RetryWait)
                waiting.TrySetResult();
        };

        var download = manager.DownloadAsync(request);
        await waiting.Task.WaitAsync(TestTimeout);
        manager.Cancel(request.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TestTimeout));
        Assert.Equal(1, server.RequestCount);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));
        var list = events.ToList();
        Assert.DoesNotContain(list, e => e.Status == DownloadStatus.Failed);
        Assert.DoesNotContain(list, e => e.Status == DownloadStatus.Succeeded);
        Assert.Contains(list.Last().Status, new[] { DownloadStatus.Cancelled, DownloadStatus.RetryWait });
    }

    [Fact]
    public async Task Download_ExternalCancellationToken_IsHonoured()
    {
        using var server = new LoopbackHttpServer((ctx, _) => LoopbackHttpServer.WriteAsync(ctx, [], status: 500));
        using var tmp = new TempDir();
        using var manager = new DownloadManager();
        using var cts = new CancellationTokenSource();

        var request = Request(server.Url(), tmp.Combine("ext.bin"), maxAttempts: 5, retrySeconds: 60);
        var download = manager.DownloadAsync(request, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Download_AlreadyCancelledToken_ThrowsImmediately()
    {
        using var tmp = new TempDir();
        using var manager = new DownloadManager();
        var request = Request("http://127.0.0.1:9/never", tmp.Combine("never.bin"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DownloadAsync(request, new CancellationToken(canceled: true)).WaitAsync(TestTimeout));
    }

    [Fact]
    public void Cancel_UnknownId_IsNoOp()
    {
        using var manager = new DownloadManager();
        manager.Cancel("nothing-running");
    }

    [Fact]
    public async Task Download_UsesInjectedHttpClient_AndSetsUserAgent()
    {
        string? ua = null;
        using var server = new LoopbackHttpServer((ctx, _) =>
        {
            ua = ctx.Request.UserAgent;
            return LoopbackHttpServer.WriteAsync(ctx, Encoding.ASCII.GetBytes("ok"));
        });
        using var tmp = new TempDir();
        var http = new HttpClient();
        using var manager = new DownloadManager(http);
        await manager.DownloadAsync(Request(server.Url(), tmp.Combine("ua.bin"))).WaitAsync(TestTimeout);
        Assert.False(string.IsNullOrWhiteSpace(ua));
        Assert.Contains("WindrunnerLauncher", ua);
    }

    [Fact]
    public async Task Download_FollowsRedirect()
    {
        var payload = Encoding.ASCII.GetBytes("redirected");
        using var server = new LoopbackHttpServer(async (ctx, _) =>
        {
            if (ctx.Request.Url!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Found;
                ctx.Response.RedirectLocation = ctx.Request.Url.GetLeftPart(UriPartial.Authority) + "/final";
                ctx.Response.ContentLength64 = 0;
                await ctx.Response.OutputStream.FlushAsync();
                return;
            }

            await LoopbackHttpServer.WriteAsync(ctx, payload);
        });
        using var tmp = new TempDir();
        using var manager = new DownloadManager();
        var dest = tmp.Combine("r.bin");
        await manager.DownloadAsync(Request(server.Url("start"), dest)).WaitAsync(TestTimeout);
        Assert.Equal("redirected", await File.ReadAllTextAsync(dest));
    }

    /// <summary>Serves <paramref name="payload"/> with an ETag and honours Range + If-Range.</summary>
    private static Task ServeRanged(HttpListenerContext ctx, byte[] payload, string etag, List<string?> ranges)
    {
        var range = ctx.Request.Headers["Range"];
        lock (ranges) ranges.Add(range);
        ctx.Response.Headers["ETag"] = etag;
        if (range is not null && ctx.Request.Headers["If-Range"] == etag)
        {
            var from = long.Parse(range["bytes=".Length..].TrimEnd('-'));
            var rest = payload[(int)from..];
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers["Content-Range"] = $"bytes {from}-{payload.Length - 1}/{payload.Length}";
            ctx.Response.ContentLength64 = rest.Length;
            return ctx.Response.OutputStream.WriteAsync(rest).AsTask();
        }

        return LoopbackHttpServer.WriteAsync(ctx, payload);
    }

    [Fact]
    public async Task Download_ResumesPartialFileLeftByEarlierRun()
    {
        var payload = TestData.Bytes(200_000, seed: 3);
        var ranges = new List<string?>();
        using var tmp = new TempDir();
        var dest = tmp.Combine("resume.bin");
        using var server = new LoopbackHttpServer((ctx, _) => ServeRanged(ctx, payload, "\"v1\"", ranges));
        var url = server.Url();

        // What a killed launcher leaves behind: half the bytes plus the resume record.
        await File.WriteAllBytesAsync(dest + ".partial", payload[..80_000]);
        await File.WriteAllLinesAsync(dest + ".partial.resume", [url, "\"v1\""]);

        using var manager = new DownloadManager();
        await manager.DownloadAsync(Request(url, dest, Checksums.Sha256Hex(payload))).WaitAsync(TestTimeout);

        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        Assert.Equal("bytes=80000-", Assert.Single(ranges));
        Assert.False(File.Exists(dest + ".partial"));
        Assert.False(File.Exists(dest + ".partial.resume"));
    }

    [Fact]
    public async Task Download_ChangedFileOnServer_RestartsInsteadOfAppending()
    {
        var payload = TestData.Bytes(50_000, seed: 4);
        var ranges = new List<string?>();
        using var tmp = new TempDir();
        var dest = tmp.Combine("changed.bin");
        using var server = new LoopbackHttpServer((ctx, _) => ServeRanged(ctx, payload, "\"v2\"", ranges));
        var url = server.Url();

        await File.WriteAllBytesAsync(dest + ".partial", TestData.Bytes(20_000, seed: 99));
        await File.WriteAllLinesAsync(dest + ".partial.resume", [url, "\"v1\""]);

        using var manager = new DownloadManager();
        await manager.DownloadAsync(Request(url, dest, Checksums.Sha256Hex(payload))).WaitAsync(TestTimeout);

        Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Download_StalledConnection_TimesOutAndRetries()
    {
        var payload = Encoding.ASCII.GetBytes("finally");
        var hits = 0;
        using var server = new LoopbackHttpServer(async (ctx, ct) =>
        {
            if (Interlocked.Increment(ref hits) == 1)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = 1000;
                await ctx.Response.OutputStream.WriteAsync(new byte[10], ct);
                await ctx.Response.OutputStream.FlushAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(20), ct); // goes quiet mid-body
                return;
            }

            await LoopbackHttpServer.WriteAsync(ctx, payload);
        });
        using var tmp = new TempDir();
        using var manager = new DownloadManager();
        var dest = tmp.Combine("stall.bin");
        var request = new DownloadRequest
        {
            Id = "stall",
            DisplayName = "stall",
            Url = server.Url(),
            DestinationPath = dest,
            MaxAttempts = 2,
            RetryDelay = TimeSpan.FromSeconds(1),
            StallTimeout = TimeSpan.FromSeconds(1)
        };

        await manager.DownloadAsync(request).WaitAsync(TestTimeout);

        Assert.Equal("finally", await File.ReadAllTextAsync(dest));
        Assert.Equal(2, hits);
    }
}
