using System.Net;
using System.Net.Http.Json;

namespace KnowledgeHub.Server.Embeddings;

/// <summary>Shared HTTP plumbing for paid embedding providers (Cohere,
/// Voyage): POST JSON with bounded 429 backoff, and the dimension-fitting
/// helper both providers apply to returned vectors.</summary>
internal static class EmbeddingHttpRetry
{
    public static readonly TimeSpan[] DefaultDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>POSTs <paramref name="request"/> as JSON, honoring
    /// <paramref name="delays"/> on HTTP 429; throws
    /// <see cref="EmbeddingProviderException"/> on persistent failure or a
    /// missing payload.</summary>
    public static async Task<TPayload> PostJsonAsync<TPayload>(
        HttpClient http, string path, object request, TimeSpan[] delays,
        string providerLabel, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            var response = await http.PostAsJsonAsync(path, request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], cancellationToken);
                attempt++;
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new EmbeddingProviderException(
                    $"{providerLabel} embeddings failed with HTTP {(int)response.StatusCode}");

            return await response.Content.ReadFromJsonAsync<TPayload>(cancellationToken)
                ?? throw new EmbeddingProviderException(
                    $"{providerLabel} returned no embeddings");
        }
    }

    /// <summary>Pads/truncates a returned vector to the configured
    /// dimensionality.</summary>
    public static float[] FitDimensions(float[] vector, int dimensions)
    {
        if (vector.Length == dimensions)
            return vector;
        var fitted = new float[dimensions];
        Array.Copy(vector, fitted, Math.Min(vector.Length, dimensions));
        return fitted;
    }
}
