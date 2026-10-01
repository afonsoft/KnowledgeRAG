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
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Mcp.Bridge;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.Telemetry;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using KnowledgeHub.Tests.Unit.Fakes;
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

    private static List<Activity> CollectActivities(Action action) =>
        CollectActivities(action, out _);

    private static List<Activity> CollectActivities(Action action, out ActivitySpanId rootSpanId)
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
        // SPEC-20260928-test-reliability-and-coverage-gate RF-001: the listener
        // is process-wide — scope collection to this test's trace tree or
        // parallel tests' spans on the shared hub source leak into the list.
        using var root = KnowledgeHubActivity.Source.StartActivity("test.scope");
        action();
        rootSpanId = root?.SpanId ?? default;
        var traceId = root?.TraceId;
        return traceId is null
            ? []
            : activities.Where(a => a.TraceId == traceId.Value).ToList();
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
        var search = new SearchService(new KnowledgeHub.Server.Services.SearchRetrievalDeps(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb), new StubVectorStore(new VectorHit(chunk.Id, 0.9)), new DisabledLexical(), cache), new KnowledgeHub.Server.Services.SearchPipelineDeps(new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance), NoOpReranker.Instance, new UnrestrictedScope(), Search.FakeGraphSettings.Enabled), new ConfigurationBuilder().Build(), NullLogger<SearchService>.Instance);

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
        Assert.False((bool)durations[0].Tags["cache_hit"]!);
        Assert.True((bool)durations[1].Tags["cache_hit"]!);

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
            new AgentOptions(), new AgentDiagnostics(null, null, null),
            NullLogger<AgentService>.Instance);

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
        var search = new SearchService(new KnowledgeHub.Server.Services.SearchRetrievalDeps(db, emb, new Fakes.FixedEmbeddingProviderResolver(emb), new StubVectorStore(new VectorHit(chunk.Id, 0.9)), new DisabledLexical(), cache), new KnowledgeHub.Server.Services.SearchPipelineDeps(new PassthroughRewriter(), NoOpExpander.Instance, new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance), NoOpReranker.Instance, new UnrestrictedScope(), Search.FakeGraphSettings.Enabled), new ConfigurationBuilder().Build(), NullLogger<SearchService>.Instance);

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

    // SPEC-20260928-observability-followups AC-2/AC-3: the new arms emit
    // spans + counters — temporal graph query (mode tag) and live-action
    // bridge execution (tool/outcome tags).
    [Fact]
    public async Task TemporalGraphQuery_EmitsSpanAndCounter()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        await using var db = new KnowledgeHubDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var retriever = new TemporalGraphRetriever(db,
            new GraphEntityLinker(db, NullLogger<GraphEntityLinker>.Instance),
            new UnrestrictedScope(), NullLogger<TemporalGraphRetriever>.Instance);

        var metrics = CollectMetrics(() =>
            retriever.SearchEpisodeContextAsync(Guid.NewGuid().ToString("N"))
                .GetAwaiter().GetResult());
        var activities = CollectActivities(() =>
            retriever.SearchEpisodeContextAsync(Guid.NewGuid().ToString("N"))
                .GetAwaiter().GetResult());

        Assert.Contains(metrics, m =>
            m.Instrument == "knowledgehub.graph.temporal_queries"
            && m.Tags.TryGetValue("mode", out var mode)
            && (mode as string) == "episode");
        Assert.Single(activities, a =>
            a.OperationName == "search.temporal_graph"
            && a.TagObjects.Any(t => t.Key == "mode" && (t.Value as string) == "episode"));
    }

    [Fact]
    public async Task LiveActions_EmitsSpanAndExecutionCounter()
    {
        var tool = new CatalogTool
        {
            Name = "tavily_search",
            Description = "t",
            InputSchema = JsonNode.Parse(
                """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""")!.AsObject(),
            ReadOnly = true,
            Handler = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = "live-data" }]
            })
        };
        var hit = new SearchResultItem
        {
            ChunkText = "cheque <!-- mcp-tool: tavily_search query=\"cotação\" --> agora",
            DocumentTitle = "d",
            SourceName = "s",
            SourceId = Guid.NewGuid(),
            Score = 0.9,
            UriReference = "u",
            ChunkId = Guid.NewGuid()
        };
        var ctx = new ToolCallContext { Services = null! };

        var metrics = CollectMetrics(() =>
            McpDynamicRagActionBridge.ExecuteAsync("q", [hit], [tool], ctx, 3,
                allowDocumentMarkers: true, CancellationToken.None)
                .GetAwaiter().GetResult());
        var activities = CollectActivities(() =>
            McpDynamicRagActionBridge.ExecuteAsync("q", [hit], [tool], ctx, 3,
                allowDocumentMarkers: true, CancellationToken.None)
                .GetAwaiter().GetResult());

        Assert.Contains(metrics, m =>
            m.Instrument == "knowledgehub.live_tool.executions"
            && (m.Tags.TryGetValue("tool", out var tn) && (tn as string) == "tavily_search")
            && (m.Tags.TryGetValue("outcome", out var oc) && (oc as string) == "success"));
        Assert.Single(activities, a => a.OperationName == "search.live_actions");
    }

    // SPEC-20260929-observability-and-tests-residual RF-002/AC-2: an in-band
    // failure (IsError=true result, no exception) must still mark the span.
    [Fact]
    public async Task LiveActions_IsErrorResult_MarksSpanError()
    {
        var tool = new CatalogTool
        {
            Name = "tavily_search",
            Description = "t",
            InputSchema = JsonNode.Parse(
                """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""")!.AsObject(),
            ReadOnly = true,
            Handler = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "upstream 500" }]
            })
        };
        var hit = new SearchResultItem
        {
            ChunkText = "cheque <!-- mcp-tool: tavily_search query=\"x\" -->",
            DocumentTitle = "d",
            SourceName = "s",
            SourceId = Guid.NewGuid(),
            Score = 0.9,
            UriReference = "u",
            ChunkId = Guid.NewGuid()
        };
        var ctx = new ToolCallContext { Services = null! };

        var activities = CollectActivities(() =>
            McpDynamicRagActionBridge.ExecuteAsync("q", [hit], [tool], ctx, 3,
                allowDocumentMarkers: true, CancellationToken.None)
                .GetAwaiter().GetResult());

        var span = Assert.Single(activities, a => a.OperationName == "search.live_actions");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    // SPEC-20260929-observability-and-tests-residual RF-003: a swallowed
    // evidence-emission failure must still mark the evidence.emit span.
    [Fact]
    public async Task EvidenceEmit_Failure_MarksSpanError()
    {
        var failing = new FailingEvidenceChain();
        var chunks = new List<SearchResultItem>
        {
            new()
            {
                ChunkText = "c", DocumentTitle = "d", SourceName = "s",
                SourceId = Guid.NewGuid(), Score = 0.9, UriReference = "u",
                ChunkId = Guid.NewGuid()
            }
        };

        var activities = CollectActivities(() =>
            KnowledgeHub.Server.Audit.Evidence.EvidenceEmission.RecordAskAsync(
                new KnowledgeHub.Server.Audit.Evidence.EvidenceEmission.EmissionContext(
                    failing, "s1", null, null), "q", chunks, "a", CancellationToken.None)
                .GetAwaiter().GetResult());

        var span = Assert.Single(activities, a => a.OperationName == "evidence.emit");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    private sealed class FailingEvidenceChain : KnowledgeHub.Server.Audit.Evidence.IEvidenceChainService
    {
        public string KeyId => "test";
        public Task<EvidenceReceipt> AppendAsync(
            KnowledgeHub.Server.Audit.Evidence.EvidenceEvent ev, CancellationToken ct) =>
            throw new InvalidOperationException("store down");
        public Task<IReadOnlyList<EvidenceReceipt>> GetSessionReceiptsAsync(
            string sessionId, CancellationToken ct) =>
            throw new InvalidOperationException("store down");
        public Task<KnowledgeHub.Server.Audit.Evidence.EvidenceVerification> VerifyAsync(
            string sessionId, CancellationToken ct) =>
            throw new InvalidOperationException("store down");
        public Task<KnowledgeHub.Server.Audit.Evidence.EvidenceVerification> VerifyReceiptsAsync(
            IReadOnlyList<KnowledgeHub.Server.Domain.Entities.EvidenceReceipt> receipts,
            CancellationToken ct) =>
            throw new InvalidOperationException("store down");
        public Task<string> SignPayloadAsync(string payload, CancellationToken ct) =>
            throw new InvalidOperationException("store down");
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

}
