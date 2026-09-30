using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace KnowledgeHub.Server.Embeddings;

/// <summary>Shared shell for hosted embedding providers (Cohere, Voyage):
/// endpoint + Bearer auth wiring, asymmetric query/document input types, and
/// fixed-size batching. Subclasses supply the provider-native request/response
/// records via <see cref="SendBatchAsync"/>.</summary>
public abstract class HostedEmbeddingProvider : IEmbeddingProvider
{
    private const string UriPathSeparator = "/";

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly int _batchSize;
    private readonly string _providerPrefix;
    private readonly string _queryInputType;
    private readonly string _documentInputType;

    protected HostedEmbeddingProvider(HttpClient http, HostedEmbeddingSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Model);
        _http = http;
        _http.BaseAddress = new Uri((spec.Endpoint ?? spec.DefaultEndpoint).TrimEnd('/') + UriPathSeparator);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", spec.ApiKey);
        _model = spec.Model;
        _dimensions = spec.Dimensions;
        _batchSize = Math.Clamp(spec.BatchSize, 1, spec.MaxBatchSize);
        _providerPrefix = spec.ProviderPrefix;
        _queryInputType = spec.QueryInputType;
        _documentInputType = spec.DocumentInputType;
        Label = spec.Label;
    }

    /// <summary>Provider display name for errors (never logs the API key).</summary>
    protected string Label { get; }
    protected HttpClient Http => _http;
    protected string Model => _model;
    protected int VectorDimensions => _dimensions;

    public string ModelId => $"{_providerPrefix}:{_model}";
    public int Dimensions => _dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], null, cancellationToken))[0];

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], _queryInputType, cancellationToken))[0];

    public async Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) =>
        (await EmbedWithTypeAsync([text], _documentInputType, cancellationToken))[0];

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(texts, null, cancellationToken);

    public Task<IReadOnlyList<float[]>> EmbedDocumentBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        EmbedWithTypeAsync(texts, _documentInputType, cancellationToken);

    private async Task<IReadOnlyList<float[]>> EmbedWithTypeAsync(
        IReadOnlyList<string> texts, string? inputType, CancellationToken cancellationToken)
    {
        var results = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += _batchSize)
        {
            var slice = texts.Skip(i).Take(_batchSize).ToList();
            var batch = await SendBatchAsync(slice, inputType, cancellationToken);
            results.AddRange(batch);
        }
        return results;
    }

    /// <summary>Sends one provider-native batch; must return one vector per
    /// input (already dimension-fitted).</summary>
    protected abstract Task<IReadOnlyList<float[]>> SendBatchAsync(
        IReadOnlyList<string> inputs, string? inputType, CancellationToken cancellationToken);

    /// <summary>Cross-provider settings record for <see cref="HostedEmbeddingProvider"/>.</summary>
    public sealed record HostedEmbeddingSpec(
        string? Endpoint, string DefaultEndpoint, string? ApiKey, string? Model,
        int Dimensions, int BatchSize, int MaxBatchSize,
        string ProviderPrefix, string QueryInputType, string DocumentInputType, string Label);
}
