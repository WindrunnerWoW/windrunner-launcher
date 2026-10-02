using WindrunnerLauncher.Core.News;

namespace WindrunnerLauncher.Core.Tests;

public class NewsArticleParserTests
{
    [Fact]
    public void Parse_KeepsStructureAndDropsMarkup()
    {
        const string html = """
            <p><img src="https://example.test/hero.webp" alt="Hero" /></p><h3>What is it</h3>
            <p>A <a href="https://example.test">special</a> bank &amp; more. I don&#39;t mind.</p>
            <br><ul><li>First</li><li>Second</li></ul>
            <script>alert(1)</script>
            """;

        var blocks = NewsArticleParser.Parse(html);

        Assert.Equal(
        [
            new NewsArticleBlock(NewsBlockKind.Heading, "What is it"),
            new NewsArticleBlock(NewsBlockKind.Paragraph, "A special bank & more. I don't mind."),
            new NewsArticleBlock(NewsBlockKind.Bullet, "First"),
            new NewsArticleBlock(NewsBlockKind.Bullet, "Second")
        ], blocks);
    }

    [Fact]
    public void Parse_FallsBackToPlainTextWithoutBlockElements()
    {
        var blocks = NewsArticleParser.Parse("Just <b>some</b> text");

        Assert.Equal([new NewsArticleBlock(NewsBlockKind.Paragraph, "Just some text")], blocks);
    }

    [Fact]
    public void Parse_ReturnsEmptyForMissingContent()
    {
        Assert.Empty(NewsArticleParser.Parse(null));
        Assert.Empty(NewsArticleParser.Parse("  "));
    }
}
