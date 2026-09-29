using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Search;

/// <summary>
/// SPEC-20260927-chunk-window-retrieval-and-autocut RF-003: dynamic tail pruning.
/// Instead of a rigid TopK, Autocut walks the descending score curve and cuts the
/// list at the N-th abrupt drop (sensitivity N ∈ [1..3]) — the elbow where
/// relevance collapses. Scores are normalized by the top hit so the rule is
/// provider-agnostic (RRF ~0.016, cosine ~0.9, bm25 arbitrary).
/// </summary>
public static class AutocutFilter
{
    /// <summary>A drop counts as abrupt when it exceeds the mean decline by this
    /// factor (>40% above average — "queda relativa maior que 40% da média").</summary>
    private const double AbruptDropFactor = 1.4;

    /// <summary>Hard safety ceiling for the number of results autocut may keep.</summary>
    public const int DefaultMaxClamp = 20;

    /// <summary>Effective ranking score of a hydrated hit — the score that
    /// actually ordered the list: rerank when present, else fused RRF, else raw
    /// (SPEC-20260929-search-scope-pipeline RF-002: autocut must read the curve
    /// the caller sees, or the elbow lands on the wrong axis).</summary>
    public static double EffectiveScore(SearchResultItem item) =>
        item.ScoreBreakdown?.Rerank ?? item.ScoreBreakdown?.Fused ?? item.Score;

    /// <summary>
    /// Count of leading items to keep for a descending score list. Always ≥1 when
    /// the input is non-empty; returns <paramref name="scores"/>.Count when no
    /// elbow is found (flat or smooth curves keep the whole list).
    /// </summary>
    public static int CutCount(IReadOnlyList<double> scores, int sensitivity = 1)
    {
        if (scores.Count <= 1)
            return scores.Count;

        var max = scores[0];
        if (max <= 0)
            return scores.Count;

        var drops = new double[scores.Count - 1];
        for (var i = 0; i < drops.Length; i++)
            drops[i] = Math.Max(0, (scores[i] - scores[i + 1]) / max);

        var mean = drops.Average();
        if (mean <= 0)
            return scores.Count; // identical scores — no elbow

        var seen = 0;
        for (var i = 0; i < drops.Length; i++)
            if (drops[i] > mean * AbruptDropFactor && ++seen >= sensitivity)
                return i + 1;
        return scores.Count;
    }

    /// <summary>Applies the cut to a ranked list; result size ∈ [1, maxClamp].</summary>
    public static List<SearchResultItem> Apply(
        IReadOnlyList<SearchResultItem> items, int sensitivity, int maxClamp = DefaultMaxClamp)
    {
        if (items.Count == 0)
            return [];
        var keep = CutCount(items.Select(EffectiveScore).ToArray(), sensitivity);
        return items.Take(Math.Clamp(keep, 1, Math.Max(1, maxClamp))).ToList();
    }
}
