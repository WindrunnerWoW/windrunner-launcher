using System.Net;
using System.Net.Http.Headers;
using WindrunnerLauncher.Core.Models;

namespace WindrunnerLauncher.Core.Downloads;

public sealed class DownloadRequest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Url { get; init; }
    public required string DestinationPath { get; init; }
    public string? ExpectedSha256 { get; init; }
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>An attempt fails when no bytes arrive for this long, instead of waiting forever.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Downloads into <c>&lt;dest&gt;.partial</c> and moves the file into place only once complete
/// and verified.
///
/// A partial file survives dropped connections and launcher restarts: the next attempt asks the
/// server for the remaining bytes, guarded by <c>If-Range</c> so a file that changed on the server
/// is fetched again from the start. User cancellation and checksum mismatches discard it.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    /// <summary>Byte-progress events are capped to this rate; every listener refreshes UI on them.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly object _gate = new();
    private bool _disposed;

    public event Action<DownloadProgress>? Progress;

    public DownloadManager(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WindrunnerLauncher/0.1");
    }

    public async Task DownloadAsync(DownloadRequest request, CancellationToken external = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxAttempts, 1);
        var dest = Path.GetFullPath(request.DestinationPath);
        var partial = dest + ".partial";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_running.TryAdd(request.Id, cts))
                throw new InvalidOperationException($"Download '{request.Id}' is already running.");
        }

        var attempt = 0;
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Exception? last = null;
            for (attempt = 1; attempt <= request.MaxAttempts; attempt++)
            {
                cts.Token.ThrowIfCancellationRequested();
                Report(request, DownloadStatus.Running, attempt, 0, null, null);
                try
                {
                    await DownloadOnceAsync(request, partial, attempt, cts.Token).ConfigureAwait(false);
                    cts.Token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
                        && !Security.Checksums.VerifyFile(partial, request.ExpectedSha256))
                    {
                        Cleanup(partial);
                        throw new InvalidDataException("SHA-256 mismatch after download.");
                    }

                    File.Move(partial, dest, overwrite: true);
                    Cleanup(ResumeInfoPath(partial));
                    var length = new FileInfo(dest).Length;
                    Report(request, DownloadStatus.Succeeded, attempt, length, length, null);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    last = ex;
                    if (attempt >= request.MaxAttempts)
                        break;
                    var remaining = (int)request.RetryDelay.TotalSeconds;
                    while (remaining > 0)
                    {
                        Report(request, DownloadStatus.RetryWait, attempt + 1, 0, null, null, remaining);
                        await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                        remaining--;
                    }
                }
            }

            // Retain partial downloads for a later attempt or launcher restart.
            Report(request, DownloadStatus.Failed, request.MaxAttempts, 0, null, last?.Message ?? "Download failed");
            throw new InvalidOperationException($"Download failed after {request.MaxAttempts} attempts: {last?.Message}", last);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Cleanup(partial);
            Report(request, DownloadStatus.Cancelled, attempt, 0, null, "Cancelled");
            throw;
        }
        finally
        {
            lock (_gate)
                _running.Remove(request.Id);
        }
    }

    public void Cancel(string id)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(id, out var cts))
                cts.Cancel();
        }
    }

    private async Task DownloadOnceAsync(DownloadRequest request, string partial, int attempt, CancellationToken ct)
    {
        var driveId = GoogleDrive.FileId(request.Url);
        if (driveId is null)
        {
            await StreamToFileAsync(request, request.Url, null, partial, attempt, rejectHtml: false, resume: true, ct).ConfigureAwait(false);
            return;
        }

        // Drive hands out one-off confirm URLs, so there is nothing stable to resume against.
        Cleanup(partial);
        foreach (var url in GoogleDrive.DirectUrls(driveId))
        {
            try
            {
                if (await StreamToFileAsync(request, url, GoogleDrive.UserAgent, partial, attempt, rejectHtml: true, resume: false, ct).ConfigureAwait(false))
                    return;
            }
            catch (HttpRequestException)
            {
                // Try the next Drive endpoint.
            }
        }

        // Large files get a "can't scan for viruses" page whose form carries the real confirm token.
        using var scan = new HttpRequestMessage(HttpMethod.Get, GoogleDrive.ScanUrl(driveId));
        scan.Headers.TryAddWithoutValidation("User-Agent", GoogleDrive.UserAgent);
        using var scanResp = await _http.SendAsync(scan, ct).ConfigureAwait(false);
        scanResp.EnsureSuccessStatusCode();
        var html = await scanResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var confirmed = GoogleDrive.ConfirmedUrl(driveId, html);
        if (!await StreamToFileAsync(request, confirmed, GoogleDrive.UserAgent, partial, attempt, rejectHtml: true, resume: false, ct).ConfigureAwait(false))
            throw new InvalidDataException("Google Drive did not return the file. Share it as \"Anyone with the link can view\".");
    }

    /// <summary>Returns false without writing anything when <paramref name="rejectHtml"/> is set and the server answered with a web page.</summary>
    private async Task<bool> StreamToFileAsync(
        DownloadRequest request, string url, string? userAgent, string partial, int attempt, bool rejectHtml, bool resume,
        CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(request.StallTimeout);
        try
        {
            var resumeInfo = ResumeInfoPath(partial);
            var validator = "";
            var offset = resume ? ResumableLength(partial, resumeInfo, url, out validator) : 0;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (userAgent is not null)
                req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            if (offset > 0)
            {
                req.Headers.Range = new RangeHeaderValue(offset, null);
                req.Headers.TryAddWithoutValidation("If-Range", validator);
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Cleanup(partial);
                Cleanup(resumeInfo);
                throw new HttpRequestException("The server rejected the resume range; restarting the download.");
            }

            resp.EnsureSuccessStatusCode();
            if (rejectHtml && resp.Content.Headers.ContentType?.MediaType is "text/html")
                return false;

            // 206 continues the partial file; anything else is the whole file again.
            var append = offset > 0
                         && resp.StatusCode == HttpStatusCode.PartialContent
                         && resp.Content.Headers.ContentRange?.From == offset;
            if (!append)
                offset = 0;
            if (resume)
                WriteResumeInfo(resumeInfo, url, resp);

            var length = resp.Content.Headers.ContentLength;
            long? total = length is null ? null : offset + length;
            await using var input = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(
                partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, true);
            var buffer = new byte[64 * 1024];
            var received = offset;
            var lastReport = System.Diagnostics.Stopwatch.StartNew();
            Report(request, DownloadStatus.Running, attempt, received, total, null);
            int read;
            while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(request.StallTimeout);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                if (lastReport.Elapsed >= ProgressInterval)
                {
                    Report(request, DownloadStatus.Running, attempt, received, total, null);
                    lastReport.Restart();
                }
            }

            Report(request, DownloadStatus.Running, attempt, received, total, null);

            if (total is not null && received < total)
                throw new IOException($"Connection closed after {received} of {total} bytes.");
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not a user cancel: the connection went quiet. Surface it as a retryable failure.
            throw new TimeoutException(
                $"No data received for {(int)request.StallTimeout.TotalSeconds}s; the connection stalled.");
        }
    }

    private static string ResumeInfoPath(string partial) => partial + ".resume";

    /// <summary>Bytes already on disk that may be continued, or 0 when the partial cannot be trusted.</summary>
    private static long ResumableLength(string partial, string resumeInfo, string url, out string validator)
    {
        validator = "";
        if (!File.Exists(partial) || !File.Exists(resumeInfo))
            return 0;
        try
        {
            var lines = File.ReadAllLines(resumeInfo);
            if (lines.Length < 2 || !string.Equals(lines[0], url, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(lines[1]))
                return 0;
            validator = lines[1];
            return new FileInfo(partial).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Remembers the server's validator for this file. Without a strong ETag or Last-Modified the
    /// file cannot be resumed safely, so no record is written and the next attempt starts over.
    /// </summary>
    private static void WriteResumeInfo(string resumeInfo, string url, HttpResponseMessage resp)
    {
        var etag = resp.Headers.ETag;
        var validator = etag is { IsWeak: false }
            ? etag.Tag
            : resp.Content.Headers.LastModified?.ToString("R");
        try
        {
            if (string.IsNullOrWhiteSpace(validator))
                Cleanup(resumeInfo);
            else
                File.WriteAllLines(resumeInfo, [url, validator]);
        }
        catch (IOException)
        {
            // Resume is an optimisation; the download itself continues.
        }
    }

    private void Report(DownloadRequest request, DownloadStatus status, int attempt, long received, long? total, string? error, int retryIn = 0)
    {
        Progress?.Invoke(new DownloadProgress
        {
            Id = request.Id,
            DisplayName = request.DisplayName,
            BytesReceived = received,
            TotalBytes = total,
            Status = status,
            Attempt = attempt,
            MaxAttempts = request.MaxAttempts,
            RetryInSeconds = retryIn,
            Error = error
        });
    }

    private static void Cleanup(string path)
    {
        TryDelete(path);
        if (path.EndsWith(".partial", StringComparison.Ordinal))
            TryDelete(ResumeInfoPath(path));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var cts in _running.Values.ToArray())
                cts.Cancel();
        }
        if (_ownsHttp)
            _http.Dispose();
    }
}
