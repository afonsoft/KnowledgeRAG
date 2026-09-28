namespace KnowledgeHub.Shared.Contracts;

/// <summary>
/// One live MCP tool execution fused into an answer
/// (SPEC-20260927-mcp-dynamic-rag-action-bridge RF-001/RF-002): emitted as
/// <c>liveToolExecutions</c> on ask/agent payloads and rendered as
/// <c>[Live Tool: name @ timestamp]</c> citations.
/// </summary>
public sealed record LiveToolExecution
{
    public required string ToolName { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    /// <summary>Compact args summary (≤200 chars) — provenance for the call.</summary>
    public required string ArgsSummary { get; init; }
    public required bool IsError { get; init; }
    /// <summary>Bounded tool output preview (≤4000 chars).</summary>
    public required string OutputPreview { get; init; }
}
