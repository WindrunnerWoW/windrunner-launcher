using System.Net;
using System.Text.RegularExpressions;

namespace WindrunnerLauncher.Core.News;

public enum NewsBlockKind
{
    Heading,
    Paragraph,
    Bullet,
    Quote
}

public sealed record NewsArticleBlock(NewsBlockKind Kind, string Text);

/// <summary>
/// Turns the feed's <c>content:encoded</c> HTML into plain text blocks the launcher can render
/// natively. Only the structure is kept: markup, images, and scripts are dropped.
/// </summary>
public static partial class NewsArticleParser
{
    private const int MaxBlocks = 80;

    public static IReadOnlyList<NewsArticleBlock> Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        var cleaned = DroppedElements().Replace(html, " ");
        var blocks = new List<NewsArticleBlock>();
        foreach (Match match in BlockElements().Matches(cleaned))
        {
            var text = ToPlainText(match.Groups["body"].Value);
            if (text.Length == 0)
                continue;

            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            var kind = tag switch
            {
                ['h', _] => NewsBlockKind.Heading,
                "li" => NewsBlockKind.Bullet,
                "blockquote" => NewsBlockKind.Quote,
                _ => NewsBlockKind.Paragraph
            };
            blocks.Add(new NewsArticleBlock(kind, text));
            if (blocks.Count == MaxBlocks)
                break;
        }

        if (blocks.Count == 0)
        {
            var text = ToPlainText(cleaned);
            if (text.Length > 0)
                blocks.Add(new NewsArticleBlock(NewsBlockKind.Paragraph, text));
        }

        return blocks;
    }

    private static string ToPlainText(string fragment)
    {
        var withoutTags = Tags().Replace(fragment, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return string.Join(" ", decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>|<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DroppedElements();

    [GeneratedRegex(@"<(?<tag>h[1-6]|p|li|blockquote)\b[^>]*>(?<body>.*?)</\k<tag>\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockElements();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
}
