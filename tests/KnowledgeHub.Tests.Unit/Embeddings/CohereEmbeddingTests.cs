using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Embeddings;

namespace KnowledgeHub.Tests.Unit.Embeddings;

// Covers SPEC-20260927-voyage-and-cohere-embeddings RF-002/RF-003:
// API payload (input_type + endpoint /v2/embed), auth header, dimension
// fitting, rate-limit retry, and batching.
public class CohereEmbeddingTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        public int RequestCount;
        public HttpStatusCode FailWith { get; set; }
        public int FailTimes { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            RequestCount++;

            if (FailWith != default && FailTimes-- > 0)
                return new HttpResponseMessage(FailWith);

            var body = JsonSerializer.Deserialize<JsonElement>(LastRequestBody!);
            var inputs = body.GetProperty("texts").EnumerateArray().Count();
            var embeddings = new StringBuilder();
            embeddings.Append('[');
            for (var i = 0; i < inputs; i++)
            {
                if (i > 0) embeddings.Append(',');
                embeddings.Append("[1.0,2.0,3.0]");
            }
            embeddings.Append(']');
            var payload = $$"""{"embeddings":{{embeddings}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private static EmbeddingOptions.CohereOptions Cohere(int dim = 1024, int batch = 96) => new()
    {
        ApiKey = "co-test",
        Model = "embed-multilingual-v3.0",
        Dimensions = dim,
        BatchSize = batch
    };

    private static CohereEmbeddingProvider Sut(EmbeddingOptions.CohereOptions opts, FakeHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.cohere.com/v2/") };
        return new CohereEmbeddingProvider(http, opts);
    }

    [Fact]
    public void ModelId_UsesCoherePrefix()
    {
        var p = Sut(Cohere(), new FakeHandler());
        Assert.Equal("cohere:embed-multilingual-v3.0", p.ModelId);
        Assert.Equal(1024, p.Dimensions);
    }

    [Fact]
    public async Task EmbedQueryAsync_SendsSearchQueryInputType()
    {
        var handler = new FakeHandler();
        var p = Sut(Cohere(), handler);

        await p.EmbedQueryAsync("hello");

        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestBody!);
        Assert.Equal("search_query", body.GetProperty("input_type").GetString());
        Assert.Equal("embed-multilingual-v3.0", body.GetProperty("model").GetString());
        Assert.EndsWith("/v2/embed", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task EmbedDocumentAsync_SendsSearchDocumentInputType()
    {
        var handler = new FakeHandler();
        var p = Sut(Cohere(), handler);

        await p.EmbedDocumentAsync("doc");

        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestBody!);
        Assert.Equal("search_document", body.GetProperty("input_type").GetString());
    }

    [Fact]
    public async Task AuthorizationHeader_IsBearer()
    {
        var handler = new FakeHandler();
        var p = Sut(Cohere(), handler);

        await p.EmbedAsync("trigger a request");

        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("co-test", auth.Parameter);
    }

    [Fact]
    public async Task EmbedDocumentBatchAsync_SendsAllTexts()
    {
        var handler = new FakeHandler();
        var p = Sut(Cohere(), handler);

        var vectors = await p.EmbedDocumentBatchAsync(["a", "b"]);
        Assert.Equal(2, vectors.Count);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestBody!);
        Assert.Equal(2, body.GetProperty("texts").GetArrayLength());
    }

    [Fact]
    public async Task LargeBatch_SplitsIntoChunks()
    {
        var handler = new FakeHandler();
        var p = Sut(Cohere(batch: 2), handler);

        var texts = Enumerable.Range(0, 5).Select(i => $"t{i}").ToList();
        var vectors = await p.EmbedDocumentBatchAsync(texts);

        Assert.Equal(5, vectors.Count);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task RateLimit_429_RetriesThenSucceeds()
    {
        var handler = new FakeHandler { FailWith = (HttpStatusCode)429, FailTimes = 1 };
        var p = Sut(Cohere(), handler);

        var vec = await p.EmbedQueryAsync("retry");
        Assert.Equal(1024, vec.Length);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DimensionMismatch_IsSliced()
    {
        var handler = new FakeHandler();
        var p = Sut(Cohere(dim: 1024), handler);

        var vec = await p.EmbedAsync("x");
        Assert.Equal(1024, vec.Length);
    }
}
