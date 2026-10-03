using System.Net.Http.Headers;
using System.Text.Json;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Settings;

/// <summary>
/// Shared probe behind GET /api/settings/{chat|assistant}/models: fetches
/// GET {endpoint}/v1/models with a Bearer key and extracts the model ids —
/// OpenAI-compatible shape <c>data[].id</c>, with a fallback for the Ollama
/// native <c>models[]</c> array (<c>name</c>/<c>model</c>/<c>id</c>). Failures
/// return <c>Ok=false</c> with a sanitized detail — the key/body never leaks.
/// </summary>
public static class ProviderModelProbe
{
    /// <summary>Probe timeout — deliberately shorter than chat requests.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Lista os ids de modelos do endpoint (OpenAI-compatible). Endpoint
    /// vazio falha rápido; erros viram Detail sanitizado.</summary>
    public static async Task<ProviderModelsResponse> ListAsync(
        HttpClient http, string? endpoint, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new ProviderModelsResponse { Ok = false, Detail = "endpoint is required" };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/v1/models");
            if (!string.IsNullOrEmpty(apiKey))
                probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await http.SendAsync(probe, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new ProviderModelsResponse { Ok = false, Detail = $"HTTP {(int)response.StatusCode}" };

            var models = await ParseIdsAsync(response, timeout.Token);
            return new ProviderModelsResponse { Ok = true, Models = models };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderModelsResponse { Ok = false, Detail = "timeout" };
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException)
        {
            return new ProviderModelsResponse { Ok = false, Detail = "connection failed" };
        }
    }

    /// <summary>Extrai ids de <c>data[].id</c> (OpenAI) ou <c>models[].name/model/id</c>
    /// (Ollama nativo). Retorna lista vazia quando o corpo não é uma lista conhecida.</summary>
    private static async Task<string[]> ParseIdsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var array = FirstArrayProperty(doc.RootElement, "data", "models");
            if (array is null)
                return [];

            return array.Value.EnumerateArray()
                .Select(m => FirstStringProperty(m, "id", "name", "model"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>First named property whose value is a JSON array, or null.</summary>
    private static JsonElement? FirstArrayProperty(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array)
                return el;
        return null;
    }

    /// <summary>First named property whose value is a JSON string, or null.</summary>
    private static string? FirstStringProperty(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }
}
