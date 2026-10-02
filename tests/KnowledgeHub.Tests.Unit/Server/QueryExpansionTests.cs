using KnowledgeHub.Server.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// SPEC-20260924-query-expansion-hyde: multi-list RRF and the LLM expander's
/// parse/cache/fallback behavior.
/// </summary>
public sealed class QueryExpansionTests
{
    // -------------------------------------------------------------------------
    // Multi-list RRF
    // -------------------------------------------------------------------------

    [Fact]
    public void Fuse_MultipleLists_AccumulatesAcrossAll()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var lists = new List<(string Arm, IReadOnlyList<Guid> Ranked)>
        {
            ("vector",  [a, b]),   // vector: a top1, b top2
            ("lexical", [c, a]),   // lexical: c top1, a top2
            ("vector",  [b, c])    // second vector list (variant): b top1, c top2
        };

        var fused = RrfFuser.Fuse(lists, 10);

        // a: 1/61 + 1/62 ≈ 0.0325; b: 1/62 + 1/61 ≈ 0.0325 — a wins by id order? no —
        // recompute: a=1/61+1/62, b=1/62+1/61, c=1/61+1/62 — all tie; order by id.
        Assert.Equal(3, fused.Count);
        // best rank recorded per arm
        var fa = fused.Single(f => f.ChunkId == a);
        Assert.Equal(1, fa.VectorRank);
        Assert.Equal(2, fa.LexicalRank);
    }

    [Fact]
    public void Fuse_MultiList_BestRankKept()
    {
        var x = Guid.NewGuid(); var y = Guid.NewGuid();
        var fused = RrfFuser.Fuse(
            [("vector", new Guid[] { x, y }), ("vector", new Guid[] { y, x })], 5);
        // x ranks 1 and 2 on vector → best = 1; y ranks 2 and 1 → best = 1
        Assert.Equal(1, fused.Single(f => f.ChunkId == x).VectorRank);
        Assert.Equal(1, fused.Single(f => f.ChunkId == y).VectorRank);
    }

    // -------------------------------------------------------------------------
    // LlmQueryExpander
    // -------------------------------------------------------------------------

    private sealed class StubChatClient(string? reply) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply ?? "")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubServices(object? chat) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IChatClient) ? chat : null;
    }

    private static LlmQueryExpander Sut(object? chat, IDistributedCache? cache = null) =>
        new(new StubServices(chat),
            cache ?? new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            new ConfigurationBuilder().Build(),
            NullLogger<LlmQueryExpander>.Instance);

    [Fact]
    public async Task ExpandQueriesAsync_ValidJson_ReturnsVariants()
    {
        var chat = new StubChatClient("""["alt one","alt two","the original query"]""");
        var sut = Sut(chat);

        var variants = await sut.ExpandQueriesAsync("the original query", 3);

        Assert.Equal(2, variants.Count); // original filtered out
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task ExpandQueriesAsync_InvalidJson_FallsBackEmpty()
    {
        var chat = new StubChatClient("I cannot produce JSON");
        var variants = await Sut(chat).ExpandQueriesAsync("q", 3);
        Assert.Empty(variants);
    }

    [Fact]
    public async Task ExpandQueriesAsync_Cached_SkipsLlm()
    {
        var chat = new StubChatClient("""["a","b"]""");
        var sut = Sut(chat);

        await sut.ExpandQueriesAsync("q", 3);
        var second = await sut.ExpandQueriesAsync("q", 3);

        Assert.Equal(2, second.Count);
        Assert.Equal(1, chat.Calls); // cache hit
    }

    [Fact]
    public async Task ExpandQueriesAsync_NoChatClient_ReturnsEmpty()
    {
        var variants = await Sut(null).ExpandQueriesAsync("q", 3);
        Assert.Empty(variants);
    }

    [Fact]
    public async Task GenerateHypotheticalAsync_ReturnsAndCachesDoc()
    {
        var chat = new StubChatClient("A hypothetical passage about the topic.");
        var sut = Sut(chat);

        var first = await sut.GenerateHypotheticalAsync("q");
        var second = await sut.GenerateHypotheticalAsync("q");

        Assert.Equal("A hypothetical passage about the topic.", first);
        Assert.Equal(first, second);
        Assert.Equal(1, chat.Calls);
    }
}
