using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Typed client for /api/flows (UI-defined agent flows).</summary>
public sealed class FlowsApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<FlowDto>> ListAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<FlowDto>>("api/flows", SharedJson.Options, ct) ?? [];

    public async Task<FlowDetailDto?> GetAsync(Guid id, CancellationToken ct = default)
        => await http.GetFromJsonAsync<FlowDetailDto>($"api/flows/{id}", SharedJson.Options, ct);

    public async Task<(FlowDetailDto? Detail, string? Error)> CreateAsync(
        CreateFlowRequest request, CancellationToken ct = default)
    {
        var r = await http.PostAsJsonAsync("api/flows", request, SharedJson.Options, ct);
        return r.IsSuccessStatusCode
            ? (await r.Content.ReadFromJsonAsync<FlowDetailDto>(SharedJson.Options, ct), null)
            : (null, await ReadErrorAsync(r, ct));
    }

    public async Task<(FlowDetailDto? Detail, string? Error)> UpdateAsync(
        Guid id, UpdateFlowRequest request, CancellationToken ct = default)
    {
        var r = await http.PutAsJsonAsync($"api/flows/{id}", request, SharedJson.Options, ct);
        return r.IsSuccessStatusCode
            ? (await r.Content.ReadFromJsonAsync<FlowDetailDto>(SharedJson.Options, ct), null)
            : (null, await ReadErrorAsync(r, ct));
    }

    public async Task<(bool Ok, string? Error)> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var r = await http.DeleteAsync($"api/flows/{id}", ct);
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(r, ct));
    }

    public async Task<(FlowRunResultDto? Result, string? Error)> RunAsync(
        Guid id, JsonObject? inputs, CancellationToken ct = default)
    {
        var r = await http.PostAsJsonAsync($"api/flows/{id}/run", new FlowRunRequest(inputs), SharedJson.Options, ct);
        if (r.IsSuccessStatusCode || (int)r.StatusCode == 422)
            return (await r.Content.ReadFromJsonAsync<FlowRunResultDto>(SharedJson.Options, ct), null);
        return (null, await ReadErrorAsync(r, ct));
    }

    public async Task<(bool Valid, string? Error)> ValidateAsync(
        FlowDefinitionDto definition, CancellationToken ct = default)
    {
        var r = await http.PostAsJsonAsync("api/flows/validate", new ValidateFlowRequest(definition), SharedJson.Options, ct);
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(r, ct));
    }

    public async Task<IReadOnlyList<FlowRunDto>> RunsAsync(Guid id, CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<FlowRunDto>>($"api/flows/{id}/runs", SharedJson.Options, ct) ?? [];

    // ── Triggers ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FlowTriggerDto>> TriggersAsync(Guid flowId, CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<FlowTriggerDto>>($"api/flows/{flowId}/triggers", SharedJson.Options, ct) ?? [];

    public async Task<(FlowTriggerDto? Trigger, string? Error)> CreateTriggerAsync(
        Guid flowId, CreateFlowTriggerRequest request, CancellationToken ct = default)
    {
        var r = await http.PostAsJsonAsync($"api/flows/{flowId}/triggers", request, SharedJson.Options, ct);
        return r.IsSuccessStatusCode
            ? (await r.Content.ReadFromJsonAsync<FlowTriggerDto>(SharedJson.Options, ct), null)
            : (null, await ReadErrorAsync(r, ct));
    }

    public async Task<(FlowTriggerDto? Trigger, string? Error)> UpdateTriggerAsync(
        Guid triggerId, UpdateFlowTriggerRequest request, CancellationToken ct = default)
    {
        var r = await http.PutAsJsonAsync($"api/flows/triggers/{triggerId}", request, SharedJson.Options, ct);
        return r.IsSuccessStatusCode
            ? (await r.Content.ReadFromJsonAsync<FlowTriggerDto>(SharedJson.Options, ct), null)
            : (null, await ReadErrorAsync(r, ct));
    }

    public async Task<(bool Ok, string? Error)> DeleteTriggerAsync(Guid triggerId, CancellationToken ct = default)
    {
        var r = await http.DeleteAsync($"api/flows/triggers/{triggerId}", ct);
        return r.IsSuccessStatusCode ? (true, null) : (false, await ReadErrorAsync(r, ct));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            var node = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
            return node?["error"]?.GetValue<string>() ?? $"HTTP {(int)r.StatusCode}";
        }
        catch (JsonException)
        {
            return $"HTTP {(int)r.StatusCode}";
        }
        catch (InvalidOperationException)
        {
            return $"HTTP {(int)r.StatusCode}";
        }
    }
}
