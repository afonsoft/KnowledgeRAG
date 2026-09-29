using System.Security.Claims;
using System.Text.Json.Nodes;
using KnowledgeHub.McpEngine.Activity;
using KnowledgeHub.Server.Agent;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Eval;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Server.Telemetry;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Serilog.Events;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260926-ops-and-ui-polish: log-level restore/generation,
// session transitions in monitor activity, agent ToolCall caller, eval
// retrieval-latency percentiles and honest vec0 diagnostics.
[Collection("SearchTelemetry")]
public sealed class OpsUiPolishTests
{
    // ---- LogLevelControl ----

    [Fact]
    public void Set_RaisedLevel_ZeroMinutes_RestoresDefault()
    {
        using var ctl = new LogLevelControl();
        var (level, resetAt) = ctl.Set(LogEventLevel.Debug, 0);
        // minutes:0 used to arm Debug forever — now it restores immediately.
        Assert.Equal("Information", level);
        Assert.Null(resetAt);
        Assert.Equal(LogEventLevel.Information, ctl.Switch.MinimumLevel);
    }

    [Fact]
    public void Set_ReRaise_ReplacesGeneration_TimerCannotKillNewer()
    {
        using var ctl = new LogLevelControl();
        ctl.Set(LogEventLevel.Debug, 30);
        var (_, resetAt) = ctl.Set(LogEventLevel.Warning, 60);
        // A stale timer callback from session 1 is generation-guarded; the
        // observable contract: the second raise keeps its own level + reset.
        Assert.Equal(LogEventLevel.Warning, ctl.Switch.MinimumLevel);
        Assert.NotNull(resetAt);
    }

    // ---- monitor: session transitions belong to activity ----

    [Fact]
    public void Replay_SessionEvents_UpdateMap_AndEnterActivity()
    {
        var sessions = new Dictionary<string, KnowledgeHub.Client.Services.McpMonitorReplay.SessionInfo>();
        var activity = new List<McpMonitorEventDto>();

        KnowledgeHub.Client.Services.McpMonitorReplay.Apply(new McpMonitorEventDto
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = McpMonitorEventKind.SessionOpened,
            SessionId = "s1",
            Caller = "alice (cookie)"
        }, sessions, activity, 10);
        KnowledgeHub.Client.Services.McpMonitorReplay.Apply(new McpMonitorEventDto
        {
            Timestamp = DateTimeOffset.UtcNow,
            Kind = McpMonitorEventKind.SessionClosed,
            SessionId = "s1"
        }, sessions, activity, 10);

        Assert.Empty(sessions); // opened then closed
        Assert.Equal(2, activity.Count); // transitions are auditable rows too
        Assert.Equal(McpMonitorEventKind.SessionOpened, activity[0].Kind);
        Assert.Equal(McpMonitorEventKind.SessionClosed, activity[1].Kind);
    }

    // ---- agent ToolCall carries the caller ----

    private sealed class RecordingFeed : IMcpActivityFeed
    {
        public List<McpActivityEvent> Events { get; } = [];
        public int Capacity => 100;
        public void Record(McpActivityEvent activityEvent) => Events.Add(activityEvent);
        public IReadOnlyList<McpActivityEvent> Snapshot() => Events;
        public event Action<McpActivityEvent>? Published { add { } remove { } }
    }

    [Fact]
    public async Task AgentToolCall_RecordsCaller_WhenAuthenticated()
    {
        var feed = new RecordingFeed();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "alice")], "test"))
            }
        };
        var services = new ServiceCollection()
            .AddSingleton<IMcpActivityFeed>(feed)
            .AddSingleton<IHttpContextAccessor>(accessor)
            .BuildServiceProvider();

        var tool = new CatalogTool
        {
            Name = "knowledge_search",
            Description = "d",
            InputSchema = new JsonObject(),
            Handler = (_, _) => new ValueTask<CallToolResult>(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "ok" }]
            })
        };
        var fn = new CatalogToolAIFunction(tool, services, 1000);

        await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>()));

        var evt = Assert.Single(feed.Events);
        Assert.NotNull(evt.Caller);
        Assert.Contains("alice", evt.Caller);
    }

    // ---- eval latency = retrieval, not the whole case ----

    private sealed class SlowSearch : ISearchService
    {
        public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
            string query, int topK, Guid? sourceId = null,
            SearchMode mode = SearchMode.Hybrid, KnowledgeHub.Server.Search.ResolvedSearchFilter? filter = null,
            string? conversationContext = null, CancellationToken ct = default)
        {
            await Task.Delay(20, ct);
            return [];
        }
    }

    private sealed class SlowAnswers : IAnswerService
    {
        public bool IsConfigured => true;
        public async Task<AskResponse> AnswerAsync(
            string question, IReadOnlyList<SearchResultItem> context, CancellationToken ct = default)
        {
            await Task.Delay(300, ct); // LLM time must not inflate search p95
            return new AskResponse
            {
                Answer = "a",
                Citations = [],
                LatencyMs = 0,
                Model = null,
                Generated = true
            };
        }

        public async IAsyncEnumerable<SseEvent> StreamAsync(
            string question, IReadOnlyList<SearchResultItem> context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
    }

    [Fact]
    public async Task Eval_LatencyMs_MeasuresSearch_NotAnswerTime()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var services = new ServiceCollection()
            .AddSingleton<IAnswerService>(new SlowAnswers())
            .BuildServiceProvider();
        var runner = new EvalRunner(new SlowSearch(), services, db,
            NullLogger<EvalRunner>.Instance);

        var report = await runner.RunAsync(
            [new EvalCase
            {
                Id = "c1", Question = "q",
                ExpectedTextMarkers = ["a"] // triggers the (slow) faith path
            }],
            "[{\"id\":\"c1\"}]", null, null, "keyword", null);

        var result = Assert.Single(report.Results);
        Assert.NotNull(result.LatencyMs);
        // Gap-based (starvation-proof): total run duration minus case latency
        // must cover the ~300ms answer path — if LatencyMs still measured the
        // whole case the gap would be ~0.
        Assert.True(report.DurationMs - result.LatencyMs.Value >= 250,
            $"gap {report.DurationMs - result.LatencyMs}ms should include the 300ms LLM answer");
    }

    // ---- diagnostics: vec0 is not an HNSW index ----

    private sealed class StubVectors : IVectorStore
    {
        public int? Dimensions => 384;
        public Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector,
            string model, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteBySourceAsync(Guid sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryVector, string model, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([]);
    }

    [Fact]
    public async Task Diagnostics_SqliteVec_ReportsVec0_NotHnsw()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["VectorStore:Provider"] = "sqlite-vec" }).Build();

        var diag = await VectorStoreDiagnostics.BuildAsync(new StubVectors(), cfg, db, default);
        var t = diag.GetType();

        Assert.False((bool)t.GetProperty("hnswIndex")!.GetValue(diag)!);
        Assert.Equal("vec0", t.GetProperty("indexType")!.GetValue(diag));
    }
}
