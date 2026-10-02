using System.Globalization;
using System.Xml.Linq;

namespace WindrunnerLauncher.Core.News;

/// <summary>
/// Reads the public Windrunner news feed. Feed failure is intentionally non-fatal: news is
/// supplementary and must never interfere with client or server management.
/// </summary>
public sealed class NewsFeedService
{
    public const string FeedUrl = "https://windrunnerwow.github.io/rss";
    private const int MaxFeedBytes = 1_000_000;
    private static readonly XNamespace MediaNamespace = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace ContentNamespace = "http://purl.org/rss/1.0/modules/content/";

    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public event Action? Changed;

    public IReadOnlyList<NewsFeedItem> Items { get; private set; } = [];
    public string? LastError { get; private set; }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
            request.Headers.UserAgent.ParseAdd("WindrunnerLauncher/0.1");
            using var response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxFeedBytes)
                return;

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (xml.Length > MaxFeedBytes)
                return;

            var document = XDocument.Parse(xml, LoadOptions.None);
            var channel = document.Root?.Elements()
                .FirstOrDefault(element => element.Name.LocalName.Equals("channel", StringComparison.OrdinalIgnoreCase));
            if (channel is null)
                return;

            var items = channel.Elements()
                .Where(element => element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
                .Select(ParseItem)
                .Where(item => item is not null)
                .Cast<NewsFeedItem>()
                .Take(4)
                .ToArray();

            if (items.Length == 0)
                return;

            Items = items;
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            // News is supplementary; retain a diagnostic without interrupting launcher setup.
            LastError = ex.Message;
        }
    }

    private static NewsFeedItem? ParseItem(XElement element)
    {
        var title = Normalize(ChildValue(element, "title"));
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var published = ChildValue(element, "pubDate") ?? ChildValue(element, "date");
        var publishedAt = DateTimeOffset.TryParse(
            published,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var date)
            ? date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : "Windrunner News";

        var content = element.Element(ContentNamespace + "encoded")?.Value;
        var summary = Normalize(ChildValue(element, "description"));
        var body = NewsArticleParser.Parse(content);
        if (summary.Length == 0 && body.Count > 0)
            summary = body.FirstOrDefault(block => block.Kind == NewsBlockKind.Paragraph)?.Text ?? "";

        return new NewsFeedItem(
            title,
            publishedAt,
            summary,
            Normalize(ChildValue(element, "link") ?? ChildValue(element, "guid")),
            element.Element(MediaNamespace + "thumbnail")?.Attribute("url")?.Value ?? "")
        {
            Category = Normalize(ChildValue(element, "category")),
            Author = Normalize(ChildValue(element, "author") ?? ChildValue(element, "creator")),
            Body = body
        };
    }

    private static string? ChildValue(XElement parent, string localName) => parent.Elements()
        .FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
        ?.Value;

    private static string Normalize(string? value) => string.Join(
        " ",
        (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public sealed record NewsFeedItem(string Title, string PublishedAt, string Description, string Link, string ThumbnailUrl)
{
    public string Category { get; init; } = "";
    public string Author { get; init; } = "";
    public IReadOnlyList<NewsArticleBlock> Body { get; init; } = [];
}
