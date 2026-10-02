using System.Diagnostics;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Telemetry;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Search;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace KnowledgeHub.Server.Services;

/// <summary>
/// Embeds the query, ranks via <see cref="IVectorStore"/> and/or the FTS5 lexical
/// index, and hydrates results (SPEC-02 RF-004, SPEC-20260914-hybrid-retrieval
/// RF-002: hybrid mode fuses both rankings with RRF k=60 over a topK×4 window).
/// SPEC-20260916-performance-memory-cache RF-005: query embeddings and result
/// sets are cached in <see cref="IDistributedCache"/> — result keys embed the
/// index-version token so any sync invalidates them.
/// </summary>
public sealed class SearchService( // NOSONAR S107 — DI resolve o ctor flat; construção manual/testes usam o deps-ctor agrupado abaixo
    KnowledgeHubDbContext db,
    IEmbeddingProvider embeddings,
    IEmbeddingProviderResolver embeddingsResolver,
    IVectorStore vectors,
    ILexicalSearchService lexical,
    IDistributedCache cache,
    IConfiguration configuration,
    IQueryRewriter rewriter,
    IQueryExpander expander,
    Graph.GraphEntityLinker graphLinker,
    IReranker reranker,
    Auth.ICallerScopeProvider callerScope,
    Settings.IGraphSettingsService graphSettings,
    ILogger<SearchService> logger) : ISearchService
{
    /// <summary>Grouped-dependency convenience ctor (S107): the retrieval and
    /// pipeline param objects in <c>SearchServiceDeps.cs</c> unpack to the
    /// primary ctor.</summary>
    public SearchService(SearchRetrievalDeps retrieval, SearchPipelineDeps pipeline,
        IConfiguration configuration, ILogger<SearchService> logger)
        : this(retrieval.Db, retrieval.Embeddings, retrieval.EmbeddingsResolver,
            retrieval.Vectors, retrieval.Lexical, retrieval.Cache,
            configuration, pipeline.Rewriter, pipeline.Expander,
            pipeline.GraphLinker, pipeline.Reranker, pipeline.CallerScope,
            pipeline.GraphSettings, logger)
    {
    }

    private const int CandidateWindowFactor = 4;
    // TTLs: region policy (emb:/search: prefixes) — SPEC-20260925-cache-region-ttl-policies.

    /// <summary>SPEC-20260926-cache-coherence-and-ttl RF-005: set by
    /// <see cref="VectorSearchAsync"/> when an arm fails-soft — the caller
    /// still returns the degraded result but never caches it.</summary>
    private sealed class DegradationState { public bool Any; }

    /// <summary>Strip CR/LF from caller-supplied values before logging.</summary>
    private static string? ForLog(string? value) =>
        value?.Replace('\r', ' ').Replace('\n', ' ');

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query, int topK, Guid? sourceId = null,
        SearchMode mode = SearchMode.Hybrid, ResolvedSearchFilter? filter = null,
        string? conversationContext = null, CancellationToken ct = default)
    {
        var modeName = mode.ToString().ToLowerInvariant();
        using var activity = KnowledgeHubActivity.Start("search");
        activity?.SetTag("search.mode", modeName);
        activity?.SetTag("search.topK", topK);
        var stopwatch = Stopwatch.StartNew();
        var cacheHit = false;
        try
        {
            var scope = await callerScope.GetAsync(ct);
            var indexVersion = await GetIndexVersionAsync(ct);
            // conversationContext alters the effective (rewritten) query — it is
            // part of the result identity (SPEC-20260924-conversational-query-context).
            var resultKey = CacheKeys.Search(
                mode.ToString(), topK, sourceId, filter?.Fingerprint() ?? "-",
                scope.SourceFingerprint,
                conversationContext is null ? query : $"{CacheKeys.Hash(conversationContext)}|{query}",
                indexVersion);
            var cached = await SafeCache.GetStringAsync(cache, resultKey, logger, ct);
            if (cached is not null)
            {
                var hit = JsonSerializerSafely(cached);
                if (hit is not null)
                {
                    cacheHit = true;
                    activity?.SetTag("cache.hit", true);
                    return hit;
                }
            }

            var degraded = new DegradationState();
            var results = await ExecuteAsync(
                new SearchInvocation(query, topK, sourceId, mode, filter, scope, conversationContext, degraded), ct);
            // RF-005: a result produced while a search arm was degraded is
            // served but never cached — a transient vector-store outage must
            // not poison the result cache for the normal TTL.
            if (!degraded.Any)
                await SafeCache.SetJsonAsync(cache, resultKey, results, null, logger, ct);
            return results;
        }
        catch (Exception ex)
        {
            KnowledgeHubActivity.Fail(activity, ex);
            throw;
        }
        finally
        {
            KnowledgeHubMetrics.SearchDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("mode", modeName),
                new KeyValuePair<string, object?>("cache_hit", cacheHit));
        }
    }

    private sealed record SearchInvocation(
        string Query, int TopK, Guid? SourceId, SearchMode Mode,
        ResolvedSearchFilter? Filter, Auth.CallerScope Scope,
        string? ConversationContext, DegradationState Degraded, int RelaxLevel = 0);

    private async Task<IReadOnlyList<SearchResultItem>> ExecuteAsync(
        SearchInvocation inv, CancellationToken ct)
    {
        var (query, topK, sourceId, mode, filter, scope, conversationContext, degraded) =
            (inv.Query, inv.TopK, inv.SourceId, inv.Mode, inv.Filter, inv.Scope,
             inv.ConversationContext, inv.Degraded);
        var activeSourceIds = await ResolveActiveSourcesAsync(sourceId, scope, ct);
        if (activeSourceIds.Count == 0)
            return [];

        var effectiveQuery = await RewriteQueryAsync(query, mode, conversationContext, ct);
        var plan = ResolveFetchPlan(topK, filter);

        var (windowed, breakdowns) = plan.UseSemanticFastPath(mode)
            ? (await SemanticFastPathAsync(effectiveQuery, plan.FetchLimit, activeSourceIds, filter, degraded, ct), null)
            : await FusedSearchAsync(
                new FusedSearchContext(query, effectiveQuery, mode, plan, activeSourceIds, filter, degraded), ct);

        var items = await HydrateTrackedAsync(windowed, breakdowns, filter, ct);
        items = await ApplyDiversityAsync(items, topK, ct);
        var final = await ApplyFinalTrimAsync(query, items, topK, filter, plan, ct);

        final = await RelaxIfShortAsync(inv, final, ct);
        return await ApplyContextExpansionAsync(final, filter, ct);
    }

    private sealed record FetchPlan(
        bool RerankEnabled, bool DiversityEnabled, int Window, int FetchLimit,
        string ExpansionMode, bool GraphEnabled, List<string>? SubQueries)
    {
        public bool UseSemanticFastPath(SearchMode mode) =>
            mode == SearchMode.Semantic && ExpansionMode == "off" && !GraphEnabled
            && SubQueries is not { Count: > 0 };
    }

    private FetchPlan ResolveFetchPlan(int topK, ResolvedSearchFilter? filter)
    {
        var rerankEnabled = configuration.GetValue("Search:Rerank:Enabled", false);
        var diversityEnabled = configuration.GetValue("Search:Diversity:Enabled", false);
        var filtersActive = filter is { IsEmpty: false };
        // SPEC-20261001-mcp-recall-ergonomics RF-001: budget narrows the
        // candidate pool — low = half the usual window; mid/high keep the
        // default pool (mid differs from high only by skipping expansion).
        var window = filter?.Budget switch
        {
            "low" => Math.Max(topK, topK * CandidateWindowFactor / 2),
            _ => topK * CandidateWindowFactor
        };
        var rerankCap = configuration.GetValue("Search:Rerank:MaxCandidates", 20);

        // Fetch a wider window when filters, diversity or rerank need room to
        // work; otherwise hydrate exactly topK — identical to the pre-filter pipeline.
        var fetchLimit = topK;
        if (rerankEnabled)
            fetchLimit = Math.Min(window, Math.Max(rerankCap, topK));
        else if (filtersActive || diversityEnabled)
            fetchLimit = window;

        // SPEC-20260924-query-expansion-hyde: expansion mode — per-call `expand`
        // filter arg wins over config; off = the classic single-query pipeline.
        // RF-001: budget low/mid skips expansion unless the caller set `expand`
        // explicitly — cheap calls must not pay for extra LLM expansion rounds.
        var expansionMode = filter?.Expansion
            ?? (filter?.SkipsExpansion == true
                ? "off"
                : configuration.GetValue("Search:QueryExpansion:Mode", "off"));

        // SPEC-20260924-graph-expanded-retrieval RF-001/RF-002: optional third
        // arm — entity-linked chunks (direct + 1-hop) fused via the same RRF.
        var graphEnabled = filter?.UseGraph
            ?? configuration.GetValue("Search:Graph:Enabled", false);

        // SPEC-20260927-multiquery RF-001: caller-supplied sub-queries force the
        // fused path — each becomes an extra vector+lexical arm under the same RRF.
        var subQueries = filter?.SubQueries?
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim())
            .Take(4)
            .ToList();
        if (subQueries is { Count: > 0 })
        {
            KnowledgeHubMetrics.MultiQueryDispatched.Add(subQueries.Count);
            Activity.Current?.SetTag("search.multiquery.count", subQueries.Count);
            logger.LogInformation("MultiQueryDispatched count={Count}", subQueries.Count);
        }

        return new FetchPlan(rerankEnabled, diversityEnabled, window, fetchLimit,
            expansionMode, graphEnabled, subQueries);
    }

    /// <summary>SPEC-20260923-source-authorization RF-003: intersect with the key's
    /// source scope before any vector/lexical call — never retrieve-then-
    /// filter. An explicit sourceId outside scope yields an empty result
    /// (not an error) and a SourceScopeDenied audit event.</summary>
    private async Task<List<Guid>> ResolveActiveSourcesAsync(
        Guid? sourceId, Auth.CallerScope scope, CancellationToken ct)
    {
        var activeSourceIds = sourceId is null
            ? await db.Sources.Where(s => s.IsActive).Select(s => s.Id).ToListAsync(ct)
            : [sourceId.Value];

        if (scope.AllowedSourceIds is { } allowed)
        {
            if (sourceId is { } requested && !allowed.Contains(requested))
                await Auth.ScopeAudit.RecordAsync(
                    db, scope.ApiKeyId, Auth.ScopeAudit.SourceDenied, sourceId: requested, ct: ct);
            activeSourceIds = activeSourceIds.Where(allowed.Contains).ToList();
        }
        return activeSourceIds;
    }

    /// <summary>RF-001/RF-002: rewrite + rerank run on the user's retrieval intent.
    /// Lexical skips rewriting unless explicitly opted in.
    /// SPEC-20260925-otel-pipeline-spans: rewrite boundary span.</summary>
    private async Task<string> RewriteQueryAsync(
        string query, SearchMode mode, string? conversationContext, CancellationToken ct)
    {
        using var span = Telemetry.KnowledgeHubActivity.Start("search.rewrite");
        try
        {
            return mode == SearchMode.Lexical
                && !configuration.GetValue("Search:QueryRewrite:LexicalToo", false)
                ? query
                : await rewriter.RewriteAsync(query, conversationContext, ct);
        }
        catch (Exception ex) { Telemetry.KnowledgeHubActivity.Fail(span, ex); throw; }
    }

    private async Task<List<VectorHit>> SemanticFastPathAsync(
        string effectiveQuery, int fetchLimit, List<Guid> activeSourceIds,
        ResolvedSearchFilter? filter, DegradationState degraded, CancellationToken ct)
    {
        var queryVector = await EmbedQueryAsync(effectiveQuery, ct);
        var hits = await VectorSearchAsync(queryVector, fetchLimit, activeSourceIds, degraded, ct);
        // RF-003: the semantic floor applies on the fast path too — the
        // common semantic+no-expansion call must not bypass minScores.
        if (filter?.MinScores?.Semantic is { } semanticFloor)
            hits = hits.Where(h => h.Score >= semanticFloor).ToList();
        return hits.ToList();
    }

    /// <summary>Inputs of a fused search run — kept as one object so the
    /// signature stays under the 7-parameter cap (RF-006/S107).</summary>
    private sealed record FusedSearchContext(
        string Query, string EffectiveQuery, SearchMode Mode, FetchPlan Plan,
        List<Guid> ActiveSourceIds, ResolvedSearchFilter? Filter, DegradationState Degraded);

    private async Task<(List<VectorHit> Windowed, IReadOnlyDictionary<Guid, SearchScoreBreakdown>? Breakdowns)>
        FusedSearchAsync(FusedSearchContext ctx, CancellationToken ct)
    {
        var (vectorLabels, vectorLists, lexicalLabels, lexicalLists) =
            await ExpandAndSearchAsync(ctx.Query, ctx.EffectiveQuery,
                new ArmRequest(ctx.Mode, ctx.Plan.ExpansionMode, ctx.Plan.Window,
                    ctx.Filter?.MinScores?.Semantic, ctx.Filter?.MinScores?.Lexical, ctx.Plan.SubQueries),
                ctx.ActiveSourceIds, ctx.Degraded, ct);

        var graphArm = ctx.Plan.GraphEnabled
            ? await GraphRankedAsync(ctx.Query, ct)
            : (Ranked: (IReadOnlyList<Guid>)Array.Empty<Guid>(), DirectChunks: new HashSet<Guid>());

        var rankedLists = vectorLists.Select(l => ("vector", l))
            .Concat(lexicalLists.Select(l => ("lexical", l)))
            .Concat(graphArm.Ranked.Count > 0 ? [("graph", graphArm.Ranked)] : [])
            .ToList();
        IReadOnlyList<FusedHit> fused;
        using (KnowledgeHubActivity.Start("search.rrf"))
            fused = RrfFuser.Fuse(rankedLists, ctx.Plan.FetchLimit);

        // RF-003: gentle boost on chunks with direct-entity evidence.
        var boost = configuration.GetValue("Search:Graph:Boost", 1.0);
        if (graphArm.DirectChunks.Count > 0 && Math.Abs(boost - 1.0) > 0.001)
            fused = fused
                .Select(f => graphArm.DirectChunks.Contains(f.ChunkId)
                    ? f with { Fused = f.Fused * boost }
                    : f)
                .OrderByDescending(f => f.Fused).ThenBy(f => f.ChunkId)
                .Take(ctx.Plan.FetchLimit)
                .ToList();

        // SPEC-20260929 RF: an empty fused result must fall through to the
        // relaxation path below — early return skips the scope cascade.
        if (fused.Count == 0)
            return ([], null);

        // ExpandedFrom: first list (in arm order) that surfaced the chunk.
        var expandedFrom = new Dictionary<Guid, string>();
        foreach (var (label, list) in vectorLabels.Zip(vectorLists).Concat(lexicalLabels.Zip(lexicalLists)))
            if (label is not null)
                foreach (var id in list)
                    expandedFrom.TryAdd(id, label);

        // E24 RF-001: normalize fused to 0-1 (1.0 = rank 1 on every
        // arm) so MinScores.Final shares the scale callers document.
        var fusedMax = rankedLists.Count * (1.0 / (RrfFuser.K + 1));
        var breakdowns = fused.ToDictionary(
            f => f.ChunkId,
            f => new SearchScoreBreakdown
            {
                VectorRank = f.VectorRank,
                LexicalRank = f.LexicalRank,
                Fused = f.Fused,
                Normalized = fusedMax > 0 ? Math.Min(1.0, f.Fused / fusedMax) : 0,
                GraphRank = f.GraphRank,
                ExpandedFrom = expandedFrom.GetValueOrDefault(f.ChunkId)
            });
        return (fused.Select(f => new VectorHit(f.ChunkId, f.Fused)).ToList(), breakdowns);
    }

    private async Task<List<SearchResultItem>> HydrateTrackedAsync(
        List<VectorHit> windowed,
        IReadOnlyDictionary<Guid, SearchScoreBreakdown>? breakdowns,
        ResolvedSearchFilter? filter, CancellationToken ct)
    {
        using var hydrateSpan = KnowledgeHubActivity.Start("hydrate");
        try
        {
            return await HydrateAsync(windowed, breakdowns, filter, ct);
        }
        catch (Exception ex)
        {
            KnowledgeHubActivity.Fail(hydrateSpan, ex);
            throw;
        }
    }

    /// <summary>Post-hydration trim: optional rerank, temporal boost, autocut
    /// elbow and the final score floor.</summary>
    private async Task<List<SearchResultItem>> ApplyFinalTrimAsync(
        string query, List<SearchResultItem> items, int topK,
        ResolvedSearchFilter? filter, FetchPlan plan, CancellationToken ct)
    {
        var final = (!plan.RerankEnabled || items.Count <= 1)
            ? items.Take(topK).ToList()
            : await RerankAsync(query, items, topK, ct);

        // SPEC-20260927-chunk-window-retrieval-and-autocut RF-003: dynamic tail
        // pruning — the elbow in the score curve decides the count (≤topK).
        // SPEC-20260929-search-scope-pipeline RF-001: unfiltered searches also
        // honor the configured default — a null filter must not force "fixed".
        // SPEC-20261001-mcp-recall-ergonomics RF-004: explicit temporal window
        // boosts in-window hits before the elbow sees the scores (boost, not
        // filter — paridade com o hindsight recall temporal_window).
        final = ApplyTemporalBoost(final, filter?.TemporalStart, filter?.TemporalEnd);

        var limitMode = filter?.EffectiveLimitMode(configuration)
            ?? configuration.GetValue("Search:LimitMode", "fixed");
        if (limitMode == "autocut" && final.Count > 1)
        {
            var sensitivity = Math.Clamp(filter?.AutocutSensitivity
                ?? configuration.GetValue("Search:Autocut:Sensitivity", 1), 1, 3);
            var maxClamp = Math.Min(topK, Math.Max(1,
                configuration.GetValue("Search:Autocut:MaxClamp", AutocutFilter.DefaultMaxClamp)));
            final = AutocutFilter.Apply(final, sensitivity, maxClamp);
        }

        // RF-003: per-call final floor — post-fusion, post-autocut. An empty
        // result is the honest answer (ask_knowledge abstains on it).
        if (filter?.MinScores?.Final is { } minFinal && final.Count > 0)
            final = final.Where(i => (i.ScoreBreakdown?.Normalized ?? i.Score) >= minFinal).ToList();

        return final.ToList();
    }

    /// <summary>SPEC-20260927-multiquery RF-002: hierarchical scope fallback —
    /// driven only from the strict level (relaxLevel==0); the loop owns the
    /// cascade. SPEC-20260929 RF-005: a denied sourceId must never widen into
    /// other sources, even ones inside the caller's scope.</summary>
    private async Task<List<SearchResultItem>> RelaxIfShortAsync(
        SearchInvocation inv, List<SearchResultItem> final, CancellationToken ct)
    {
        var relaxAllowed = inv.RelaxLevel == 0
            && (inv.Filter?.AllowRelaxation
                ?? configuration.GetValue("Search:Relaxation:Enabled", true));
        var deniedSource = inv.SourceId is { } s
            && inv.Scope.AllowedSourceIds is { } callerAllowed
            && !callerAllowed.Contains(s);
        var minResults = Math.Max(0, configuration.GetValue("Search:Relaxation:MinResults", 1));
        if (relaxAllowed && !deniedSource && final.Count < minResults)
            final = await ApplyRelaxationAsync(
                new RelaxationQuery(inv.Query, inv.TopK, inv.SourceId, inv.Filter,
                    inv.Mode, inv.Scope, inv.ConversationContext, inv.Degraded, minResults),
                final.ToList(), ct);
        return final;
    }

    private const double RelaxationPenalty = 0.85;

    /// <summary>RF-004: recency boost — hits whose document was indexed inside
    /// the window rank higher (boost, not filter). The boost applies to every
    /// score axis downstream readers see — <see cref="SearchResultItem.Score"/>,
    /// fused and rerank breakdown — so the autocut elbow and the final floor
    /// read the same boosted curve that ordered the list.</summary>
    /// <remarks>The null-window early return lives here (not at the caller)
    /// so the helper is safe to call unconditionally.</remarks>
    private static List<SearchResultItem> ApplyTemporalBoost(
        IReadOnlyList<SearchResultItem> items, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null && end is null)
            return items.ToList();
        return items
            .Select(i => ApplyTemporalBoost(i, start, end))
            .OrderByDescending(AutocutFilter.EffectiveScore)
            .ToList();
    }

    private static SearchResultItem ApplyTemporalBoost(
        SearchResultItem i, DateTimeOffset? start, DateTimeOffset? end)
    {
        if (i.IndexedAt is not { } at
            || (start is not null && at < start)
            || (end is not null && at > end))
            return i;
        return i with
        {
            Score = i.Score * TemporalBoost,
            ScoreBreakdown = i.ScoreBreakdown is { } bd
                ? bd with
                {
                    Fused = bd.Fused * TemporalBoost,
                    Normalized = Math.Min(1.0, bd.Normalized * TemporalBoost),
                    Rerank = bd.Rerank * TemporalBoost
                }
                : null
        };
    }

    /// <summary>RF-004: in-window boost factor (modest — window ranks higher
    /// without drowning out strong out-of-window matches).</summary>
    private const double TemporalBoost = 1.1;

    /// <summary>Query context for the relaxation cascade (SPEC-20260927-multiquery RF-002).</summary>
    private sealed record RelaxationQuery(
        string Query, int TopK, Guid? SourceId, ResolvedSearchFilter? Filter,
        SearchMode Mode, Auth.CallerScope Scope, string? ConversationContext,
        DegradationState Degraded, int MinResults);

    /// <summary>
    /// SPEC-20260927-multiquery RF-002: cascades the scope until MinResults is
    /// met — drop pathPrefix → sourceId→its SourceType → global (still bounded by
    /// the caller's allowed-source scope). Relaxed hits merge with the strict
    /// ones under a 0.85^level score penalty so strict matches keep precedence.
    /// </summary>
    private async Task<List<SearchResultItem>> ApplyRelaxationAsync(
        RelaxationQuery q, List<SearchResultItem> strict, CancellationToken ct)
    {
        var (query, topK, sourceId, filter, mode, scope, conversationContext, degraded, minResults) = q;
        var merged = strict.ToList();
        var seen = strict.Where(i => i.ChunkId is not null)
            .Select(i => i.ChunkId!.Value).ToHashSet();
        var curSourceId = sourceId;
        var curFilter = filter;
        var relaxed = new List<SearchResultItem>();

        // Stop cascading as soon as strict+relaxed hits satisfy MinResults —
        // wider scopes than needed would dilute the response with off-scope hits.
        for (var level = 1; level <= 3 && merged.Count + relaxed.Count < minResults; level++)
        {
            var next = await NextScopeAsync(curSourceId, curFilter, ct);
            if (next is null)
                break;
            curSourceId = next.Value.SourceId;
            curFilter = next.Value.Filter;

            var hits = await ExecuteAsync(
                new SearchInvocation(query, topK, curSourceId, mode, curFilter,
                    scope, conversationContext, degraded, level), ct);
            var penalty = Math.Pow(RelaxationPenalty, level);
            var added = 0;
            foreach (var h in hits.Where(h => h.ChunkId is not { } cid || seen.Add(cid)))
            {
                relaxed.Add(h with
                {
                    IsRelaxed = true,
                    RelaxedScope = next.Value.Description,
                    Score = h.Score * penalty,
                    ScoreBreakdown = h.ScoreBreakdown is { } b
                        ? b with { Fused = b.Fused * penalty, Normalized = b.Normalized * penalty } : null
                });
                added++;
            }
            if (added == 0)
                continue;

            KnowledgeHubMetrics.FilterRelaxations.Add(1,
                new KeyValuePair<string, object?>("level", level));
            Activity.Current?.SetTag("search.relaxation.level", level);
            logger.LogInformation(
                "RetrievalFilterRelaxed level={Level} scope={Scope} added={Added}",
                level, next.Value.Description, added);
        }

        // SPEC-20260929 RF-005: strict hits keep their lead — relaxed results are
        // appended after (ordered among themselves), never re-sorted above them.
        return merged
            .Concat(relaxed.OrderByDescending(i => i.ScoreBreakdown?.Fused ?? i.Score))
            .Take(topK)
            .ToList();
    }

    /// <summary>Next scope in the relaxation cascade; null when fully relaxed.</summary>
    private async Task<(Guid? SourceId, ResolvedSearchFilter? Filter, string Description)?>
        NextScopeAsync(Guid? sourceId, ResolvedSearchFilter? filter, CancellationToken ct)
    {
        // Level 1: drop the tag-like restriction, keep the source.
        if (filter?.PathPrefix is not null)
        {
            var f = filter with { PathPrefix = null };
            return (sourceId, f, ResolvedSearchFilter.DescribeScope(sourceId, f));
        }

        // Level 2: drop the concrete source, keep its connector type.
        if (sourceId is { } sid)
        {
            var type = await db.Sources.AsNoTracking()
                .Where(s => s.Id == sid)
                .Select(s => (SourceType?)s.SourceType)
                .FirstOrDefaultAsync(ct);
            var f = filter is null
                ? new ResolvedSearchFilter(type, null, null, null)
                : filter with { SourceType = type };
            return (null, f, ResolvedSearchFilter.DescribeScope(null, f));
        }

        // Level 3: drop the source-type restriction — global (still caller-scoped).
        if (filter?.SourceType is not null)
        {
            var f = filter with { SourceType = null };
            return (null, f, "global");
        }

        return null;
    }

    /// <summary>RF-002: re-orders the hydrated window by rerank score; any
    /// failure preserves the fused order (fail-open).</summary>
    private async Task<IReadOnlyList<SearchResultItem>> RerankAsync(
        string query, List<SearchResultItem> items, int topK, CancellationToken ct)
    {
        IReadOnlyList<RerankScore> scores;
        using var span = Telemetry.KnowledgeHubActivity.Start("search.rerank");
        span?.SetTag("rerank.candidates", items.Count);
        try
        {
            scores = await reranker.RerankAsync(query, items, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Telemetry.KnowledgeHubActivity.Fail(span, ex);
            logger.LogWarning(ex, "Reranker failed — returning fused order");
            return items.Take(topK).ToList();
        }

        if (scores.Count == 0)
            return items.Take(topK).ToList();

        var scoreById = scores.ToDictionary(s => s.ChunkId, s => s.Score);
        var order = items.Select((item, i) => (item, i)).ToList();
        order.Sort((a, b) =>
        {
            var sa = a.item.ChunkId is { } id && scoreById.TryGetValue(id, out var s) ? s : double.NegativeInfinity;
            var sb = b.item.ChunkId is { } id2 && scoreById.TryGetValue(id2, out var s2) ? s2 : double.NegativeInfinity;
            var cmp = sb.CompareTo(sa);
            return cmp != 0 ? cmp : a.i.CompareTo(b.i); // stable: fused order on ties
        });

        return order.Take(topK).Select(x =>
        {
            var rerank = x.item.ChunkId is { } id && scoreById.TryGetValue(id, out var s) ? s : (double?)null;
            var bd = x.item.ScoreBreakdown ?? new SearchScoreBreakdown();
            return x.item with
            {
                ScoreBreakdown = new SearchScoreBreakdown
                {
                    VectorRank = bd.VectorRank,
                    LexicalRank = bd.LexicalRank,
                    Fused = bd.Fused,
                    Normalized = bd.Normalized,
                    Rerank = rerank
                }
            };
        }).ToList();
    }

    /// <summary>SPEC-20260924-mmr-diversity: score floor (<c>Search:MinScore</c>)
    /// applies always; per-document quota + MMR only when
    /// <c>Search:Diversity:Enabled</c>. Vectors for MMR similarity come from the
    /// persisted <c>DocumentChunk.Embedding</c> blobs — no extra provider calls;
    /// chunks without a stored vector degrade to score/quota-only treatment.</summary>
    private async Task<List<SearchResultItem>> ApplyDiversityAsync(
        List<SearchResultItem> items, int topK, CancellationToken ct)
    {
        items = ApplyMinScoreFloor(items);

        if (!configuration.GetValue("Search:Diversity:Enabled", false) || items.Count <= 1)
            return items;

        return await ApplyMmrAsync(items, topK, ct);
    }

    /// <summary>Configured score floor — drops sub-threshold candidates and
    /// counts them in metrics/activity.</summary>
    private List<SearchResultItem> ApplyMinScoreFloor(List<SearchResultItem> items)
    {
        var minScore = configuration.GetValue("Search:MinScore", 0.0);
        if (minScore <= 0)
            return items;

        var before = items.Count;
        var kept = items
            .Where(i => (i.ScoreBreakdown?.Fused ?? i.Score) >= minScore)
            .ToList();
        var dropped = before - kept.Count;
        if (dropped > 0)
        {
            KnowledgeHubMetrics.SearchCandidatesDropped.Add(dropped,
                new KeyValuePair<string, object?>("reason", "floor"));
            Activity.Current?.SetTag("search.floor.removed", dropped);
        }
        return kept;
    }

    /// <summary>MMR diversity re-rank over stored chunk embeddings; drops and
    /// chunkless items are counted/appended deterministically.</summary>
    private async Task<List<SearchResultItem>> ApplyMmrAsync(
        List<SearchResultItem> items, int topK, CancellationToken ct)
    {
        var lambda = configuration.GetValue("Search:Diversity:Lambda", 0.7);
        var maxPerDoc = configuration.GetValue("Search:Diversity:MaxPerDocument", 0);

        var ids = items.Where(i => i.ChunkId is not null).Select(i => i.ChunkId!.Value).ToList();
        var vectorLookup = await db.Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.Embedding != null)
            .Select(c => new { c.Id, c.Embedding })
            .ToDictionaryAsync(c => c.Id, c => c.Embedding!, ct);

        var candidates = items.Select(i => new MmrSelector.Candidate(
            i.ChunkId ?? Guid.Empty,
            i.DocumentId ?? Guid.Empty,
            i.ScoreBreakdown?.Fused ?? i.Score,
            i.ChunkId is { } id && vectorLookup.TryGetValue(id, out var blob)
                ? EmbeddingVectorCodec.FromBytes(blob)
                : null)).ToList();

        List<Guid> orderedIds;
        using (Telemetry.KnowledgeHubActivity.Start("search.mmr"))
            orderedIds = MmrSelector.Select(candidates, topK, lambda, maxPerDoc);
        var byId = items.Where(i => i.ChunkId is not null)
            .ToDictionary(i => i.ChunkId!.Value);
        var ordered = orderedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        ordered.AddRange(items.Where(i => i.ChunkId is null));

        var removed = items.Count - ordered.Count;
        if (removed > 0)
        {
            KnowledgeHubMetrics.SearchCandidatesDropped.Add(removed,
                new KeyValuePair<string, object?>("reason", "diversity"));
            Activity.Current?.SetTag("search.diversity.removed", removed);
        }
        return ordered;
    }

    /// <summary>
    /// SPEC-20260924-hierarchical-retrieval + SPEC-20260927-chunk-window: attaches
    /// surrounding context to each hit — window = ±WindowSize same-document
    /// neighbours (gated by the normalized-score threshold, RF-002); section = the
    /// full parent section (degrades to window when no SectionPath). A per-call
    /// <c>windowSize</c> implies window mode. Never changes which/how many hits
    /// are returned; total context bounded by MaxTotalTokens.
    /// </summary>
    private async Task<List<SearchResultItem>> ApplyContextExpansionAsync(
        IReadOnlyList<SearchResultItem> items, ResolvedSearchFilter? filter, CancellationToken ct)
    {
        // windowSize arg implies window mode; explicit contextExpand wins.
        var contextExpand = filter?.ContextExpand
            ?? (filter?.WindowSize > 0 ? "window" : null);
        if (contextExpand is null or "none" || items.Count == 0)
            return items.ToList();

        var windowSize = Math.Clamp(filter?.WindowSize
            ?? configuration.GetValue("Search:Expansion:WindowSize", 1), 0, 5);
        if (contextExpand == "window" && windowSize == 0)
            return items.ToList(); // W=0: no neighbour query, hit preserved intact

        var maxParentChars = Math.Max(200,
            configuration.GetValue("Search:Expansion:MaxParentTokens", 1500)) * 4;
        var budgetChars = Math.Max(400,
            configuration.GetValue("Search:Expansion:MaxTotalTokens", 6000)) * 4;
        // SPEC-20260929 RF-007: per-document ceiling — several hits in one doc
        // must not let expansion blow past the per-doc token budget.
        var docBudgetChars = Math.Max(200,
            configuration.GetValue("Search:Expansion:MaxDocTokens", 2000)) * 4;
        // SPEC-20260929 RF-004: neighbours go through the same flagged filter —
        // a suspicious chunk must not re-enter via window/section expansion.
        var excludeFlagged = configuration.GetValue("Security:Injection:ExcludeFlagged", true);

        // RF-002: only high-relevance hits earn a window — score normalized by
        // the top hit so the gate works across RRF/cosine/bm25 scales.
        var thresholdPct = Math.Clamp(
            configuration.GetValue("Search:Expansion:WindowThresholdPercent", 80), 0, 100);
        var maxScore = items.Max(i => AutocutFilter.EffectiveScore(i));
        var expandable = items
            .Select((Item, Pos) => (Item, Pos))
            .Where(t => t.Item.DocumentId is not null && t.Item.ChunkIndex is not null
                && (contextExpand != "window" || thresholdPct <= 0 || maxScore <= 0
                    || AutocutFilter.EffectiveScore(t.Item) >= maxScore * thresholdPct / 100.0))
            .ToList();
        if (expandable.Count == 0)
            return items.ToList();

        var result = items.ToList();
        var spent = 0;
        var addedChunks = 0;
        var budgetSkipped = 0;

        foreach (var docGroup in expandable.GroupBy(t => t.Item.DocumentId!.Value))
        {
            var (addedSpent, chunksAdded, skipped) = await ExpandDocumentAsync(
                docGroup, contextExpand,
                new ExpansionLimits(windowSize, maxParentChars,
                    budgetChars - spent, docBudgetChars, excludeFlagged),
                result, ct);
            spent += addedSpent;
            addedChunks += chunksAdded;
            budgetSkipped += skipped;
        }

        Activity.Current?.SetTag("search.expansion.chunks", addedChunks);
        Activity.Current?.SetTag("search.expansion.budget_skipped", budgetSkipped);
        if (budgetSkipped > 0)
            logger.LogInformation("Expansion budget skipped {Count} hits", budgetSkipped);
        return result;
    }

    /// <summary>Expands one document's hits: loads the indexed neighbour range
    /// (and section texts for <c>section</c> mode), then attaches context to
    /// each expandable hit under the global and per-doc budgets.</summary>
    private sealed record ExpansionLimits(
        int WindowSize, int MaxParentChars, int BudgetLeft,
        int DocBudgetChars, bool ExcludeFlagged);

    private async Task<(int Spent, int ChunksAdded, int Skipped)> ExpandDocumentAsync(
        IGrouping<Guid, (SearchResultItem Item, int Pos)> docGroup,
        string contextExpand, ExpansionLimits limits,
        List<SearchResultItem> result, CancellationToken ct)
    {
        var indexes = docGroup.Select(t => t.Item.ChunkIndex!.Value).ToList();
        var min = indexes.Min() - limits.WindowSize;
        var max = indexes.Max() + limits.WindowSize;

        // One indexed range query per document covers every hit's window.
        var neighbours = await db.Chunks.AsNoTracking()
            .Where(c => c.KnowledgeDocumentId == docGroup.Key
                && c.ChunkIndex >= min && c.ChunkIndex <= max
                && (!limits.ExcludeFlagged || c.SuspicionFlags == null))
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new NeighbourChunk(c.ChunkIndex, c.TextContent))
            .ToListAsync(ct);

        var sections = contextExpand == "section"
            ? await LoadSectionsAsync(docGroup, limits.ExcludeFlagged, ct)
            : null;

        // RF-007: chunks already delivered as their own hits are excluded —
        // overlapping windows must not duplicate passages in the context.
        var hitChunkIndexes = indexes.ToHashSet();
        var docSpent = 0;
        var spent = 0;
        var addedChunks = 0;
        var skipped = 0;

        foreach (var (item, pos) in docGroup.OrderBy(t => t.Pos))
        {
            var built = BuildItemContext(item, contextExpand, limits.WindowSize,
                limits.MaxParentChars, sections, neighbours, hitChunkIndexes);
            if (built is not { } pair)
                continue;
            var (context, expanded) = pair;
            // E24 RF-002: budget skips are observable (activity tag + log)
            // — dropped context must not be silent.
            if (spent + context.Length > limits.BudgetLeft
                || docSpent + context.Length > limits.DocBudgetChars)
            {
                skipped++;
                continue;
            }
            spent += context.Length;
            docSpent += context.Length;
            addedChunks += expanded.Count;
            // Claim the emitted neighbours so overlapping windows of later
            // hits don't repeat the same passage (and don't double-spend).
            hitChunkIndexes.UnionWith(expanded);
            result[pos] = item with { Context = context, ExpandedChunkIndices = expanded };
        }

        return (spent, addedChunks, skipped);
    }

    /// <summary>Section texts grouped by path — only queried in <c>section</c>
    /// mode for the paths the doc's hits actually carry.</summary>
    private async Task<Dictionary<string, List<(int Idx, string Text)>>?> LoadSectionsAsync(
        IGrouping<Guid, (SearchResultItem Item, int Pos)> docGroup,
        bool excludeFlagged, CancellationToken ct)
    {
        var paths = docGroup
            .Where(t => t.Item.SectionPath is not null)
            .Select(t => t.Item.SectionPath!)
            .Distinct().ToList();
        if (paths.Count == 0)
            return null;
        return (await db.Chunks.AsNoTracking()
            .Where(c => c.KnowledgeDocumentId == docGroup.Key
                && c.SectionPath != null && paths.Contains(c.SectionPath)
                && (!excludeFlagged || c.SuspicionFlags == null))
            .OrderBy(c => c.ChunkIndex)
            .Select(c => new { c.ChunkIndex, c.TextContent, c.SectionPath })
            .ToListAsync(ct))
            .GroupBy(c => c.SectionPath!)
            .ToDictionary(g => g.Key,
                g => g.Select(c => (Idx: c.ChunkIndex, Text: c.TextContent)).ToList());
    }

    private sealed record NeighbourChunk(int ChunkIndex, string TextContent);

    /// <summary>Builds the (context, expanded-indexes) pair for one hit —
    /// <c>section</c> delivers the whole parent section capped by
    /// maxParentChars; otherwise ±windowSize neighbours. Null = no context.</summary>
    private static (string Context, List<int> Expanded)? BuildItemContext(
        SearchResultItem item, string contextExpand, int windowSize, int maxParentChars,
        Dictionary<string, List<(int Idx, string Text)>>? sections,
        List<NeighbourChunk> neighbours, HashSet<int> hitChunkIndexes)
    {
        var own = item.ChunkIndex!.Value;
        var parts = new List<string>();
        var expanded = new List<int>();

        if (contextExpand == "section" && item.SectionPath is { } path
            && sections is not null && sections.TryGetValue(path, out var sectionTexts))
        {
            // Whole parent section minus the hits themselves, capped —
            // ExpandedChunkIndices only lists chunks fully delivered.
            var used = 0;
            foreach (var t in sectionTexts
                .Where(t => !hitChunkIndexes.Contains(t.Idx) && t.Text != item.ChunkText))
            {
                if (used + t.Text.Length + 2 > maxParentChars)
                    break;
                parts.Add(t.Text);
                expanded.Add(t.Idx);
                used += t.Text.Length + 2;
            }
        }
        else
        {
            var picked = neighbours
                .Where(n => n.ChunkIndex != own
                    && !hitChunkIndexes.Contains(n.ChunkIndex)
                    && n.ChunkIndex >= own - windowSize
                    && n.ChunkIndex <= own + windowSize)
                .ToList();
            parts.AddRange(picked.Select(n => n.TextContent));
            expanded.AddRange(picked.Select(n => n.ChunkIndex));
        }

        var context = string.Join("\n\n", parts);
        return context.Length == 0 ? null : (context, expanded);
    }

    /// <summary>
    /// SPEC-20260924-query-expansion-hyde: runs the retrieval arms for the query
    /// and its expansion variants in parallel. Returns per-arm labels (null =
    /// original query, "hyde" = hypothetical document) paired with ranked lists.
    /// </summary>
    private async Task<(
        List<string?> VectorLabels, List<IReadOnlyList<Guid>> VectorLists,
        List<string?> LexicalLabels, List<IReadOnlyList<Guid>> LexicalLists)>
        ExpandAndSearchAsync(
            string rawQuery, string effectiveQuery, ArmRequest arms,
            IReadOnlyCollection<Guid> activeSourceIds,
            DegradationState degraded, CancellationToken ct)
    {
        var (mode, expansionMode, window, minSemantic, minLexical, subQueries) = arms;
        var (variants, hydeText) = await GenerateExpansionAsync(
            rawQuery, effectiveQuery, expansionMode, ct);
        var (vectorTexts, lexicalQueries) = BuildArmTexts(
            effectiveQuery, expansionMode, variants, hydeText, subQueries);

        var vectorLists = mode != SearchMode.Lexical
            ? await RunVectorArmsAsync(vectorTexts, window, minSemantic,
                activeSourceIds, degraded, ct)
            : [];
        var lexicalLists = mode != SearchMode.Semantic
            ? await RunLexicalArmsAsync(lexicalQueries, window, minLexical,
                activeSourceIds, degraded, ct)
            : [];

        return (mode != SearchMode.Lexical ? vectorTexts.Select(t => t.Label).ToList() : [],
                vectorLists,
                mode != SearchMode.Semantic ? lexicalQueries.Select(q => q.Label).ToList() : [],
                lexicalLists);
    }

    /// <summary>RF-001: expansion generation — multi returns query variants,
    /// hyde a hypothetical document, both both. off/unknown → no variants.</summary>
    private async Task<(IReadOnlyList<string> Variants, string? HydeText)> GenerateExpansionAsync(
        string rawQuery, string effectiveQuery, string expansionMode, CancellationToken ct)
    {
        if (expansionMode is not ("multi" or "hyde" or "both"))
            return ([], null);

        var count = Math.Clamp(configuration.GetValue("Search:QueryExpansion:Count", 3), 1, 5);
        var variantsTask = expansionMode is "multi" or "both"
            ? expander.ExpandQueriesAsync(effectiveQuery, count, ct)
            : Task.FromResult<IReadOnlyList<string>>([]);
        var hydeTask = expansionMode is "hyde" or "both"
            ? expander.GenerateHypotheticalAsync(rawQuery, ct)
            : Task.FromResult<string?>(null);
        var variants = await variantsTask;
        var hydeText = await hydeTask;
        var activity = Activity.Current;
        activity?.SetTag("search.expansion.mode", expansionMode);
        activity?.SetTag("search.expansion.variants",
            variants.Count + (hydeText is null ? 0 : 1));
        return (variants, hydeText);
    }

    /// <summary>Arm contents per mode — HyDE replaces the vector query (spec
    /// RF-002); lexical always keeps real queries (the hypothetical doc would
    /// pollute FTS). SPEC-20260927-multiquery RF-001: caller sub-queries ride
    /// both arms as extra ranked lists, fused by RRF.</summary>
    private static (List<(string Text, string? Label)> Vector, List<(string Query, string? Label)> Lexical)
        BuildArmTexts(
            string effectiveQuery, string expansionMode,
            IReadOnlyList<string> variants, string? hydeText,
            IReadOnlyList<string>? subQueries)
    {
        var vectorTexts = new List<(string Text, string? Label)> { (effectiveQuery, null) };
        var lexicalQueries = new List<(string Query, string? Label)> { (effectiveQuery, null) };
        switch (expansionMode)
        {
            case "multi":
                vectorTexts.AddRange(variants.Select<string, (string, string?)>(v => (v, v)));
                lexicalQueries.AddRange(variants.Select<string, (string, string?)>(v => (v, v)));
                break;
            case "hyde" or "both" when hydeText is not null:
                vectorTexts.Clear();
                vectorTexts.Add((hydeText, "hyde"));
                if (expansionMode == "both")
                    lexicalQueries.AddRange(variants.Select<string, (string, string?)>(v => (v, v)));
                break;
            case "both":
                lexicalQueries.AddRange(variants.Select<string, (string, string?)>(v => (v, v)));
                break;
        }

        if (subQueries is { Count: > 0 })
        {
            vectorTexts.AddRange(subQueries.Select<string, (string, string?)>(q => (q, q)));
            lexicalQueries.AddRange(subQueries.Select<string, (string, string?)>(q => (q, q)));
        }
        return (vectorTexts, lexicalQueries);
    }

    /// <summary>SPEC-20261001-mcp-recall-ergonomics RF-003: the semantic floor
    /// prunes the vector arm by cosine score before fusion (calibrated 0-1).
    /// A failing arm degrades instead of aborting the whole search.</summary>
    private async Task<List<IReadOnlyList<Guid>>> RunVectorArmsAsync(
        List<(string Text, string? Label)> vectorTexts, int window, double? minSemantic,
        IReadOnlyCollection<Guid> activeSourceIds, DegradationState degraded,
        CancellationToken ct)
    {
        var tasks = vectorTexts.Select(async t =>
        {
            try
            {
                var hits = await VectorSearchAsync(await EmbedQueryAsync(t.Text, ct), window, activeSourceIds, degraded, ct);
                if (minSemantic is { } floor)
                    hits = hits.Where(h => h.Score >= floor).ToList();
                return hits.Select(h => h.ChunkId).ToList() as IReadOnlyList<Guid>;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Search arm failed (label {Label}) — continuing",
                    ForLog(t.Label) ?? "primary");
                degraded.Any = true; // RF-005: never cache a result built on a failed arm
                return Array.Empty<Guid>();
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<List<IReadOnlyList<Guid>>> RunLexicalArmsAsync(
        List<(string Query, string? Label)> lexicalQueries, int window, double? minLexical,
        IReadOnlyCollection<Guid> activeSourceIds, DegradationState degraded,
        CancellationToken ct)
    {
        var tasks = lexicalQueries.Select(async q =>
        {
            try
            {
                return await LexicalRankedAsync(q.Query, window, activeSourceIds, ct, minLexical);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Lexical arm failed (label {Label}) — continuing",
                    ForLog(q.Label) ?? "primary");
                degraded.Any = true; // RF-005: never cache a result built on a failed arm
                return Array.Empty<Guid>();
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>Per-arm options for <see cref="ExpandAndSearchAsync"/> — mode,
    /// window breadth and the RF-003 score floors.</summary>
    private sealed record ArmRequest(
        SearchMode Mode, string ExpansionMode, int Window,
        double? MinSemantic, double? MinLexical,
        IReadOnlyList<string>? SubQueries);
    /// <summary>
    /// SPEC-20260924-graph-expanded-retrieval: entity-link the query, expand
    /// 1-hop, return evidence chunks ranked (direct first). Empty arm when the
    /// graph has no match — fusion stays unchanged.
    /// </summary>
    private async Task<(List<Guid> Ranked, HashSet<Guid> DirectChunks)> GraphRankedAsync(
        string query, CancellationToken ct)
    {
        var maxEntities = Math.Clamp(configuration.GetValue("Search:Graph:MaxEntities", 5), 1, 20);
        var maxNeighbors = Math.Clamp(configuration.GetValue("Search:Graph:MaxNeighbors", 10), 1, 50);

        var nodeIds = await graphLinker.LinkAsync(query, maxEntities, ct);
        if (nodeIds.Count == 0)
            return ([], []);

        var (ranked, direct) = await graphLinker.EvidenceChunksAsync(nodeIds, maxNeighbors, ct);
        Activity.Current?.SetTag("search.graph.hits", ranked.Count);
        return (ranked, direct);
    }

    /// <summary>Lexical arm call wrapped in span + duration metric.
    /// SPEC-20261001-mcp-recall-ergonomics RF-003: <paramref name="minLexical"/>
    /// prunes the arm as a fraction (0–1) of its best score — sign-agnostic:
    /// FTS5 bm25 ranks more-negative-first while pg ts_rank is positive.</summary>
    private async Task<IReadOnlyList<Guid>> LexicalRankedAsync(
        string query, int topK, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct,
        double? minLexical = null)
    {
        using var span = KnowledgeHubActivity.Start("lexical_search");
        var sw = Stopwatch.StartNew();
        try
        {
            var hits = await lexical.SearchAsync(query, topK, sourceIds, ct);
            if (minLexical is { } floor && hits.Count > 1)
            {
                var best = hits[0].Bm25;
                hits = best > 0
                    ? hits.Where(h => h.Bm25 >= best * floor).ToList()
                    : hits.Where(h => h.Bm25 <= best * floor).ToList();
            }
            return hits.Select(h => h.ChunkId).ToList();
        }
        catch (Exception ex)
        {
            KnowledgeHubActivity.Fail(span, ex);
            throw;
        }
        finally
        {
            KnowledgeHubMetrics.LexicalDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Vector store call wrapped in a span + duration histogram.</summary>
    private async Task<IReadOnlyList<VectorHit>> VectorSearchAsync(
        float[] queryVector, int topK, IReadOnlyCollection<Guid> sourceIds,
        DegradationState degraded, CancellationToken ct)
    {
        var store = vectors.GetType().Name;
        using var span = KnowledgeHubActivity.Start("vector_search");
        span?.SetTag("vector.store", store);
        var sw = Stopwatch.StartNew();
        try
        {
            return await vectors.SearchAsync(queryVector, embeddings.ModelId, topK, sourceIds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // SPEC-20260925-vectorstore-metrics RF-004: the vector arm is
            // fail-soft — FTS still serves results; record the error span and
            // metric, log once, return empty so RRF fuses FTS-only.
            KnowledgeHubMetrics.VectorErrors.Add(1,
                new KeyValuePair<string, object?>("store", store),
                new KeyValuePair<string, object?>("op", "search"));
            KnowledgeHubActivity.Fail(span, ex);
            logger.LogWarning(ex, "vector search failed ({Store}) — continuing with lexical only", store);
            degraded.Any = true; // RF-005: this result must not be cached
            return [];
        }
        finally
        {
            KnowledgeHubMetrics.VectorSearchDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("store", store));
        }
    }

    /// <summary>Query embedding via the distributed cache — embeddings are
    /// deterministic per (modelId, text) so the key needs no version.
    /// SPEC-20260925-hybrid-cache-l1l2: GetOrCreate collapses concurrent
    /// misses on the same query to a single provider call.</summary>
    private async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var key = CacheKeys.Embedding(embeddings.ModelId, embeddingsResolver.Fingerprint, query);
        var vector = await SafeCache.GetOrCreateAsync<float[]>(
            cache, key,
            async innerCt =>
            {
                using var span = KnowledgeHubActivity.Start("embed_query");
                span?.SetTag("llm.model", embeddings.ModelId);
                var sw = Stopwatch.StartNew();
                try
                {
                    var v = await embeddings.EmbedQueryAsync(query, innerCt);
                    KnowledgeHubMetrics.EmbeddingDuration.Record(sw.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("provider", embeddings.GetType().Name),
                        new KeyValuePair<string, object?>("model", embeddings.ModelId));
                    return v;
                }
                catch (Exception ex)
                {
                    KnowledgeHubActivity.Fail(span, ex);
                    throw;
                }
            },
            EmbeddingVectorCodec.Codec,
            ttl: null, logger, ct);
        return vector!; // factory never returns null (provider throws instead)
    }

    /// <summary>Current index-version token — shared helper so the answer
    /// cache keys invalidate on the same signal (RF-003).</summary>
    private Task<string> GetIndexVersionAsync(CancellationToken ct) =>
        IndexVersionToken.GetAsync(cache, logger, ct);

    private static List<SearchResultItem>? JsonSerializerSafely(string payload)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<SearchResultItem>>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupt payload behaves as a miss
        }
    }

    private async Task<List<SearchResultItem>> HydrateAsync(
        IReadOnlyList<VectorHit> hits,
        IReadOnlyDictionary<Guid, SearchScoreBreakdown>? breakdowns,
        ResolvedSearchFilter? filter,
        CancellationToken ct)
    {
        if (hits.Count == 0)
            return [];

        var chunkIds = hits.Select(h => h.ChunkId).ToList();
        // RF-003: sourceType/pathPrefix filter in SQL; indexedAfter filters in
        // memory (SQLite cannot compare DateTimeOffset); language filters the
        // derived metadata map (documents carry no language column yet).
        var query = db.Chunks.AsNoTracking()
            .Where(c => chunkIds.Contains(c.Id))
            .Join(db.Documents, c => c.KnowledgeDocumentId, d => d.Id,
                (c, d) => new { c.Id, c.TextContent, c.SuspicionFlags, c.ChunkKind, c.SymbolPath, c.SectionPath, c.ChunkIndex, DocId = d.Id, d.Title, d.UriReference, d.IndexedAt, d.KnowledgeSourceId })
            .Join(db.Sources, x => x.KnowledgeSourceId, s => s.Id,
                (x, s) => new HydratedChunk
                {
                    Id = x.Id,
                    TextContent = x.TextContent,
                    SuspicionFlags = x.SuspicionFlags,
                    ChunkKind = x.ChunkKind,
                    SymbolPath = x.SymbolPath,
                    SectionPath = x.SectionPath,
                    ChunkIndex = x.ChunkIndex,
                    DocId = x.DocId,
                    Title = x.Title,
                    UriReference = x.UriReference,
                    IndexedAt = x.IndexedAt,
                    KnowledgeSourceId = x.KnowledgeSourceId,
                    SourceName = s.Name,
                    SourceType = s.SourceType
                });
        if (filter?.SourceType is { } st)
            query = query.Where(x => x.SourceType == st);
        if (filter?.PathPrefix is { } pp)
            query = query.Where(x => x.UriReference.StartsWith(pp));
        var chunks = await query.ToListAsync(ct);

        if (filter?.IndexedAfter is { } ia)
            chunks = chunks.Where(x => x.IndexedAt >= ia).ToList();

        var byId = chunks.ToDictionary(c => c.Id);
        // SPEC-20260923-prompt-injection-guard RF-004: flagged chunks are dropped
        // post-rank when ExcludeFlagged is on (default true) — ranking window is
        // unaffected, the answer just never sees them.
        var excludeFlagged = configuration.GetValue("Security:Injection:ExcludeFlagged", true);
        var excluded = excludeFlagged
            ? hits.Count(h => byId.TryGetValue(h.ChunkId, out var c) && c.SuspicionFlags is not null)
            : 0;
        var items = hits
            .Where(h => byId.ContainsKey(h.ChunkId))
            .Select(h => ProjectHit(h, byId[h.ChunkId], breakdowns, filter, excludeFlagged))
            .OfType<SearchResultItem>()
            .ToList();

        if (excluded > 0)
            logger.LogInformation("Excluded {Count} flagged chunk(s) from search context", excluded);
        await AttachComponentsAsync(items, ct);
        return items;
    }

    /// <summary>Flat row produced by the chunk×document×source hydration join.</summary>
    private sealed class HydratedChunk
    {
        public Guid Id { get; init; }
        public required string TextContent { get; init; }
        public string? SuspicionFlags { get; init; }
        public required string ChunkKind { get; init; }
        public string? SymbolPath { get; init; }
        public string? SectionPath { get; init; }
        public int ChunkIndex { get; init; }
        public Guid DocId { get; init; }
        public required string Title { get; init; }
        public required string UriReference { get; init; }
        public DateTimeOffset IndexedAt { get; init; }
        public Guid KnowledgeSourceId { get; init; }
        public required string SourceName { get; init; }
        public SourceType SourceType { get; init; }
    }

    /// <summary>Maps one hydrated chunk join row to a result item — returns null
    /// when the row is filtered out post-rank (flagged chunk or language miss).</summary>
    private static SearchResultItem? ProjectHit(
        VectorHit h, HydratedChunk c,
        IReadOnlyDictionary<Guid, SearchScoreBreakdown>? breakdowns,
        ResolvedSearchFilter? filter, bool excludeFlagged)
    {
        if (c.SuspicionFlags is not null && excludeFlagged)
            return null;
        // RF-003: language is metadata-derived (no column) — a set
        // filter keeps only items whose metadata carries a match.
        if (filter?.Language is { } lang)
        {
            var meta = BuildMetadata(c.SourceType, c.UriReference, c.ChunkKind, c.SymbolPath);
            if (!meta.TryGetValue("language", out var l) ||
                !l.Equals(lang, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return new SearchResultItem
        {
            ChunkText = c.TextContent,
            DocumentTitle = c.Title,
            SourceName = c.SourceName,
            SourceId = c.KnowledgeSourceId,
            SourceType = c.SourceType,
            Score = h.Score,
            UriReference = c.UriReference,
            ScoreBreakdown = breakdowns?.GetValueOrDefault(h.ChunkId),
            SuspicionFlags = c.SuspicionFlags,
            SectionPath = c.SectionPath,
            ChunkId = c.Id,
            DocumentId = c.DocId,
            ChunkIndex = c.ChunkIndex,
            Metadata = BuildMetadata(c.SourceType, c.UriReference, c.ChunkKind, c.SymbolPath),
            IndexedAt = c.IndexedAt
        };
    }

    /// <summary>SPEC-20260924-graph-tool-discovery RF-001: attaches the
    /// knowledge-graph entity names evidenced by each returned chunk so callers
    /// can feed them straight into the find_* tools. One batched query on the
    /// indexed EvidenceChunkId column; skipped entirely when GraphRAG is off.</summary>
    private async Task AttachComponentsAsync(List<SearchResultItem> items, CancellationToken ct)
    {
        if (!graphSettings.GetEffective().Enabled)
            return;
        var chunkIds = items.Where(i => i.ChunkId is not null)
            .Select(i => i.ChunkId!.Value).ToList();
        if (chunkIds.Count == 0)
            return;

        var edges = await db.KgEdges.AsNoTracking()
            .Where(e => chunkIds.Contains(e.EvidenceChunkId))
            .Select(e => new { e.EvidenceChunkId, FromName = e.From.Name, ToName = e.To.Name })
            .ToListAsync(ct);
        if (edges.Count == 0)
            return;

        var byChunk = edges
            .GroupBy(e => e.EvidenceChunkId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.SelectMany(e => new[] { e.FromName, e.ToName })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].ChunkId is { } id && byChunk.TryGetValue(id, out var names))
                items[i] = items[i] with { Components = names };
        }
    }

    /// <summary>RF-004: provenance metadata derived from stored columns.</summary>
    private static IReadOnlyDictionary<string, string> BuildMetadata(
        SourceType sourceType, string uri, string chunkKind, string? symbolPath)
    {
        var meta = new Dictionary<string, string>
        {
            ["sourceType"] = sourceType.ToString(),
            ["path"] = uri,
            ["chunkKind"] = chunkKind
        };
        if (symbolPath is not null)
            meta["symbolPath"] = symbolPath;
        return meta;
    }
}
