using System.Text.Json;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Mcp.ToolProviders;
using KnowledgeHub.Server.VectorStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Tests.Unit.Mcp;

/// <summary>
/// SPEC-20261111-mcp-ergonomics-wave2 RF-002/RF-003: operations_* job tools and
/// document_*/source_rescan corpus management.
/// </summary>
public class OperationsToolsProviderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kh-ops-{Guid.NewGuid():N}.db");

    public OperationsToolsProviderTests()
    {
        using var db = Open();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort */ }
    }

    private KnowledgeHubDbContext Open() => new(
        new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private sealed class FakeQueue : IIngestionQueue
    {
        private readonly KnowledgeHubDbContext _db;
        public List<Guid> Cancelled { get; } = [];
        public FakeQueue(KnowledgeHubDbContext db) => _db = db;
        public System.Threading.Channels.ChannelReader<Guid> Reader => System.Threading.Channels.Channel.CreateUnbounded<Guid>().Reader;
        public CancellationToken TokenFor(Guid jobId, CancellationToken stopping) => stopping;
        public void Complete(Guid jobId) { }
        public bool TryCancel(Guid jobId) { Cancelled.Add(jobId); return true; }
        public async Task<IngestionJobEnqueueResult> EnqueueAsync(Guid sourceId, string kind, CancellationToken ct)
        {
            var job = new IngestionJob { SourceId = sourceId, Kind = kind };
            _db.IngestionJobs.Add(job);
            await _db.SaveChangesAsync(ct);
            return new IngestionJobEnqueueResult(job, Existing: false);
        }
    }

    private sealed class FakeScope(CallerScope scope) : ICallerScopeProvider
    {
        public Task<CallerScope> GetAsync(CancellationToken ct) => Task.FromResult(scope);
    }

    private sealed class FakeVectors : IVectorStore
    {
        public List<Guid> DeletedDocs { get; } = [];
        public Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector, string model,
            IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
        { DeletedDocs.Add(documentId); return Task.CompletedTask; }
        public Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryVector, string model, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VectorHit>>([]);
    }

    private async Task<(OperationsToolsProvider Provider, IServiceProvider Services, KnowledgeSource Source)> SetupAsync(
        CallerScope? scope = null)
    {
        var db = Open();
        var source = new KnowledgeSource { Name = "src", SourceType = KnowledgeHub.Shared.Contracts.SourceType.ObsidianVault };
        db.Sources.Add(source);
        await db.SaveChangesAsync();

        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IIngestionQueue>(new FakeQueue(db))
            .AddSingleton<IVectorStore>(new FakeVectors())
            .AddSingleton<ICallerScopeProvider>(new FakeScope(scope ?? CallerScope.Unrestricted))
            .BuildServiceProvider();
        return (new OperationsToolsProvider(), services, source);
    }

    private static ToolCallContext Ctx(IServiceProvider sp, Dictionary<string, JsonElement>? args = null) => new()
    {
        Services = sp,
        Arguments = args
    };

    private static JsonElement J(object value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private static async Task<CatalogTool> ToolAsync(OperationsToolsProvider p, IServiceProvider sp, string name)
    {
        var tools = await p.GetToolsAsync(sp, CancellationToken.None);
        return tools.Single(t => t.Name == name);
    }

    private static JsonElement Structured(CallToolResult r) =>
        r.StructuredContent!.Value;

    [Fact]
    public async Task OperationsList_FiltersStatusAndScope()
    {
        // Covers RF-002 AC-01: status filter + caller sees only own sources.
        var (p, _, source) = await SetupAsync();
        var other = new KnowledgeSource { Name = "other", SourceType = KnowledgeHub.Shared.Contracts.SourceType.ObsidianVault };
        await using (var db = Open())
        {
            db.Sources.Add(other);
            db.IngestionJobs.Add(new IngestionJob { SourceId = source.Id, Status = "running" });
            db.IngestionJobs.Add(new IngestionJob { SourceId = source.Id, Status = "done" });
            db.IngestionJobs.Add(new IngestionJob { SourceId = other.Id, Status = "running" });
            await db.SaveChangesAsync();
        }
        var scoped = CallerScope.FromJson(Guid.NewGuid(),
            JsonSerializer.Serialize(new[] { source.Id }), null);
        var sp = new ServiceCollection()
            .AddSingleton(Open())
            .AddSingleton<IIngestionQueue>(new FakeQueue(Open()))
            .AddSingleton<ICallerScopeProvider>(new FakeScope(scoped))
            .BuildServiceProvider();

        var tool = await ToolAsync(p, sp, "operations_list");
        var res = await tool.Handler(Ctx(sp, new() { ["status"] = J("running") }), CancellationToken.None);
        var ops = Structured(res).GetProperty("operations");
        Assert.Equal(1, ops.GetArrayLength());
        Assert.Equal(source.Id, ops[0].GetProperty("sourceId").GetGuid());
    }

    [Fact]
    public async Task OperationsGet_UnknownId_IsError()
    {
        var (p, sp, _) = await SetupAsync();
        var tool = await ToolAsync(p, sp, "operations_get");
        var res = await tool.Handler(Ctx(sp, new() { ["id"] = J(Guid.NewGuid().ToString()) }), CancellationToken.None);
        Assert.True(res.IsError);
    }

    [Fact]
    public async Task OperationsCancel_Queued_Cancels()
    {
        // Covers RF-002 AC-03: queued job → cancelled.
        var (p, sp, source) = await SetupAsync();
        var job = new IngestionJob { SourceId = source.Id, Status = "queued" };
        await using (var db = Open()) { db.IngestionJobs.Add(job); await db.SaveChangesAsync(); }

        var tool = await ToolAsync(p, sp, "operations_cancel");
        var res = await tool.Handler(Ctx(sp, new() { ["id"] = J(job.Id.ToString()) }), CancellationToken.None);
        Assert.False(res.IsError);
        await using var check = Open();
        Assert.Equal("cancelled", (await check.IngestionJobs.FindAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task OperationsCancel_Done_IsError()
    {
        // Covers RF-002: terminal state → error (REST 409 equivalent).
        var (p, sp, source) = await SetupAsync();
        var job = new IngestionJob { SourceId = source.Id, Status = "done" };
        await using (var db = Open()) { db.IngestionJobs.Add(job); await db.SaveChangesAsync(); }

        var tool = await ToolAsync(p, sp, "operations_cancel");
        var res = await tool.Handler(Ctx(sp, new() { ["id"] = J(job.Id.ToString()) }), CancellationToken.None);
        Assert.True(res.IsError);
    }

    [Fact]
    public async Task OperationsRetry_Failed_Requeues()
    {
        // Covers RF-002: failed job → new queued job for same source+kind.
        var (p, sp, source) = await SetupAsync();
        var job = new IngestionJob { SourceId = source.Id, Status = "failed", Kind = "reindex" };
        await using (var db = Open()) { db.IngestionJobs.Add(job); await db.SaveChangesAsync(); }

        var tool = await ToolAsync(p, sp, "operations_retry");
        var res = await tool.Handler(Ctx(sp, new() { ["id"] = J(job.Id.ToString()) }), CancellationToken.None);
        Assert.False(res.IsError);
        var op = Structured(res).GetProperty("operation");
        Assert.Equal("queued", op.GetProperty("status").GetString());
        Assert.Equal("reindex", op.GetProperty("kind").GetString());
        Assert.NotEqual(job.Id, op.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task DocumentUpdate_ById_SetsTitle()
    {
        // Covers RF-003: update title by id.
        var (p, sp, source) = await SetupAsync();
        var doc = new KnowledgeDocument
        { KnowledgeSourceId = source.Id, Title = "old", UriReference = "notes/a.md" };
        await using (var db = Open()) { db.Documents.Add(doc); await db.SaveChangesAsync(); }

        var tool = await ToolAsync(p, sp, "document_update");
        var res = await tool.Handler(Ctx(sp, new()
        {
            ["id"] = J(doc.Id.ToString()),
            ["title"] = J("new title")
        }), CancellationToken.None);
        Assert.False(res.IsError);
        await using var check = Open();
        Assert.Equal("new title", (await check.Documents.FindAsync(doc.Id))!.Title);
    }

    [Fact]
    public async Task DocumentDelete_RequiresConfirm()
    {
        // Covers RF-003 AC-04: confirm:true gate.
        var (p, sp, source) = await SetupAsync();
        var doc = new KnowledgeDocument
        { KnowledgeSourceId = source.Id, Title = "t", UriReference = "u" };
        doc.Chunks.Add(new DocumentChunk { ChunkIndex = 0, TextContent = "x" });
        await using (var db = Open()) { db.Documents.Add(doc); await db.SaveChangesAsync(); }

        var tool = await ToolAsync(p, sp, "document_delete");
        var noConfirm = await tool.Handler(Ctx(sp, new() { ["id"] = J(doc.Id.ToString()) }), CancellationToken.None);
        Assert.True(noConfirm.IsError);

        var ok = await tool.Handler(Ctx(sp, new()
        {
            ["id"] = J(doc.Id.ToString()),
            ["confirm"] = J(true)
        }), CancellationToken.None);
        Assert.False(ok.IsError);
        await using var check = Open();
        Assert.Null(await check.Documents.FindAsync(doc.Id));
        Assert.Equal(0, await check.Chunks.CountAsync());
    }

    [Fact]
    public async Task SourceRescan_EnqueuesSync()
    {
        // Covers RF-003: rescan → queued operation.
        var (p, sp, source) = await SetupAsync();
        var tool = await ToolAsync(p, sp, "source_rescan");
        var res = await tool.Handler(Ctx(sp, new() { ["sourceId"] = J(source.Id.ToString()) }), CancellationToken.None);
        Assert.False(res.IsError);
        var op = Structured(res).GetProperty("operation");
        Assert.Equal("queued", op.GetProperty("status").GetString());
        Assert.Equal("sync", op.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task OperationsList_ScopedKey_HidesOtherSources()
    {
        // Covers RF-002 AC-02: a key scoped to source A cannot see source B's jobs.
        var (p, sp, source) = await SetupAsync();
        var other = new KnowledgeSource { Name = "other", SourceType = KnowledgeHub.Shared.Contracts.SourceType.ObsidianVault };
        await using (var db = Open())
        {
            db.Sources.Add(other);
            db.IngestionJobs.Add(new IngestionJob { SourceId = source.Id });
            db.IngestionJobs.Add(new IngestionJob { SourceId = other.Id });
            await db.SaveChangesAsync();
        }
        var scoped = CallerScope.FromJson(Guid.NewGuid(),
            JsonSerializer.Serialize(new[] { source.Id }), null);
        var services = new ServiceCollection()
            .AddSingleton(Open())
            .AddSingleton<IIngestionQueue>(new FakeQueue(Open()))
            .AddSingleton<ICallerScopeProvider>(new FakeScope(scoped))
            .BuildServiceProvider();

        var tool = await ToolAsync(p, services, "operations_list");
        var res = await tool.Handler(Ctx(services), CancellationToken.None);
        var ops = Structured(res).GetProperty("operations");
        Assert.Equal(1, ops.GetArrayLength());
        Assert.Equal(source.Id, ops[0].GetProperty("sourceId").GetGuid());
    }
}
