using System.Text.RegularExpressions;

namespace WindrunnerLauncher.Core.Downloads;

/// <summary>
/// Resolves a fuckingfast.net share page to its direct download link.
///
/// The page carries a short-lived signed token, so a direct link cannot live in the signed
/// manifest: it expires after a couple of days. The page link itself is stable, so the manifest
/// points at that and the direct link is resolved again for every download attempt.
///
/// The page and its download path sit behind a Cloudflare managed challenge that rejects .NET's
/// TLS fingerprint with <c>Cf-Mitigated: challenge</c> whatever headers are sent, so the page is
/// fetched with curl. The link curl returns is a plain file URL that .NET downloads normally,
/// including resuming.
/// </summary>
public static class FuckingFast
{
    /// <summary>Cloudflare only serves the page to a browser-shaped request.</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Matches the share page only; the direct <c>ts.</c> host and the download path do not match.</summary>
    private static readonly Regex PagePattern = new(
        @"^https?://(?:www\.)?fuckingfast\.net/([A-Za-z0-9]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The signed <c>/{id}/download?t=…</c> path behind the page's "Copy download link" button.</summary>
    private static readonly Regex DownloadPathPattern = new(
        @"/([A-Za-z0-9]+)/download\?t=([A-Za-z0-9_.\-]+)",
        RegexOptions.Compiled);

    /// <summary>File id when <paramref name="url"/> is a share page, otherwise null.</summary>
    public static string? FileId(string url) =>
        PagePattern.Match(url) is { Success: true } match ? match.Groups[1].Value : null;

    public static string PageUrl(string fileId) => "https://fuckingfast.net/" + fileId;

    /// <summary>The signed download path the page hands out, or null when the page has no link.</summary>
    public static string? DownloadPath(string html)
    {
        var match = DownloadPathPattern.Match(html);
        return match.Success ? match.Value : null;
    }

    public static string DownloadUrl(string downloadPath) => "https://fuckingfast.net" + downloadPath;

    /// <summary>
    /// True when the body is Cloudflare's interstitial rather than the file page. The file page
    /// loads Turnstile too, so <c>challenge-platform</c> is not a marker; <c>_cf_chl_opt</c> is.
    /// </summary>
    public static bool IsChallenge(string body) =>
        body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || body.Contains("Enable JavaScript and cookies", StringComparison.OrdinalIgnoreCase)
        || body.Contains("_cf_chl_opt", StringComparison.OrdinalIgnoreCase)
        || body.Contains("cf-mitigated", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fetches a fresh token from <paramref name="pageUrl"/> and follows it to the direct file URL.
    /// </summary>
    public static async Task<string> ResolveAsync(string pageUrl, CancellationToken ct = default)
    {
        if (FileId(pageUrl) is null)
            throw new InvalidOperationException($"Not a fuckingfast.net share page: {pageUrl}");

        var html = await Curl.GetAsync(pageUrl, ct).ConfigureAwait(false);
        if (IsChallenge(html))
            throw new InvalidDataException("fuckingfast.net served a Cloudflare challenge instead of the file page.");

        var path = DownloadPath(html)
                   ?? throw new InvalidDataException(
                       "fuckingfast.net did not return a download link. The file may have expired or been removed.");

        var direct = await Curl.RedirectUrlAsync(DownloadUrl(path), ct).ConfigureAwait(false);
        if (!direct.Contains("/d/", StringComparison.Ordinal))
            throw new InvalidDataException("fuckingfast.net did not redirect to a direct download link.");

        return direct;
    }
}
