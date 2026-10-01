using System.Net.Http.Json;
using System.Text.Json.Serialization;

// SonarQube S3236: o segundo argumento de ThrowIfNullOrWhiteSpace é a mensagem de erro (chave de config), não um parâmetro caller-info — falso positivo.
#pragma warning disable S3236

// SonarQube S1075: endpoint e path 'api/embeddings' vêm de Embeddings:Endpoint/protocolo Ollama — não há URI hardcoded.
#pragma warning disable S1075

namespace KnowledgeHub.Server.Embeddings;

/// <summary>
/// Ollama embeddings (SPEC-03 RF-003): POST {Endpoint}/api/embeddings {model, input}
/// → embeddings[0]. Errors are sanitized — never echo endpoint internals beyond status.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _dimensions;

    public OllamaEmbeddingProvider(HttpClient http, EmbeddingOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Endpoint, "Embeddings:Endpoint");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model, "Embeddings:Model");
        _http = http;
        _http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
        _model = options.Model;
        _dimensions = options.Dimensions;
    }

    public string ModelId => $"ollama:{_model}";
    public int Dimensions => _dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("api/embeddings",
            new OllamaRequest(_model, text), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new EmbeddingProviderException($"Ollama embeddings failed with HTTP {(int)response.StatusCode}");

        var payload = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken);
        var vector = payload?.Embeddings?.FirstOrDefault()
            ?? throw new EmbeddingProviderException("Ollama returned no embedding");
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

    private sealed record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record OllamaResponse(
        [property: JsonPropertyName("embeddings")] float[][]? Embeddings);
}
