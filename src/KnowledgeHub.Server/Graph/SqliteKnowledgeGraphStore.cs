using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Graph;

/// <summary>
/// SQLite adjacency-table graph store (SPEC-20260923-graphrag RF-002/RF-004):
/// bounded BFS in C# over indexed FK columns — each node visited once, result
/// size hard-capped. Recursive-CTE was considered; per-level EF queries are
/// equally bounded and stay provider-portable.
/// </summary>
public sealed class SqliteKnowledgeGraphStore(
    KnowledgeHubDbContext db,
    ILogger<SqliteKnowledgeGraphStore> logger) : IKnowledgeGraphStore
{
    /// <inheritdoc />
    public async Task<KgNode> ResolveNodeAsync(
        string name, string? type, Guid sourceId, CancellationToken ct, Guid? episodeId = null)
    {
        var normalized = EntityResolver.Normalize(name);
        var nodeType = EntityResolver.NormalizeType(type);
        if (normalized.Length == 0)
            throw new ArgumentException("entity name cannot be empty", nameof(name));

        var node = await db.KgNodes
            .FirstOrDefaultAsync(n => n.NormalizedName == normalized && n.Type == nodeType, ct);
        if (node is null)
        {
            node = await CreateNodeWithConflictsAsync(
                name, normalized, nodeType, sourceId, episodeId, ct);
        }
        else
        {
            // SPEC-20260927-temporal-episodic-knowledge-graph RF-001: every
            // re-observation moves the temporal cursor.
            node.ObservedAt = DateTime.UtcNow;
            if (node.Name != name.Trim()
                && !await AliasExistsOrPendingAsync(normalized, node.Id, ct))
            {
                // Variant spelling merged into the canonical node — recorded.
                db.KgAliases.Add(new KgAlias
                {
                    AliasNormalized = normalized,
                    KgNodeId = node.Id,
                    KnowledgeSourceId = sourceId,
                    Reason = "merge"
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return node;
    }

    /// <summary>Creates the node and records conflict aliases for every sibling
    /// holding the same normalized name under a different type.</summary>
    private async Task<KgNode> CreateNodeWithConflictsAsync(
        string name, string normalized, string nodeType, Guid sourceId,
        Guid? episodeId, CancellationToken ct)
    {
        var node = new KgNode
        {
            Name = name.Trim(),
            NormalizedName = normalized,
            Type = nodeType,
            EpisodeId = episodeId
        };
        db.KgNodes.Add(node);
        // Conflict visibility: another node already holds this normalized
        // name under a different type — both get conflict alias rows.
        var siblings = await db.KgNodes
            .Where(n => n.NormalizedName == normalized && n.Type != nodeType)
            .ToListAsync(ct);
        // SPEC-20260926-kg-alias-conflict-dedup RF-001: siblings may already
        // carry a conflict/merge alias for this normalized name (entity
        // gaining a 3rd+ type, or pending adds in the same batch) — the
        // (AliasNormalized, KgNodeId) unique index would blow the save.
        // Where-refactor not applicable — the skip predicate is awaited.
        foreach (var siblingId in siblings.Select(sibling => sibling.Id))
        {
            if (await AliasExistsOrPendingAsync(normalized, siblingId, ct))
                continue;
            db.KgAliases.Add(new KgAlias
            {
                AliasNormalized = normalized,
                KgNodeId = siblingId,
                KnowledgeSourceId = sourceId,
                Reason = "conflict"
            });
        }
        if (siblings.Count > 0)
        {
            logger.LogInformation(
                "entity '{Name}' now exists under {Count}+1 types — conflict aliases recorded",
                normalized, siblings.Count);
            if (!await AliasExistsOrPendingAsync(normalized, node.Id, ct))
                db.KgAliases.Add(new KgAlias
                {
                    AliasNormalized = normalized,
                    KgNodeId = node.Id,
                    KnowledgeSourceId = sourceId,
                    Reason = "conflict"
                });
        }
        return node;
    }

    /// <summary>RF-001: the AnyAsync check alone misses rows still pending in the
    /// change tracker (Added but not yet flushed) — both must be consulted.</summary>
    private async Task<bool> AliasExistsOrPendingAsync(string normalized, Guid nodeId, CancellationToken ct)
    {
        var pending = db.ChangeTracker.Entries<KgAlias>()
            .Any(e => e.State == EntityState.Added
                && e.Entity.AliasNormalized == normalized
                && e.Entity.KgNodeId == nodeId);
        if (pending)
            return true;
        return await db.KgAliases.AnyAsync(
            a => a.AliasNormalized == normalized && a.KgNodeId == nodeId, ct);
    }

    /// <inheritdoc />
    public async Task<KgNode?> FindNodeAsync(string name, CancellationToken ct)
    {
        var normalized = EntityResolver.Normalize(name);
        var node = await db.KgNodes
            .Where(n => n.NormalizedName == normalized && n.ValidTo == null)
            .OrderBy(n => n.Type)
            .FirstOrDefaultAsync(ct);
        if (node is not null)
            return node;
        var alias = await db.KgAliases
            .Where(a => a.AliasNormalized == normalized)
            .Select(a => (Guid?)a.KgNodeId)
            .FirstOrDefaultAsync(ct);
        return alias is { } aliasId
            ? await db.KgNodes.FirstOrDefaultAsync(n => n.Id == aliasId && n.ValidTo == null, ct)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KgNode>> SuggestAsync(string name, int max, CancellationToken ct)
    {
        var normalized = EntityResolver.Normalize(name);
        if (normalized.Length == 0)
            return [];
        // Prefix first, then contains — bounded by max.
        var prefix = await db.KgNodes
            .Where(n => n.ValidTo == null && n.NormalizedName.StartsWith(normalized))
            .OrderBy(n => n.NormalizedName).Take(max).ToListAsync(ct);
        if (prefix.Count >= max)
            return prefix;
        var rest = await db.KgNodes
            .Where(n => n.ValidTo == null && n.NormalizedName.Contains(normalized)
                && !prefix.Select(p => p.Id).Contains(n.Id))
            .OrderBy(n => n.NormalizedName).Take(max - prefix.Count).ToListAsync(ct);
        return [.. prefix, .. rest];
    }

    /// <inheritdoc />
    public async Task<int> AddEdgesAsync(IEnumerable<KgEdge> edges, CancellationToken ct)
    {
        var batch = edges.ToList();
        if (batch.Count == 0)
            return 0;
        var now = DateTime.UtcNow;
        var added = 0;
        // provenance is non-negotiable — EvidenceChunkId.Empty is skipped
        foreach (var edge in batch.Where(e => e.EvidenceChunkId != Guid.Empty))
        {

            // Re-observation of the identical fact: bump ObservedAt only.
            var identical = await FindIdenticalAsync(edge, ct);
            if (identical is not null)
            {
                identical.ObservedAt = now;
                continue;
            }

            await SoftHistoricizeAsync(edge, now, ct);

            edge.ObservedAt = now;
            edge.ValidFrom = now;
            db.KgEdges.Add(edge);
            added++;
        }
        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <inheritdoc />
    public async Task<GraphSubgraph> TraverseAsync(
        Guid startNodeId, GraphDirection direction, int depth, int maxEdges, CancellationToken ct)
    {
        depth = Math.Clamp(depth, 1, 3);
        var visited = new HashSet<Guid> { startNodeId };
        var edges = new List<KgEdge>();
        var frontier = new List<Guid> { startNodeId };
        var truncated = false;

        for (var d = 0; d < depth && frontier.Count > 0; d++)
        {
            var batch = await LoadFrontierEdgesAsync(frontier, direction, maxEdges - edges.Count + 1, ct);
            if (batch.Count == 0)
                break;
            truncated = edges.Count + batch.Count > maxEdges;
            frontier = CollectNeighbors(batch, edges, visited, direction, maxEdges - edges.Count);
            if (truncated)
                break;
        }

        var nodes = await db.KgNodes.Where(n => visited.Contains(n.Id)).ToListAsync(ct);
        return new GraphSubgraph(nodes, edges, truncated);
    }

    /// <summary>One BFS level of still-valid edges touching the frontier,
    /// bounded by <paramref name="take"/>.</summary>
    private async Task<List<KgEdge>> LoadFrontierEdgesAsync(
        List<Guid> frontier, GraphDirection direction, int take, CancellationToken ct)
        => await db.KgEdges
            .Include(e => e.From).Include(e => e.To).Include(e => e.Document)
            .Where(e => e.ValidTo == null && (direction == GraphDirection.Outbound
                ? frontier.Contains(e.FromNodeId)
                : frontier.Contains(e.ToNodeId)))
            .Take(take)
            .ToListAsync(ct);

    /// <summary>Appends up to <paramref name="take"/> batch edges to the result
    /// and returns the next frontier (first-visit neighbors only).</summary>
    private static List<Guid> CollectNeighbors(
        List<KgEdge> batch, List<KgEdge> edges, HashSet<Guid> visited,
        GraphDirection direction, int take)
    {
        var next = new List<Guid>();
        foreach (var e in batch.Take(take))
        {
            edges.Add(e);
            var neighbor = direction == GraphDirection.Outbound ? e.ToNodeId : e.FromNodeId;
            if (visited.Add(neighbor))
                next.Add(neighbor);
        }
        return next;
    }

    /// <inheritdoc />
    /// <summary>Persisted or pending (change-tracker) edge identical to
    /// <paramref name="edge"/> — same endpoints, kind, and evidence chunk.</summary>
    private async Task<KgEdge?> FindIdenticalAsync(KgEdge edge, CancellationToken ct)
    {
        var existing = await db.KgEdges.FirstOrDefaultAsync(x =>
            x.FromNodeId == edge.FromNodeId && x.ToNodeId == edge.ToNodeId
            && x.Kind == edge.Kind && x.EvidenceChunkId == edge.EvidenceChunkId, ct);
        return existing
            ?? db.ChangeTracker.Entries<KgEdge>()
                .FirstOrDefault(x => x.State == EntityState.Added
                    && x.Entity.ValidTo == null
                    && x.Entity.FromNodeId == edge.FromNodeId
                    && x.Entity.ToNodeId == edge.ToNodeId
                    && x.Entity.Kind == edge.Kind
                    && x.Entity.EvidenceChunkId == edge.EvidenceChunkId)?.Entity;
    }

    /// <summary>Same relation learned from fresher evidence → soft-historicize
    /// the still-valid prior rows (ValidTo instead of delete).</summary>
    private async Task SoftHistoricizeAsync(KgEdge edge, DateTime now, CancellationToken ct)
    {
        var superseded = await db.KgEdges
            .Where(x => x.FromNodeId == edge.FromNodeId && x.ToNodeId == edge.ToNodeId
                && x.Kind == edge.Kind && x.ValidTo == null)
            .ToListAsync(ct);
        foreach (var stale in superseded)
            stale.ValidTo = now;
        // Also catch identical triples still pending in the change tracker.
        foreach (var pending in db.ChangeTracker.Entries<KgEdge>()
                     .Where(x => x.State == EntityState.Added
                         && x.Entity.FromNodeId == edge.FromNodeId
                         && x.Entity.ToNodeId == edge.ToNodeId
                         && x.Entity.Kind == edge.Kind
                         && x.Entity.ValidTo == null))
            pending.Entity.ValidTo = now;
    }

    public async Task<IReadOnlyList<IReadOnlyList<KgEdge>>> FindPathsAsync(
        Guid fromId, Guid toId, int depth, int maxPaths, CancellationToken ct)
    {
        depth = Math.Clamp(depth, 1, 3);
        var paths = new List<IReadOnlyList<KgEdge>>();
        // BFS with parent tracking; stop at first level that reaches `toId`
        // (shortest paths only) or when maxPaths is hit.
        var parent = new Dictionary<Guid, (Guid Prev, KgEdge Edge)>();
        var frontier = new List<Guid> { fromId };
        var visited = new HashSet<Guid> { fromId };
        var reached = false;

        for (var d = 0; d < depth && frontier.Count > 0 && !reached; d++)
        {
            var batch = await db.KgEdges
                .Include(e => e.From).Include(e => e.To).Include(e => e.Document)
                .Where(e => e.ValidTo == null && frontier.Contains(e.FromNodeId))
                .ToListAsync(ct);
            frontier = ExpandFrontier(batch, parent, visited, toId, ref reached);
        }

        if (!reached)
            return paths;

        // Reconstruct the shortest path from parent pointers.
        var path = new List<KgEdge>();
        for (var cur = toId; cur != fromId && parent.TryGetValue(cur, out var p); cur = p.Prev)
            path.Insert(0, p.Edge);
        if (path.Count > 0)
            paths.Add(path);
        return paths;
    }

    /// <summary>One BFS level: visits every edge target once, records the
    /// parent edge, and reports whether <paramref name="toId"/> was reached.</summary>
    private static List<Guid> ExpandFrontier(
        List<KgEdge> batch, Dictionary<Guid, (Guid Prev, KgEdge Edge)> parent,
        HashSet<Guid> visited, Guid toId, ref bool reached)
    {
        var next = new List<Guid>();
        foreach (var e in batch.Where(e => visited.Add(e.ToNodeId)))
        {
            parent[e.ToNodeId] = (e.FromNodeId, e);
            if (e.ToNodeId == toId)
                reached = true;
            else
                next.Add(e.ToNodeId);
        }
        return next;
    }

    /// <inheritdoc />
    public async Task<GraphSubgraph> ImpactAsync(Guid nodeId, int maxEdges, CancellationToken ct)
    {
        // 1-hop dependents (inbound) + the documents behind the evidence.
        var edges = await db.KgEdges
            .Include(e => e.From).Include(e => e.To).Include(e => e.Document)
            .Where(e => e.ToNodeId == nodeId && e.ValidTo == null)
            .Take(maxEdges + 1)
            .ToListAsync(ct);
        var truncated = edges.Count > maxEdges;
        edges = edges.Take(maxEdges).ToList();

        var nodeIds = edges.Select(e => e.FromNodeId).Append(nodeId).Distinct().ToList();
        var nodes = await db.KgNodes.Where(n => nodeIds.Contains(n.Id)).ToListAsync(ct);
        return new GraphSubgraph(nodes, edges, truncated);
    }
}
