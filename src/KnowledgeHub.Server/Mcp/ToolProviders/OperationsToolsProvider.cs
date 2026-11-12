using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Server.VectorStore;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// SPEC-20261111-mcp-ergonomics-wave2 RF-002/RF-003: operation (async job)
/// tools + document/source CRUD gaps, so long-running work and corpus
/// management are fully drivable over MCP — parity with
/// <c>/api/ingestion/jobs</c> and the source rescan flow.
/// </summary>
public sealed class OperationsToolsProvider : IToolProvider
{
    private const string OperationNotFound = "operation not found";
    private const string CancelledStatus = "cancelled";
    private const string ArgSourceId = "sourceId";

    private static readonly JsonObject OperationsListSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "status":{"type":["string","null"],"enum":["queued","running","done","failed",CancelledStatus,null],"description":"Filter by job status"},
          "kind":{"type":["string","null"],"enum":["sync","reindex",null],"description":"Filter by job kind"},
          ArgSourceId:{"type":["string","null"],"description":"Filter by source id (GUID)"},
          "limit":{"type":"integer","description":"Max jobs to return (default 20, cap 100)"},
          "offset":{"type":"integer","description":"Skip the newest N jobs (default 0)"}
        },
        "examples":[{"status":"running"},{"kind":"reindex","limit":5}]}
        """)!.AsObject();

    private static readonly JsonObject IdSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "id":{"type":"string","description":"Operation (job) id — GUID"}
        },"required":["id"],
        "examples":[{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}]}
        """)!.AsObject();

    private static readonly JsonObject DocumentUpdateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "id":{"type":["string","null"],"description":"Document id (GUID)"},
          "path":{"type":["string","null"],"description":"Document uriReference (path/URL) — resolves the document when id is absent"},
          ArgSourceId:{"type":["string","null"],"description":"Source id (GUID) — narrows path resolution"},
          "title":{"type":["string","null"],"description":"New title"}
        },
        "examples":[{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","title":"New title"},
                    {"path":"notes/design.md",ArgSourceId:"9f1b…","title":"Design doc"}]}
        """)!.AsObject();

    private static readonly JsonObject DocumentDeleteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "id":{"type":"string","description":"Document id (GUID)"},
          "confirm":{"type":"boolean","description":"Must be true — deletes the document, its chunks and its vectors"}
        },"required":["id","confirm"],
        "examples":[{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","confirm":true}]}
        """)!.AsObject();

    private static readonly JsonObject SourceRescanSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          ArgSourceId:{"type":"string","description":"Source id (GUID) to re-sync"},
          "kind":{"type":["string","null"],"enum":["sync","reindex",null],"description":"sync (default, incremental) or reindex (force re-chunk)"}
        },"required":[ArgSourceId],
        "examples":[{ArgSourceId:"9f1b3c2e-…"},{ArgSourceId:"9f1b3c2e-…","kind":"reindex"}]}
        """)!.AsObject();

    public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "operations_list",
                Title = "List operations",
                Description = "List async jobs (ingestion syncs/reindexes, memory jobs). Poll after fire-and-forget calls to track progress. Only jobs for sources visible to the caller are returned.",
                InputSchema = OperationsListSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = ListAsync
            },
            new CatalogTool
            {
                Name = "operations_get",
                Title = "Get operation",
                Description = "Get one async job by id — status, counters and warnings.",
                InputSchema = IdSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = GetAsync
            },
            new CatalogTool
            {
                Name = "operations_cancel",
                Title = "Cancel operation",
                Description = "Cooperatively cancel a queued or running job. Terminal states (done/failed/cancelled) are an error.",
                InputSchema = IdSchema,
                ReadOnly = false,
                DestructiveHint = true,
                Handler = CancelAsync
            },
            new CatalogTool
            {
                Name = "operations_retry",
                Title = "Retry operation",
                Description = "Requeue a failed or cancelled job — enqueues a new job of the same kind for the same source.",
                InputSchema = IdSchema,
                ReadOnly = false,
                Handler = RetryAsync
            },
            new CatalogTool
            {
                Name = "document_update",
                Title = "Update document",
                Description = "Update a knowledge document's title by id or path. To change content, use source_rescan — chunks re-derive from the stored raw content.",
                InputSchema = DocumentUpdateSchema,
                ReadOnly = false,
                IdempotentHint = true,
                Handler = DocumentUpdateAsync
            },
            new CatalogTool
            {
                Name = "document_delete",
                Title = "Delete document",
                Description = "Delete a document, its derived chunks and its vectors. Requires confirm:true.",
                InputSchema = DocumentDeleteSchema,
                ReadOnly = false,
                DestructiveHint = true,
                Handler = DocumentDeleteAsync
            },
            new CatalogTool
            {
                Name = "source_rescan",
                Title = "Rescan source",
                Description = "Enqueue an on-demand sync (or forced reindex) for a knowledge source — returns the queued operation for polling via operations_get.",
                InputSchema = SourceRescanSchema,
                ReadOnly = false,
                Handler = SourceRescanAsync
            }
        ];
        return Task.FromResult(tools);
    }

    private static async ValueTask<CallToolResult> ListAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var scope = await ResolveScopeAsync(ctx, ct);
        var status = ToolArgs.OptionalString(ctx, "status");
        var kind = ToolArgs.OptionalString(ctx, "kind");
        var limit = ToolArgs.OptionalInt(ctx, "limit", 20, 100);
        var offset = ToolArgs.OptionalInt(ctx, "offset", 0, 1_000_000);
        var sourceId = ParseGuid(ToolArgs.OptionalString(ctx, ArgSourceId), ArgSourceId);

        var ingest = (await db.IngestionJobs.AsNoTracking().ToListAsync(ct))
            .Where(j => scope.AllowsSource(j.SourceId))
            .Where(j => status is null || j.Status == status)
            .Where(j => kind is null || j.Kind == kind)
            .Where(j => sourceId is null || j.SourceId == sourceId)
            .Select(j => new OpView(j.Id, "ingestion", j.SourceId, j.Kind, j.Status,
                j.DocsProcessed + j.DocsSkipped + j.DocsFailed, j.Error, null,
                j.DocsProcessed, j.DocsSkipped, j.DocsFailed, j.ChunksCreated, null,
                j.CreatedAt, j.StartedAt, j.FinishedAt))
            .ToList();
        // SPEC-20261111-agent-memory-retain: async retains surface here too —
        // callers see only operations their API key enqueued.
        var scopeKey = scope.ApiKeyId ?? Guid.Empty;
        var memOps = await db.MemoryOperations.AsNoTracking()
            .Where(o => o.ApiKeyId == scopeKey)
            .Where(o => status == null || o.Status == status)
            .Where(o => kind == null || o.Kind == kind)
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit + offset)
            .Select(o => new OpView(o.Id, "memory", null, o.Kind, o.Status,
                o.UnitsCreated, o.Error, o.ResultJson,
                null, null, null, null, o.BankId,
                Utc(o.CreatedAt), Utc(o.StartedAt), Utc(o.FinishedAt)))
            .ToListAsync(ct);
        var all = ingest.Concat(memOps)
            .OrderByDescending(o => o.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToList();
        return await ToolResults.Structured($"{all.Count} operation(s)", new { count = all.Count, operations = all });
    }

    private static async ValueTask<CallToolResult> GetAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var scope = await ResolveScopeAsync(ctx, ct);
        var id = ParseGuid(ToolArgs.RequiredString(ctx, "id"), "id")!.Value;
        var job = await db.IngestionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is not null)
        {
            if (!scope.AllowsSource(job.SourceId))
                return await ToolResults.Error(OperationNotFound);
            return await ToolResults.Structured("operation", Project(job));
        }
        var scopeKey = scope.ApiKeyId ?? Guid.Empty;
        var memOp = await db.MemoryOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.ApiKeyId == scopeKey, ct);
        if (memOp is null)
            return await ToolResults.Error(OperationNotFound);
        return await ToolResults.Structured("operation", new OpView(
            memOp.Id, "memory", null, memOp.Kind, memOp.Status, memOp.UnitsCreated,
            memOp.Error, memOp.ResultJson, null, null, null, null, memOp.BankId,
            Utc(memOp.CreatedAt), Utc(memOp.StartedAt), Utc(memOp.FinishedAt)));
    }

    private static async ValueTask<CallToolResult> CancelAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var queue = ctx.Services.GetRequiredService<IIngestionQueue>();
        var scope = await ResolveScopeAsync(ctx, ct);
        var id = ParseGuid(ToolArgs.RequiredString(ctx, "id"), "id")!.Value;

        var job = await db.IngestionJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is not null && !scope.AllowsSource(job.SourceId))
            job = null;
        if (job is null)
        {
            var scopeKey = scope.ApiKeyId ?? Guid.Empty;
            var memOp = await db.MemoryOperations
                .FirstOrDefaultAsync(o => o.Id == id && o.ApiKeyId == scopeKey, ct);
            if (memOp is null)
                return await ToolResults.Error(OperationNotFound);
            var memQueue = ctx.Services.GetRequiredService<Memory.IMemoryRetainQueue>();
            if (memOp.Status == "queued")
            {
                memOp.Status = CancelledStatus;
                memOp.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                memQueue.TryCancel(id);
                return await ToolResults.Structured("operation cancelled", new OpView(
                    memOp.Id, "memory", null, memOp.Kind, memOp.Status, memOp.UnitsCreated,
                    memOp.Error, memOp.ResultJson, null, null, null, null, memOp.BankId,
                    Utc(memOp.CreatedAt), Utc(memOp.StartedAt), Utc(memOp.FinishedAt)));
            }
            if (memOp.Status == "running")
            {
                memQueue.TryCancel(id);
                return await ToolResults.Structured(
                    "cancellation requested — the worker stops before commit (no partial units)", new { id });
            }
            return await ToolResults.Error($"operation is already {memOp.Status}");
        }

        if (job.Status == "queued")
        {
            job.Status = CancelledStatus;
            job.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            queue.TryCancel(id);
            return await ToolResults.Structured("operation cancelled", Project(job));
        }
        if (job.Status == "running")
        {
            queue.TryCancel(id);
            return await ToolResults.Structured("cancellation requested — the worker stops at its next checkpoint", Project(job));
        }
        return await ToolResults.Error($"operation is already {job.Status}");
    }

    private static async ValueTask<CallToolResult> RetryAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var queue = ctx.Services.GetRequiredService<IIngestionQueue>();
        var scope = await ResolveScopeAsync(ctx, ct);
        var id = ParseGuid(ToolArgs.RequiredString(ctx, "id"), "id")!.Value;

        var job = await db.IngestionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is not null && !scope.AllowsSource(job.SourceId))
            job = null;
        if (job is null)
            return await RetryMemoryOperationAsync(ctx, db, scope, id, ct);
        if (job.Status is not ("failed" or CancelledStatus))
            return await ToolResults.Error($"only failed or cancelled operations can be retried (current: {job.Status})");

        var enqueued = await queue.EnqueueAsync(job.SourceId, job.Kind, ct);
        return await ToolResults.Structured(
            enqueued.Existing ? "an equivalent operation is already queued/running" : "operation requeued",
            new { requeued = !enqueued.Existing, operation = Project(enqueued.Job) });
    }

    private static async ValueTask<CallToolResult> RetryMemoryOperationAsync(
        ToolCallContext ctx, KnowledgeHubDbContext db, CallerScope scope, Guid id, CancellationToken ct)
    {
        var scopeKey = scope.ApiKeyId ?? Guid.Empty;
        var memOp = await db.MemoryOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.ApiKeyId == scopeKey, ct);
        if (memOp is null)
            return await ToolResults.Error(OperationNotFound);
        if (memOp.Status is not ("failed" or CancelledStatus))
            return await ToolResults.Error($"only failed or cancelled operations can be retried (current: {memOp.Status})");
        var bank = await db.MemoryBanks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == memOp.BankId, ct);
        if (bank is null)
            return await ToolResults.Error("the operation's memory bank no longer exists");
        var payload = JsonSerializer.Deserialize<Memory.MemoryRetainRequest>(memOp.PayloadJson);
        if (payload is null)
            return await ToolResults.Error("the operation payload is corrupt");
        var memQueue = ctx.Services.GetRequiredService<Memory.IMemoryRetainQueue>();
        var requeued = await memQueue.EnqueueAsync(bank, memOp.ApiKeyId, payload, ct);
        return await ToolResults.Structured("operation requeued",
            new { requeued = true, operationId = requeued.Id });
    }

    private static async ValueTask<CallToolResult> DocumentUpdateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var scope = await ResolveScopeAsync(ctx, ct);
        var id = ParseGuid(ToolArgs.OptionalString(ctx, "id"), "id");
        var path = ToolArgs.OptionalString(ctx, "path");
        var sourceId = ParseGuid(ToolArgs.OptionalString(ctx, ArgSourceId), ArgSourceId);
        var title = ToolArgs.OptionalString(ctx, "title");

        if (id is null && path is null)
            return await ToolResults.Error("document_update requires 'id' or 'path'");
        if (title is null)
            return await ToolResults.Error("nothing to update — pass 'title'");

        var query = db.Documents.AsQueryable();
        if (id is { } gid) query = query.Where(d => d.Id == gid);
        if (path is { } p) query = query.Where(d => d.UriReference == p);
        if (sourceId is { } sid) query = query.Where(d => d.KnowledgeSourceId == sid);
        var doc = await query.FirstOrDefaultAsync(ct);
        if (doc is null || !scope.AllowsSource(doc.KnowledgeSourceId))
            return await ToolResults.Error("document not found");

        doc.Title = title;
        await db.SaveChangesAsync(ct);
        return await ToolResults.Structured("document updated",
            new { doc.Id, doc.Title, doc.UriReference, doc.KnowledgeSourceId });
    }

    private static async ValueTask<CallToolResult> DocumentDeleteAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var vectors = ctx.Services.GetRequiredService<IVectorStore>();
        var scope = await ResolveScopeAsync(ctx, ct);
        var id = ParseGuid(ToolArgs.RequiredString(ctx, "id"), "id")!.Value;
        if (ToolArgs.OptionalBool(ctx, "confirm") != true)
            return await ToolResults.Error("document_delete requires confirm:true");

        var doc = await db.Documents.Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null || !scope.AllowsSource(doc.KnowledgeSourceId))
            return await ToolResults.Error("document not found");

        var chunkCount = doc.Chunks.Count;
        await vectors.DeleteByDocumentAsync(doc.Id, ct);
        db.Chunks.RemoveRange(doc.Chunks);
        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        return await ToolResults.Structured("document deleted",
            new { doc.Id, removedChunks = chunkCount });
    }

    private static async ValueTask<CallToolResult> SourceRescanAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var queue = ctx.Services.GetRequiredService<IIngestionQueue>();
        var scope = await ResolveScopeAsync(ctx, ct);
        var sourceId = ParseGuid(ToolArgs.RequiredString(ctx, ArgSourceId), ArgSourceId)!.Value;
        var kind = ToolArgs.OptionalString(ctx, "kind") ?? "sync";

        if (!scope.AllowsSource(sourceId))
            return await ToolResults.Error("source not found");
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null)
            return await ToolResults.Error("source not found");

        var enqueued = await queue.EnqueueAsync(sourceId, kind, ct);
        return await ToolResults.Structured(
            enqueued.Existing ? "a sync is already queued/running for this source" : "rescan queued",
            new { queued = !enqueued.Existing, operation = Project(enqueued.Job) });
    }

    private static async Task<CallerScope> ResolveScopeAsync(ToolCallContext ctx, CancellationToken ct) =>
        ctx.Services.GetService<ICallerScopeProvider>() is { } provider
            ? await provider.GetAsync(ct)
            : CallerScope.Unrestricted;

    private static Guid? ParseGuid(string? raw, string name)
    {
        if (raw is null) return null;
        return Guid.TryParse(raw, out var g)
            ? g
            : throw new ModelContextProtocol.McpProtocolException(
                $"argument '{name}' must be a GUID", ModelContextProtocol.McpErrorCode.InvalidParams);
    }

    private static object Project(IngestionJob j) => new
    {
        j.Id,
        type = "ingestion",
        j.SourceId,
        j.Kind,
        j.Status,
        j.DocsProcessed,
        j.DocsSkipped,
        j.DocsFailed,
        j.ChunksCreated,
        j.Error,
        j.CreatedAt,
        j.StartedAt,
        j.FinishedAt
    };

    private static DateTimeOffset Utc(DateTime dt) => new(dt, TimeSpan.Zero);
    private static DateTimeOffset? Utc(DateTime? dt) => dt is { } v ? new DateTimeOffset(v, TimeSpan.Zero) : null;

    /// <summary>Unified list/get projection for ingestion + memory ops —
    /// union of both payloads; non-applicable fields serialize as null.</summary>
    private sealed record OpView(Guid Id, string Type, Guid? SourceId, string Kind, string Status,
        int ItemsProcessed, string? Error, string? ResultJson,
        int? DocsProcessed, int? DocsSkipped, int? DocsFailed, int? ChunksCreated, Guid? BankId,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
}
