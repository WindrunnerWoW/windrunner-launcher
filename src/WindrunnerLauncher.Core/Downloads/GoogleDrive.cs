using System.Text.RegularExpressions;

namespace WindrunnerLauncher.Core.Downloads;

/// <summary>Turns public Google Drive share links into direct downloads, including the large-file confirm page.</summary>
public static class GoogleDrive
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WindrunnerLauncher";

    private static readonly Regex[] IdPatterns =
    [
        new(@"drive\.google\.com/file/d/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"drive\.google\.com/open\?id=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"drive(?:\.usercontent)?\.google\.com/.*[?&]id=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    private static readonly Regex ConfirmValue = new(@"name=""confirm""\s+value=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConfirmQuery = new(@"confirm=([0-9A-Za-z_-]+)", RegexOptions.Compiled);
    private static readonly Regex UuidValue = new(@"name=""uuid""\s+value=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? FileId(string url)
    {
        foreach (var re in IdPatterns)
        {
            var m = re.Match(url);
            if (m.Success)
                return m.Groups[1].Value;
        }

        return null;
    }

    public static string[] DirectUrls(string fileId) =>
    [
        "https://drive.usercontent.google.com/download?id=" + fileId + "&export=download&confirm=t",
        "https://drive.google.com/uc?export=download&confirm=t&id=" + fileId
    ];

    public static string ScanUrl(string fileId) => "https://drive.google.com/uc?export=download&id=" + fileId;

    public static string ConfirmedUrl(string fileId, string confirmPageHtml)
    {
        var confirm = "t";
        var m = ConfirmValue.Match(confirmPageHtml);
        if (m.Success)
            confirm = m.Groups[1].Value;
        else if ((m = ConfirmQuery.Match(confirmPageHtml)).Success)
            confirm = m.Groups[1].Value;

        var url = "https://drive.usercontent.google.com/download?id=" + fileId + "&export=download&confirm=" + Uri.EscapeDataString(confirm);
        m = UuidValue.Match(confirmPageHtml);
        if (m.Success)
            url += "&uuid=" + Uri.EscapeDataString(m.Groups[1].Value);
        return url;
    }
}
