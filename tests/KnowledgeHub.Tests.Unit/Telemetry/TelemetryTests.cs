using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using KnowledgeHub.McpEngine.Activity;
using KnowledgeHub.Server.Agent;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Evaluation;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.Telemetry;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Tests.Unit.Telemetry;

/// <summary>
/// SPEC-20260923-observability-metrics: instrument emission via MeterListener /
/// ActivityListener, cache hit/miss counters, agent span tree, error status on
/// LLM failure, and the tag allowlist audit (no PII/query text in telemetry).
/// </summary>
[Collection("SearchTelemetry")]
public sealed class TelemetryTests
{
    private sealed record MetricSample(string Instrument, double Value, Dictionary<string, object?> Tags);

    private static List<MetricSample> CollectMetrics(Action action)
    {
        var samples = new List<MetricSample>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KnowledgeHubMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            samples.Add(new MetricSample(instrument.Name, value, tags.ToArray()
                .ToDictionary(t => t.Key, t => t.Value))));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            samples.Add(new MetricSample(instrument.Name, value, tags.ToArray()
                .ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();
        action();
        return samples;
    }

    private static List<Activity> CollectActivities(Action action)
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == KnowledgeHubMetrics.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) activities.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        action();
        return activities;
    }

    [Fact]
    public async Task Search_RecordsDuration_WithModeAndCacheHitTags()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault, IsActive = true };
        var doc = new KnowledgeDocument { Title = "d", UriReference = "u", KnowledgeSourceId = source.Id };
        var chunk = new DocumentChunk { KnowledgeDocumentId = doc.Id, ChunkIndex = 0, TextContent = "text" };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var emb = new StubEmbeddings();
        var search = new SearchService(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb),
            new StubVectorStore(new VectorHit(chunk.Id, 0.9)), new DisabledLexical(), cache,
            new ConfigurationBuilder().Build(), new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance),
            NoOpReranker.Instance, new UnrestrictedScope(), Search.FakeGraphSettings.Enabled, NullLogger<SearchService>.Instance);

        var samples = CollectMetrics(() =>
        {
            search.SearchAsync("q", 5, mode: SearchMode.Semantic).GetAwaiter().GetResult();
            search.SearchAsync("q", 5, mode: SearchMode.Semantic).GetAwaiter().GetResult();
        });

        var durations = samples.Where(s => s.Instrument == "knowledgehub.search.duration").ToList();
        Assert.Equal(2, durations.Count);
        Assert.All(durations, d =>
        {
            Assert.Equal("semantic", d.Tags["mode"]);
            Assert.True(d.Value >= 0);
        });
        Assert.Equal(false, durations[0].Tags["cache_hit"]);
        Assert.Equal(true, durations[1].Tags["cache_hit"]);

        Assert.Contains(samples, s =>
            s.Instrument == "knowledgehub.vector_search.duration" &&
            s.Tags.ContainsKey("store"));
        Assert.Contains(samples, s =>
            s.Instrument == "knowledgehub.embedding.duration" &&
            Equals(s.Tags["model"], "fake:4"));
    }

    [Fact]
    public async Task SafeCache_RecordsHitsAndMisses_ByRegion()
    {
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        await cache.SetStringAsync("emb:hit", "v");

        var samples = CollectMetrics(() =>
        {
            SafeCache.GetStringAsync(cache, "emb:hit", NullLogger.Instance).GetAwaiter().GetResult();
            SafeCache.GetStringAsync(cache, "search:miss", NullLogger.Instance).GetAwaiter().GetResult();
        });

        var hits = samples.Where(s => s.Instrument == "knowledgehub.cache.hits").ToList();
        var misses = samples.Where(s => s.Instrument == "knowledgehub.cache.misses").ToList();
        Assert.Contains(hits, h => Equals(h.Tags["region"], "embedding"));
        Assert.Contains(misses, m => Equals(m.Tags["region"], "search"));
    }

    [Fact]
    public void McpRequestMetrics_RecordsPerMethodAndSessionMode()
    {
        IMcpRequestMetrics metrics = new KnowledgeHubMetrics();
        var samples = CollectMetrics(() =>
        {
            metrics.Record("tools/list", "stateful", true);
            metrics.Record("tools/call", "stateless", false);
        });

        var requests = samples.Where(s => s.Instrument == "knowledgehub.mcp.requests").ToList();
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, r =>
            Equals(r.Tags["method"], "tools/list") && Equals(r.Tags["session_mode"], "stateful"));
        Assert.Contains(requests, r =>
            Equals(r.Tags["method"], "tools/call") && Equals(r.Tags["succeeded"], false));
    }

    [Fact]
    public async Task Agent_EmitsSpanTree_IterationAndToolChildren()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var tool = new CatalogTool
        {
            Name = "echo_tool",
            Description = "echo",
            InputSchema = new JsonObject { ["type"] = "object" },
            ReadOnly = true,
            Handler = (_, _) => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "ok" }]
            })
        };
        var agent = new AgentService(new TwoTurnChatClient(),
            new ServiceCollection().BuildServiceProvider(), new StubCatalog(tool), db,
            new AgentOptions(), null, NullLogger<AgentService>.Instance);

        var activities = CollectActivities(() =>
            agent.RunAsync(new AgentRequest { Prompt = "hi" }).GetAwaiter().GetResult());

        var root = activities.Single(a => a.OperationName == "agent_chat");
        var iterations = activities.Where(a => a.OperationName == "agent_iteration").ToList();
        var toolSpans = activities.Where(a => a.OperationName == "tool").ToList();

        Assert.Equal(2, iterations.Count);
        Assert.Single(toolSpans);
        Assert.All(iterations, i => Assert.Equal(root.SpanId, i.ParentSpanId));
        Assert.Equal(root.SpanId, toolSpans[0].ParentSpanId);
        Assert.Equal("echo_tool", toolSpans[0].TagObjects
            .First(t => t.Key == "tool.name").Value);
    }

    [Fact]
    public async Task FailingLlm_SpanCarriesErrorStatus()
    {
        var svc = new AnswerService(new ThrowingChatClient(),
            new ChatProviderOptions { Provider = "ollama", Model = "m" },
            new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            new ConfigurationBuilder().Build(), NullLogger<AnswerService>.Instance,
            NoopRagEvaluationEnqueuer.Instance);

        var activities = CollectActivities(() =>
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.AnswerAsync("q", [Hit()])).GetAwaiter().GetResult());

        var llm = activities.Single(a => a.OperationName == "llm_synthesis");
        Assert.Equal(ActivityStatusCode.Error, llm.Status);
    }

    [Fact]
    public async Task EmittedTagKeys_StayWithinAllowlist()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var source = new KnowledgeSource { Name = "s", SourceType = SourceType.ObsidianVault, IsActive = true };
        var doc = new KnowledgeDocument { Title = "d", UriReference = "u", KnowledgeSourceId = source.Id };
        var chunk = new DocumentChunk { KnowledgeDocumentId = doc.Id, ChunkIndex = 0, TextContent = "text" };
        db.Sources.Add(source);
        db.Documents.Add(doc);
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var emb = new StubEmbeddings();
        var search = new SearchService(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb),
            new StubVectorStore(new VectorHit(chunk.Id, 0.9)), new DisabledLexical(), cache,
            new ConfigurationBuilder().Build(), new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance),
            NoOpReranker.Instance, new UnrestrictedScope(), Search.FakeGraphSettings.Enabled, NullLogger<SearchService>.Instance);

        var samples = CollectMetrics(() =>
            search.SearchAsync("sensitive user query", 5, mode: SearchMode.Semantic)
                .GetAwaiter().GetResult());
        var activities = CollectActivities(() =>
            search.SearchAsync("sensitive user query", 5, mode: SearchMode.Semantic)
                .GetAwaiter().GetResult());

        Assert.All(samples.SelectMany(s => s.Tags.Keys), key =>
            Assert.Contains(key, TelemetryTags.AllowedMetricKeys));
        Assert.All(activities.SelectMany(a => a.TagObjects.Select(t => t.Key)), key =>
            Assert.Contains(key, TelemetryTags.AllowedActivityKeys));
    }

    [Fact]
    public void TelemetryOptions_ZeroConfig_DisablesExporters()
    {
        var options = TelemetryOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Null(options.OtlpEndpoint);
        Assert.False(options.Prometheus);
    }

    private static SearchResultItem Hit() => new()
    {
        ChunkText = "ctx",
        DocumentTitle = "Doc",
        SourceName = "src",
        SourceId = Guid.NewGuid(),
        SourceType = SourceType.WebPage,
        Score = 0.9,
        UriReference = "uri-1"
    };

    private sealed class StubEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
    }

    private sealed class StubVectorStore(VectorHit hit) : IVectorStore
    {
        public Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector,
            string model, IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryVector, string model, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([hit]);
    }

    private sealed class DisabledLexical : ILexicalSearchService
    {
        public bool Enabled => false;
        public Task<IReadOnlyList<LexicalHit>> SearchAsync(string query, int topK,
            IReadOnlyCollection<Guid>? sourceIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LexicalHit>>([]);
        public Task ReconcileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PassthroughRewriter : IQueryRewriter
    {
        public Task<string> RewriteAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(query);
    }

    private sealed class UnrestrictedScope : KnowledgeHub.Server.Auth.ICallerScopeProvider
    {
        public Task<KnowledgeHub.Server.Auth.CallerScope> GetAsync(CancellationToken ct) =>
            Task.FromResult(KnowledgeHub.Server.Auth.CallerScope.Unrestricted);
    }

    /// <summary>First call requests echo_tool; second answers with text.</summary>
    private sealed class TwoTurnChatClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "echo_tool", new Dictionary<string, object?>())])));
            }
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "final answer"))
            { ModelId = "stub" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("llm down");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubCatalog(CatalogTool tool) : IDynamicToolCatalog
    {
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogTool>>([tool]);
        public Task<IReadOnlyList<CatalogTool>> GetUnfilteredToolsAsync(IServiceProvider services, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogTool>>([tool]);
    }

    private sealed class NoopRagEvaluationEnqueuer : IRagEvaluationEnqueuer
    {
        public static readonly NoopRagEvaluationEnqueuer Instance = new();
        public System.Threading.Channels.ChannelReader<KnowledgeHub.Server.Evaluation.RagEvaluationTask> Reader { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<KnowledgeHub.Server.Evaluation.RagEvaluationTask>().Reader;
        public bool TryEnqueue(string queryId, string question, IReadOnlyList<string> contextChunks, string answer) => false;
    }
}
