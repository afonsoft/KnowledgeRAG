using System.Xml.Linq;
using KnowledgeHub.Server.Ingestion.Connectors;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-rss-feed-connector RF-002: RSS 2.0 + Atom 1.0 parsing,
// namespace tolerance, content:encoded preference, date parsing, HTML stripping.
public class FeedParserTests
{
    private const string Rss20 = """
<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:content="http://purl.org/rss/1.0/modules/content/">
  <channel>
    <title>Test Blog</title>
    <link>https://blog.test</link>
    <description>A test feed</description>
    <item>
      <title>First Post</title>
      <link>https://blog.test/1</link>
      <guid>post-1</guid>
      <pubDate>Mon, 01 Sep 2026 10:00:00 GMT</pubDate>
      <description>&lt;p&gt;A short teaser&lt;/p&gt;</description>
      <content:encoded>&lt;p&gt;The full &lt;b&gt;content&lt;/b&gt; body&lt;/p&gt;</content:encoded>
    </item>
    <item>
      <title>Second Post</title>
      <link>https://blog.test/2</link>
      <guid>post-2</guid>
      <pubDate>invalid date</pubDate>
      <description>&lt;p&gt;Only description here&lt;/p&gt;</description>
    </item>
    <item>
      <link>https://blog.test/3</link>
    </item>
  </channel>
</rss>
""";

    private const string Atom10 = """
<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <title>Atom Blog</title>
  <entry>
    <title>Entry Alpha</title>
    <link href="https://atom.test/alpha" rel="alternate" />
    <id>tag:atom.test,2026:alpha</id>
    <published>2026-09-01T12:00:00Z</published>
    <content type="html">&lt;p&gt;Alpha content body&lt;/p&gt;</content>
  </entry>
  <entry>
    <title>Entry Beta</title>
    <link href="https://atom.test/beta-rel" rel="related" />
    <link href="https://atom.test/beta" rel="alternate" />
    <id>tag:atom.test,2026:beta</id>
    <updated>2026-09-02T08:30:00+02:00</updated>
    <summary type="text">Beta summary</summary>
  </entry>
</feed>
""";

    [Fact]
    public void Rss20_ParsesItems_WithContentEncodedPreference()
    {
        var items = FeedParser.Parse(Rss20);
        Assert.Equal(3, items.Count);

        Assert.Equal("First Post", items[0].Title);
        Assert.Equal("https://blog.test/1", items[0].Link);
        Assert.Equal("post-1", items[0].Guid);
        Assert.NotNull(items[0].PublishedAt);
        Assert.Contains("full", items[0].Content);
        Assert.Contains("content", items[0].Content);
    }

    [Fact]
    public void Rss20_FallsBackToDescription_WhenNoContentEncoded()
    {
        var items = FeedParser.Parse(Rss20);
        Assert.Contains("Only description", items[1].Content);
    }

    [Fact]
    public void Rss20_InvalidDate_BecomesNull()
    {
        var items = FeedParser.Parse(Rss20);
        Assert.Null(items[1].PublishedAt);
    }

    [Fact]
    public void Rss20_ItemWithoutTitle_KeptWithLinkFallback()
    {
        var items = FeedParser.Parse(Rss20);
        Assert.Equal("https://blog.test/3", items[2].Link);
    }

    [Fact]
    public void Atom10_ParsesEntries()
    {
        var items = FeedParser.Parse(Atom10);
        Assert.Equal(2, items.Count);

        Assert.Equal("Entry Alpha", items[0].Title);
        Assert.Equal("https://atom.test/alpha", items[0].Link);
        Assert.Equal("tag:atom.test,2026:alpha", items[0].Guid);
        Assert.Contains("Alpha content", items[0].Content);
    }

    [Fact]
    public void Atom10_PrefersAlternateRelLink()
    {
        var items = FeedParser.Parse(Atom10);
        Assert.Equal("https://atom.test/beta", items[1].Link);
    }

    [Fact]
    public void Atom10_UsesSummary_WhenNoContent()
    {
        var items = FeedParser.Parse(Atom10);
        Assert.Contains("Beta summary", items[1].Content);
    }

    [Fact]
    public void Atom10_ParsesUpdatedDate()
    {
        var items = FeedParser.Parse(Atom10);
        Assert.NotNull(items[1].PublishedAt);
    }

    [Fact]
    public void HtmlInContent_IsStripped()
    {
        var items = FeedParser.Parse(Rss20);
        Assert.DoesNotContain("<p>", items[0].Content);
        Assert.DoesNotContain("<b>", items[0].Content);
    }

    [Fact]
    public void MalformedXml_ThrowsFeedParseException()
    {
        Assert.Throws<FeedParseException>(() => FeedParser.Parse("<<<not xml"));
    }

    [Fact]
    public void UnknownRoot_ThrowsFeedParseException()
    {
        var ex = Assert.Throws<FeedParseException>(() =>
            FeedParser.Parse("""<?xml version="1.0"?><html><body>not a feed</body></html>"""));
        Assert.Contains("RSS", ex.Message);
    }

    [Fact]
    public void DcDate_IsParsed()
    {
        var xml = """<?xml version="1.0"?><rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/"><channel><item><title>DC Item</title><link>https://dc.test/1</link><guid>dc-1</guid><dc:date>2026-09-15T14:30:00Z</dc:date><description>body</description></item></channel></rss>""";
        var items = FeedParser.Parse(xml);
        Assert.NotNull(items[0].PublishedAt);
    }

    [Fact]
    public void EmptyFeed_ReturnsEmptyList()
    {
        var xml = """<?xml version="1.0"?><rss version="2.0"><channel></channel></rss>""";
        Assert.Empty(FeedParser.Parse(xml));
    }

    [Fact]
    public void ItemWithOnlyTitle_IsKept()
    {
        var xml = """<?xml version="1.0"?><rss version="2.0"><channel><item><title>Lone Title</title></item></channel></rss>""";
        var items = FeedParser.Parse(xml);
        var item = Assert.Single(items);
        Assert.Equal("Lone Title", item.Title);
    }

    [Fact]
    public void ItemWithoutAnything_IsSkipped()
    {
        var xml = """<?xml version="1.0"?><rss version="2.0"><channel><item></item></channel></rss>""";
        Assert.Empty(FeedParser.Parse(xml));
    }
}
