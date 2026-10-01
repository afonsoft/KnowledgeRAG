using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

// SonarQube S3236: ThrowIfNullOrWhiteSpace recebe a chave de config (Embeddings:Endpoint (OpenAI)) como mensagem, não um parâmetro caller-info — falso positivo.
#pragma warning disable S3236

// SonarQube S1075: endpoint e path 'v1/embeddings' vêm de Embeddings:Endpoint/protocolo OpenAI — não há URI hardcoded.
#pragma warning disable S1075

namespace KnowledgeHub.Server.Embeddings;

/// <summary>
/// OpenAI-compatible embeddings (SPEC-03 RF-003): POST {Endpoint}/v1/embeddings
/// {model, input} + Bearer {ApiKey}. Covers OpenAI, Azure-compatible gateways,
/// LM Studio, vLLM, etc. The ApiKey is never logged or included in errors.
/// </summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly string? _queryInputType;
    private readonly string? _documentInputType;

    public OpenAiEmbeddingProvider(HttpClient http, EmbeddingOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Endpoint, "Embeddings:Endpoint");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model, "Embeddings:Model");
        _http = http;
        _http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        _model = options.Model;
        _dimensions = options.Dimensions;
        // SPEC-20260924-asymmetric-embeddings RF-002: input_type for providers
        // that support it (Voyage-compatible gateways); OpenAI ignores it.
        _queryInputType = options.QueryInputType;
        _documentInputType = options.DocumentInputType;
    }

    public string ModelId => $"openai:{_model}";
    public int Dimensions => _dimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(text, null, cancellationToken);

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(text, _queryInputType, cancellationToken);

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(text, _documentInputType, cancellationToken);

    private async Task<float[]> EmbedWithTypeAsync(string text, string? inputType, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync("v1/embeddings",
            new OpenAiRequest(_model, text, inputType), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new EmbeddingProviderException($"OpenAI embeddings failed with HTTP {(int)response.StatusCode}");

        var payload = await response.Content.ReadFromJsonAsync<OpenAiResponse>(cancellationToken);
        var vector = payload?.Data?.FirstOrDefault()?.Embedding
            ?? throw new EmbeddingProviderException("OpenAI returned no embedding");
        return FitDimensions(vector);
    }

    private float[] FitDimensions(float[] vector)
    {
        if (vector.Length == _dimensions)
            return vector;
        var fitted = new float[_dimensions];
        Array.Copy(vector, fitted, Math.Min(vector.Length, _dimensions));
        return fitted;
    }

    private sealed record OpenAiRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("input_type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputType);

    private sealed record OpenAiResponse(
        [property: JsonPropertyName("data")] List<OpenAiEmbedding>? Data);

    private sealed record OpenAiEmbedding(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
