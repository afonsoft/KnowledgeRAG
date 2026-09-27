using System.Net;
using System.Text;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-rss-feed-connector RF-003/RF-004: fetch + map,
// maxItems/Truncated, incremental fingerprint/stub, forceRefresh,
// fetchFullContent fallback, dedup, error sanitization.
public class RssFeedConnectorTests
{
    private const string FeedUrl = "https://feed.test/blog";
    private const string Marker = "RSSTOKEN99";

    private static string RssXml => """
<?xml version="1.0"?>
<rss version="2.0">
  <channel>
    <item>
      <title>Alpha</title><link>https://blog.test/1</link><guid>post-1</guid>
      <pubDate>Mon, 01 Sep 2026 10:00:00 GMT</pubDate>
      <description>alpha body</description>
    </item>
    <item>
      <title>Beta</title><link>https://blog.test/2</link><guid>post-2</guid>
      <description>beta body</description>
    </item>
    <item>
      <title>Gamma</title><link>https://blog.test/3</link>
      <description>gamma body RSSTOKEN99</description>
    </item>
  </channel>
</rss>
""";

    private sealed class FakeApi
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public List<(string Method, string Url)> Calls { get; } = [];

        public FakeApi OnGet(string url, string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "text/xml")
        {
            _routes[url] = _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };
            return this;
        }

        public HttpResponseMessage Route(HttpRequestMessage request)
        {
            var url = request.RequestUri!.ToString();
            Calls.Add((request.Method.Method, url));
            return _routes.TryGetValue(url, out var handler)
                ? handler(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found", Encoding.UTF8, "text/plain")
                };
        }
    }

    private sealed class RouterHandler(FakeApi api) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(api.Route(request));
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static RssFeedConnector Sut(FakeApi api) =>
        new(new FakeFactory(new RouterHandler(api)), NullLogger<RssFeedConnector>.Instance);

    private static KnowledgeSource Source(object configuration) => new()
    {
        Id = Guid.NewGuid(),
        Name = "rss-src",
        SourceType = SourceType.RssFeed,
        ConfigurationJson = System.Text.Json.JsonSerializer.Serialize(configuration)
    };

    [Fact]
    public async Task Fetch_ItemsBecomeDocuments()
    {
        var api = new FakeApi().OnGet(FeedUrl, RssXml);

        var result = await Sut(api).FetchAsync(Source(new { feedUrl = FeedUrl }), CancellationToken.None);

        Assert.Equal(3, result.Documents.Count);
        Assert.Equal("Alpha", result.Documents[0].Title);
        Assert.Contains("alpha body", result.Documents[0].TextContent);
        Assert.Contains("Alpha", result.Documents[0].TextContent);
        Assert.Equal("https://blog.test/1", result.Documents[0].UriReference);
    }

    [Fact]
    public async Task Fetch_DedupByUriReference_WithinSync()
    {
        // Same link appears twice in the feed — only first kept.
        var xml = """
<?xml version="1.0"?>
<rss version="2.0"><channel>
  <item><title>A</title><link>https://x/1</link><description>x</description></item>
  <item><title>A2</title><link>https://x/1</link><description>x</description></item>
</channel></rss>
""";
        var api = new FakeApi().OnGet(FeedUrl, xml);

        var result = await Sut(api).FetchAsync(Source(new { feedUrl = FeedUrl }), CancellationToken.None);

        Assert.Single(result.Documents);
    }

    [Fact]
    public async Task MaxItems_TruncatesWithWarningAndFlag()
    {
        var api = new FakeApi().OnGet(FeedUrl, RssXml);

        var result = await Sut(api).FetchAsync(Source(new { feedUrl = FeedUrl, maxItems = 2 }), CancellationToken.None);

        Assert.Equal(2, result.Documents.Count);
        Assert.True(result.Truncated);
        Assert.Contains(result.Warnings, w => w.Contains("maxItems"));
    }

    [Fact]
    public async Task Incremental_MatchingFingerprints_BecomeStubs()
    {
        var api = new FakeApi().OnGet(FeedUrl, RssXml);

        var fingerprints = new Dictionary<string, string>
        {
            ["https://blog.test/1"] = "rss:post-1",
            ["https://blog.test/2"] = "rss:post-2"
        };

        var result = await Sut(api).FetchAsync(
            Source(new { feedUrl = FeedUrl }), fingerprints, CancellationToken.None);

        // First two are stubs (empty content), third is real.
        Assert.Equal(3, result.Documents.Count);
        Assert.Equal("", result.Documents[0].TextContent);
        Assert.Equal("", result.Documents[1].TextContent);
        Assert.NotEqual("", result.Documents[2].TextContent);
        Assert.Equal("rss:post-1", result.Documents[0].Fingerprint);
    }

    [Fact]
    public async Task ForceRefresh_IgnoresFingerprints_AllItemsProcessed()
    {
        var api = new FakeApi().OnGet(FeedUrl, RssXml);

        var fingerprints = new Dictionary<string, string>
        {
            ["https://blog.test/1"] = "rss:post-1",
            ["https://blog.test/2"] = "rss:post-2"
        };

        var result = await Sut(api).FetchAsync(
            Source(new { feedUrl = FeedUrl, forceRefresh = true }),
            fingerprints, CancellationToken.None);

        Assert.Equal(3, result.Documents.Count);
        Assert.NotEqual("", result.Documents[0].TextContent);
        Assert.NotEqual("", result.Documents[1].TextContent);
        Assert.NotEqual("", result.Documents[2].TextContent);
    }

    [Fact]
    public async Task ItemWithoutGuid_UsesSha256Fingerprint()
    {
        var api = new FakeApi().OnGet(FeedUrl, RssXml);

        var result = await Sut(api).FetchAsync(Source(new { feedUrl = FeedUrl }), CancellationToken.None);

        // Third item has no guid — fingerprint starts with rss:
        Assert.StartsWith("rss:", result.Documents[2].Fingerprint);
        Assert.NotEqual("rss:post-1", result.Documents[2].Fingerprint);
        Assert.NotEqual("rss:post-2", result.Documents[2].Fingerprint);
    }

    [Fact]
    public async Task FetchFullContent_GetsLinkedPage()
    {
        var api = new FakeApi()
            .OnGet(FeedUrl, RssXml)
            .OnGet("https://blog.test/1", "<html><body><article><p>Full content from page</p></article></body></html>");

        var result = await Sut(api).FetchAsync(
            Source(new { feedUrl = FeedUrl, fetchFullContent = true }), CancellationToken.None);

        Assert.Contains("Full content from page", result.Documents[0].TextContent);
    }

    [Fact]
    public async Task FetchFullContent_Failure_FallsBackToFeedContent()
    {
        var api = new FakeApi()
            .OnGet(FeedUrl, RssXml)
            .OnGet("https://blog.test/1", "boom", HttpStatusCode.InternalServerError);

        var result = await Sut(api).FetchAsync(
            Source(new { feedUrl = FeedUrl, fetchFullContent = true }), CancellationToken.None);

        Assert.Contains("alpha body", result.Documents[0].TextContent);
        Assert.Contains(result.Warnings, w => w.Contains("full content") || w.Contains("blog.test/1"));
    }

    [Fact]
    public async Task FeedFailure_ThrowsWithSanitizedUrl()
    {
        var secretUrl = "https://user:pass@feed.test/secret";
        var api = new FakeApi().OnGet(secretUrl, "boom", HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api).FetchAsync(Source(new { feedUrl = secretUrl }), CancellationToken.None));

        Assert.DoesNotContain("user:pass", ex.Message);
        Assert.Contains("***@", ex.Message);
    }

    [Fact]
    public async Task NonXmlResponse_ThrowsClearError()
    {
        var api = new FakeApi().OnGet(FeedUrl, "<html>not a feed</html>", HttpStatusCode.OK, "text/html");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api).FetchAsync(Source(new { feedUrl = FeedUrl }), CancellationToken.None));

        Assert.Contains("RSS", ex.Message);
    }

    [Fact]
    public async Task InvalidFeedUrl_ThrowsClearError()
    {
        var api = new FakeApi();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api).FetchAsync(Source(new { feedUrl = "not-a-url" }), CancellationToken.None));

        Assert.Contains("feedUrl", ex.Message);
    }

    [Fact]
    public async Task FetchItemAsync_ReParsesFeed_ReturnsMatchingItem()
    {
        var api = new FakeApi().OnGet(FeedUrl, RssXml);

        var doc = await Sut(api).FetchItemAsync(
            Source(new { feedUrl = FeedUrl }),
            "https://blog.test/2",
            CancellationToken.None);

        Assert.NotNull(doc);
        Assert.Equal("Beta", doc!.Title);
        Assert.Contains("beta body", doc.TextContent);
    }

    [Fact]
    public async Task FetchItemAsync_ItemNoLongerInFeed_ReturnsNull()
    {
        var api = new FakeApi().OnGet(FeedUrl, RssXml);

        var doc = await Sut(api).FetchItemAsync(
            Source(new { feedUrl = FeedUrl }),
            "https://blog.test/999",
            CancellationToken.None);

        Assert.Null(doc);
    }
}
