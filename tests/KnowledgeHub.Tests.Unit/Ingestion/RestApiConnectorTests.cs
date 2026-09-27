using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-restapi-sqldatabase-connectors RF-001/RF-002/RF-003:
// GET + pagination, itemsPath/contentFields mapping, header secret resolution,
// error semantics (first page fails the sync, later pages warn and keep).
public class RestApiConnectorTests
{
    private sealed class FakeApi
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public List<(string Method, string Url)> Calls { get; } = [];
        public List<(string Name, string? Value)> SeenHeaders { get; } = [];

        public FakeApi OnGet(string url, string body, HttpStatusCode status = HttpStatusCode.OK,
            string contentType = "application/json", string? retryAfter = null)
        {
            _routes[url] = _ => Response(status, body, contentType, retryAfter);
            return this;
        }

        public FakeApi OnGetSequence(string url, params (HttpStatusCode Status, string Body, string? RetryAfter)[] responses)
        {
            var i = 0;
            _routes[url] = _ =>
            {
                var (s, b, ra) = responses[Math.Min(i++, responses.Length - 1)];
                return Response(s, b, "application/json", ra);
            };
            return this;
        }

        public HttpResponseMessage Route(HttpRequestMessage request)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.ToString()));
            foreach (var h in request.Headers)
                SeenHeaders.Add((h.Key, h.Value.FirstOrDefault()));
            var url = request.RequestUri.ToString();
            return _routes.TryGetValue(url, out var handler)
                ? handler(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found", Encoding.UTF8, "text/plain")
                };
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body, string contentType, string? retryAfter)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };
            if (retryAfter is not null)
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
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

    private sealed class FakeSecrets(string? headersJson = null) : IIntegrationSecretStore
    {
        public Task<string?> GetAsync(string provider, CancellationToken ct = default) => Task.FromResult(headersJson);
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);
        public Task SetAsync(string provider, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static RestApiConnector Sut(FakeApi api, string? headersJson = null) =>
        new(new FakeFactory(new RouterHandler(api)), new FakeSecrets(headersJson),
            NullLogger<RestApiConnector>.Instance);

    private static KnowledgeSource Source(object configuration) => new()
    {
        Id = Guid.NewGuid(),
        Name = "api-src",
        SourceType = SourceType.RestApi,
        ConfigurationJson = JsonSerializer.Serialize(configuration)
    };

    private const string Base = "https://api.test/data";

    [Fact]
    public async Task RootArray_ItemsBecomeDocuments()
    {
        var api = new FakeApi()
            .OnGet(Base, """[{"id":"a","title":"Alpha","body":"alpha body"},{"id":"b","title":"Beta","body":"beta body"}]""");

        var result = await Sut(api).FetchAsync(
            Source(new { endpoint = Base, idField = "id", titleField = "title" }), CancellationToken.None);

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal("rest:a", result.Documents[0].UriReference);
        Assert.Equal("Alpha", result.Documents[0].Title);
        Assert.Contains("alpha body", result.Documents[0].TextContent);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ItemsPathAndFields_Mapped()
    {
        var api = new FakeApi().OnGet(Base,
            """{"data":{"results":[{"id":1,"title":"T","body":"B","link":"https://x/1"}]}}""");

        var result = await Sut(api).FetchAsync(Source(new
        {
            endpoint = Base,
            itemsPath = "data.results",
            titleField = "title",
            contentFields = new[] { "title", "body" },
            urlField = "link"
        }), CancellationToken.None);

        var doc = Assert.Single(result.Documents);
        Assert.Equal("T", doc.Title);
        Assert.Equal("T\n\nB", doc.TextContent);
        Assert.Equal("https://x/1", doc.UriReference);
    }

    [Fact]
    public async Task MissingItemsPath_WarnsOnce_AndSerializesBody()
    {
        var api = new FakeApi().OnGet(Base, """{"data":{"other":1},"title":"Root"}""");

        var result = await Sut(api).FetchAsync(Source(new
        {
            endpoint = Base,
            itemsPath = "data.results",
            titleField = "title"
        }), CancellationToken.None);

        Assert.Single(result.Documents);
        Assert.Equal("Root", result.Documents[0].Title);
        Assert.Single(result.Warnings);
        Assert.Contains("data.results", result.Warnings[0]);
    }

    [Fact]
    public async Task DefaultSerialization_KeyValueLines_WithCompactJson()
    {
        var api = new FakeApi().OnGet(Base, """[{"id":1,"nested":{"a":1},"flag":true,"title":"T"}]""");

        var result = await Sut(api).FetchAsync(Source(new { endpoint = Base, idField = "id" }), CancellationToken.None);

        var content = Assert.Single(result.Documents).TextContent;
        Assert.Contains("id: 1", content);
        Assert.Contains("""nested: {"a":1}""", content);
        Assert.Contains("flag: true", content);
        Assert.Contains("title: T", content);
    }

    [Fact]
    public async Task NoIdNoUrl_Sha256FallbackUri()
    {
        var api = new FakeApi().OnGet(Base, """[{"title":"T","body":"B"}]""");

        var result = await Sut(api).FetchAsync(Source(new { endpoint = Base, titleField = "title" }),
            CancellationToken.None);

        var uri = Assert.Single(result.Documents).UriReference;
        Assert.StartsWith("rest:", uri);
        Assert.Equal(64, uri["rest:".Length..].Length);
    }

    [Fact]
    public async Task Pagination_StopsOnEmptyPage()
    {
        var api = new FakeApi()
            .OnGet($"{Base}?page=1", """[{"id":"a","title":"A","body":"x"}]""")
            .OnGet($"{Base}?page=2", "[]")
            .OnGet($"{Base}?page=3", """[{"id":"c","title":"C","body":"z"}]""");

        var result = await Sut(api).FetchAsync(Source(new { endpoint = Base, pageParam = "page", maxPages = 3 }),
            CancellationToken.None);

        Assert.Single(result.Documents);
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public async Task Pagination_StopsAtMaxPages()
    {
        var api = new FakeApi()
            .OnGet($"{Base}?page=1", """[{"id":"a","title":"A","body":"x"}]""")
            .OnGet($"{Base}?page=2", """[{"id":"b","title":"B","body":"y"}]""")
            .OnGet($"{Base}?page=3", """[{"id":"c","title":"C","body":"z"}]""");

        var result = await Sut(api).FetchAsync(Source(new { endpoint = Base, pageParam = "page", maxPages = 2 }),
            CancellationToken.None);

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public async Task Pagination_ExistingQuery_UsesAmpersand()
    {
        var url = "https://api.test/data?size=50";
        var api = new FakeApi()
            .OnGet($"{url}&page=1", "[]");

        await Sut(api).FetchAsync(Source(new { endpoint = url, pageParam = "page" }), CancellationToken.None);

        Assert.Single(api.Calls);
        Assert.Equal($"{url}&page=1", api.Calls[0].Url);
    }

    [Fact]
    public async Task FirstPage401_Throws_ClearMessage_NoHeaderEcho()
    {
        var api = new FakeApi().OnGet(Base, "denied", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api, """{"Authorization":"Bearer secret-value"}""").FetchAsync(
                Source(new { endpoint = Base }), CancellationToken.None));

        Assert.Contains("inválidos", ex.Message);
        Assert.DoesNotContain("secret-value", ex.Message);
    }

    [Fact]
    public async Task FirstPage429_RetriesOnce_ThenSucceeds()
    {
        var api = new FakeApi().OnGetSequence(Base,
            (HttpStatusCode.TooManyRequests, """{"error":"slow"}""", "0"),
            (HttpStatusCode.OK, """[{"id":"a","title":"A","body":"B"}]""", null));

        var result = await Sut(api).FetchAsync(Source(new { endpoint = Base }), CancellationToken.None);

        Assert.Single(result.Documents);
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public async Task FirstPage429_RetriesOnce_StillFailing_Throws()
    {
        var api = new FakeApi().OnGetSequence(Base,
            (HttpStatusCode.TooManyRequests, "slow", "0"),
            (HttpStatusCode.TooManyRequests, "slow", "0"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api).FetchAsync(Source(new { endpoint = Base }), CancellationToken.None));

        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public async Task LaterPageFailure_WarnsAndKeepsCollected()
    {
        var api = new FakeApi()
            .OnGet($"{Base}?page=1", """[{"id":"a","title":"A","body":"x"}]""")
            .OnGet($"{Base}?page=2", "boom", HttpStatusCode.InternalServerError);

        var result = await Sut(api).FetchAsync(Source(new { endpoint = Base, pageParam = "page", maxPages = 3 }),
            CancellationToken.None);

        Assert.Single(result.Documents);
        Assert.Single(result.Warnings);
        Assert.Contains("page 2", result.Warnings[0]);
    }

    [Fact]
    public async Task NonJsonBody_ThrowsClearError()
    {
        var api = new FakeApi().OnGet(Base, "<html><body>oops</body></html>", HttpStatusCode.OK, "text/html");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api).FetchAsync(Source(new { endpoint = Base }), CancellationToken.None));

        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public async Task HeadersSecret_AppliedToRequest()
    {
        var api = new FakeApi().OnGet(Base, "[]");

        await Sut(api, """{"X-Api-Key":"k-123","Authorization":"Bearer tok"}""").FetchAsync(
            Source(new { endpoint = Base }), CancellationToken.None);

        Assert.Contains(("X-Api-Key", "k-123"), api.SeenHeaders);
        Assert.Contains(("Authorization", "Bearer tok"), api.SeenHeaders);
    }

    [Fact]
    public async Task HasHeaderConfiguredButNoSecret_Throws()
    {
        var api = new FakeApi().OnGet(Base, "[]");

        // hasKey=true persists in config; secret store returns nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api).FetchAsync(Source(new { endpoint = Base, hasKey = true }), CancellationToken.None));
    }

    [Fact]
    public async Task ItemWithoutContent_SkippedWithWarning()
    {
        var api = new FakeApi().OnGet(Base, """[{},{"id":"b","title":"B","body":"y"}]""");

        var result = await Sut(api).FetchAsync(
            Source(new { endpoint = Base, idField = "id", titleField = "title" }), CancellationToken.None);

        Assert.Single(result.Documents);
        Assert.Single(result.Warnings);
        Assert.Contains("no content", result.Warnings[0]);
    }
}
