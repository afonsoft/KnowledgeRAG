using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Typed client for /api/sources (SPEC-05 RF-001/RF-002).</summary>
public sealed class SourceApiClient(HttpClient http)
{
    public Task<List<KnowledgeSourceDto>?> ListAsync(CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<KnowledgeSourceDto>>("api/sources", SharedJson.Options, ct);

    public async Task<ApiResult<KnowledgeSourceDto>> CreateAsync(CreateKnowledgeSourceRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/sources", request, SharedJson.Options, ct);
        return await ReadResultAsync<KnowledgeSourceDto>(response, ct);
    }

    public async Task<ApiResult<KnowledgeSourceDto>> UpdateAsync(Guid id, UpdateKnowledgeSourceRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"api/sources/{id}", request, SharedJson.Options, ct);
        return await ReadResultAsync<KnowledgeSourceDto>(response, ct);
    }

    public async Task<ApiResult<KnowledgeSourceDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/sources/{id}/{(active ? "activate" : "deactivate")}", null, ct);
        return await ReadResultAsync<KnowledgeSourceDto>(response, ct);
    }

    /// <summary>Legacy synchronous sync — <c>?wait=true</c> (detail dialog needs
    /// the full SyncResult). The grid uses <see cref="EnqueueSyncAsync"/>.</summary>
    public async Task<ApiResult<SyncResultDto>> SyncAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/sources/{id}/sync?wait=true", null, ct);
        return await ReadResultAsync<SyncResultDto>(response, ct);
    }

    /// <summary>SPEC-20260924-async-ingestion-queue: enqueue a sync job (202).</summary>
    public async Task<ApiResult<SyncJobEnqueueDto>> EnqueueSyncAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/sources/{id}/sync", null, ct);
        return await ReadResultAsync<SyncJobEnqueueDto>(response, ct);
    }

    /// <summary>Poll a queued/running ingestion job.</summary>
    public Task<IngestionJobDto?> GetJobAsync(Guid jobId, CancellationToken ct = default) =>
        http.GetFromJsonAsync<IngestionJobDto>($"api/ingestion/jobs/{jobId}", SharedJson.Options, ct);

    /// <summary>SPEC-20260925-sources-grid-ux RF-003: latest jobs of a source
    /// (status detail popup).</summary>
    public Task<List<IngestionJobDto>?> JobsAsync(Guid sourceId, int limit = 5, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<IngestionJobDto>>(
            $"api/ingestion/jobs?sourceId={sourceId}&limit={limit}", SharedJson.Options, ct);

    public async Task<ApiResult<object>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"api/sources/{id}", ct);
        return await ReadResultAsync<object>(response, ct);
    }

    public Task<List<KnowledgeDocumentDto>?> DocumentsAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<KnowledgeDocumentDto>>($"api/sources/{id}/documents", SharedJson.Options, ct);

    private static async Task<ApiResult<T>> ReadResultAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(SharedJson.Options, cancellationToken: ct);
            return new ApiResult<T>(value, null);
        }

        var (error, detail) = await TryReadErrorAsync(response, ct);
        return new ApiResult<T>(default, error ?? $"HTTP {(int)response.StatusCode}", detail);
    }

    private static async Task<(string? Error, string Detail)> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var detail = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
                detail += $"\n{body}";
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
            var error = dict is not null &&
                (dict.TryGetValue("error", out var e) || dict.TryGetValue("detail", out e))
                    ? e : null;
            return (error, detail);
        }
        catch
        {
            return (response.StatusCode == HttpStatusCode.Conflict ? "Conflict" : null, detail);
        }
    }
}

public sealed record ApiResult<T>(T? Value, string? Error, string? Detail = null)
{
    public bool IsSuccess => Error is null;
}

/// <summary>202 response from POST /api/sources/{id}/sync (queued mode).</summary>
public sealed record SyncJobEnqueueDto(Guid JobId, string Status, bool Existing);

/// <summary>GET /api/ingestion/jobs/{id} response.</summary>
public sealed record IngestionJobDto
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "";
    public int DocsProcessed { get; init; }
    public int DocsSkipped { get; init; }
    public int DocsFailed { get; init; }
    public int ChunksCreated { get; init; }
    public string? Error { get; init; }
    /// <summary>SPEC-20260926-job-error-details RF-002: per-document warnings/failures.</summary>
    public List<string>? Warnings { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}
