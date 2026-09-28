using KnowledgeHub.Server.Domain.Entities;

namespace KnowledgeHub.Server.Graph;

/// <summary>Traversal direction for bounded BFS.</summary>
public enum GraphDirection { Outbound, Inbound }

/// <summary>A bounded traversal result — nodes reached + edges walked.</summary>
public sealed record GraphSubgraph(
    IReadOnlyList<KgNode> Nodes,
    IReadOnlyList<KgEdge> Edges,
    bool Truncated);

/// <summary>
/// Graph persistence abstraction (SPEC-20260923-graphrag RF-002): the MVP is
/// SQLite adjacency tables; a Neo4j implementation can plug in later without
/// touching tools or ingestion.
/// </summary>
public interface IKnowledgeGraphStore
{
    /// <summary>Normalized merge: same (normalizedName, type) reuses the node;
    /// a different type creates a sibling node and records a conflict alias.
    /// Re-resolution bumps <c>ObservedAt</c> (SPEC-20260927-temporal-episodic-
    /// knowledge-graph RF-001); <paramref name="episodeId"/> stamps only newly
    /// created nodes — re-observed nodes keep their origin episode.</summary>
    Task<KgNode> ResolveNodeAsync(
        string name, string? type, Guid sourceId, CancellationToken ct, Guid? episodeId = null);

    /// <summary>Normalized-name lookup across nodes + aliases (tool arg → node).</summary>
    Task<KgNode?> FindNodeAsync(string name, CancellationToken ct);

    /// <summary>Prefix/contains suggestions for unknown-component errors (top N).</summary>
    Task<IReadOnlyList<KgNode>> SuggestAsync(string name, int max, CancellationToken ct);

    /// <summary>Adds edges with mandatory evidence chunk ids. Re-observation of
    /// the exact (from,to,kind,evidenceChunk) tuple bumps <c>ObservedAt</c>; a
    /// (from,to,kind) relation re-learned from different evidence inserts a new
    /// row and sets <c>ValidTo</c> on the superseded one — history is preserved
    /// (SPEC-20260927-temporal-episodic-knowledge-graph RF-001).</summary>
    Task<int> AddEdgesAsync(IEnumerable<KgEdge> edges, CancellationToken ct);

    /// <summary>Depth-capped BFS from a node — each node visited once
    /// (cycle-safe), edge count capped by <paramref name="maxEdges"/>.</summary>
    Task<GraphSubgraph> TraverseAsync(
        Guid startNodeId, GraphDirection direction, int depth, int maxEdges, CancellationToken ct);

    /// <summary>Bounded BFS shortest paths between two nodes.</summary>
    Task<IReadOnlyList<IReadOnlyList<KgEdge>>> FindPathsAsync(
        Guid fromId, Guid toId, int depth, int maxPaths, CancellationToken ct);

    /// <summary>All nodes reached by 1-hop inbound edges plus affected docs
    /// (analyze_impact).</summary>
    Task<GraphSubgraph> ImpactAsync(Guid nodeId, int maxEdges, CancellationToken ct);
}
