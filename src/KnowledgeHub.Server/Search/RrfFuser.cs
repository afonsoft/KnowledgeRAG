namespace KnowledgeHub.Server.Search;

/// <summary>One fused ranking entry: chunk id + per-ranker provenance + RRF score.</summary>
public sealed record FusedHit(
    Guid ChunkId, int? VectorRank, int? LexicalRank, double Fused, int? GraphRank = null);

/// <summary>
/// Reciprocal Rank Fusion (SPEC-20260914-hybrid-retrieval RF-002):
/// score = Σ 1/(k + rank) with k=60 over the vector and lexical rankings.
/// </summary>
public static class RrfFuser
{
    public const int K = 60;

    /// <param name="vectorRanked">Chunk ids ordered best-first by the vector ranker.</param>
    /// <param name="lexicalRanked">Chunk ids ordered best-first by the lexical ranker.</param>
    public static IReadOnlyList<FusedHit> Fuse(
        IReadOnlyList<Guid> vectorRanked,
        IReadOnlyList<Guid> lexicalRanked,
        int topK) =>
        Fuse(
            [("vector", vectorRanked), ("lexical", lexicalRanked)],
            topK);

    /// <summary>
    /// N-list fusion (SPEC-20260924-query-expansion-hyde RF-001,
    /// SPEC-20260924-graph-expanded-retrieval RF-002): each ranked list
    /// contributes 1/(k+rank) per entry; the per-arm rank fields record the best
    /// rank achieved on each arm across all variant lists.
    /// Arms: "vector" | "lexical" | "graph".
    /// </summary>
    public static IReadOnlyList<FusedHit> Fuse(
        IReadOnlyList<(string Arm, IReadOnlyList<Guid> Ranked)> lists, int topK)
    {
        var scores = new Dictionary<Guid, FusedHit>();

        foreach (var (arm, ranked) in lists)
            Accumulate(ranked, arm);

        return scores.Values
            .OrderByDescending(h => h.Fused)
            .ThenBy(h => h.ChunkId)
            .Take(topK)
            .ToList();

        void Accumulate(IReadOnlyList<Guid> ranked, string arm)
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                var rank = i + 1;
                var contribution = 1.0 / (K + rank);
                if (scores.TryGetValue(ranked[i], out var hit))
                {
                    int? BestRank(string armName, int? existing) =>
                        arm != armName ? existing
                            : existing is null ? rank : Math.Min(existing.Value, rank);
                    scores[ranked[i]] = hit with
                    {
                        VectorRank = BestRank("vector", hit.VectorRank),
                        LexicalRank = BestRank("lexical", hit.LexicalRank),
                        GraphRank = BestRank("graph", hit.GraphRank),
                        Fused = hit.Fused + contribution
                    };
                }
                else
                    scores[ranked[i]] = new FusedHit(
                        ranked[i],
                        arm == "vector" ? rank : null,
                        arm == "lexical" ? rank : null,
                        contribution,
                        arm == "graph" ? rank : null);
            }
        }
    }
}
