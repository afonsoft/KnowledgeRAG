namespace KnowledgeHub.Server.Agent;

/// <summary>
/// SPEC-20261021-agent-streaming-events RF-4: opt-in channel for tools to emit
/// partial output deltas while executing. <see cref="AgentService"/> sets
/// <see cref="Current"/> to an SSE sink for the duration of each tool call —
/// a tool that produces incremental output (long crawls, multi-step fetches)
/// can report it via <see cref="ReportAsync"/>. Tools that never check
/// <see cref="Current"/> behave exactly as before.
/// </summary>
public static class ToolOutputDeltaChannel
{
    private static readonly AsyncLocal<Func<string, CancellationToken, ValueTask>?> Slot = new();

    /// <summary>Reporter bound to the current tool call — null outside an
    /// agent-loop tool execution or when no streaming sink is attached.</summary>
    public static Func<string, CancellationToken, ValueTask>? Current => Slot.Value;

    /// <summary>Convenience for tools: report a partial output delta if a
    /// reporter is attached; no-op otherwise.</summary>
    public static ValueTask ReportAsync(string delta, CancellationToken ct = default) =>
        Slot.Value?.Invoke(delta, ct) ?? ValueTask.CompletedTask;

    /// <summary>Agent-loop internal: bind a reporter for the current async flow.</summary>
    internal static void Bind(Func<string, CancellationToken, ValueTask>? reporter) =>
        Slot.Value = reporter;
}
