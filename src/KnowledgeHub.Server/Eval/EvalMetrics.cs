using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Eval;

/// <summary>
/// SPEC-20260923-eval-harness RF-002: pure metric functions — no I/O, fully
/// unit-testable. All aggregates rounded to 4 decimals.
/// </summary>
public static class EvalMetrics
{
    /// <summary>A result hits when its URI is expected or its text contains all markers.</summary>
    public static bool IsHit(SearchResultItem result, EvalCase evalCase)
    {
        if (evalCase.ExpectedUris.Contains(result.UriReference, StringComparer.Ordinal))
            return true;
        return evalCase.ExpectedTextMarkers.Count > 0
            && evalCase.ExpectedTextMarkers.All(m =>
                result.ChunkText.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Fraction of expected URIs found in the result list.</summary>
    public static double RecallAtK(IReadOnlyList<SearchResultItem> results, EvalCase evalCase)
    {
        if (evalCase.ExpectNoAnswer)
            return results.Count == 0 ? 1.0 : 0.0;
        var expected = evalCase.ExpectedUris.Distinct(StringComparer.Ordinal).ToList();
        if (expected.Count == 0 && evalCase.ExpectedTextMarkers.Count > 0)
            return results.Any(r => IsHit(r, evalCase)) ? 1.0 : 0.0;
        if (expected.Count == 0)
            return 0;
        // Count distinct expected URIs found — several chunks share a document URI.
        var found = expected.Count(uri =>
            results.Any(r => string.Equals(r.UriReference, uri, StringComparison.Ordinal)));
        return (double)found / expected.Count;
    }

    /// <summary>Fraction of returned results that are hits.</summary>
    public static double PrecisionAtK(IReadOnlyList<SearchResultItem> results, EvalCase evalCase)
    {
        if (evalCase.ExpectNoAnswer)
            return results.Count == 0 ? 1.0 : 0.0;
        if (results.Count == 0)
            return 0;
        var hits = results.Count(r => IsHit(r, evalCase));
        return (double)hits / results.Count;
    }

    /// <summary>1/rank of the first hit (1-based); 0 on miss. Skipped for expectNoAnswer.</summary>
    public static double ReciprocalRank(IReadOnlyList<SearchResultItem> results, EvalCase evalCase)
    {
        if (evalCase.ExpectNoAnswer)
            return 0;
        for (var i = 0; i < results.Count; i++)
            if (IsHit(results[i], evalCase))
                return 1.0 / (i + 1);
        return 0;
    }

    /// <summary>
    /// Audit 2026-10-03 (R6): binary-graded nDCG@k — ranks hit placement, unlike
    /// recall (coverage) or MRR (first hit only). IDCG is the hits-first
    /// ordering of the same hit count, so nDCG is 1 whenever every hit precedes
    /// every miss regardless of how many hits exist.
    /// </summary>
    public static double NdcgAtK(IReadOnlyList<SearchResultItem> results, EvalCase evalCase)
    {
        if (evalCase.ExpectNoAnswer)
            return results.Count == 0 ? 1.0 : 0.0;
        var dcg = 0.0;
        var hits = 0;
        for (var i = 0; i < results.Count; i++)
        {
            if (!IsHit(results[i], evalCase))
                continue;
            hits++;
            dcg += 1.0 / Math.Log2(i + 2);
        }
        if (hits == 0)
            return 0;
        var idcg = 0.0;
        for (var i = 0; i < Math.Min(hits, results.Count); i++)
            idcg += 1.0 / Math.Log2(i + 2);
        return dcg / idcg;
    }

    public static double Round(double value) => Math.Round(value, 4);
}
