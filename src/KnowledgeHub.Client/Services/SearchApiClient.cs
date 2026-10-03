using System.Net.Http.Json;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Typed client for /api/search (SPEC-05 RF-004).</summary>
public sealed class SearchApiClient(HttpClient http)
{
    public async Task<SearchResponse?> SearchAsync(string query, int topK, CancellationToken ct = default)
    {
        var uri = $"api/search?query={Uri.EscapeDataString(query)}&topK={topK}";
        return await http.GetFromJsonAsync<SearchResponse>(uri, SharedJson.Options, ct);
    }
}
