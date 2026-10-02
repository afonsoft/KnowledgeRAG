using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace KnowledgeHub.Server.Embeddings;

/// <summary>
/// Local ONNX Runtime embedding provider (SPEC-20260917-onnx-local-embeddings
/// RF-001): sentence-transformers/all-MiniLM-L6-v2 running on CPU — real
/// semantics with no external service. The model artifacts (model.onnx +
/// vocab.txt) live outside the repo under <c>Embeddings:ModelPath</c>
/// (default <c>models/all-MiniLM-L6-v2</c>); a missing model is a clear
/// startup error, never a silent fallback.
/// </summary>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    /// <summary>all-MiniLM-L6-v2 hidden size — fixed for this model.</summary>
    public const int EmbeddingDimensions = 384;

    /// <summary>Token budget per input (BERT window).</summary>
    public const int MaxTokens = 256;

    public const string ModelFileName = "model.onnx";
    public const string VocabFileName = "vocab.txt";
    public const string DefaultModelDirectory = "models/all-MiniLM-L6-v2";

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly string _outputName;
    private readonly bool _expectsTokenTypeIds;
    private readonly int _dimensions;

    private OnnxEmbeddingProvider(InferenceSession session, BertTokenizer tokenizer)
    {
        _session = session;
        _tokenizer = tokenizer;
        _outputName = session.OutputMetadata.Keys.First();
        _expectsTokenTypeIds = session.InputMetadata.ContainsKey("token_type_ids");
        // SPEC-20260926-embeddings-swap-safety RF-001: the model file declares
        // its own output width — never assume EmbeddingDimensions. A non-384
        // model must surface its real size so the settings gate can reject
        // mismatched configurations instead of silently slicing vectors.
        var outputDims = session.OutputMetadata[_outputName].Dimensions;
        // RF-604: a dynamic last dim (-1) cannot describe the produced vector
        // width — refuse the model rather than slice into a garbage length.
        if (outputDims.Length == 0 || outputDims[^1] <= 0)
            throw new EmbeddingProviderException(
                $"onnx model output '{_outputName}' has a dynamic width — only fixed-width embedding models are supported");
        _dimensions = outputDims[^1];
    }

    public string ModelId => "onnx:all-MiniLM-L6-v2";

    public int Dimensions => _dimensions;

    /// <summary>Loads the tokenizer and ONNX session from <paramref name="modelDirectory"/>;
    /// throws a descriptive <see cref="EmbeddingProviderException"/> when artifacts are missing.</summary>
    public static OnnxEmbeddingProvider Load(string? modelDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(modelDirectory) ? DefaultModelDirectory : modelDirectory;
        var modelFile = Path.Join(dir, ModelFileName);
        var vocabFile = Path.Join(dir, VocabFileName);

        if (!File.Exists(modelFile) || !File.Exists(vocabFile))
            throw new EmbeddingProviderException(
                $"ONNX model not found at '{Path.GetFullPath(dir)}' — expected {ModelFileName} and {VocabFileName}. " +
                "Download them from https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2 " +
                "(onnx/model.onnx + vocab.txt) or set Embeddings:ModelPath.");

        var session = new InferenceSession(modelFile);
        try
        {
            var tokenizer = BertTokenizer.Create(vocabFile);
            return new OnnxEmbeddingProvider(session, tokenizer);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        // ONNX Runtime inference is CPU-bound; offload so async callers stay responsive.
        Task.Run(() => Embed(text), cancellationToken);

    private float[] Embed(string text)
    {
        var ids = _tokenizer.EncodeToIds(
            text, MaxTokens, addSpecialTokens: true,
            normalizedText: out _, charsConsumed: out _,
            considerPreTokenization: true, considerNormalization: true);

        var length = ids.Count;
        var dims = new[] { 1, length };
        var inputIds = new long[length];
        for (var i = 0; i < length; i++)
            inputIds[i] = ids[i];

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dims)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), dims))
        };
        if (_expectsTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[length], dims)));

        using var results = _session.Run(inputs);
        var hidden = results.First(r => r.Name == _outputName).AsTensor<float>();

        // Mean pooling over the token axis weighted by the attention mask
        // (all ones — padding is never emitted), then L2 normalize like the
        // other providers so cosine similarity stays valid.
        var pooled = new float[_dimensions];
        for (var t = 0; t < length; t++)
            for (var d = 0; d < _dimensions; d++)
                pooled[d] += hidden[0, t, d];
        for (var d = 0; d < _dimensions; d++)
            pooled[d] /= length;

        Normalize(pooled);
        return pooled;
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

    public void Dispose() => _session.Dispose();
}
