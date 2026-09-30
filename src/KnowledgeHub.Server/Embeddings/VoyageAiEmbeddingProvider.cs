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
public sealed class VoyageAiEmbeddingProvider : HostedEmbeddingProvider
{
    private const string DefaultEndpoint = "https://api.voyageai.com/v1/";
    private static readonly TimeSpan[] RetryDelays = EmbeddingHttpRetry.DefaultDelays;

    public VoyageAiEmbeddingProvider(HttpClient http, EmbeddingOptions.VoyageOptions options)
        : base(http, new HostedEmbeddingSpec(
            options.Endpoint, DefaultEndpoint, options.ApiKey, options.Model,
            options.Dimensions, options.BatchSize, 128,
            "voyage", "query", "document", "Voyage AI"))
    {
    }

    protected override async Task<IReadOnlyList<float[]>> SendBatchAsync(
        IReadOnlyList<string> inputs, string? inputType, CancellationToken cancellationToken)
    {
        var payload = await EmbeddingHttpRetry.PostJsonAsync<VoyageResponse>(
            Http, "embeddings", new VoyageRequest(Model, inputs.ToList(), inputType),
            RetryDelays, Label, cancellationToken);
        var vectors = payload.Data?.Select(d => Fit(d.Embedding)).ToList()
            ?? throw new EmbeddingProviderException("Voyage AI returned no embeddings");
        if (vectors.Count != inputs.Count)
            throw new EmbeddingProviderException("Voyage AI returned a mismatched number of embeddings");
        return vectors;
    }

    private float[] Fit(float[] vector) =>
        EmbeddingHttpRetry.FitDimensions(vector, VectorDimensions);

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
