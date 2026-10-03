using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Flows;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// /api/approvals — HITL gate for mutating tools invoked by the agent loop
/// (SPEC-20260914-hitl-tool-approval RF-002/RF-003) and for mid-flow
/// approval steps (RequestedBy="flow" → resolves + resumes the flow run).
/// </summary>
public static class ApprovalsEndpoints
{
    public static RouteGroupBuilder MapApprovalsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/approvals");

        group.MapGet("/", async (IApprovalService approvals, string? status, CancellationToken ct) =>
            Results.Ok(await approvals.ListAsync(status, ct)));

        group.MapPost("/{id:guid}/approve", async (
            Guid id, ApproveApprovalRequest? body, IApprovalService approvals,
            KnowledgeHubDbContext db, FlowService flows, HttpContext http, CancellationToken ct) =>
        {
            // Flow gates: resolve the approval, then resume the suspended run
            // in the same request — the approval row is the single source of
            // truth for the gate's outcome.
            if (await IsFlowApprovalAsync(db, id, ct))
            {
                await approvals.ApproveAsync(id, body?.ApprovedArgs, ct);
                var run = await flows.ResumeByApprovalAsync(id, http.RequestServices, ct);
                return Results.Ok(new { approvalId = id, run });
            }
            return Results.Ok(await approvals.ApproveAsync(id, body?.ApprovedArgs, ct));
        });

        // Deny resolves the gate AND continues the loop with a "denied" tool result,
        // so the model answers without mutating anything (AC: deny → responde sem escrever).
        group.MapPost("/{id:guid}/deny", async (
            Guid id, IApprovalService approvals, IAgentService agent,
            KnowledgeHubDbContext db, FlowService flows, HttpContext http, CancellationToken ct) =>
        {
            if (await IsFlowApprovalAsync(db, id, ct))
            {
                await approvals.DenyAsync(id, ct);
                var run = await flows.ResumeByApprovalAsync(id, http.RequestServices, ct);
                return Results.Ok(new { approvalId = id, run });
            }
            await approvals.DenyAsync(id, ct);
            return Results.Ok(await agent.ResumeAsync(id, allowDenied: true, ct));
        });

        return group;
    }

    private static Task<bool> IsFlowApprovalAsync(KnowledgeHubDbContext db, Guid id, CancellationToken ct) =>
        db.Approvals.AnyAsync(a => a.Id == id && a.RequestedBy == "flow", ct);
}
