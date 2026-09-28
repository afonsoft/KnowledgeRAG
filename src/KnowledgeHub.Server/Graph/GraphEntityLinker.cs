using KnowledgeHub.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Graph;

/// <summary>
/// SPEC-20260924-graph-expanded-retrieval RF-001/RF-002: lexical entity linking
/// for the retrieval path — matches normalized query terms against entity
/// names/aliases (zero LLM calls), expands 1-hop through edges and returns the
/// evidence chunks as a ranking: direct-entity evidence first, then
/// neighbour-entity evidence.
/// </summary>
public sealed class GraphEntityLinker(
    KnowledgeHubDbContext db,
    ILogger<GraphEntityLinker> logger)
{
    /// <summary>Minimum normalized entity-name length that may match — shorter
    /// names ("api", "db") would link almost every query.</summary>
    private const int MinNameLength = 4;
    private const int NodeScanCap = 5000;

    /// <summary>Entity nodes named (word-boundary match) inside the normalized query.</summary>
    public async Task<IReadOnlyList<Guid>> LinkAsync(
        string query, int maxEntities, CancellationToken ct)
    {
        var normalized = " " + EntityResolver.Normalize(query) + " ";

        // Word-boundary contains is not expressible in SQL — scan the bounded
        // node/alias name set and match in memory.
        var names = await db.KgNodes.AsNoTracking()
            .Where(n => n.NormalizedName.Length >= MinNameLength && n.ValidTo == null)
            .OrderBy(n => n.FirstSeenAt)
            .Take(NodeScanCap)
            .Select(n => new { n.Id, n.NormalizedName })
            .ToListAsync(ct);
        var aliasNames = await db.KgAliases.AsNoTracking()
            .Where(a => a.AliasNormalized.Length >= MinNameLength)
            .OrderBy(a => a.CreatedAt)
            .Take(NodeScanCap)
            .Select(a => new { a.KgNodeId, a.AliasNormalized })
            .ToListAsync(ct);

        var matched = new List<Guid>();
        foreach (var n in names.Concat(aliasNames.Select(a =>
                     new { Id = a.KgNodeId, NormalizedName = a.AliasNormalized })))
        {
            if (normalized.Contains(" " + n.NormalizedName + " ", StringComparison.Ordinal)
                && !matched.Contains(n.Id))
                matched.Add(n.Id);
            if (matched.Count >= maxEntities)
                break;
        }
        logger.LogDebug("Graph entity linking matched {Count} node(s)", matched.Count);
        return matched;
    }

    /// <summary>
    /// Ranked evidence chunks: edges incident to the matched entities first
    /// (direct evidence), then edges of their 1-hop neighbours (expanded
    /// evidence). Returns the ordered chunk ids plus the set sourced from
    /// direct entities — used for the optional relevance boost.
    /// </summary>
    public async Task<(List<Guid> Ranked, HashSet<Guid> DirectChunks)> EvidenceChunksAsync(
        IReadOnlyList<Guid> nodeIds, int maxNeighbors, CancellationToken ct)
    {
        if (nodeIds.Count == 0)
            return ([], []);

        var directEdges = await db.KgEdges.AsNoTracking()
            .Where(e => e.ValidTo == null
                && (nodeIds.Contains(e.FromNodeId) || nodeIds.Contains(e.ToNodeId)))
            .Select(e => new { e.FromNodeId, e.ToNodeId, e.EvidenceChunkId })
            .Take(maxNeighbors * 4)
            .ToListAsync(ct);

        var directChunks = directEdges.Select(e => e.EvidenceChunkId).Distinct().ToList();

        var neighborIds = directEdges
            .SelectMany(e => new[] { e.FromNodeId, e.ToNodeId })
            .Where(id => !nodeIds.Contains(id))
            .Distinct()
            .Take(maxNeighbors)
            .ToList();

        List<Guid> neighborChunks = [];
        if (neighborIds.Count > 0)
            neighborChunks = await db.KgEdges.AsNoTracking()
                .Where(e => e.ValidTo == null
                    && (neighborIds.Contains(e.FromNodeId) || neighborIds.Contains(e.ToNodeId)))
                .Select(e => e.EvidenceChunkId)
                .Distinct()
                .Take(maxNeighbors * 4)
                .ToListAsync(ct);

        var ranked = directChunks.Concat(neighborChunks).Distinct().ToList();
        return (ranked, directChunks.ToHashSet());
    }
}
