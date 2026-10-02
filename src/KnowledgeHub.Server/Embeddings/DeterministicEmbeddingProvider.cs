using System.Text;

namespace KnowledgeHub.Server.Embeddings;

/// <summary>
/// Offline default provider: feature-hashing bag-of-words into a fixed-size
/// L2-normalized vector. Deterministic — identical input yields identical output.
/// Captures term overlap, not real semantics; swap via config for ollama/openai.
/// </summary>
public sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    private readonly int _dimensions;

    public DeterministicEmbeddingProvider(int dimensions = 384)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 8);
        _dimensions = dimensions;
    }

    public string ModelId => $"deterministic:hash{_dimensions}";

    public int Dimensions => _dimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var vector = new float[_dimensions];
        foreach (var hash in Tokenize(text).Select(StableHash))
        {
            var bucket = (int)(hash % (uint)_dimensions);
            vector[bucket] += (hash & 0x8000_0000) == 0 ? 1f : -1f; // signed hashing reduces collisions
        }
        Normalize(vector);
        return Task.FromResult(vector);
    }

    internal static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(char.ToLowerInvariant(ch));
            }
            else if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }
        if (token.Length > 0)
            yield return token.ToString();
    }

    // FNV-1a — stable across processes, no RandomString hashing.
    private static uint StableHash(string value)
    {
        const uint offset = 2166136261, prime = 16777619;
        var hash = offset;
        foreach (var ch in value)
        {
            hash = (hash ^ (ch & 0xFFu)) * prime;
            hash = (hash ^ ((uint)ch >> 8)) * prime;
        }
        return hash;
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector)
            sum += v * v;
        if (sum <= 0)
            return;
        var norm = (float)Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
            vector[i] /= norm;
    }
}
