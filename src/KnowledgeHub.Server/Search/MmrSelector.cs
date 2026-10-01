namespace KnowledgeHub.Server.Search;

/// <summary>
/// SPEC-20260924-mmr-diversity RF-001/RF-002: Maximal Marginal Relevance over the
/// fused candidate window — <c>score(i) = λ·rel(i) − (1−λ)·maxSim(i, selected)</c>
/// where rel is the candidate score normalised by the best candidate's score and
/// sim is cosine over the already-persisted chunk embeddings. Pure — no I/O.
/// Per-document quota is applied as a hard rule inside the selection loop.
/// </summary>
public static class MmrSelector
{
    public sealed record Candidate(Guid ChunkId, Guid DocumentId, double Score, float[]? Vector);

    /// <summary>Returns the selected chunk ids in final order (≤ topK).
    /// <paramref name="minScore"/> filters candidates before selection (score floor);
    /// <paramref name="maxPerDocument"/> ≤ 0 disables the quota.</summary>
    public static List<Guid> Select(
        IReadOnlyList<Candidate> candidates, int topK, double lambda,
        int maxPerDocument = 0, double minScore = 0)
    {
        var pool = candidates
            .Where(c => minScore <= 0 || c.Score >= minScore)
            .OrderByDescending(c => c.Score)
            .ToList();
        if (pool.Count == 0)
            return [];

        var maxScore = pool[0].Score;
        var selected = new List<Candidate>(topK);
        var perDoc = new Dictionary<Guid, int>();

        while (selected.Count < topK && pool.Count > 0)
        {
            var best = PickBest(pool, selected, perDoc, lambda, maxScore, maxPerDocument);
            if (best is null)
                break; // every remaining candidate hit the per-document quota
            pool.Remove(best);
            selected.Add(best);
            perDoc[best.DocumentId] = perDoc.GetValueOrDefault(best.DocumentId) + 1;
        }

        return selected.Select(c => c.ChunkId).ToList();
    }

    /// <summary>One MMR round: the quota-eligible candidate with the highest
    /// marginal score, or null when the quota rules every candidate out.</summary>
    private static Candidate? PickBest(
        List<Candidate> pool, List<Candidate> selected,
        Dictionary<Guid, int> perDoc, double lambda, double maxScore, int maxPerDocument)
    {
        Candidate? best = null;
        var bestMmr = double.NegativeInfinity;
        foreach (var c in pool)
        {
            if (maxPerDocument > 0 && perDoc.GetValueOrDefault(c.DocumentId) >= maxPerDocument)
                continue;
            var rel = maxScore > 0 ? c.Score / maxScore : 0;
            var mmr = lambda * rel - (1 - lambda) * MaxSimilarity(c, selected);
            if (mmr > bestMmr)
            {
                bestMmr = mmr;
                best = c;
            }
        }
        return best;
    }

    private static double MaxSimilarity(Candidate c, List<Candidate> selected)
    {
        if (c.Vector is null)
            return 0;
        var max = 0.0;
        foreach (var s in selected.Where(s => s.Vector is not null))
        {
            var sim = Cosine(c.Vector, s.Vector!);
            if (sim > max)
                max = sim;
        }
        return max;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na <= 0 || nb <= 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
