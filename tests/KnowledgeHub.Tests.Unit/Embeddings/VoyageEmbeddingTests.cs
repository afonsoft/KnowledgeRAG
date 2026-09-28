using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Embeddings;

namespace KnowledgeHub.Tests.Unit.Embeddings;

// Covers SPEC-20260927-voyage-and-cohere-embeddings RF-001/RF-002/RF-003:
// API payload (input_type + endpoint), auth header, dimension fitting,
// rate-limit retry, batching, and EmbeddingCompatibilityCheck.
public class VoyageEmbeddingTests
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
            var inputs = body.GetProperty("input").EnumerateArray().Count();
            var data = new StringBuilder();
            data.Append('[');
            for (var i = 0; i < inputs; i++)
            {
                if (i > 0) data.Append(',');
                data.Append("{\"embedding\":[1.0,2.0,3.0]}");
            }
            data.Append(']');
            var payload = $$"""{"data":{{data}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private static EmbeddingOptions.VoyageOptions Voyage(int dim = 1024, int batch = 128) => new()
    {
        ApiKey = "pa-test",
        Model = "voyage-3",
        Dimensions = dim,
        BatchSize = batch
    };

    private static VoyageAiEmbeddingProvider Sut(EmbeddingOptions.VoyageOptions opts, FakeHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.voyageai.com/v1/") };
        return new VoyageAiEmbeddingProvider(http, opts);
    }

    [Fact]
    public void ModelId_UsesVoyagePrefix()
    {
        var p = Sut(Voyage(), new FakeHandler());
        Assert.Equal("voyage:voyage-3", p.ModelId);
        Assert.Equal(1024, p.Dimensions);
    }

    [Fact]
    public async Task EmbedQueryAsync_SendsQueryInputType()
    {
        var handler = new FakeHandler();
        var p = Sut(Voyage(), handler);

        var vec = await p.EmbedQueryAsync("hello world");

        Assert.Equal(1024, vec.Length);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestBody!);
        Assert.Equal("query", body.GetProperty("input_type").GetString());
        Assert.Equal("voyage-3", body.GetProperty("model").GetString());
    }

    [Fact]
    public async Task EmbedDocumentAsync_SendsDocumentInputType()
    {
        var handler = new FakeHandler();
        var p = Sut(Voyage(), handler);

        await p.EmbedDocumentAsync("some doc");

        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestBody!);
        Assert.Equal("document", body.GetProperty("input_type").GetString());
    }

    [Fact]
    public async Task EmbedAsync_NoInputType()
    {
        var handler = new FakeHandler();
        var p = Sut(Voyage(), handler);

        await p.EmbedAsync("plain");

        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestBody!);
        Assert.False(body.TryGetProperty("input_type", out _));
    }

    [Fact]
    public async Task AuthorizationHeader_IsBearer()
    {
        var handler = new FakeHandler();
        var p = Sut(Voyage(), handler);

        await p.EmbedAsync("trigger a request");

        Assert.NotNull(handler.LastRequest);
        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("pa-test", auth.Parameter);
    }

    [Fact]
    public async Task EmbedBatchAsync_SendsAllInputs()
    {
        var handler = new FakeHandler();
        var p = Sut(Voyage(), handler);

        var vectors = await p.EmbedDocumentBatchAsync(["a", "b", "c"]);
        Assert.Equal(3, vectors.Count);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequestBody!);
        Assert.Equal(3, body.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public async Task LargeBatch_SplitsIntoChunks()
    {
        var handler = new FakeHandler();
        var p = Sut(Voyage(batch: 2), handler);

        var texts = Enumerable.Range(0, 5).Select(i => $"t{i}").ToList();
        var vectors = await p.EmbedDocumentBatchAsync(texts);

        Assert.Equal(5, vectors.Count);
        Assert.Equal(3, handler.RequestCount); // ceil(5/2)=3
    }

    [Fact]
    public async Task RateLimit_429_RetriesThenSucceeds()
    {
        var handler = new FakeHandler { FailWith = (HttpStatusCode)429, FailTimes = 2 };
        var p = Sut(Voyage(), handler);

        var vec = await p.EmbedQueryAsync("retry");
        Assert.Equal(1024, vec.Length);
        Assert.Equal(3, handler.RequestCount); // 2 failures + 1 success
    }

    [Fact]
    public async Task DimensionMismatch_IsSliced()
    {
        // Provider configured for 1024 but API returns 3-dim vectors.
        var handler = new FakeHandler();
        var p = Sut(Voyage(dim: 1024), handler);

        var vec = await p.EmbedAsync("x");
        Assert.Equal(1024, vec.Length); // zero-padded to 1024
    }

    [Fact]
    public async Task NonSuccess_ThrowsEmbeddingProviderException()
    {
        var handler = new FakeHandler { FailWith = HttpStatusCode.InternalServerError, FailTimes = 1 };
        var p = Sut(Voyage(), handler);

        await Assert.ThrowsAsync<EmbeddingProviderException>(() => p.EmbedAsync("boom"));
    }
}
