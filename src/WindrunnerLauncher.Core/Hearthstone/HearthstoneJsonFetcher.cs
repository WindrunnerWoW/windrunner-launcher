using System.Diagnostics;
using System.Net;

namespace WindrunnerLauncher.Core.Hearthstone;

/// <summary>
/// Downloads from HearthstoneJSON the way the module's fetch scripts do: a User-Agent, at most about 4.5 requests
/// per second, retries with back-off, and a 404 answer remembered as "not published" for the rest of the run.
/// The service asks users to cache instead of hot-linking, so every file is fetched once and kept.
/// </summary>
public sealed class HearthstoneJsonFetcher
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(0.22);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _last = TimeSpan.FromDays(-1);

    public HearthstoneJsonFetcher(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Fetches the first URL that answers into <paramref name="path"/>. False when every URL answers 404.
    /// A file that is already there is kept.
    /// </summary>
    public async Task<bool> FetchAsync(ArtDownload item, CancellationToken ct = default)
    {
        if (File.Exists(item.CachePath) && new FileInfo(item.CachePath).Length > 0)
            return true;
        foreach (var url in item.Urls)
        {
            var data = await GetAsync(url, ct).ConfigureAwait(false);
            if (data is null)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(item.CachePath)!);
            var tmp = item.CachePath + ".partial";
            await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
            File.Move(tmp, item.CachePath, overwrite: true);
            return true;
        }

        return false;
    }

    /// <summary>The body, or null for 404. Other failures are retried, then thrown.</summary>
    private async Task<byte[]?> GetAsync(string url, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            await ThrottleAsync(ct).ConfigureAwait(false);
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return null;
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= 5)
                    throw new HttpRequestException($"Giving up on {url}: {ex.Message}", ex);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last + MinInterval - _clock.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);
            _last = _clock.Elapsed;
        }
        finally
        {
            _gate.Release();
        }
    }
}
