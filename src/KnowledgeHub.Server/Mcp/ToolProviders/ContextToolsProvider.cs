using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Context;
using KnowledgeHub.Server.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// <c>context_save</c> / <c>context_recovery</c> / <c>context_compact</c> —
/// SPEC-20261004-context-snapshots RF-004/005/008/009. Repository-scoped
/// working-context snapshots persisted in <c>ContextSnapshots</c> (never the
/// vault, never <c>ConversationMessage</c>). All three require
/// <c>repo</c> (<c>owner/name</c> or github URL, normalized lowercase).
/// <para>
/// All three are <c>ReadOnly=true</c> on purpose: context snapshots are
/// caller-scoped working state, not knowledge-base writes — per the spec
/// decision, <c>context_compact save=true</c> works under read-only keys
/// (the generic write gate must NOT block it).
/// </para>
/// </summary>
public sealed class ContextToolsProvider : IToolProvider
{
    public const string SourceTag = "mcp";

    /// <summary>RF-002 Source enum — the handler's real surface.</summary>
    private static string SourceOf(ToolCallContext ctx) =>
        ctx.Transport == "rest" ? "rest" : SourceTag;

    private static readonly JsonObject SaveSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "repo":{"type":"string","description":"Repository scope 'owner/name' (or github.com URL); normalized lowercase","examples":["afonsoft/knowledgerag"]},
          "label":{"type":"string","description":"Optional human label for the snapshot","examples":["sprint-42"]},
          "idempotencyKey":{"type":"string","description":"Optional dedup key — a repeated save with the same key returns the existing snapshot instead of a new version"},
          "items":{"type":"array","description":"Typed context items to persist","items":{
            "type":"object","properties":{
              "kind":{"type":"string","enum":["decision","note","passage","tool_output","summary"]},
              "id":{"type":"string"},"title":{"type":"string"},"path":{"type":"string"},
              "tool":{"type":"string","description":"Originating tool name for tool_output items"},
              "content":{"type":"string"},"score":{"type":"number"}},
            "required":["kind","content"]}},
          "compact":{"type":"string","enum":["none","elision","elision+summary"],"description":"Compact before saving (default none)"},
          "tokenBudget":{"type":"integer","description":"Token budget for the optional compaction (256–32768, default 4096)"}
        },"required":["repo","items"],
        "examples":[{"repo":"afonsoft/knowledgerag","label":"sprint-42","items":[{"kind":"decision","title":"Usar Tier 4","content":"..."}],"compact":"none"}]}
        """)!.AsObject();

    private static readonly JsonObject RecoverySchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "repo":{"type":"string","description":"Repository scope 'owner/name' (or github.com URL); normalized lowercase","examples":["afonsoft/knowledgerag"]},
          "version":{"type":"integer","description":"Specific version (default: latest)"},
          "label":{"type":"string","description":"Latest snapshot carrying this label (alternative to version)"},
          "tokenBudget":{"type":"integer","description":"Token budget for the returned items (clamped 256–32768, default 4096)"},
          "strategy":{"type":"string","enum":["none","elision","elision+summary"],"description":"Compaction strategy applied to a copy (default none)"}
        },"required":["repo"],
        "examples":[{"repo":"afonsoft/knowledgerag","tokenBudget":2048,"strategy":"elision"}]}
        """)!.AsObject();

    private static readonly JsonObject CompactSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "repo":{"type":"string","description":"Repository scope 'owner/name' (or github.com URL); normalized lowercase","examples":["afonsoft/knowledgerag"]},
          "items":{"type":"array","description":"Items to compact (exclusive with fromVersion)","items":{
            "type":"object","properties":{
              "kind":{"type":"string","enum":["decision","note","passage","tool_output","summary"]},
              "id":{"type":"string"},"title":{"type":"string"},"path":{"type":"string"},
              "tool":{"type":"string"},"content":{"type":"string"},"score":{"type":"number"}},
            "required":["kind","content"]}},
          "fromVersion":{"type":"integer","description":"Compact this saved version (exclusive with items)"},
          "strategy":{"type":"string","enum":["none","elision","elision+summary"],"description":"Default elision+summary"},
          "tokenBudget":{"type":"integer","description":"Clamped 256–32768, default 4096"},
          "save":{"type":"boolean","description":"Persist the compacted result as a new snapshot (default false)"},
          "label":{"type":"string","description":"Label for the saved snapshot"}
        },"required":["repo"],
        "examples":[{"repo":"afonsoft/knowledgerag","fromVersion":3,"strategy":"elision+summary","tokenBudget":2048,"save":true,"label":"pos-compact"}]}
        """)!.AsObject();

    private static readonly JsonObject SaveOutputSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "id":{"type":"string"},"repo":{"type":"string"},"version":{"type":"integer"},
          "createdAt":{"type":"string"},"tokenEstimate":{"type":"integer"},
          "compacted":{"type":"boolean"},"strategy":{"type":"string"},
          "pruned":{"type":"integer"},"idempotentHit":{"type":"boolean"}},
         "required":["id","repo","version","createdAt","tokenEstimate","compacted","strategy"]}
        """)!.AsObject();

    private static readonly JsonObject RecoveryOutputSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "repo":{"type":"string"},"version":{"type":"integer"},"label":{"type":["string","null"]},
          "createdAt":{"type":"string"},"items":{"type":"array","items":{"type":"object"}},
          "stats":{"type":"object"},"withinBudget":{"type":"boolean"}},
         "required":["repo","version","items","stats","withinBudget"]}
        """)!.AsObject();

    // SPEC-20261021-context-rest-contract-fixes RF-3: the real response shape
    // (`compacted` array + optional `savedVersion`), not the recovery shape.
    private static readonly JsonObject CompactOutputSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "repo":{"type":"string"},
          "compacted":{"type":"array","items":{"type":"object"}},
          "droppedCount":{"type":"integer"},"elidedCount":{"type":"integer"},
          "stats":{"type":"object"},"withinBudget":{"type":"boolean"},
          "savedVersion":{"type":["integer","null"]}},
         "required":["repo","compacted","stats","withinBudget"]}
        """)!.AsObject();

    public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "context_save",
                Title = "Save context snapshot",
                Description = "Persists a versioned working-context snapshot for a repository (typed items: decision/note/passage/tool_output/summary). Use to checkpoint long-running work; recover later with context_recovery. Never writes to the vault.",
                InputSchema = SaveSchema,
                OutputSchema = SaveOutputSchema,
                ReadOnly = true, // caller-scoped state, not a knowledge-base write
                IdempotentHint = true, // via idempotencyKey
                Handler = SaveAsync
            },
            new CatalogTool
            {
                Name = "context_recovery",
                Title = "Recover context snapshot",
                Description = "Returns the latest (or selected) context snapshot for a repository, compacted to the requested token budget. The stored snapshot is never mutated.",
                InputSchema = RecoverySchema,
                OutputSchema = RecoveryOutputSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = RecoverAsync
            },
            new CatalogTool
            {
                Name = "context_compact",
                Title = "Compact context",
                Description = "Compacts a set of items (or an existing snapshot) with deterministic elision plus optional LLM summarization, then optionally persists the result via save=true.",
                InputSchema = CompactSchema,
                OutputSchema = CompactOutputSchema,
                ReadOnly = true, // save=true writes caller-scoped snapshots only
                IdempotentHint = true,
                Handler = CompactAsync
            }
        ];
        return Task.FromResult(tools);
    }

    // ---- shared plumbing -------------------------------------------------

    private static RepoScope ParseRepo(ToolCallContext ctx) =>
        RepoScope.Parse(ToolArgs.OptionalString(ctx, "repo"));

    private static int TokenBudget(ToolCallContext ctx, ContextOptions options) =>
        Math.Clamp(
            ToolArgs.OptionalIntOrNull(ctx, "tokenBudget") ?? options.DefaultTokenBudget,
            256, 32768);

    private static string Strategy(ToolCallContext ctx, string fallback)
    {
        var s = ToolArgs.OptionalString(ctx, "strategy") ?? fallback;
        return s is ContextCompactionService.StrategyNone
            or ContextCompactionService.StrategyElision
            or ContextCompactionService.StrategyElisionSummary
            ? s
            : throw new McpProtocolException(
                $"invalid_payload: unknown strategy '{s}' (none|elision|elision+summary)",
                McpErrorCode.InvalidParams);
    }

    private static JsonElement? ItemsArg(ToolCallContext ctx) =>
        ctx.Arguments is not null && ctx.Arguments.TryGetValue("items", out var el)
            ? el
            : null;

    private static object StatsObject(ContextCompactionService.CompactionStats s) => new
    {
        tokensBefore = s.TokensBefore,
        tokensAfter = s.TokensAfter,
        budget = s.Budget,
        droppedCount = s.DroppedCount,
        elidedCount = s.ElidedCount,
        summaryUsed = s.SummaryUsed,
        summaryFallback = s.SummaryFallback,
        cacheHit = s.CacheHit
    };

    // ---- handlers --------------------------------------------------------

    private static async ValueTask<CallToolResult> SaveAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        using var activity = KnowledgeHubActivity.Start("context.save");
        var repo = ParseRepo(ctx);
        var options = ctx.Services.GetRequiredService<IOptions<ContextOptions>>().Value;
        var items = ContextPayload.ParseItems(ItemsArg(ctx), options.MaxItemsPerSnapshot);
        var label = ToolArgs.OptionalString(ctx, "label");
        ContextPayload.EnsureNoSecret(label, "label");
        var compact = Strategy(ctx, "none");
        var budget = TokenBudget(ctx, options);

        var compacted = false;
        ContextCompactionService.CompactionStats? compactionStats = null;
        if (compact != ContextCompactionService.StrategyNone)
        {
            var compactor = ctx.Services.GetRequiredService<ContextCompactionService>();
            var result = await compactor.CompactAsync(items, compact, budget, ct);
            items = result.Items;
            compacted = true;
            compactionStats = result.Stats;
        }

        var service = ctx.Services.GetRequiredService<ContextSnapshotService>();
        var save = await service.SaveAsync(
            repo, items, label, ToolArgs.OptionalString(ctx, "idempotencyKey"),
            compact, compacted, SourceOf(ctx), CallerIdentity.TryGetApiKeyId(ctx), ct);

        var snap = save.Snapshot;
        return await ToolResults.Structured(
            $"Saved context snapshot v{snap.Version} for {repo.Key}" +
            (save.Pruned > 0 ? $" (pruned {save.Pruned} old)" : "") +
            (save.IdempotentHit ? " [idempotent hit]" : ""),
            new
            {
                id = snap.Id,
                repo = repo.Key,
                version = snap.Version,
                createdAt = snap.CreatedAt,
                tokenEstimate = snap.TokenEstimate,
                compacted = snap.Compacted,
                strategy = snap.Strategy,
                pruned = save.Pruned,
                idempotentHit = save.IdempotentHit,
                stats = compactionStats is null ? null : StatsObject(compactionStats)
            });
    }

    private static async ValueTask<CallToolResult> RecoverAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        using var activity = KnowledgeHubActivity.Start("context.recovery");
        var repo = ParseRepo(ctx);
        var options = ctx.Services.GetRequiredService<IOptions<ContextOptions>>().Value;
        var service = ctx.Services.GetRequiredService<ContextSnapshotService>();
        var cache = ctx.Services.GetRequiredService<ContextCacheService>();
        var strategy = Strategy(ctx, "none");
        var budget = TokenBudget(ctx, options);
        var version = ToolArgs.OptionalIntOrNull(ctx, "version");
        var label = ToolArgs.OptionalString(ctx, "label");
        if (version is not null && label is not null)
            return await ToolResults.Error(
                "invalid_payload: 'version' and 'label' are mutually exclusive");

        var selector = version?.ToString() ?? (label is not null ? $"label:{label}" : "latest");
        var cacheKey = ContextCacheKeys.Recovery(repo.Key, selector, strategy, budget);

        var (response, hit) = await cache.GetOrCreateAsync<RecoveryResponse>(
            cacheKey, ContextCacheKeys.RepoTag(repo.Key),
            async token =>
            {
                var snap = version is { } v
                    ? await service.GetByVersionAsync(repo, v, token)
                    : label is { } l
                        ? await service.GetByLabelAsync(repo, l, token)
                        : await service.GetLatestAsync(repo, token);
                if (snap is null)
                    return null!;
                var compactor = ctx.Services.GetRequiredService<ContextCompactionService>();
                var result = await compactor.CompactAsync(
                    ContextSnapshotService.ItemsOf(snap), strategy, budget, token);
                return new RecoveryResponse(
                    repo.Key, snap.Version, snap.Label, snap.CreatedAt,
                    result.Items, result.Stats with { CacheHit = false },
                    result.WithinBudget,
                    result.Stats.SummaryFallback);
            }, ct);

        if (response is not RecoveryResponse found)
            return await ToolResults.Error(
                "context_not_found: no snapshot for the requested repo/selector");

        var stats = found.Stats with { CacheHit = hit };
        return await ToolResults.Structured(
            $"Context v{found.Version} for {repo.Key}: {found.Items.Count} items, " +
            $"{stats.TokensAfter}/{stats.Budget} tokens" +
            (hit ? " [cache hit]" : "") +
            (found.WithinBudget ? "" : " [over budget — protected items only]"),
            new
            {
                repo = found.Repo,
                version = found.Version,
                label = found.Label,
                createdAt = found.CreatedAt,
                items = found.Items,
                stats = StatsObject(stats),
                withinBudget = found.WithinBudget
            });
    }

    private sealed record RecoveryResponse(
        string Repo, int Version, string? Label, DateTimeOffset CreatedAt,
        List<ContextItem> Items, ContextCompactionService.CompactionStats Stats,
        bool WithinBudget, bool SummaryFallback) : IContextCacheBypass
    {
        public bool BypassCache => SummaryFallback;
    }

    private static async ValueTask<CallToolResult> CompactAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        using var activity = KnowledgeHubActivity.Start("context.compact");
        var repo = ParseRepo(ctx);
        var options = ctx.Services.GetRequiredService<IOptions<ContextOptions>>().Value;
        var itemsEl = ItemsArg(ctx);
        var fromVersion = ToolArgs.OptionalIntOrNull(ctx, "fromVersion");
        var hasItems = itemsEl is { ValueKind: JsonValueKind.Array };
        if (hasItems == (fromVersion is not null))
            return await ToolResults.Error(
                "invalid_payload: exactly one of 'items' or 'fromVersion' is required");

        var service = ctx.Services.GetRequiredService<ContextSnapshotService>();
        var cache = ctx.Services.GetRequiredService<ContextCacheService>();
        var strategy = Strategy(ctx, ContextCompactionService.StrategyElisionSummary);
        var budget = TokenBudget(ctx, options);
        var save = ToolArgs.OptionalBool(ctx, "save") == true;
        var label = ToolArgs.OptionalString(ctx, "label");
        ContextPayload.EnsureNoSecret(label, "label");

        List<ContextItem> input;
        if (hasItems)
            input = ContextPayload.ParseItems(itemsEl, options.MaxItemsPerSnapshot);
        else
        {
            var snap = await service.GetByVersionAsync(repo, fromVersion!.Value, ct);
            if (snap is null)
                return await ToolResults.Error(
                    $"context_not_found: version {fromVersion} not found for {repo.Key}");
            input = ContextSnapshotService.ItemsOf(snap);
        }

        var canonical = ContextPayload.Serialize(input);
        var cacheKey = ContextCacheKeys.Compact(repo.Key, canonical, strategy, budget);

        // save=true bypasses the cache for the write path (RF-007).
        var (result, hit) = save
            ? (await CompactFresh(ctx, input, strategy, budget, ct), false)
            : await cache.GetOrCreateAsync(
                cacheKey, ContextCacheKeys.RepoTag(repo.Key),
                token => CompactFresh(ctx, input, strategy, budget, token), ct);

        if (result is null)
            return await ToolResults.Error("internal: compaction produced no result");

        int? savedVersion = null;
        if (save)
        {
            var saved = await service.SaveAsync(
                repo, result.Items, label, null,
                strategy, compacted: true, SourceOf(ctx), CallerIdentity.TryGetApiKeyId(ctx), ct);
            savedVersion = saved.Snapshot.Version;
        }

        var stats = result.Stats with { CacheHit = hit };
        return await ToolResults.Structured(
            $"Compacted {result.Items.Count} items for {repo.Key}: " +
            $"{stats.TokensBefore}→{stats.TokensAfter} tokens" +
            (stats.SummaryFallback ? " [deterministic summary fallback]" : "") +
            (savedVersion is { } v ? $" saved as v{v}" : ""),
            new
            {
                repo = repo.Key,
                compacted = result.Items,
                droppedCount = stats.DroppedCount,
                elidedCount = stats.ElidedCount,
                stats = StatsObject(stats),
                withinBudget = result.WithinBudget,
                savedVersion
            });
    }

    private static async Task<ContextCompactionService.CompactionResult?> CompactFresh(
        ToolCallContext ctx, List<ContextItem> input, string strategy,
        int budget, CancellationToken token) =>
        await ctx.Services.GetRequiredService<ContextCompactionService>()
            .CompactAsync(input, strategy, budget, token);
}
