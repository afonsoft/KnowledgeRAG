using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Api;

/// <summary>POST /api/agent — agentic tool-calling loop (SPEC-20260914-agent-chat-loop RF-002).</summary>
public static class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent");

        group.MapPost("/", async (AgentRequest request, IAgentService agent, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Prompt) && request.Messages is not { Count: > 0 })
                return Results.BadRequest(new { error = "prompt or messages[] is required" });
            if (!agent.IsConfigured)
                return Results.BadRequest(new { error = "agent requires a chat provider (Chat:Provider)" });

            // SPEC-20261021-agent-structured-output RF-5: invalid schema → 400.
            try { Agent.StructuredOutput.Validate(request.ResponseFormat); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

            return Results.Ok(await agent.RunAsync(request, ct));
        });

        // Continues a run suspended on an approved (or denied→denied-result) tool call.
        group.MapPost("/resume", async (ResumeAgentRequest request, IAgentService agent, CancellationToken ct) =>
        {
            if (!agent.IsConfigured)
                return Results.BadRequest(new { error = "agent requires a chat provider (Chat:Provider)" });
            return Results.Ok(await agent.ResumeAsync(request.ApprovalId, cancellationToken: ct));
        });

        return group;
    }
}
