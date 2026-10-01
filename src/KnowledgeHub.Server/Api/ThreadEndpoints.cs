using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Api;

/// <summary>/api/threads — persistent agent conversations (SPEC-20260914-conversation-threads RF-001).</summary>
public static class ThreadEndpoints
{
    public static RouteGroupBuilder MapThreadsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/threads");

        group.MapGet("/", async (IConversationService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct)));

        group.MapPost("/", async (CreateThreadRequest req, IConversationService svc, CancellationToken ct) =>
            Results.Ok(await svc.CreateAsync(req?.Title, ct)));

        group.MapGet("/{id:guid}", async (Guid id, IConversationService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct)));

        group.MapPut("/{id:guid}", async (Guid id, RenameThreadRequest req, IConversationService svc, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(req?.Title)
                ? Results.BadRequest(new { error = "title is required" })
                : Results.Ok(await svc.RenameAsync(id, req.Title, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, IConversationService svc, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        // Shortcut: run the agent with thread context and persist both sides.
        group.MapPost("/{id:guid}/messages", async (
            Guid id, PostThreadMessageRequest req, IAgentService agent, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Content))
                return Results.BadRequest(new { error = "content is required" });
            if (!agent.IsConfigured)
                return Results.BadRequest(new { error = "agent requires a chat provider (Chat:Provider)" });

            return Results.Ok(await agent.RunAsync(new AgentRequest
            {
                Prompt = req.Content,
                ThreadId = id,
                Tools = req.Tools,
                MaxIterations = req.MaxIterations,
                AllowWrite = req.AllowWrite
            }, ct));
        });

        return group;
    }
}
