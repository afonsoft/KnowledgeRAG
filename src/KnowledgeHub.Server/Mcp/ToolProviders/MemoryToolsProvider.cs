using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Memory;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-002/RF-003/RF-004: the agent-memory
/// MCP surface — retain/recall plus unit & bank CRUD, all scoped to banks
/// owned by the calling API key.
/// </summary>
public sealed class MemoryToolsProvider : IToolProvider
{
    private static readonly JsonObject RetainSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"],"description":"Target bank (default 'default'); auto-created on first use"},
          "content":{"type":"string","description":"Text to remember — observations, decisions, facts, transcripts"},
          "context":{"type":["string","null"],"description":"Why this is being remembered (helps extraction)"},
          "timestamp":{"type":["string","null"],"description":"ISO-8601 moment the content happened (default: now)"},
          "tags":{"type":["array","null"],"items":{"type":"string"},"description":"Free-form tags"},
          "metadata":{"type":["object","null"],"description":"Flat string key/value payload"},
          "mode":{"type":["string","null"],"enum":["sync","async",null],"description":"sync returns unit ids; async returns operationId (poll operations_get)"},
          "strategy":{"type":["string","null"],"enum":["extract","verbatim",null],"description":"extract = LLM fact extraction (default); verbatim = store content as-is, no LLM"},
          "documentId":{"type":["string","null"],"description":"Logical document id — enables delta-retain (unchanged chunks keep their ids)"},
          "documentTitle":{"type":["string","null"],"description":"Title for the logical document"}
        },"required":["content"],
        "examples":[{"content":"Deploy da API falhou por falta de memória no nó 3","strategy":"verbatim"},
                    {"bankKey":"projeto-x","content":"O time decidiu migrar para Postgres em março","mode":"async"}]}
        """)!.AsObject();

    private static readonly JsonObject RecallSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"],"description":"Bank to search (default 'default')"},
          "query":{"type":"string","description":"Natural-language recall query"},
          "topK":{"type":"integer","description":"Max results (default 8, cap 100)"},
          "minScore":{"type":["number","null"],"description":"Drop results with normalized score below this (0..1)"},
          "window":{"type":["string","null"],"description":"Temporal window — whitelist: 1h, 6h, 24h, 7d — enables the temporal arm"},
          "budget":{"type":["integer","null"],"description":"Token budget for returned texts"},
          "factType":{"type":["string","null"],"enum":["world","experience","observation",null],"description":"Restrict to one fact type"}
        },"required":["query"],
        "examples":[{"query":"quando foi o deploy que falhou?","window":"7d"},
                    {"query":"decisões sobre Postgres","topK":5,"factType":"experience"}]}
        """)!.AsObject();

    private static readonly JsonObject ListSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "factType":{"type":["string","null"],"enum":["world","experience","observation",null]},
          "tag":{"type":["string","null"]},
          "limit":{"type":"integer","description":"Default 20, cap 200"},
          "offset":{"type":"integer"}
        },
        "examples":[{"bankKey":"default","factType":"world","limit":10}]}
        """)!.AsObject();

    private static readonly JsonObject IdSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string","description":"Memory unit id (GUID)"}
        },"required":["id"],
        "examples":[{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}]}
        """)!.AsObject();

    private static readonly JsonObject UpdateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string","description":"Memory unit id (GUID)"},
          "text":{"type":["string","null"],"description":"New fact text (rehashes + re-embeds)"},
          "context":{"type":["string","null"]},
          "tags":{"type":["array","null"],"items":{"type":"string"}},
          "metadata":{"type":["object","null"]}
        },"required":["id"],
        "examples":[{"id":"3fa85f64-…","tags":["decisão","postgres"]}]}
        """)!.AsObject();

    private static readonly JsonObject DeleteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string","description":"Memory unit id (GUID)"},
          "confirm":{"type":"boolean","description":"Must be true"},
          "hard":{"type":["boolean","null"],"description":"true = permanent delete; default soft-delete"}
        },"required":["id","confirm"],
        "examples":[{"id":"3fa85f64-…","confirm":true}]}
        """)!.AsObject();

    private static readonly JsonObject BankCreateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":"string","description":"Slug (unique per API key)"},
          "name":{"type":["string","null"],"description":"Display name (default = bankKey)"},
          "mission":{"type":["string","null"],"description":"Bank purpose — guides future reflect"},
          "disposition":{"type":["object","null"],"description":"{\"skepticism\":1-5,\"literalism\":1-5,\"empathy\":1-5}"},
          "config":{"type":["object","null"],"description":"Per-bank config overrides (maxUnitsPerRetain, defaultStrategy, …)"}
        },"required":["bankKey"],
        "examples":[{"bankKey":"projeto-x","name":"Projeto X","mission":"memória do agente do projeto X"}]}
        """)!.AsObject();

    private static readonly JsonObject BankUpdateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":"string"},
          "name":{"type":["string","null"]},
          "mission":{"type":["string","null"]},
          "disposition":{"type":["object","null"]},
          "config":{"type":["object","null"]}
        },"required":["bankKey"],
        "examples":[{"bankKey":"projeto-x","mission":"memória do agente + decisões arquiteturais"}]}
        """)!.AsObject();

    private static readonly JsonObject ConsolidateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "wait":{"type":["boolean","null"],"description":"true (default) runs inline and returns the summary; false queues a consolidate operation (poll operations_get)"}
        },
        "examples":[{"bankKey":"projeto-x"},{"bankKey":"default","wait":false}]}
        """)!.AsObject();

    private static readonly JsonObject ObsHistorySchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "observationId":{"type":"string","description":"Observation unit id (GUID)"}
        },"required":["observationId"],
        "examples":[{"observationId":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}]}
        """)!.AsObject();

    private static readonly JsonObject BankDeleteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":"string"},
          "confirm":{"type":"boolean","description":"Must be true — deletes the bank and ALL its memories"}
        },"required":["bankKey","confirm"],
        "examples":[{"bankKey":"projeto-x","confirm":true}]}
        """)!.AsObject();

    public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "memory_retain",
                Title = "Retain memory",
                Description = "Store facts into an agent memory bank. strategy=extract (default) pulls typed facts/entities/dates via LLM; strategy=verbatim stores as-is. mode=async returns an operationId for long contents. Repeat calls with the same documentId delta-update: unchanged chunks keep their unit ids.",
                InputSchema = RetainSchema,
                Handler = RetainAsync
            },
            new CatalogTool
            {
                Name = "memory_recall",
                Title = "Recall memories",
                Description = "Search a memory bank across four arms — semantic, lexical, entity-graph and temporal (temporal only when window is set) — fused with reciprocal rank. Returns scored facts with entities and dates. Call BEFORE starting tasks to recover context.",
                InputSchema = RecallSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = RecallAsync
            },
            new CatalogTool
            {
                Name = "memory_list",
                Title = "List memories",
                Description = "List memory units in a bank (newest first), filterable by fact type and tag.",
                InputSchema = ListSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = ListAsync
            },
            new CatalogTool
            {
                Name = "memory_get",
                Title = "Get memory",
                Description = "Fetch one memory unit by id.",
                InputSchema = IdSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = GetAsync
            },
            new CatalogTool
            {
                Name = "memory_update",
                Title = "Update memory",
                Description = "Patch a memory unit's text/context/tags/metadata. Text changes rehash and re-embed the unit.",
                InputSchema = UpdateSchema,
                IdempotentHint = true,
                Handler = UpdateAsync
            },
            new CatalogTool
            {
                Name = "memory_delete",
                Title = "Delete memory",
                Description = "Delete a memory unit (soft-delete default; hard=true removes permanently). Requires confirm=true.",
                InputSchema = DeleteSchema,
                DestructiveHint = true,
                Handler = DeleteAsync
            },
            new CatalogTool
            {
                Name = "memory_bank_list",
                Title = "List memory banks",
                Description = "List the caller's memory banks with unit counts.",
                InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}""")!.AsObject(),
                ReadOnly = true,
                IdempotentHint = true,
                Handler = BankListAsync
            },
            new CatalogTool
            {
                Name = "memory_bank_create",
                Title = "Create memory bank",
                Description = "Create an isolated memory bank (one per user/agent/project). bankKey is unique per API key.",
                InputSchema = BankCreateSchema,
                Handler = BankCreateAsync
            },
            new CatalogTool
            {
                Name = "memory_bank_update",
                Title = "Update memory bank",
                Description = "Update a bank's name/mission/disposition/config.",
                InputSchema = BankUpdateSchema,
                IdempotentHint = true,
                Handler = BankUpdateAsync
            },
            new CatalogTool
            {
                Name = "memory_consolidate",
                Title = "Consolidate memories",
                Description = "Run observation consolidation on a bank: unconsolidated facts are merged into durable observations (evidence-backed, refine-not-overwrite) by an LLM pass. wait=false queues the run as an operation.",
                InputSchema = ConsolidateSchema,
                IdempotentHint = true,
                Handler = ConsolidateAsync
            },
            new CatalogTool
            {
                Name = "memory_observation_history",
                Title = "Observation history",
                Description = "Audit trail of an observation — every create/update version with its added source ids and proof count.",
                InputSchema = ObsHistorySchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = ObsHistoryAsync
            },
            new CatalogTool
            {
                Name = "memory_bank_delete",
                Title = "Delete memory bank",
                Description = "Delete a bank and ALL its memories. Requires confirm=true.",
                InputSchema = BankDeleteSchema,
                DestructiveHint = true,
                Handler = BankDeleteAsync
            }
        ];
        return Task.FromResult(tools);
    }

    // ---------- retain / recall ----------

    private static async ValueTask<CallToolResult> RetainAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var content = ToolArgs.RequiredString(ctx, "content");
        var mode = ToolArgs.OptionalString(ctx, "mode") ?? "sync";
        var req = new MemoryRetainRequest(
            content,
            ToolArgs.OptionalString(ctx, "context"),
            ParseDate(ToolArgs.OptionalString(ctx, "timestamp")),
            OptionalStringList(ctx, "tags"),
            OptionalStringMap(ctx, "metadata"),
            ToolArgs.OptionalString(ctx, "strategy") ?? "extract",
            ToolArgs.OptionalString(ctx, "documentId"),
            ToolArgs.OptionalString(ctx, "documentTitle"));
        var bankKey = MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, "bankKey"));
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();

        if (mode == "async")
        {
            var queue = ctx.Services.GetRequiredService<IMemoryRetainQueue>();
            var bank = await banks.ResolveAsync(bankKey, ct);
            var scope = await banks.ScopeKeyAsync(ct);
            var op = await queue.EnqueueAsync(bank, scope, req, ct);
            return await ToolResults.Structured("retain queued", new
            { bankId = bank.Id, operationId = op.Id, status = op.Status, async = true });
        }

        var retain = ctx.Services.GetRequiredService<MemoryRetainService>();
        try
        {
            var result = await retain.RetainAsync(bankKey, req, ct);
            return await ToolResults.Structured(
                $"retained {result.UnitsCreated} unit(s), {result.UnitsPreserved} preserved", result);
        }
        catch (MemoryServiceUnavailableException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> RecallAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var req = new MemoryRecallRequest(
            ToolArgs.RequiredString(ctx, "query"),
            ToolArgs.OptionalInt(ctx, "topK", 8, 100),
            ToolArgs.OptionalDouble(ctx, "minScore"),
            ToolArgs.OptionalString(ctx, "window"),
            ToolArgs.OptionalIntOrNull(ctx, "budget"),
            ToolArgs.OptionalString(ctx, "factType"));
        var recall = ctx.Services.GetRequiredService<MemoryRecallService>();
        var result = await recall.RecallAsync(MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, "bankKey")), req, ct);
        return await ToolResults.Structured($"{result.Results.Count} memor(ies)", result);
    }

    // ---------- unit CRUD ----------

    private static async ValueTask<CallToolResult> ListAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryUnitService>();
        var items = await svc.ListAsync(
            MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, "bankKey")),
            ToolArgs.OptionalString(ctx, "factType"),
            ToolArgs.OptionalString(ctx, "tag"),
            ToolArgs.OptionalInt(ctx, "limit", 20, 200),
            ToolArgs.OptionalInt(ctx, "offset", 0, 1_000_000), ct);
        return await ToolResults.Structured($"{items.Count} unit(s)", new { count = items.Count, units = items });
    }

    private static async ValueTask<CallToolResult> GetAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryUnitService>();
        try
        {
            var unit = await svc.GetAsync(MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, "bankKey")),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")), ct);
            return await ToolResults.Structured("memory unit", unit);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> UpdateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryUnitService>();
        var embeddings = ctx.Services.GetRequiredService<IEmbeddingProvider>();
        try
        {
            var unit = await svc.UpdateAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, "bankKey")),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")),
                ToolArgs.OptionalString(ctx, "text"),
                ToolArgs.OptionalString(ctx, "context"),
                OptionalStringList(ctx, "tags"),
                OptionalStringMap(ctx, "metadata"),
                embeddings, ct);
            return await ToolResults.Structured("memory updated", unit);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> DeleteAsync(ToolCallContext ctx, CancellationToken ct)
    {
        if (ToolArgs.OptionalBool(ctx, "confirm") != true)
            return await ToolResults.Error("memory_delete requires confirm:true");
        var svc = ctx.Services.GetRequiredService<MemoryUnitService>();
        try
        {
            await svc.DeleteAsync(MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, "bankKey")),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")),
                ToolArgs.OptionalBool(ctx, "hard") == true, ct);
            return await ToolResults.Structured("memory deleted", new { deleted = true });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    // ---------- consolidation (SPEC-20261111-memory-observations-consolidation) ----------

    private static async ValueTask<CallToolResult> ConsolidateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var bankKey = MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, "bankKey"));
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            var bank = await banks.ResolveAsync(bankKey, ct, create: false);
            if (ToolArgs.OptionalBool(ctx, "wait") == false)
            {
                var queue = ctx.Services.GetRequiredService<IMemoryRetainQueue>();
                var scope = await banks.ScopeKeyAsync(ct);
                var op = await queue.EnqueueAsync(bank, scope, "consolidate",
                    new { bankKey = bank.BankKey }, ct);
                return await ToolResults.Structured("consolidation queued",
                    new MemoryConsolidateResult(bank.Id, bank.BankKey, 0, 0, 0, 0, 0, 0, true, op.Id));
            }
            var svc = ctx.Services.GetRequiredService<MemoryConsolidationService>();
            var result = await svc.RunAsync(bank, ct);
            return await ToolResults.Structured(
                $"consolidated {result.Processed} fact(s): {result.Created} created, " +
                $"{result.Updated} updated, {result.Skipped} skipped, {result.Failed} failed",
                result);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (MemoryConflictException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (MemoryServiceUnavailableException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> ObsHistoryAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryConsolidationService>();
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            var history = await svc.HistoryAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, "bankKey")),
                ParseGuid(ToolArgs.RequiredString(ctx, "observationId")), banks, ct);
            return await ToolResults.Structured($"{history.Count} version(s)",
                new { count = history.Count, versions = history });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    // ---------- bank CRUD ----------

    private static async ValueTask<CallToolResult> BankListAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        var items = await banks.ListAsync(ct);
        return await ToolResults.Structured($"{items.Count} bank(s)", new { count = items.Count, banks = items });
    }

    private static async ValueTask<CallToolResult> BankCreateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            var bank = await banks.CreateAsync(
                ToolArgs.RequiredString(ctx, "bankKey"),
                ToolArgs.OptionalString(ctx, "name") ?? "",
                ToolArgs.OptionalString(ctx, "mission"),
                OptionalJson(ctx, "disposition"),
                OptionalJson(ctx, "config"), ct);
            return await ToolResults.Structured("bank created", new { bank.Id, bank.BankKey, bank.Name });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> BankUpdateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            var bank = await banks.UpdateAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.RequiredString(ctx, "bankKey"))!,
                ToolArgs.OptionalString(ctx, "name"),
                ToolArgs.OptionalString(ctx, "mission"),
                OptionalJson(ctx, "disposition"),
                OptionalJson(ctx, "config"), ct);
            return await ToolResults.Structured("bank updated", new { bank.Id, bank.BankKey, bank.Name });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> BankDeleteAsync(ToolCallContext ctx, CancellationToken ct)
    {
        if (ToolArgs.OptionalBool(ctx, "confirm") != true)
            return await ToolResults.Error("memory_bank_delete requires confirm:true");
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            await banks.DeleteAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.RequiredString(ctx, "bankKey"))!, ct);
            return await ToolResults.Structured("bank deleted", new { deleted = true });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    // ---------- helpers ----------

    private static Guid ParseGuid(string raw) =>
        Guid.TryParse(raw, out var g)
            ? g
            : throw new McpProtocolException("argument 'id' must be a GUID", McpErrorCode.InvalidParams);

    private static DateTimeOffset? ParseDate(string? raw) =>
        raw is null ? null
            : DateTimeOffset.TryParse(raw, out var d)
                ? d
                : throw new McpProtocolException("argument 'timestamp' must be ISO-8601", McpErrorCode.InvalidParams);

    private static IReadOnlyList<string>? OptionalStringList(ToolCallContext ctx, string name) =>
        ToolArgs.OptionalStringArray(ctx, name);

    private static IReadOnlyDictionary<string, string>? OptionalStringMap(ToolCallContext ctx, string name)
    {
        if (ToolArgs.OptionalElement(ctx, name) is not { } el || el.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string>();
        foreach (var p in el.EnumerateObject())
            map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString();
        return map;
    }

    private static string? OptionalJson(ToolCallContext ctx, string name) =>
        ToolArgs.OptionalElement(ctx, name) is { } el && el.ValueKind == JsonValueKind.Object
            ? el.GetRawText() : null;
}
