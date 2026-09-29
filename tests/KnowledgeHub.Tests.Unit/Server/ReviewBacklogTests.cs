using System.Text.Json;
using KnowledgeHub.Server.Agent;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Health;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Security;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260926-review-backlog-remediation — fixes verified per RF.
[Collection("SearchTelemetry")]
public sealed class ReviewBacklogTests
{
    private static (SqliteConnection, DbContextOptions<KnowledgeHubDbContext>) NewDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        using var ctx = new KnowledgeHubDbContext(opts);
        ctx.Database.EnsureCreated();
        return (conn, opts);
    }

    // ---------- RF-101: tool results reach the provider ----------

    [Fact]
    public void ChatClients_ResultText_SerializesFunctionResult()
    {
        var result = new FunctionResultContent("call-1", "the actual payload");
        Assert.Equal("the actual payload", OpenAiChatClient.ResultText(result));
        Assert.Equal("the actual payload", OllamaChatClient.ResultText(result));

        var structured = new FunctionResultContent("call-2", new { value = 42 });
        Assert.Contains("42", OpenAiChatClient.ResultText(structured));
    }

    // ---------- RF-104 + RF-103 + RF-102: agent resume ----------

    private sealed class ScriptedChatClient(Queue<ChatResponse> responses) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(responses.Dequeue());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeCatalog(List<CatalogTool> tools) : IDynamicToolCatalog
    {
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogTool>>(tools);
        public Task<IReadOnlyList<CatalogTool>> GetUnfilteredToolsAsync(IServiceProvider services, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CatalogTool>>(tools);
    }

    [Fact]
    public async Task Resume_ExecutesSiblingCalls_AndPersistsThread()
    {
        var (conn, opts) = NewDb();
        await using var _ = conn;
        var services = new ServiceCollection().BuildServiceProvider();

        var siblingRan = 0;
        var tools = new List<CatalogTool>
        {
            new()
            {
                Name = "gated_write", Description = "d", InputSchema = new System.Text.Json.Nodes.JsonObject(),
                ReadOnly = false,
                Handler = (_, _) => new ValueTask<CallToolResult>(
                    new CallToolResult { Content = [new TextContentBlock { Text = "wrote" }] })
            },
            new()
            {
                Name = "sibling_read", Description = "d", InputSchema = new System.Text.Json.Nodes.JsonObject(),
                ReadOnly = true,
                Handler = (_, _) =>
                {
                    siblingRan++;
                    return new ValueTask<CallToolResult>(
                        new CallToolResult { Content = [new TextContentBlock { Text = "sibling-data" }] });
                }
            }
        };

        var responses = new Queue<ChatResponse>([
            // turn 1: model calls both tools — the gated one suspends the loop
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("c1", "gated_write", new Dictionary<string, object?> { ["x"] = 1 }),
                new FunctionCallContent("c2", "sibling_read", new Dictionary<string, object?>())
            ])),
            // post-resume: model answers from the tool results
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "all done"))
        ]);

        await using var db = new KnowledgeHubDbContext(opts);
        var thread = new ConversationThread { Title = "t" };
        db.Threads.Add(thread);
        await db.SaveChangesAsync();

        var agent = new AgentService(
            new ScriptedChatClient(responses), services,
            new FakeCatalog(tools), db,
            new AgentOptions { RequireApprovalFor = ["*"] },
            feed: null, NullLogger<AgentService>.Instance);

        var request = new AgentRequest { Prompt = "do it", AllowWrite = true, ThreadId = thread.Id };
        var first = await agent.RunAsync(request);
        Assert.NotNull(first.AwaitingApprovalId);
        Assert.Equal(0, siblingRan); // sibling suspended with the gated call

        var approval = await db.Approvals.FirstAsync(a => a.Id == first.AwaitingApprovalId);
        approval.Status = "approved";
        await db.SaveChangesAsync();

        var resumed = await agent.ResumeAsync(approval.Id);
        Assert.Equal(1, siblingRan); // RF-104: sibling answered on resume
        Assert.Equal(thread.Id, resumed.ThreadId); // RF-103
        Assert.Contains(resumed.Steps, s => s.Tool == "sibling_read");
        Assert.True(await db.ThreadMessages.AnyAsync(m => m.ThreadId == thread.Id)); // RF-103

        // RF-102: a second resume is refused
        await Assert.ThrowsAsync<KnowledgeHub.Server.Api.ConflictException>(() => agent.ResumeAsync(approval.Id));
    }

    // ---------- RF-207 + RF-201: chunker guard + transactional replace ----------

    [Fact]
    public void Chunker_MaxTokensZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MarkdownChunker.Chunk("some text", maxTokens: 0, overlapTokens: 0));
    }

    private sealed class ChunkingSettings(int maxTokens, int overlap) : IEmbeddingSettingsService
    {
        public EmbeddingOptions GetEffectiveOptions() => new() { Provider = "deterministic", Dimensions = 384 };
        public (int, int) GetChunking() => (maxTokens, overlap);
        public Task<EmbeddingSettingsDto> DescribeAsync(CancellationToken ct = default) =>
            Task.FromResult<EmbeddingSettingsDto>(null!);
        public Task SaveAsync(SaveEmbeddingSettingsRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task RemoveKeyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private sealed class NoopSanitizer : IContentSanitizer
    {
        public IReadOnlyList<string> Scan(string text) => [];
    }

    private sealed class FakeGraphSettings : IGraphSettingsService
    {
        public GraphSettingsSnapshot GetEffective() => new(false, 0, 0, 0, "default");
        public Task<GraphSettingsDto> DescribeAsync(CancellationToken ct = default) =>
            Task.FromResult<GraphSettingsDto>(null!);
        public Task SaveAsync(SaveGraphSettingsRequest request, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private sealed class FakeDistCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _store = new();
        public byte[]? Get(string key) => _store.GetValueOrDefault(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        { _store[key] = value; return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default)
        { _store.Remove(key); return Task.CompletedTask; }
    }

    private sealed class FakeEmbedder : IEmbeddingProvider
    {
        public string ModelId => "fake:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new[] { 1f, 0f, 0f, 0f });
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new[] { 1f, 0f, 0f, 0f }).ToList());
    }

    [Fact]
    public async Task Reindex_ChunkerFailure_PreservesExistingChunks()
    {
        // Shared-cache memory db — the service's scoped DbContext opens its
        // OWN connection; :memory: would give each connection a private db.
        var conn = new SqliteConnection($"Data Source=kh_t_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        conn.Open();
        var opts = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;
        using (var init = new KnowledgeHubDbContext(opts))
            init.Database.EnsureCreated();
        await using var _ = conn;

        var vault = Directory.CreateTempSubdirectory("kh-vault-");
        try
        {
            File.WriteAllText(Path.Join(vault.FullName, "note.md"), "# changed content\n\nnew body");

            Guid docId;
            await using (var db = new KnowledgeHubDbContext(opts))
            {
                var src = new KnowledgeSource
                {
                    Name = "v",
                    SourceType = SourceType.ObsidianVault,
                    ConfigurationJson = JsonSerializer.Serialize(new { path = vault.FullName })
                };
                db.Sources.Add(src);
                var doc = new KnowledgeDocument
                {
                    KnowledgeSourceId = src.Id,
                    Title = "note",
                    UriReference = "note.md",
                    ContentHash = "stale-hash",
                    Chunks = [new DocumentChunk { ChunkIndex = 0, TextContent = "old content" }]
                };
                db.Documents.Add(doc);
                await db.SaveChangesAsync();
                docId = doc.Id;
            }

            var sc = new ServiceCollection();
            sc.AddSingleton(opts);
            sc.AddDbContext<KnowledgeHubDbContext>(b => b.UseSqlite(conn.ConnectionString));
            var sp = sc.BuildServiceProvider();

            var ingestion = new IngestionService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                new FakeEmbedder(),
                new ConfigurationBuilder().Build(),
                connectors: [],
                new FakeDistCache(),
                new NoopSanitizer(),
                new FakeGraphSettings(),
                // RF-207 in action: MaxTokens=0 now throws inside the chunker —
                // and RF-201's transaction must preserve the old chunks.
                new ChunkingSettings(0, 0),
                NullLogger<IngestionService>.Instance);

            var srcId = await db_src(opts);
            await ingestion.SyncAsync(srcId, new SyncOptions { ForceReindex = true });

            await using (var check = new KnowledgeHubDbContext(opts))
            {
                var chunks = await check.Chunks.Where(c => c.KnowledgeDocumentId == docId).ToListAsync();
                Assert.Single(chunks);
                Assert.Equal("old content", chunks[0].TextContent); // rollback preserved them
            }
        }
        finally
        {
            vault.Delete(recursive: true);
        }

        static async Task<Guid> db_src(DbContextOptions<KnowledgeHubDbContext> opts)
        {
            await using var db = new KnowledgeHubDbContext(opts);
            return (await db.Sources.FirstAsync()).Id;
        }
    }

    // ---------- RF-301 + RF-606: invalidation subscriber ----------

    private sealed class RecordingManager : ICacheManagerService
    {
        public int ClearCalls;
        public Task<CacheStatsDto> GetStatsAsync(CancellationToken ct = default) =>
            Task.FromResult(new CacheStatsDto());
        public Task<ClearCacheResultDto> ClearAllAsync(CancellationToken ct = default) =>
            Task.FromResult(new ClearCacheResultDto());
        public void TrackKey(string key, long sizeBytes, TimeSpan? ttl = null) { }
        public void RemoveKey(string key) { }
        public Task<CacheKeyRemovalResult> RemoveEntryAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(new CacheKeyRemovalResult { Tracked = false, Removed = true });
        public Task ClearLocalTrackedAsync(CancellationToken ct = default)
        {
            ClearCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSettings : IEmbeddingSettingsService
    {
        public int InvalidateCalls;
        public EmbeddingOptions GetEffectiveOptions() => new();
        public (int, int) GetChunking() => (500, 50);
        public Task<EmbeddingSettingsDto> DescribeAsync(CancellationToken ct = default) =>
            Task.FromResult<EmbeddingSettingsDto>(null!);
        public Task SaveAsync(SaveEmbeddingSettingsRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveKeyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() => InvalidateCalls++;
    }

    private sealed class FakeBus : ICacheInvalidationBus
    {
        public event EventHandler<string>? Received;
        public void Raise(string topic) => Received?.Invoke(this, topic);
        public Task PublishAsync(string topic, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Subscriber_RemoteClear_WithoutL1_StillClearsTracked()
    {
        var bus = new FakeBus();
        var manager = new RecordingManager();
        var subscriber = new InvalidationSubscriber(
            bus, new FakeDistCache(), // NOT an L1L2Cache — Cache:L1Enabled=false path
            NullLogger<InvalidationSubscriber>.Instance, manager);
        await subscriber.StartAsync(CancellationToken.None);

        bus.Raise("cache-clear");
        await Task.Delay(100);
        Assert.Equal(1, manager.ClearCalls); // RF-301: cleanup ran despite no L1
        await subscriber.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Subscriber_SettingsChanged_InvalidatesEmbeddingSettings()
    {
        var bus = new FakeBus();
        var settings = new RecordingSettings();
        var subscriber = new InvalidationSubscriber(
            bus, new FakeDistCache(), NullLogger<InvalidationSubscriber>.Instance,
            manager: null, embeddingSettings: settings);
        await subscriber.StartAsync(CancellationToken.None);

        bus.Raise("settings-changed");
        await Task.Delay(50);
        Assert.Equal(1, settings.InvalidateCalls); // RF-606
        await subscriber.StopAsync(CancellationToken.None);
    }

    // ---------- RF-401: default sqlite vector store serializes expansion ----------

    [Fact]
    public async Task SqliteVectorStore_ConcurrentSearches_AllSucceed()
    {
        var (conn, opts) = NewDb();
        await using var _ = conn;
        await using var db = new KnowledgeHubDbContext(opts);

        var src = new KnowledgeSource { Name = "s", SourceType = SourceType.DocumentFile };
        db.Sources.Add(src);
        var doc = new KnowledgeDocument
        {
            KnowledgeSourceId = src.Id,
            Title = "d",
            UriReference = "u",
            Chunks = [new DocumentChunk
            {
                ChunkIndex = 0, TextContent = "c",
                Embedding = KnowledgeHub.Server.Embeddings.EmbeddingVectorCodec.ToBytes(new[] { 1f, 0f, 0f, 0f }),
                EmbeddingModel = "fake:4"
            }]
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        var store = new SqliteVectorStore(db);
        var hits = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            store.SearchAsync(new[] { 1f, 0f, 0f, 0f }, "fake:4", 5)));
        Assert.All(hits, h => Assert.Single(h)); // gate serialized — no EF concurrency error
    }

    // ---------- RF-605: asymmetric decorator delegates role-aware calls ----------

    private sealed class MethodTracker : IEmbeddingProvider
    {
        public List<string> Called { get; } = [];
        public string ModelId => "t:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        { Called.Add("EmbedAsync"); return Task.FromResult(new[] { 1f, 0f, 0f, 0f }); }
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        { Called.Add("EmbedBatchAsync"); return Task.FromResult<IReadOnlyList<float[]>>([]); }
        public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
        { Called.Add("EmbedQueryAsync"); return Task.FromResult(new[] { 1f, 0f, 0f, 0f }); }
        public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default)
        { Called.Add("EmbedDocumentAsync"); return Task.FromResult(new[] { 1f, 0f, 0f, 0f }); }
        public Task<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        { Called.Add("EmbedDocumentBatchAsync"); return Task.FromResult<IReadOnlyList<float[]>>([]); }
    }

    [Fact]
    public async Task AsymDecorator_DelegatesToInnerRoleAwareMethods()
    {
        var inner = new MethodTracker();
        var provider = new AsymmetricEmbeddingProvider(inner, new EmbeddingOptions
        {
            Asymmetric = { Enabled = true, QueryPrefix = "search_query:", DocumentPrefix = "passage:" }
        });
        await provider.EmbedQueryAsync("q");
        await provider.EmbedDocumentAsync("d");
        await provider.EmbedDocumentBatchAsync(["d1"]);
        // RF-605: inner's role-aware methods (which attach input_type) must
        // receive the calls — not the generic EmbedAsync path.
        Assert.Equal(
            ["EmbedQueryAsync", "EmbedDocumentAsync", "EmbedDocumentBatchAsync"],
            inner.Called);
    }

    // ---------- RF-607: readiness probes the provider ----------

    private sealed class FailingEmbedder : IEmbeddingProvider
    {
        public string ModelId => "down:4";
        public int Dimensions => 4;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            throw new HttpRequestException("connection refused");
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new HttpRequestException("connection refused");
    }

    [Fact]
    public async Task EmbeddingHealthCheck_ProviderDown_Unhealthy()
    {
        var check = new EmbeddingHealthCheck(new FailingEmbedder());
        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, result.Status);
    }

    // ---------- RF-704: slug suffix collisions ----------

    [Fact]
    public void ToolSlugger_SuffixCollision_AssignsUnique()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var slugs = ToolSlugger.Assign([
            (a, "notes"), (b, "notes_2"), (c, "notes")
        ]);
        Assert.Equal(3, slugs.Values.Distinct().Count());
    }
}
