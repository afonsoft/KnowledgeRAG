using System.Net.Http.Json;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Client for /api/graph/* (SPEC-20260928-graph-timeline-viewer RF-001).</summary>
public sealed class GraphApiClient(HttpClient http)
{
    /// <summary>GET /api/graph/nodes — paged, vigente-only by default.</summary>
    public Task<GraphNodesResponse?> NodesAsync(
        int skip = 0, int take = 100, bool includeHistorical = false,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<GraphNodesResponse>(
            $"api/graph/nodes?skip={skip}&take={take}&includeHistorical={includeHistorical}", SharedJson.Options, ct);

    /// <summary>GET /api/graph/timeline — nodes whose validity window intersects [from,to].</summary>
    public async Task<List<GraphNodeDto>?> TimelineAsync(
        DateTime? from, DateTime? to, int take = 100, bool includeHistorical = false,
        CancellationToken ct = default)
    {
        var url = $"api/graph/timeline?take={take}&includeHistorical={includeHistorical}";
        if (from is { } f) url += $"&from={f:O}";
        if (to is { } t) url += $"&to={t:O}";
        return await http.GetFromJsonAsync<List<GraphNodeDto>>(url, SharedJson.Options, ct);
    }

    /// <summary>GET /api/graph/nodes/{id}/edges — edges for one node.</summary>
    public Task<List<GraphEdgeDto>?> EdgesAsync(
        string nodeId, bool includeHistorical = false, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<GraphEdgeDto>>(
            $"api/graph/nodes/{nodeId}/edges?includeHistorical={includeHistorical}", SharedJson.Options, ct);

    /// <summary>GET /api/graph/episodes — recent ingestion/session episodes.</summary>
    public Task<List<GraphEpisodeDto>?> EpisodesAsync(int take = 100, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<GraphEpisodeDto>>($"api/graph/episodes?take={take}", SharedJson.Options, ct);
}
