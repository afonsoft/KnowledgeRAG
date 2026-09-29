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
    IKnowledgeGraphStore store,
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
    private async Task<TemporalSearchResult> TrackAsync(string mode, Func<Task<TemporalSearchResult>> work)
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

        var linked = string.IsNullOrWhiteSpace(query)
            ? []
            : await linker.LinkAsync(query, maxEntities: 8, ct);

        // Temporal queries are history-aware: superseded rows (ValidTo set)
        // remain retrievable by their observation time — ValidTo is surfaced
        // in the payload so callers can tell current facts from history.
        IQueryable<KgEdge> edgesQuery = db.KgEdges.AsNoTracking();
        if (start is not null)
            edgesQuery = edgesQuery.Where(e => e.ObservedAt >= start);
        if (end is not null)
            edgesQuery = edgesQuery.Where(e => e.ObservedAt <= end);
        if (linked.Count > 0)
            edgesQuery = edgesQuery.Where(e =>
                linked.Contains(e.FromNodeId) || linked.Contains(e.ToNodeId));

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
        var nodes = nodeIds.Count == 0
            ? await db.KgNodes.AsNoTracking()
                .Where(n => (start == null || n.ObservedAt >= start)
                    && (end == null || n.ObservedAt <= end))
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
        var root = await store.FindNodeAsync(entity, ct)
            ?? throw new ArgumentException($"unknown entity '{entity}'", nameof(entity));
        depth = Math.Clamp(depth, 1, MaxDepth);
        maxResults = Math.Clamp(maxResults, 1, 100);

        var visited = new HashSet<Guid> { root.Id };
        var frontier = new List<Guid> { root.Id };
        var edges = new List<KgEdge>();
        var truncated = false;

        for (var d = 0; d < depth && frontier.Count > 0; d++)
        {
            var batch = await db.KgEdges.AsNoTracking()
                .Where(e => e.ValidTo == null
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

        var nodes = await db.KgNodes.AsNoTracking()
            .Where(n => visited.Contains(n.Id))
            .OrderByDescending(n => n.ObservedAt)
            .Take(maxResults)
            .ToListAsync(ct);
        var keep = nodes.Select(n => n.Id).ToHashSet();
        edges = edges.Where(e => keep.Contains(e.FromNodeId) && keep.Contains(e.ToNodeId)).ToList();

        return new TemporalSearchResult(nodes, edges, null, null, null, truncated);
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

        var episode = await db.KgEpisodes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        var nodes = await db.KgNodes.AsNoTracking()
            .Where(n => n.EpisodeId == id)
            .OrderByDescending(n => n.ObservedAt)
            .Take(maxResults + 1)
            .ToListAsync(ct);
        var truncated = nodes.Count > maxResults;
        if (truncated)
            nodes.RemoveRange(maxResults, nodes.Count - maxResults);

        var edges = await db.KgEdges.AsNoTracking()
            .Where(e => e.EpisodeId == id)
            .OrderByDescending(e => e.ObservedAt)
            .Take(EdgeScanCap)
            .ToListAsync(ct);

        return new TemporalSearchResult(nodes, edges, episode, null, null, truncated);
    }
}
