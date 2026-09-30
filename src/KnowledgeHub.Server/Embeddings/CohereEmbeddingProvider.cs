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
public sealed class CohereEmbeddingProvider : HostedEmbeddingProvider
{
    private const string DefaultEndpoint = "https://api.cohere.com/v2/";
    private static readonly TimeSpan[] RetryDelays = EmbeddingHttpRetry.DefaultDelays;

    public CohereEmbeddingProvider(HttpClient http, EmbeddingOptions.CohereOptions options)
        : base(http, new HostedEmbeddingSpec(
            options.Endpoint, DefaultEndpoint, options.ApiKey, options.Model,
            options.Dimensions, options.BatchSize, 96,
            "cohere", "search_query", "search_document", "Cohere"))
    {
    }

    protected override async Task<IReadOnlyList<float[]>> SendBatchAsync(
        IReadOnlyList<string> inputs, string? inputType, CancellationToken cancellationToken)
    {
        var payload = await EmbeddingHttpRetry.PostJsonAsync<CohereResponse>(
            Http, "embed", new CohereRequest(Model, inputs.ToList(), inputType),
            RetryDelays, Label, cancellationToken);
        var vectors = payload.Embeddings?.Select(Fit).ToList()
            ?? throw new EmbeddingProviderException("Cohere returned no embeddings");
        if (vectors.Count != inputs.Count)
            throw new EmbeddingProviderException("Cohere returned a mismatched number of embeddings");
        return vectors;
    }

    private float[] Fit(float[] vector) =>
        EmbeddingHttpRetry.FitDimensions(vector, VectorDimensions);

    private sealed record CohereRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("texts")] List<string> Texts,
        [property: JsonPropertyName("input_type"),
         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputType);

    private sealed record CohereResponse(
        [property: JsonPropertyName("embeddings")] List<float[]>? Embeddings);
}
