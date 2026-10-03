namespace KnowledgeHub.Shared.Contracts;

/// <summary>
/// Wire contract between the <c>/hubs/mcp</c> SignalR feed and the Monitor UI
/// (SPEC-20260915-mcp-monitor-activity RF-001): one shape for both the
/// connect-time <c>Snapshot</c> payload and the live <c>Activity</c> broadcast,
/// so history replay and live rendering share a single code path.
/// </summary>
public sealed record McpMonitorEventDto
{
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// <see cref="McpMonitorEventKind"/> member name — distinguishes session
    /// lifecycle entries from renderable activity rows.
    /// </summary>
    public required string Kind { get; init; }

    public string? SessionId { get; init; }

    /// <summary>JSON-RPC method; <c>tools/call:{tool}</c> for tool invocations.</summary>
    public string? Method { get; init; }

    /// <summary>Error message on failure, otherwise the transport.</summary>
    public string? Detail { get; init; }

    public double? DurationMs { get; init; }
    public bool? Succeeded { get; init; }

    /// <summary>Authenticated caller label for audit (user + auth method).</summary>
    public string? Caller { get; init; }
}

/// <summary>
/// Kind names carried by <see cref="McpMonitorEventDto.Kind"/> — mirrors the
/// server-side McpActivityKind member names (pinned by contract tests).
/// </summary>
public static class McpMonitorEventKind
{
    public const string SessionOpened = "SessionOpened";
    public const string SessionClosed = "SessionClosed";
    public const string Request = "Request";
    public const string ToolCall = "ToolCall";
    public const string ApprovalRequested = "ApprovalRequested";
    public const string ApprovalResolved = "ApprovalResolved";
}

/// <summary>SignalR <c>SessionOpened</c> payload from /hubs/mcp.</summary>
public sealed record SessionOpenedEvent(string SessionId, DateTimeOffset ConnectedAt, string? Caller);

/// <summary>SignalR <c>SessionClosed</c> payload from /hubs/mcp.</summary>
public sealed record SessionClosedEvent(string SessionId, string? Caller);

/// <summary>
/// SignalR <c>IngestionProgress</c> payload (SPEC-20260925-job-progress-feed) —
/// per-job counters only, never doc content.
/// </summary>
public sealed record IngestionProgressEventDto(
    Guid JobId, Guid SourceId, string Status,
    int Processed, int Skipped, int Failed, int ChunksCreated);
