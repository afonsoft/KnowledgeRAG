using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace KnowledgeHub.Server.Embeddings;

/// <summary>
/// Cohere Embed v3 embeddings (SPEC-20260927-voyage-and-cohere-embeddings RF-002):
/// POST https://api.cohere.com/v2/embed {texts, model, input_type} + Bearer
/// {ApiKey}. Applies <c>input_type="search_query"</c> for queries and
/// <c>"search_document"</c> for documents. The ApiKey is never logged. Batches
/// up to 96 inputs with exponential-backoff retry on HTTP 429.
/// </summary>
public sealed class CohereEmbeddingProvider : IEmbeddingProvider
{
    private const string UriPathSeparator = "/";
    private const string DefaultEndpoint = "https://api.cohere.com/v2/";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly int _batchSize;

    public CohereEmbeddingProvider(HttpClient http, EmbeddingOptions.CohereOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        _http = http;
        _http.BaseAddress = new Uri((options.Endpoint ?? DefaultEndpoint).TrimEnd('/') + UriPathSeparator);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        _model = options.Model;
        _dimensions = options.Dimensions;
        _batchSize = Math.Clamp(options.BatchSize, 1, 96);
    }

    public string ModelId => $"cohere:{_model}";
    public int Dimensions => _dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], null, cancellationToken))[0];

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], "search_query", cancellationToken))[0];

    public async Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], "search_document", cancellationToken))[0];

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(texts, null, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(texts, "search_document", cancellationToken);

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
        var attempt = 0;
        while (true)
        {
            var request = new CohereRequest(_model, inputs.ToList(), inputType);
            var response = await _http.PostAsJsonAsync("embed", request, cancellationToken);

            if (response.StatusCode == (System.Net.HttpStatusCode)429 && attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken);
                attempt++;
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new EmbeddingProviderException($"Cohere embeddings failed with HTTP {(int)response.StatusCode}");

            var payload = await response.Content.ReadFromJsonAsync<CohereResponse>(cancellationToken);
            var vectors = payload?.Embeddings?.Select(FitDimensions).ToList()
                ?? throw new EmbeddingProviderException("Cohere returned no embeddings");
            if (vectors.Count != inputs.Count)
                throw new EmbeddingProviderException("Cohere returned a mismatched number of embeddings");
            return vectors;
        }
    }

    private float[] FitDimensions(float[] vector)
    {
        if (vector.Length == _dimensions)
            return vector;
        var fitted = new float[_dimensions];
        Array.Copy(vector, fitted, Math.Min(vector.Length, _dimensions));
        return fitted;
    }

    private sealed record CohereRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("texts")] List<string> Texts,
        [property: JsonPropertyName("input_type"),
         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputType);

    private sealed record CohereResponse(
        [property: JsonPropertyName("embeddings")] List<float[]>? Embeddings);
}
