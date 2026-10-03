using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Typed client for /api/tools (SPEC-20260914-playground-tools).</summary>
public sealed class ToolsApiClient(HttpClient http)
{
    public async Task<ToolListResponse?> ListAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<ToolListResponse>("api/tools", SharedJson.Options, ct);

    /// <summary>Calls a tool with a JSON arguments object. Returns the raw CallToolResult JSON.</summary>
    public async Task<JsonElement> CallAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var response = await http.PostAsJsonAsync($"api/tools/{Uri.EscapeDataString(name)}", doc.RootElement, SharedJson.Options, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"unknown tool '{name}'");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(SharedJson.Options, ct));
    }
}
