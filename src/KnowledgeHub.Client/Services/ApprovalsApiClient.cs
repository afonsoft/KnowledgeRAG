using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Client.Services;

/// <summary>Typed client for /api/approvals (SPEC-20260914-hitl-tool-approval).</summary>
public sealed class ApprovalsApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<ApprovalDto>> ListAsync(string? status = null, CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<ApprovalDto>>(
               $"api/approvals{(string.IsNullOrWhiteSpace(status) ? "" : $"?status={status}")}", SharedJson.Options, ct)
           ?? [];

    public async Task<ApprovalDto?> ApproveAsync(Guid id, string? approvedArgsJson = null, CancellationToken ct = default)
    {
        ApproveApprovalRequest? body = approvedArgsJson is { Length: > 0 } json
            ? new ApproveApprovalRequest { ApprovedArgs = JsonDocument.Parse(json).RootElement }
            : null;
        var response = await http.PostAsJsonAsync($"api/approvals/{id}/approve", body, SharedJson.Options, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApprovalDto>(SharedJson.Options, ct);
    }

    public async Task DenyAsync(Guid id, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"api/approvals/{id}/deny", null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>POST /api/agent/resume — continua o agente após aprovação.</summary>
    public async Task<AgentResponse?> ResumeAgentAsync(Guid approvalId, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("api/agent/resume",
            new ResumeAgentRequest { ApprovalId = approvalId }, SharedJson.Options, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentResponse>(SharedJson.Options, ct);
    }
}
