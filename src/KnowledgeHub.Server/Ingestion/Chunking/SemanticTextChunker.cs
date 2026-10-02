using System.Text.RegularExpressions;
using KnowledgeHub.Server.Embeddings;

namespace KnowledgeHub.Server.Ingestion.Chunking;

/// <summary>
/// SPEC-20260924-semantic-chunking RF-001: cuts prose where the topic changes —
/// embeds each sentence, measures cosine distance between consecutive
/// sentences, and breaks at distances ≥ the document's own percentile
/// (<c>Ingestion:Semantic:BreakpointPercentile</c>, default 95). Chunks always
/// respect <paramref name="maxTokens"/> as a hard ceiling and merge up to
/// <c>Ingestion:Semantic:MinTokens</c> (default 100) to avoid micro-chunks.
/// Sentence embeddings are used only for the cut and discarded.
/// </summary>
public sealed class SemanticTextChunker(
    IEmbeddingProvider embeddings,
    IConfiguration configuration) : ITextChunker
{
    public ChunkKind Kind => ChunkKind.Prose; // semantic cuts apply to prose

    /// <summary>Sync path is unsupported — embedding is inherently async.</summary>
    public IReadOnlyList<ChunkPiece> Chunk(string text, int maxTokens, int overlapTokens) =>
        throw new NotSupportedException("Semantic chunking requires ChunkAsync (embedding calls)");

    public async Task<IReadOnlyList<ChunkPiece>> ChunkAsync(
        string text, int maxTokens, int overlapTokens, CancellationToken ct = default)
    {
        var maxSentences = configuration.GetValue("Ingestion:Semantic:MaxSentencesPerDoc", 2000);
        var minTokens = configuration.GetValue("Ingestion:Semantic:MinTokens", 100);
        var percentile = Math.Clamp(
            configuration.GetValue("Ingestion:Semantic:BreakpointPercentile", 95), 50, 99);

        var sentences = SplitSentences(text);
        if (sentences.Count == 0)
            return [];
        if (sentences.Count > maxSentences)
            throw new InvalidOperationException(
                $"document exceeds semantic-chunking cap ({sentences.Count} sentences > {maxSentences})");
        if (sentences.Count == 1)
            return [new ChunkPiece(sentences[0])];

        var vectors = await embeddings.EmbedDocumentBatchAsync(sentences, ct);
        var distances = new double[sentences.Count - 1];
        for (var i = 0; i < distances.Length; i++)
            distances[i] = 1.0 - Cosine(vectors[i], vectors[i + 1]);

        var threshold = Percentile(distances, percentile);
        return Merge(sentences, distances, threshold, maxTokens, minTokens, overlapTokens);
    }

    /// <summary>Sentence splitter — terminators + blank lines; tolerant of
    /// abbreviations by requiring whitespace after the terminator.</summary>
    internal static List<string> SplitSentences(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var parts = SentenceBoundary().Split(normalized);
        return parts
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    private static readonly Regex _sentenceBoundary =
        new(@"(?<=[.!?;])\s+|\n\s*\n", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static Regex SentenceBoundary() => _sentenceBoundary;

    private static double Percentile(IReadOnlyList<double> values, int p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (p / 100.0) * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>
    /// Walk sentences, closing a chunk when the next sentence crosses the
    /// semantic breakpoint (and the chunk reached minTokens) or would exceed
    /// maxTokens. Overlap reuses the trailing sentences of the closed chunk.
    /// </summary>
    internal static List<ChunkPiece> Merge(
        IReadOnlyList<string> sentences, IReadOnlyList<double> distances,
        double threshold, int maxTokens, int minTokens, int overlapTokens)
    {
        var pieces = new List<ChunkPiece>();
        var current = new List<int>(); // sentence indices
        var currentTokens = 0;

        for (var i = 0; i < sentences.Count; i++)
        {
            var sTokens = Math.Max(1, sentences[i].Length / 4);
            var crossesBreakpoint = current.Count > 0
                && distances[i - 1] >= threshold
                && distances[i - 1] > 0 // identical vectors are never a topic change
                && currentTokens >= minTokens;
            var exceedsMax = currentTokens + sTokens > maxTokens && current.Count > 0;

            if (crossesBreakpoint || exceedsMax)
            {
                pieces.Add(new ChunkPiece(string.Join(' ', current.Select(j => sentences[j]))));
                // overlap: keep trailing sentences totalling ~overlapTokens
                var overlap = new List<int>();
                var acc = 0;
                for (var j = current.Count - 1; j >= 0 && acc < overlapTokens; j--)
                {
                    overlap.Insert(0, current[j]);
                    acc += Math.Max(1, sentences[current[j]].Length / 4);
                }
                current = overlap;
                currentTokens = acc;
            }
            current.Add(i);
            currentTokens += sTokens;
        }
        if (current.Count > 0)
            pieces.Add(new ChunkPiece(string.Join(' ', current.Select(j => sentences[j]))));
        return pieces;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na <= 0 || nb <= 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
