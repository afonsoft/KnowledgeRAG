using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Embeddings;

/// <summary>Selects the concrete <see cref="IEmbeddingProvider"/> from configuration.</summary>
public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingOptions options, IHttpClientFactory httpClientFactory)
    {
        var http = httpClientFactory.CreateClient("embeddings");
        IEmbeddingProvider inner = options.Provider.ToLowerInvariant() switch
        {
            "ollama" => new OllamaEmbeddingProvider(http, options),
            "openai" => new OpenAiEmbeddingProvider(http, options),
            // SPEC-20260927-voyage-and-cohere-embeddings: Voyage AI and Cohere Embed v3.
            // Sub-options (Embeddings:Voyage / Embeddings:Cohere) win; the top-level
            // ApiKey/Model/Dimensions are the fallback so the store-backed settings
            // UI (single-key flow) still configures these providers end-to-end.
            "voyage" => new VoyageAiEmbeddingProvider(http, Merge(options.Voyage, options)),
            "cohere" => new CohereEmbeddingProvider(http, Merge(options.Cohere, options)),
            // SPEC-20260917-onnx-local-embeddings: local all-MiniLM-L6-v2 —
            // missing artifacts fail fast with download instructions.
            "onnx" => OnnxEmbeddingProvider.Load(options.ModelPath),
            _ => new DeterministicEmbeddingProvider(options.Dimensions)
        };
        // SPEC-20260924-asymmetric-embeddings: role prefixes wrap any provider.
        return options.Asymmetric.Enabled
            ? new AsymmetricEmbeddingProvider(inner, options)
            : inner;
    }

    /// <summary>Merges sub-options with top-level Embeddings fallback values
    /// (SPEC-20260927-voyage-and-cohere-embeddings). When the sub-block is
    /// entirely unset (store-backed settings UI posts only top-level values),
    /// the top-level ApiKey/Model/Dimensions/Endpoint are used end-to-end;
    /// otherwise explicit sub-values win over top-level nulls/zeros.</summary>
    private static EmbeddingOptions.VoyageOptions Merge(
        EmbeddingOptions.VoyageOptions sub, EmbeddingOptions options)
    {
        if (sub.ApiKey is null && sub.Model is null && sub.Endpoint is null
            && sub.Dimensions == 0 && sub.BatchSize == 0)
            return new()
            {
                ApiKey = options.ApiKey,
                Model = options.Model,
                Dimensions = options.Dimensions,
                Endpoint = options.Endpoint,
                BatchSize = 128
            };
        return new()
        {
            ApiKey = sub.ApiKey ?? options.ApiKey,
            Model = sub.Model ?? options.Model,
            Dimensions = sub.Dimensions == 0 ? options.Dimensions : sub.Dimensions,
            Endpoint = sub.Endpoint ?? options.Endpoint,
            BatchSize = sub.BatchSize == 0 ? 128 : sub.BatchSize
        };
    }

    /// <summary>See <see cref="Merge(EmbeddingOptions.VoyageOptions, EmbeddingOptions)"/>.</summary>
    private static EmbeddingOptions.CohereOptions Merge(
        EmbeddingOptions.CohereOptions sub, EmbeddingOptions options)
    {
        if (sub.ApiKey is null && sub.Model is null && sub.Endpoint is null
            && sub.Dimensions == 0 && sub.BatchSize == 0)
            return new()
            {
                ApiKey = options.ApiKey,
                Model = options.Model,
                Dimensions = options.Dimensions,
                Endpoint = options.Endpoint,
                BatchSize = 96
            };
        return new()
        {
            ApiKey = sub.ApiKey ?? options.ApiKey,
            Model = sub.Model ?? options.Model,
            Dimensions = sub.Dimensions == 0 ? options.Dimensions : sub.Dimensions,
            Endpoint = sub.Endpoint ?? options.Endpoint,
            BatchSize = sub.BatchSize == 0 ? 96 : sub.BatchSize
        };
    }
}
