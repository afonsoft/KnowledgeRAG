using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Graph;

/// <summary>One temporal/episodic search result — subgraph plus the window
/// that produced it.</summary>
public sealed record TemporalSearchResult(
    IReadOnlyList<KgNode> Nodes,
    IReadOnlyList<KgEdge> Edges,
    KgEpisode? Episode,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    bool Truncated);

/// <summary>
/// Temporal and episodic retrieval over the knowledge graph
/// (SPEC-20260927-temporal-episodic-knowledge-graph RF-002/RF-003/RF-004):
/// windowed fact search, sliding recent windows, multi-hop relationship
/// exploration (default max_depth = 2), cluster-diversified results and
/// per-episode context. Depth is hard-capped at 3 per the guardrails.
/// </summary>
public sealed class TemporalGraphRetriever(
    KnowledgeHubDbContext db,
    GraphEntityLinker linker,
    Auth.ICallerScopeProvider scopeProvider,
    ILogger<TemporalGraphRetriever> logger)
{
    /// <summary>Defensive response ceiling for MCP payloads (RF-005).</summary>
    public const int MaxGraphResponsePreviewBytes = 8192;

    /// <summary>Multi-hop depth: default 2 (SPEC), hard cap 3 (guardrails).</summary>
    public const int DefaultDepth = 2;
    public const int MaxDepth = 3;

    /// <summary>Upper bound for edge scans feeding one response.</summary>
    private const int EdgeScanCap = 400;

    /// <summary>SPEC-20260928-observability-followups RF-002/RF-003: every mode
    /// emits a <c>search.temporal_graph</c> span (tag <c>mode</c>) + a
    /// <c>graph.temporal_queries</c> counter; failures mark the span Error.</summary>
    private static async Task<TemporalSearchResult> TrackAsync(string mode, Func<Task<TemporalSearchResult>> work)
    {
        Telemetry.KnowledgeHubMetrics.TemporalGraphQueries.Add(1,
            new KeyValuePair<string, object?>("mode", mode));
        using var span = Telemetry.KnowledgeHubActivity.Start("search.temporal_graph");
        span?.SetTag("mode", mode);
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            Telemetry.KnowledgeHubActivity.Fail(span, ex);
            throw;
        }
    }

    /// <summary>SPEC-20260929 RF-006: node ids a caller may see — reachable via
    /// an edge whose KnowledgeSourceId is allowed, or produced by an episode of
    /// an allowed source. Null = unrestricted caller.</summary>
    private async Task<HashSet<Guid>?> AllowedNodeIdsAsync(
        Auth.CallerScope scope, CancellationToken ct)
    {
        if (scope.AllowedSourceIds is not { } allowed)
            return null;
        if (allowed.Count == 0)
            return []; // deny-all scope — never a missing-filter fallback
        var viaEdges = db.KgEdges.AsNoTracking()
            .Where(e => allowed.Contains(e.KnowledgeSourceId))
            .Select(e => e.FromNodeId)
            .Concat(db.KgEdges.AsNoTracking()
                .Where(e => allowed.Contains(e.KnowledgeSourceId))
                .Select(e => e.ToNodeId));
        var viaEpisodes = db.KgNodes.AsNoTracking()
            .Where(n => n.Episode != null && n.Episode.KnowledgeSourceId != null
                && allowed.Contains(n.Episode.KnowledgeSourceId.Value))
            .Select(n => n.Id);
        return (await viaEdges.Concat(viaEpisodes).Distinct().ToListAsync(ct))
            .ToHashSet();
    }

    /// <summary>Linker overfetch: resolve more candidates than needed so
    /// scope-filtering restricted entities can't crowd every allowed one out
    /// of the working set (devin-review #402).</summary>
    private const int MaxLinkCandidates = 24;

    /// <summary>Resolves an entity by name among scope-ALLOWED candidates —
    /// a restricted homonym must not shadow an allowed twin with the same
    /// normalized name (devin-review #402).</summary>
    private async Task<KgNode?> FindAllowedNodeAsync(
        string entity, HashSet<Guid>? allowedNodes, CancellationToken ct)
    {
        var normalized = EntityResolver.Normalize(entity);
        var candidates = await db.KgNodes.AsNoTracking()
            .Where(n => n.NormalizedName == normalized && n.ValidTo == null)
            .OrderBy(n => n.Type)
            .ToListAsync(ct);
        var node = allowedNodes is null
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(n => allowedNodes.Contains(n.Id));
        if (node is not null)
            return node;

        var aliasIds = await db.KgAliases.AsNoTracking()
            .Where(a => a.AliasNormalized == normalized)
            .Select(a => a.KgNodeId)
            .ToListAsync(ct);
        if (aliasIds.Count == 0)
            return null;
        var aliasNodes = await db.KgNodes.AsNoTracking()
            .Where(n => aliasIds.Contains(n.Id) && n.ValidTo == null)
            .ToListAsync(ct);
        return allowedNodes is null
            ? aliasNodes.FirstOrDefault()
            : aliasNodes.FirstOrDefault(n => allowedNodes.Contains(n.Id));
    }

    /// <summary>Facts whose <c>ObservedAt</c> falls inside [start, end].
    /// When the query links to known entities the window is intersected with
    /// their neighbourhoods; otherwise the window is browsed directly.</summary>
    public Task<TemporalSearchResult> SearchTemporalWindowAsync(
        string query, DateTime? start, DateTime? end,
        int maxResults = 15, CancellationToken ct = default) =>
        TrackAsync("window", () => SearchTemporalWindowCoreAsync(query, start, end, maxResults, ct));

    private async Task<TemporalSearchResult> SearchTemporalWindowCoreAsync(
        string query, DateTime? start, DateTime? end,
        int maxResults, CancellationToken ct)
    {
        if (start is not null && end is not null && start > end)
            throw new ArgumentException(
                $"start '{start:O}' must precede end '{end:O}'", nameof(start));
        maxResults = Math.Clamp(maxResults, 1, 100);

        // SPEC-20260929 RF-006: scope resolved once — edges and nodes are
        // filtered before they ever leave this service.
        var scope = await scopeProvider.GetAsync(ct);
        var allowedNodes = await AllowedNodeIdsAsync(scope, ct);

        var linkedRaw = string.IsNullOrWhiteSpace(query)
            ? []
            : await linker.LinkAsync(query, maxEntities: MaxLinkCandidates, ct);
        var linked = linkedRaw
            .Where(id => allowedNodes is null || allowedNodes.Contains(id))
            .Take(8)
            .ToList();
        // RF-006: a non-empty query whose every linked entity is scope-filtered
        // must NOT fall through to an unfiltered window scan — that would
        // return facts unrelated to the query. Empty result is the honest
        // answer (devin-review #402).
        if (!string.IsNullOrWhiteSpace(query) && linkedRaw.Count > 0 && linked.Count == 0)
            return new TemporalSearchResult([], [], null, start, end, false);

        // Temporal queries are history-aware: superseded rows (ValidTo set)
        // remain retrievable by their observation time — ValidTo is surfaced
        // in the payload so callers can tell current facts from history.
        var edgesQuery = BuildWindowEdgeQuery(scope, start, end, linked);
        var edges = await edgesQuery
            .OrderByDescending(e => e.ObservedAt)
            .Take(EdgeScanCap + 1)
            .ToListAsync(ct);
        var truncated = edges.Count > EdgeScanCap;
        if (truncated)
            edges.RemoveRange(EdgeScanCap, edges.Count - EdgeScanCap);

        var nodeIds = edges
            .SelectMany(e => new[] { e.FromNodeId, e.ToNodeId })
            .Concat(linked)
            .Distinct()
            .ToList();
        var unscoped = db.KgNodes.AsNoTracking();
        if (start is { } startAt)
            unscoped = unscoped.Where(n => n.ObservedAt >= startAt);
        if (end is { } endAt)
            unscoped = unscoped.Where(n => n.ObservedAt <= endAt);
        if (allowedNodes is not null)
            unscoped = unscoped.Where(n => allowedNodes.Contains(n.Id));
        var nodes = nodeIds.Count == 0
            ? await unscoped
                .OrderByDescending(n => n.ObservedAt)
                .Take(maxResults)
                .ToListAsync(ct)
            : await db.KgNodes.AsNoTracking()
                .Where(n => nodeIds.Contains(n.Id))
                .OrderByDescending(n => n.ObservedAt)
                .Take(maxResults)
                .ToListAsync(ct);

        var keep = nodes.Select(n => n.Id).ToHashSet();
        edges = edges.Where(e => keep.Contains(e.FromNodeId) && keep.Contains(e.ToNodeId)).ToList();

        logger.LogDebug(
            "temporal window search '{Query}' [{Start}..{End}] → {Nodes} node(s), {Edges} edge(s)",
            query, start, end, nodes.Count, edges.Count);
        return new TemporalSearchResult(nodes, edges, null, start, end, truncated);
    }

    /// <summary>Composes the window edge query: caller scope, observation
    /// bounds and linked-entity restriction (history-aware — ValidTo rows stay
    /// retrievable by ObservedAt).</summary>
    private IQueryable<KgEdge> BuildWindowEdgeQuery(
        Auth.CallerScope scope, DateTime? start, DateTime? end, List<Guid> linked)
    {
        IQueryable<KgEdge> edgesQuery = db.KgEdges.AsNoTracking();
        if (scope.AllowedSourceIds is { } allowed)
            edgesQuery = edgesQuery.Where(e => allowed.Contains(e.KnowledgeSourceId));
        if (start is not null)
            edgesQuery = edgesQuery.Where(e => e.ObservedAt >= start);
        if (end is not null)
            edgesQuery = edgesQuery.Where(e => e.ObservedAt <= end);
        if (linked.Count > 0)
            edgesQuery = edgesQuery.Where(e =>
                linked.Contains(e.FromNodeId) || linked.Contains(e.ToNodeId));
        return edgesQuery;
    }

    /// <summary>Sliding-window variant: facts observed since
    /// <c>UtcNow - window</c> (RF-003 — window comes pre-validated).</summary>
    public Task<TemporalSearchResult> SearchRecentContextAsync(
        string query, TimeSpan window, int maxResults = 10, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - window;
        // Own mode tag — calls Core directly so one call = one span/counter.
        return TrackAsync("recent",
            () => SearchTemporalWindowCoreAsync(query, cutoff, null, maxResults, ct));
    }

    /// <summary>Multi-hop relationship exploration around an entity — BFS over
    /// currently-valid edges in BOTH directions (Graphiti-style), default
    /// depth 2, hard-capped at <see cref="MaxDepth"/>.</summary>
    public Task<TemporalSearchResult> SearchEntityRelationshipsAsync(
        string entity, int depth = DefaultDepth, int maxResults = 15,
        CancellationToken ct = default) =>
        TrackAsync("relationships",
            () => SearchEntityRelationshipsCoreAsync(entity, depth, maxResults, ct));

    private async Task<TemporalSearchResult> SearchEntityRelationshipsCoreAsync(
        string entity, int depth, int maxResults, CancellationToken ct)
    {
        var scope = await scopeProvider.GetAsync(ct);
        var allowedNodes = await AllowedNodeIdsAsync(scope, ct);

        var root = await FindAllowedNodeAsync(entity, allowedNodes, ct);
        // RF-006: a restricted entity is indistinguishable from an unknown one.
        if (root is null)
            throw new ArgumentException($"unknown entity '{entity}'", nameof(entity));
        depth = Math.Clamp(depth, 1, MaxDepth);
        maxResults = Math.Clamp(maxResults, 1, 100);

        var edges = new List<KgEdge>();
        var (visited, truncated) = await ExpandRelationshipFrontierAsync(
            scope, root.Id, depth, edges, ct);

        var nodes = await db.KgNodes.AsNoTracking()
            .Where(n => visited.Contains(n.Id))
            .OrderByDescending(n => n.ObservedAt)
            .Take(maxResults)
            .ToListAsync(ct);
        var keep = nodes.Select(n => n.Id).ToHashSet();
        edges = edges.Where(e => keep.Contains(e.FromNodeId) && keep.Contains(e.ToNodeId)).ToList();

        return new TemporalSearchResult(nodes, edges, null, null, null, truncated);
    }

    /// <summary>Layered BFS over still-valid edges (<see cref="EdgeScanCap"/>
    /// global cap). Returns the visited node set and whether the cap cut the
    /// expansion short.</summary>
    private async Task<(HashSet<Guid> Visited, bool Truncated)> ExpandRelationshipFrontierAsync(
        Auth.CallerScope scope, Guid rootId, int depth, List<KgEdge> edges, CancellationToken ct)
    {
        var visited = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        var truncated = false;

        for (var d = 0; d < depth && frontier.Count > 0; d++)
        {
            var batch = await db.KgEdges.AsNoTracking()
                .Where(e => e.ValidTo == null
                    && (scope.AllowedSourceIds == null
                        || scope.AllowedSourceIds.Contains(e.KnowledgeSourceId))
                    && (frontier.Contains(e.FromNodeId) || frontier.Contains(e.ToNodeId)))
                .OrderByDescending(e => e.ObservedAt)
                .Take(EdgeScanCap - edges.Count + 1)
                .ToListAsync(ct);
            truncated = batch.Count > EdgeScanCap - edges.Count;

            var next = new List<Guid>();
            foreach (var e in batch.Take(EdgeScanCap - edges.Count))
            {
                edges.Add(e);
                if (visited.Add(e.FromNodeId))
                    next.Add(e.FromNodeId);
                if (visited.Add(e.ToNodeId))
                    next.Add(e.ToNodeId);
            }
            frontier = next;
            if (truncated)
                break;
        }
        return (visited, truncated);
    }

    /// <summary>Cluster-diversified neighbourhood of an entity (RF-004):
    /// 2-hop candidates spread across label/type clusters.</summary>
    public Task<TemporalSearchResult> SearchDiverseResultsAsync(
        string entity, string diversityLevel = "medium", int maxResults = 10,
        CancellationToken ct = default) =>
        TrackAsync("diverse",
            () => SearchDiverseResultsCoreAsync(entity, diversityLevel, maxResults, ct));

    private async Task<TemporalSearchResult> SearchDiverseResultsCoreAsync(
        string entity, string diversityLevel, int maxResults, CancellationToken ct)
    {
        if (!DiversityRanker.IsValidLevel(diversityLevel))
            throw new ArgumentException(
                $"invalid diversityLevel '{diversityLevel}' — permitted values: " +
                string.Join(", ", DiversityRanker.Levels),
                nameof(diversityLevel));

        var sub = await SearchEntityRelationshipsCoreAsync(
            entity, DefaultDepth, 100, ct);
        var selected = DiversityRanker.Select(sub.Nodes, diversityLevel, maxResults);
        var keep = selected.Select(n => n.Id).ToHashSet();
        var edges = sub.Edges
            .Where(e => keep.Contains(e.FromNodeId) && keep.Contains(e.ToNodeId))
            .ToList();
        return new TemporalSearchResult(
            selected, edges, null, null, null, sub.Truncated || selected.Count < sub.Nodes.Count);
    }

    /// <summary>All facts attributed to one episode (ingestion run / agent
    /// session) — newest first, capped.</summary>
    public Task<TemporalSearchResult> SearchEpisodeContextAsync(
        string episodeId, int maxResults = 10, CancellationToken ct = default) =>
        TrackAsync("episode",
            () => SearchEpisodeContextCoreAsync(episodeId, maxResults, ct));

    private async Task<TemporalSearchResult> SearchEpisodeContextCoreAsync(
        string episodeId, int maxResults, CancellationToken ct)
    {
        if (!Guid.TryParse(episodeId?.Trim(), out var id))
            throw new ArgumentException(
                $"invalid episodeId '{episodeId}' — expected a GUID", nameof(episodeId));
        maxResults = Math.Clamp(maxResults, 1, 100);

        var scope = await scopeProvider.GetAsync(ct);
        var episode = await db.KgEpisodes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        // RF-006: an episode of a restricted source returns nothing —
        // existence stays indistinguishable from "no such episode".
        if (episode?.KnowledgeSourceId is { } epSource
            && scope.AllowedSourceIds is { } allowed
            && !allowed.Contains(epSource))
            return new TemporalSearchResult([], [], null, null, null, false);

        // RF-006: an episode with a null KnowledgeSourceId (agent session)
        // passes the episode-level check — its NODES still carry source
        // provenance and must respect the caller's scope (devin-review #402).
        var allowedNodes = await AllowedNodeIdsAsync(scope, ct);
        var nodes = await db.KgNodes.AsNoTracking()
            .Where(n => n.EpisodeId == id
                && (allowedNodes == null || allowedNodes.Contains(n.Id)))
            .OrderByDescending(n => n.ObservedAt)
            .Take(maxResults + 1)
            .ToListAsync(ct);
        var truncated = nodes.Count > maxResults;
        if (truncated)
            nodes.RemoveRange(maxResults, nodes.Count - maxResults);

        var edges = await db.KgEdges.AsNoTracking()
            .Where(e => e.EpisodeId == id
                && (scope.AllowedSourceIds == null
                    || scope.AllowedSourceIds.Contains(e.KnowledgeSourceId)))
            .OrderByDescending(e => e.ObservedAt)
            .Take(EdgeScanCap)
            .ToListAsync(ct);

        return new TemporalSearchResult(nodes, edges, episode, null, null, truncated);
    }
}
