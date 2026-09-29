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
public sealed class SearchService(
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
            var results = await ExecuteAsync(query, topK, sourceId, mode, filter, scope, conversationContext, degraded, ct);
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

    private async Task<IReadOnlyList<SearchResultItem>> ExecuteAsync(
        string query, int topK, Guid? sourceId,
        SearchMode mode, ResolvedSearchFilter? filter,
        Auth.CallerScope scope, string? conversationContext,
        DegradationState degraded, CancellationToken ct, int relaxLevel = 0)
    {
        var activeSourceIds = sourceId is null
            ? await db.Sources.Where(s => s.IsActive).Select(s => s.Id).ToListAsync(ct)
            : [sourceId.Value];

        // SPEC-20260923-source-authorization RF-003: intersect with the key's
        // source scope before any vector/lexical call — never retrieve-then-
        // filter. An explicit sourceId outside scope yields an empty result
        // (not an error) and a SourceScopeDenied audit event.
        if (scope.AllowedSourceIds is { } allowed)
        {
            if (sourceId is { } requested && !allowed.Contains(requested))
                await Auth.ScopeAudit.RecordAsync(
                    db, scope.ApiKeyId, Auth.ScopeAudit.SourceDenied, sourceId: requested, ct: ct);
            activeSourceIds = activeSourceIds.Where(allowed.Contains).ToList();
        }
        if (activeSourceIds.Count == 0)
            return [];

        // RF-001/RF-002: rewrite + rerank run on the user's retrieval intent.
        // Lexical skips rewriting unless explicitly opted in.
        // SPEC-20260925-otel-pipeline-spans: rewrite boundary span.
        string effectiveQuery;
        using (var span = Telemetry.KnowledgeHubActivity.Start("search.rewrite"))
        {
            try
            {
                effectiveQuery = mode == SearchMode.Lexical
                    && !configuration.GetValue("Search:QueryRewrite:LexicalToo", false)
                    ? query
                    : await rewriter.RewriteAsync(query, conversationContext, ct);
            }
            catch (Exception ex) { Telemetry.KnowledgeHubActivity.Fail(span, ex); throw; }
        }

        var rerankEnabled = configuration.GetValue("Search:Rerank:Enabled", false);
        var diversityEnabled = configuration.GetValue("Search:Diversity:Enabled", false);
        var filtersActive = filter is { IsEmpty: false };
        var window = topK * CandidateWindowFactor;
        var rerankCap = configuration.GetValue("Search:Rerank:MaxCandidates", 20);

        // Fetch a wider window when filters, diversity or rerank need room to
        // work; otherwise hydrate exactly topK — identical to the pre-filter pipeline.
        var fetchLimit = rerankEnabled || filtersActive || diversityEnabled
            ? (rerankEnabled ? Math.Min(window, Math.Max(rerankCap, topK)) : window)
            : topK;

        List<VectorHit> windowed;
        IReadOnlyDictionary<Guid, SearchScoreBreakdown>? breakdowns = null;

        // SPEC-20260924-query-expansion-hyde: expansion mode — per-call `expand`
        // filter arg wins over config; off = the classic single-query pipeline.
        var expansionMode = filter?.Expansion
            ?? configuration.GetValue("Search:QueryExpansion:Mode", "off");

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

        if (mode == SearchMode.Semantic && expansionMode == "off" && !graphEnabled
            && subQueries is not { Count: > 0 })
        {
            var queryVector = await EmbedQueryAsync(effectiveQuery, ct);
            windowed = (await VectorSearchAsync(queryVector, fetchLimit, activeSourceIds, degraded, ct)).ToList();
        }
        else
        {
            var (vectorLabels, vectorLists, lexicalLabels, lexicalLists) =
                await ExpandAndSearchAsync(query, effectiveQuery, mode, expansionMode,
                    window, activeSourceIds, degraded, ct, subQueries);

            var graphArm = graphEnabled
                ? await GraphRankedAsync(query, ct)
                : (Ranked: (IReadOnlyList<Guid>)Array.Empty<Guid>(), DirectChunks: new HashSet<Guid>());

            var rankedLists = vectorLists.Select(l => ("vector", (IReadOnlyList<Guid>)l))
                .Concat(lexicalLists.Select(l => ("lexical", (IReadOnlyList<Guid>)l)))
                .Concat(graphArm.Ranked.Count > 0 ? [("graph", graphArm.Ranked)] : [])
                .ToList();
            IReadOnlyList<FusedHit> fused;
            using (KnowledgeHubActivity.Start("search.rrf"))
                fused = RrfFuser.Fuse(rankedLists, fetchLimit);

            // RF-003: gentle boost on chunks with direct-entity evidence.
            var boost = configuration.GetValue("Search:Graph:Boost", 1.0);
            if (graphArm.DirectChunks.Count > 0 && Math.Abs(boost - 1.0) > 0.001)
                fused = fused
                    .Select(f => graphArm.DirectChunks.Contains(f.ChunkId)
                        ? f with { Fused = f.Fused * boost }
                        : f)
                    .OrderByDescending(f => f.Fused).ThenBy(f => f.ChunkId)
                    .Take(fetchLimit)
                    .ToList();

            // SPEC-20260929 RF: an empty fused result must fall through to the
            // relaxation path below — early return skips the scope cascade.
            if (fused.Count > 0)
            {
                // ExpandedFrom: first list (in arm order) that surfaced the chunk.
                var expandedFrom = new Dictionary<Guid, string>();
                foreach (var (label, list) in vectorLabels.Zip(vectorLists).Concat(lexicalLabels.Zip(lexicalLists)))
                    if (label is not null)
                        foreach (var id in list)
                            expandedFrom.TryAdd(id, label);

                breakdowns = fused.ToDictionary(
                    f => f.ChunkId,
                    f => new SearchScoreBreakdown
                    {
                        VectorRank = f.VectorRank,
                        LexicalRank = f.LexicalRank,
                        Fused = f.Fused,
                        GraphRank = f.GraphRank,
                        ExpandedFrom = expandedFrom.GetValueOrDefault(f.ChunkId)
                    });
                windowed = fused.Select(f => new VectorHit(f.ChunkId, f.Fused)).ToList();
            }
            else
            {
                windowed = [];
            }
        }

        List<SearchResultItem> items;
        using (var hydrateSpan = KnowledgeHubActivity.Start("hydrate"))
        {
            try
            {
                items = await HydrateAsync(windowed, breakdowns, filter, ct);
            }
            catch (Exception ex)
            {
                KnowledgeHubActivity.Fail(hydrateSpan, ex);
                throw;
            }
        }
        items = await ApplyDiversityAsync(items, topK, ct);

        var final = (!rerankEnabled || items.Count <= 1)
            ? items.Take(topK).ToList()
            : await RerankAsync(query, items, breakdowns, topK, ct);

        // SPEC-20260927-chunk-window-retrieval-and-autocut RF-003: dynamic tail
        // pruning — the elbow in the score curve decides the count (≤topK).
        // SPEC-20260929-search-scope-pipeline RF-001: unfiltered searches also
        // honor the configured default — a null filter must not force "fixed".
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

        // SPEC-20260927-multiquery RF-002: hierarchical scope fallback — driven
        // only from the strict level (relaxLevel==0); the loop owns the cascade.
        var relaxAllowed = relaxLevel == 0
            && (filter?.AllowRelaxation
                ?? configuration.GetValue("Search:Relaxation:Enabled", true));
        // SPEC-20260929 RF-005: a sourceId the caller is not authorized for was
        // already denied+audited — relaxation must never widen a denied request
        // into other sources, even ones inside the caller's scope.
        var deniedSource = sourceId is { } s
            && scope.AllowedSourceIds is { } callerAllowed
            && !callerAllowed.Contains(s);
        var minResults = Math.Max(0, configuration.GetValue("Search:Relaxation:MinResults", 1));
        if (relaxAllowed && !deniedSource && final.Count < minResults)
            final = await ApplyRelaxationAsync(query, topK, sourceId, filter, mode,
                scope, conversationContext, degraded, final.ToList(), minResults, ct);

        // SPEC-20260924-hierarchical-retrieval: post-selection context expansion
        // (neighbours / parent section) — never affects ranking.
        return await ApplyContextExpansionAsync(final, filter, ct);
    }

    private const double RelaxationPenalty = 0.85;

    /// <summary>
    /// SPEC-20260927-multiquery RF-002: cascades the scope until MinResults is
    /// met — drop pathPrefix → sourceId→its SourceType → global (still bounded by
    /// the caller's allowed-source scope). Relaxed hits merge with the strict
    /// ones under a 0.85^level score penalty so strict matches keep precedence.
    /// </summary>
    private async Task<List<SearchResultItem>> ApplyRelaxationAsync(
        string query, int topK, Guid? sourceId, ResolvedSearchFilter? filter,
        SearchMode mode, Auth.CallerScope scope, string? conversationContext,
        DegradationState degraded, List<SearchResultItem> strict,
        int minResults, CancellationToken ct)
    {
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

            var hits = await ExecuteAsync(query, topK, curSourceId, mode, curFilter,
                scope, conversationContext, degraded, ct, relaxLevel: level);
            var penalty = Math.Pow(RelaxationPenalty, level);
            var added = 0;
            foreach (var h in hits)
            {
                if (h.ChunkId is { } cid && !seen.Add(cid))
                    continue;
                relaxed.Add(h with
                {
                    IsRelaxed = true,
                    RelaxedScope = next.Value.Description,
                    Score = h.Score * penalty,
                    ScoreBreakdown = h.ScoreBreakdown is { } b
                        ? b with { Fused = b.Fused * penalty } : null
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
        string query, List<SearchResultItem> items,
        IReadOnlyDictionary<Guid, SearchScoreBreakdown>? breakdowns, int topK, CancellationToken ct)
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
        var minScore = configuration.GetValue("Search:MinScore", 0.0);
        if (minScore > 0)
        {
            var before = items.Count;
            items = items
                .Where(i => (i.ScoreBreakdown?.Fused ?? i.Score) >= minScore)
                .ToList();
            var dropped = before - items.Count;
            if (dropped > 0)
            {
                KnowledgeHubMetrics.SearchCandidatesDropped.Add(dropped,
                    new KeyValuePair<string, object?>("reason", "floor"));
                Activity.Current?.SetTag("search.floor.removed", dropped);
            }
        }

        if (!configuration.GetValue("Search:Diversity:Enabled", false) || items.Count <= 1)
            return items;

        var lambda = configuration.GetValue("Search:Diversity:Lambda", 0.7);
        var maxPerDoc = configuration.GetValue("Search:Diversity:MaxPerDocument", 0);

        var ids = items.Where(i => i.ChunkId is not null).Select(i => i.ChunkId!.Value).ToList();
        var vectors = await db.Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.Embedding != null)
            .Select(c => new { c.Id, c.Embedding })
            .ToDictionaryAsync(c => c.Id, c => c.Embedding!, ct);

        var candidates = items.Select(i => new MmrSelector.Candidate(
            i.ChunkId ?? Guid.Empty,
            i.DocumentId ?? Guid.Empty,
            i.ScoreBreakdown?.Fused ?? i.Score,
            i.ChunkId is { } id && vectors.TryGetValue(id, out var blob)
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

        foreach (var docGroup in expandable.GroupBy(t => t.Item.DocumentId!.Value))
        {
            var indexes = docGroup.Select(t => t.Item.ChunkIndex!.Value).ToList();
            var min = indexes.Min() - windowSize;
            var max = indexes.Max() + windowSize;

            // One indexed range query per document covers every hit's window.
            var neighbours = await db.Chunks.AsNoTracking()
                .Where(c => c.KnowledgeDocumentId == docGroup.Key
                    && c.ChunkIndex >= min && c.ChunkIndex <= max
                    && (!excludeFlagged || c.SuspicionFlags == null))
                .OrderBy(c => c.ChunkIndex)
                .Select(c => new { c.ChunkIndex, c.TextContent })
                .ToListAsync(ct);

            Dictionary<string, List<(int Idx, string Text)>>? sections = null;
            if (contextExpand == "section")
            {
                var paths = docGroup
                    .Where(t => t.Item.SectionPath is not null)
                    .Select(t => t.Item.SectionPath!)
                    .Distinct().ToList();
                if (paths.Count > 0)
                    sections = (await db.Chunks.AsNoTracking()
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

            // RF-007: chunks already delivered as their own hits are excluded —
            // overlapping windows must not duplicate passages in the context.
            var hitChunkIndexes = indexes.ToHashSet();
            var docSpent = 0;

            foreach (var (item, pos) in docGroup.OrderBy(t => t.Pos))
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
                    foreach (var t in sectionTexts)
                    {
                        if (hitChunkIndexes.Contains(t.Idx) || t.Text == item.ChunkText)
                            continue;
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
                if (context.Length == 0 || spent + context.Length > budgetChars
                    || docSpent + context.Length > docBudgetChars)
                    continue;
                spent += context.Length;
                docSpent += context.Length;
                addedChunks += expanded.Count;
                // Claim the emitted neighbours so overlapping windows of later
                // hits don't repeat the same passage (and don't double-spend).
                hitChunkIndexes.UnionWith(expanded);
                result[pos] = item with { Context = context, ExpandedChunkIndices = expanded };
            }
        }

        Activity.Current?.SetTag("search.expansion.chunks", addedChunks);
        return result;
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
            string rawQuery, string effectiveQuery, SearchMode mode,
            string expansionMode, int window,
            IReadOnlyCollection<Guid> activeSourceIds,
            DegradationState degraded, CancellationToken ct,
            IReadOnlyList<string>? subQueries = null)
    {
        IReadOnlyList<string> variants = [];
        string? hydeText = null;
        if (expansionMode is "multi" or "hyde" or "both")
        {
            var count = Math.Clamp(configuration.GetValue("Search:QueryExpansion:Count", 3), 1, 5);
            var variantsTask = expansionMode is "multi" or "both"
                ? expander.ExpandQueriesAsync(effectiveQuery, count, ct)
                : Task.FromResult<IReadOnlyList<string>>([]);
            var hydeTask = expansionMode is "hyde" or "both"
                ? expander.GenerateHypotheticalAsync(rawQuery, ct)
                : Task.FromResult<string?>(null);
            variants = await variantsTask;
            hydeText = await hydeTask;
            var activity = Activity.Current;
            activity?.SetTag("search.expansion.mode", expansionMode);
            activity?.SetTag("search.expansion.variants",
                variants.Count + (hydeText is null ? 0 : 1));
        }

        // Arm contents per mode — HyDE replaces the vector query (spec RF-002);
        // lexical always keeps real queries (the hypothetical doc would pollute FTS).
        var vectorTexts = new List<(string Text, string? Label)> { (effectiveQuery, null) };
        var lexicalQueries = new List<(string Query, string? Label)> { (effectiveQuery, null) };
        switch (expansionMode)
        {
            case "multi":
                vectorTexts.AddRange(variants.Select(v => (v, (string?)v)));
                lexicalQueries.AddRange(variants.Select(v => (v, (string?)v)));
                break;
            case "hyde" or "both" when hydeText is not null:
                vectorTexts.Clear();
                vectorTexts.Add((hydeText, "hyde"));
                if (expansionMode == "both")
                    lexicalQueries.AddRange(variants.Select(v => (v, (string?)v)));
                break;
            case "both":
                lexicalQueries.AddRange(variants.Select(v => (v, (string?)v)));
                break;
        }

        // SPEC-20260927-multiquery RF-001: caller-supplied sub-queries ride both
        // arms as extra ranked lists (label = the sub-query text), fused by RRF.
        if (subQueries is { Count: > 0 })
        {
            vectorTexts.AddRange(subQueries.Select(q => (q, (string?)q)));
            lexicalQueries.AddRange(subQueries.Select(q => (q, (string?)q)));
        }

        // SPEC-20260929 RF-006: each arm is isolated — one failed sub-query
        // returns an empty list for itself, the other arms still fuse.
        var vectorLists = new List<IReadOnlyList<Guid>>();
        var vectorLabels = new List<string?>();
        if (mode != SearchMode.Lexical)
        {
            var tasks = vectorTexts.Select(async t =>
            {
                try
                {
                    return (await VectorSearchAsync(await EmbedQueryAsync(t.Text, ct), window, activeSourceIds, degraded, ct))
                        .Select(h => h.ChunkId).ToList() as IReadOnlyList<Guid>;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Search arm failed (label {Label}) — continuing",
                        ForLog(t.Label) ?? "primary");
                    degraded.Any = true; // RF-005: never cache a result built on a failed arm
                    return (IReadOnlyList<Guid>)[];
                }
            });
            vectorLists.AddRange(await Task.WhenAll(tasks));
            vectorLabels.AddRange(vectorTexts.Select(t => t.Label));
        }

        var lexicalLists = new List<IReadOnlyList<Guid>>();
        var lexicalLabels = new List<string?>();
        if (mode != SearchMode.Semantic)
        {
            var tasks = lexicalQueries.Select(async q =>
            {
                try
                {
                    return await LexicalRankedAsync(q.Query, window, activeSourceIds, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Lexical arm failed (label {Label}) — continuing",
                        ForLog(q.Label) ?? "primary");
                    degraded.Any = true; // RF-005: never cache a result built on a failed arm
                    return (IReadOnlyList<Guid>)[];
                }
            });
            lexicalLists.AddRange(await Task.WhenAll(tasks));
            lexicalLabels.AddRange(lexicalQueries.Select(q => q.Label));
        }

        return (vectorLabels, vectorLists, lexicalLabels, lexicalLists);
    }

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

    /// <summary>Lexical arm call wrapped in span + duration metric.</summary>
    private async Task<IReadOnlyList<Guid>> LexicalRankedAsync(
        string query, int topK, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct)
    {
        using var span = KnowledgeHubActivity.Start("lexical_search");
        var sw = Stopwatch.StartNew();
        try
        {
            var hits = await lexical.SearchAsync(query, topK, sourceIds, ct);
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
            EmbeddingVectorCodec.ToBytes, EmbeddingVectorCodec.FromBytes,
            ttl: null, logger, ct);
        return vector!; // factory never returns null (provider throws instead)
    }

    /// <summary>Current index-version token — shared helper so the answer
    /// cache keys invalidate on the same signal (RF-003).</summary>
    private Task<string> GetIndexVersionAsync(CancellationToken ct) =>
        IndexVersionToken.GetAsync(cache, logger, ct);

    private List<SearchResultItem>? JsonSerializerSafely(string payload)
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
                (x, s) => new { x.Id, x.TextContent, x.SuspicionFlags, x.ChunkKind, x.SymbolPath, x.SectionPath, x.ChunkIndex, x.DocId, x.Title, x.UriReference, x.IndexedAt, x.KnowledgeSourceId, SourceName = s.Name, s.SourceType });
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
        var excluded = 0;
        var items = hits
            .Where(h => byId.ContainsKey(h.ChunkId))
            .Select(h =>
            {
                var c = byId[h.ChunkId];
                var flagged = c.SuspicionFlags is not null;
                if (flagged && excludeFlagged)
                    excluded++;
                if (flagged && excludeFlagged)
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
            })
            .OfType<SearchResultItem>()
            .ToList();

        if (excluded > 0)
            logger.LogInformation("Excluded {Count} flagged chunk(s) from search context", excluded);
        await AttachComponentsAsync(items, ct);
        return items;
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
