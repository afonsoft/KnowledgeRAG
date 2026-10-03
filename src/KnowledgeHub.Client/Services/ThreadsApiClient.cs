using System.Net.Http.Json;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Typed client for /api/threads (SPEC-20260914-conversation-threads).</summary>
public sealed class ThreadsApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<ThreadDto>> ListAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<ThreadDto>>("api/threads", SharedJson.Options, ct) ?? [];

    public async Task<ThreadDto?> CreateAsync(string? title = null, CancellationToken ct = default)
    {
        var r = await http.PostAsJsonAsync("api/threads", new { title }, SharedJson.Options, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ThreadDto>(SharedJson.Options, ct);
    }

    public async Task<ThreadDetailDto?> GetAsync(Guid id, CancellationToken ct = default)
        => await http.GetFromJsonAsync<ThreadDetailDto>($"api/threads/{id}", SharedJson.Options, ct);

    public async Task RenameAsync(Guid id, string title, CancellationToken ct = default)
    {
        var r = await http.PutAsJsonAsync($"api/threads/{id}", new { title }, SharedJson.Options, ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var r = await http.DeleteAsync($"api/threads/{id}", ct);
        r.EnsureSuccessStatusCode();
    }

    public async Task<AgentResponse?> SendAsync(Guid id, string content, CancellationToken ct = default)
    {
        var r = await http.PostAsJsonAsync($"api/threads/{id}/messages", new { content }, SharedJson.Options, ct);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<AgentResponse>(SharedJson.Options, ct);
    }
}
