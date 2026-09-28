using KnowledgeHub.Server.Domain.Entities;

namespace KnowledgeHub.Server.Graph;

/// <summary>
/// Cluster-aware diversification for graph search
/// (SPEC-20260927-temporal-episodic-knowledge-graph RF-004): candidates are
/// grouped by label (first label wins, falling back to entity type) and the
/// result round-robins across clusters so no single neighbourhood monopolizes
/// the response.
/// </summary>
public static class DiversityRanker
{
    /// <summary>Accepted levels and their per-cluster caps.</summary>
    public const int LowPerCluster = 5;
    public const int MediumPerCluster = 2;
    public const int HighPerCluster = 1;

    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high"];

    public static bool IsValidLevel(string? level) =>
        level is null || Levels.Contains(level.Trim().ToLowerInvariant());

    /// <summary>Per-cluster cap for a level — unknown levels throw (caller
    /// validates first so the error can list <see cref="Levels"/>).</summary>
    public static int MaxPerCluster(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        null or "medium" => MediumPerCluster,
        "low" => LowPerCluster,
        "high" => HighPerCluster,
        _ => throw new ArgumentException(
            $"invalid diversityLevel '{level}' — permitted values: {string.Join(", ", Levels)}",
            nameof(level))
    };

    /// <summary>Cluster key: first label when present, else the entity type.</summary>
    public static string ClusterOf(KgNode node) =>
        node.Labels is { Count: > 0 } ? node.Labels[0] : node.Type;

    /// <summary>
    /// Distributes candidates across clusters: each round takes the most recent
    /// remaining node of every cluster (capped per cluster) until
    /// <paramref name="maxResults"/> is reached.
    /// </summary>
    public static IReadOnlyList<KgNode> Select(
        IEnumerable<KgNode> candidates, string? level, int maxResults)
    {
        var cap = MaxPerCluster(level);
        var clusters = candidates
            .OrderByDescending(n => n.ObservedAt)
            .GroupBy(ClusterOf, StringComparer.Ordinal)
            .Select(g => g.Take(cap).ToList())
            .ToList();

        var selected = new List<KgNode>();
        while (selected.Count < maxResults)
        {
            var progressed = false;
            foreach (var cluster in clusters.OrderByDescending(c => c.FirstOrDefault()?.ObservedAt))
            {
                if (selected.Count >= maxResults)
                    break;
                if (cluster.Count == 0)
                    continue;
                selected.Add(cluster[0]);
                cluster.RemoveAt(0);
                progressed = true;
            }
            if (!progressed)
                break;
        }
        return selected;
    }
}
