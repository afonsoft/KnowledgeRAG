using System.Diagnostics;
using KnowledgeHub.Server.Mcp;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Agent;

/// <summary>
/// SPEC-20261021-agent-middleware-pipeline RF-2: per-iteration Activity and
/// LLM-duration metric around each model call. Registered outermost so the
/// span covers compaction plus the call itself (as the inline code did).
/// </summary>
internal sealed class TelemetryMiddleware(string agentTag) : IAgentMiddleware
{
    public async Task<ChatResponse> WrapModelCallAsync(
        AgentMiddlewareContext ctx, Func<Task<ChatResponse>> next, CancellationToken ct)
    {
        using var iterSpan = Telemetry.KnowledgeHubActivity.Start("agent_iteration");
        iterSpan?.SetTag("agent.iteration", ctx.Iteration);
        var sw = Stopwatch.StartNew();
        try
        {
            return await next();
        }
        catch (Exception ex)
        {
            Telemetry.KnowledgeHubActivity.Fail(iterSpan, ex);
            throw;
        }
        finally
        {
            Telemetry.KnowledgeHubMetrics.LlmDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ctx.ChatClient?.GetType().Name),
                new KeyValuePair<string, object?>("model", agentTag),
                new KeyValuePair<string, object?>("kind", agentTag));
        }
    }
}

/// <summary>
/// SPEC-20261021-agent-middleware-pipeline RF-2: mutating tools suspend the
/// run into a pending HITL approval instead of executing. Runs outermost of
/// the tool-call built-ins so a gated call never reaches the budget counter
/// or the dispatch — the persisted approval resumes the run later.
/// </summary>
internal sealed class HitlApprovalMiddleware(AgentOptions options) : IAgentMiddleware
{
    public async Task<object?> WrapToolCallAsync(
        AgentMiddlewareContext ctx, FunctionCallContent call,
        Func<Task<object?>> next, CancellationToken ct)
    {
        if (!ctx.ToolsByName.TryGetValue(call.Name, out var tool) || !RequiresApproval(tool))
            return await next();

        if (ctx.SuspendAsync is not { } suspend)
            throw new InvalidOperationException(
                "AgentMiddlewareContext.SuspendAsync was not provided — a gated " +
                "tool cannot suspend without the service's approval mechanism");
        ctx.SuspendedResponse = await suspend(call, ctx.RemainingCalls, ct);
        return null; // suspended — the terminal dispatch never runs.
    }

    private bool RequiresApproval(CatalogTool tool) =>
        !tool.ReadOnly
        && (options.RequireApprovalFor.Contains("*")
            || options.RequireApprovalFor.Contains(tool.Name, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// SPEC-20261021-agent-middleware-pipeline RF-2: tool-call budget
/// (<see cref="AgentOptions.MaxToolCalls"/>) and a defensive iteration guard.
/// The counter runs inside the wrap so gated calls (short-circuited by
/// <see cref="HitlApprovalMiddleware"/> upstream) never consume budget.
/// </summary>
internal sealed class IterationLimitMiddleware(AgentOptions options) : IAgentMiddleware
{
    public Task<ChatResponse> WrapModelCallAsync(
        AgentMiddlewareContext ctx, Func<Task<ChatResponse>> next, CancellationToken ct)
    {
        // Structural guard — the loop's while condition normally prevents this.
        if (ctx.Iteration > ctx.MaxIterations)
        {
            ctx.LimitReached = true;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
        }
        return next();
    }

    public async Task<object?> WrapToolCallAsync(
        AgentMiddlewareContext ctx, FunctionCallContent call,
        Func<Task<object?>> next, CancellationToken ct)
    {
        ctx.ToolCalls++;
        if (ctx.ToolCalls > options.MaxToolCalls)
        {
            ctx.LimitReached = true;
            return null;
        }
        return await next();
    }
}

/// <summary>
/// SPEC-20261021-agent-middleware-pipeline RF-2: repairs dangling tool calls
/// and compacts the live history before each model call (never the persisted
/// transcript) — the ChainAst compactor step that used to inline-precede the
/// model invocation.
/// </summary>
internal sealed class ChainAstCompactionMiddleware(
    McpEngine.Agents.ChainAst.IChainCompactor? compactor, AgentOptions options) : IAgentMiddleware
{
    public async Task<ChatResponse> WrapModelCallAsync(
        AgentMiddlewareContext ctx, Func<Task<ChatResponse>> next, CancellationToken ct)
    {
        if (compactor is not null)
        {
            var ast = McpEngine.Agents.ChainAst.ChainAstParser.Parse(
                ctx.Messages, options.ContextManagement.AutoRepairBrokenToolCalls);
            ast = await compactor.CompactAsync(ast, ct);
            ctx.Messages.Clear();
            ctx.Messages.AddRange(ast.ToChatMessages());
        }
        return await next();
    }
}
