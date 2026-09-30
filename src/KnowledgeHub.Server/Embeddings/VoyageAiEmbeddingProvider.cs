using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace KnowledgeHub.Server.Embeddings;

/// <summary>
/// Voyage AI embeddings (SPEC-20260927-voyage-and-cohere-embeddings RF-001):
/// POST https://api.voyageai.com/v1/embeddings {input, model, input_type} +
/// Bearer {ApiKey}. Applies <c>input_type="query"</c> for queries and
/// <c>"document"</c> for documents (asymmetric compression). The ApiKey is
/// never logged or included in error messages. Batches up to 128 inputs with
/// exponential-backoff retry on HTTP 429.
/// </summary>
public sealed class VoyageAiEmbeddingProvider : IEmbeddingProvider
{
    private const string UriPathSeparator = "/";
    private const string DefaultEndpoint = "https://api.voyageai.com/v1/";
    private static readonly TimeSpan[] RetryDelays = EmbeddingHttpRetry.DefaultDelays;

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly int _batchSize;

    public VoyageAiEmbeddingProvider(HttpClient http, EmbeddingOptions.VoyageOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        _http = http;
        _http.BaseAddress = new Uri((options.Endpoint ?? DefaultEndpoint).TrimEnd('/') + UriPathSeparator);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        _model = options.Model;
        _dimensions = options.Dimensions;
        _batchSize = Math.Clamp(options.BatchSize, 1, 128);
    }

    public string ModelId => $"voyage:{_model}";
    public int Dimensions => _dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], null, cancellationToken))[0];

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], "query", cancellationToken))[0];

    public async Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], "document", cancellationToken))[0];

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(texts, null, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(texts, "document", cancellationToken);

    private async Task<IReadOnlyList<float[]>> EmbedWithTypeAsync(
        IReadOnlyList<string> texts, string? inputType, CancellationToken cancellationToken)
    {
        var results = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += _batchSize)
        {
            var slice = texts.Skip(i).Take(_batchSize).ToList();
            var batch = await SendWithRetryAsync(slice, inputType, cancellationToken);
            results.AddRange(batch);
        }
        return results;
    }

    private async Task<IReadOnlyList<float[]>> SendWithRetryAsync(
        IReadOnlyList<string> inputs, string? inputType, CancellationToken cancellationToken)
    {
        var payload = await EmbeddingHttpRetry.PostJsonAsync<VoyageResponse>(
            _http, "embeddings", new VoyageRequest(_model, inputs.ToList(), inputType),
            RetryDelays, "Voyage AI", cancellationToken);
        var vectors = payload.Data?.Select(d => FitDimensions(d.Embedding)).ToList()
            ?? throw new EmbeddingProviderException("Voyage AI returned no embeddings");
        if (vectors.Count != inputs.Count)
            throw new EmbeddingProviderException("Voyage AI returned a mismatched number of embeddings");
        return vectors;
    }

    private float[] FitDimensions(float[] vector) =>
        EmbeddingHttpRetry.FitDimensions(vector, _dimensions);

    private sealed record VoyageRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] List<string> Input,
        [property: JsonPropertyName("input_type"),
         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputType);

    private sealed record VoyageResponse(
        [property: JsonPropertyName("data")] List<VoyageEmbedding>? Data);

    private sealed record VoyageEmbedding(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
