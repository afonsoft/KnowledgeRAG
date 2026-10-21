using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Agent;

/// <summary>
/// SPEC-20261021-agent-middleware-pipeline RF-1: composable hooks around the
/// agent loop — the LangChain v1 AgentMiddleware analogue
/// (before_agent / wrap_model_call / wrap_tool_call / after_agent).
/// All hooks have default no-op implementations; middlewares registered
/// first are outermost (they see the call first, and the result last).
/// </summary>
public interface IAgentMiddleware
{
    /// <summary>Stable name used by <c>Agent:Middleware</c> ordering config.</summary>
    string Name => GetType().Name;

    /// <summary>Runs once before the first model call of the run.</summary>
    ValueTask BeforeAgentAsync(AgentMiddlewareContext ctx, CancellationToken ct) =>
        ValueTask.CompletedTask;

    /// <summary>Wraps each model invocation (once per iteration). Call
    /// <paramref name="next"/> to invoke the inner handler; short-circuit by
    /// returning a response without calling it.</summary>
    Task<ChatResponse> WrapModelCallAsync(
        AgentMiddlewareContext ctx, Func<Task<ChatResponse>> next, CancellationToken ct) =>
        next();

    /// <summary>Wraps each tool dispatch. <paramref name="next"/> executes the
    /// tool; the result is whatever the function returned (already stringified
    /// by the loop for message purposes).</summary>
    Task<object?> WrapToolCallAsync(
        AgentMiddlewareContext ctx, FunctionCallContent call,
        Func<Task<object?>> next, CancellationToken ct) =>
        next();

    /// <summary>Runs once after the loop produced a final response —
    /// including suspended (awaiting-approval) and limit-reached endings.</summary>
    ValueTask AfterAgentAsync(AgentMiddlewareContext ctx, CancellationToken ct) =>
        ValueTask.CompletedTask;
}

/// <summary>Context handed to every middleware hook — the run's request, live
/// message list, current iteration and service provider, plus the mutable
/// slots built-in middlewares use to steer the loop.</summary>
public sealed class AgentMiddlewareContext
{
    public required AgentRequest Request { get; init; }
    public required List<ChatMessage> Messages { get; init; }
    /// <summary>1-based iteration of the current model call (0 before the loop).</summary>
    public int Iteration { get; set; }
    public required IServiceProvider Services { get; init; }

    /// <summary>Chat client handling the current model call (telemetry tags).</summary>
    public IChatClient? ChatClient { get; set; }
    /// <summary>Effective iteration ceiling resolved for this run.</summary>
    public int MaxIterations { get; set; }
    /// <summary>Tool dispatches counted so far this run.</summary>
    public int ToolCalls { get; set; }
    /// <summary>Set by middleware to mark the run as limit-reached.</summary>
    public bool LimitReached { get; set; }
    /// <summary>Set by middleware to suspend the run with this response —
    /// the loop returns it instead of continuing (HITL approval).</summary>
    public AgentResponse? SuspendedResponse { get; set; }
    /// <summary>Sibling calls after the current one in the same model turn —
    /// persisted into suspend state so resume can answer them.</summary>
    public IReadOnlyList<FunctionCallContent> RemainingCalls { get; set; } = [];
    /// <summary>Catalog tools visible this run, keyed by name — gate decisions.</summary>
    public IReadOnlyDictionary<string, Mcp.CatalogTool> ToolsByName { get; set; } =
        new Dictionary<string, Mcp.CatalogTool>();
    /// <summary>Service-provided suspend mechanism — persists the approval
    /// record and returns the suspended <see cref="AgentResponse"/>.</summary>
    public Func<FunctionCallContent, IReadOnlyList<FunctionCallContent>, CancellationToken,
        Task<AgentResponse>>? SuspendAsync
    { get; set; }
}
