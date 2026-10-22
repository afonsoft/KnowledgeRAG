using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Agent;

/// <summary>
/// SPEC-20261021-agent-middleware-pipeline RF-1/RF-3: ordered composition of
/// <see cref="IAgentMiddleware"/> — first registered = outermost. When
/// <c>Agent:Middleware</c> names are configured they order (and filter) the
/// resolved set by <see cref="IAgentMiddleware.Name"/>.
/// </summary>
public sealed class AgentMiddlewarePipeline
{
    private readonly IReadOnlyList<IAgentMiddleware> _middlewares;

    public AgentMiddlewarePipeline(
        IEnumerable<IAgentMiddleware> middlewares, IReadOnlyList<string>? order = null)
    {
        var list = middlewares.ToList();
        _middlewares = order is { Count: > 0 }
            ? [.. order.Select(name =>
                    list.FirstOrDefault(m => m.Name == name)
                        ?? throw new InvalidOperationException(
                            $"Agent:Middleware names unknown middleware '{name}'"))]
            : list;
    }

    public IReadOnlyList<IAgentMiddleware> Middlewares => _middlewares;

    public async ValueTask BeforeAgentAsync(AgentMiddlewareContext ctx, CancellationToken ct)
    {
        foreach (var m in _middlewares)
            await m.BeforeAgentAsync(ctx, ct);
    }

    public async ValueTask AfterAgentAsync(AgentMiddlewareContext ctx, CancellationToken ct)
    {
        foreach (var m in _middlewares)
            await m.AfterAgentAsync(ctx, ct);
    }

    /// <summary>Onion composition — middleware[0] wraps middleware[1] wraps
    /// … wraps the terminal model call.</summary>
    public Task<ChatResponse> InvokeModelCallAsync(
        AgentMiddlewareContext ctx, Func<Task<ChatResponse>> terminal, CancellationToken ct)
    {
        var next = terminal;
        for (var i = _middlewares.Count - 1; i >= 0; i--)
        {
            var inner = next;
            var mw = _middlewares[i];
            next = () => mw.WrapModelCallAsync(ctx, inner, ct);
        }
        return next();
    }

    /// <summary>Same onion composition around a tool dispatch.</summary>
    public Task<object?> InvokeToolCallAsync(
        AgentMiddlewareContext ctx, FunctionCallContent call,
        Func<Task<object?>> terminal, CancellationToken ct)
    {
        var next = terminal;
        for (var i = _middlewares.Count - 1; i >= 0; i--)
        {
            var inner = next;
            var mw = _middlewares[i];
            next = () => mw.WrapToolCallAsync(ctx, call, inner, ct);
        }
        return next();
    }
}
