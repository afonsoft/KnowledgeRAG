using System.Text.Json;

namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// Transport-neutral invocation context for <see cref="CatalogTool.Handler"/>
/// (SPEC-20260914-playground-tools RF-001): the MCP CallToolHandler adapts
/// <c>RequestContext</c> into this, and REST tool endpoints build it directly —
/// the same handler serves both.
/// </summary>
public sealed record ToolCallContext
{
    public required IServiceProvider Services { get; init; }
    public IDictionary<string, JsonElement>? Arguments { get; init; }
    /// <summary>SPEC-20260924-conversational-query-context: compact snapshot of
    /// the ongoing conversation (last N turns) when the call happens inside an
    /// agent thread — lets retrieval tools contextualise follow-up questions.
    /// Null outside agent loops.</summary>
    public string? ConversationContext { get; init; }

    /// <summary>SPEC-20261001-a2a-task-durability RF-002: optional progress
    /// sink — transports that support incremental updates (A2A task events)
    /// set it; long-running tools report human-readable progress lines.
    /// Best-effort: handlers invoke it, callers tolerate null.</summary>
    public Func<string, CancellationToken, ValueTask>? OnProgress { get; init; }
}
