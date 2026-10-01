using System.Text.Json.Serialization;

namespace KnowledgeHub.Shared.Contracts;

/// <summary>POST /api/agent request / agent_chat args (SPEC-20260914-agent-chat-loop).</summary>
public sealed record AgentRequest
{
    /// <summary>Single-turn prompt; either this or <see cref="Messages"/> is required.</summary>
    public string? Prompt { get; init; }
    /// <summary>Prior conversation turns (user/assistant); appended after the system prompt.</summary>
    public IReadOnlyList<AgentMessage>? Messages { get; init; }
    /// <summary>Optional tool allowlist — restricts the surface exposed to the model.</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    public int? MaxIterations { get; init; }
    /// <summary>Opt-in: include mutating tools (write_knowledge/write_note) in the loop.</summary>
    public bool AllowWrite { get; init; }
    /// <summary>Continue an existing conversation thread (SPEC-20260914-conversation-threads).</summary>
    public Guid? ThreadId { get; init; }
    /// <summary>Create a new thread and persist this run's turns when no ThreadId is given.</summary>
    public bool Persist { get; init; }
    /// <summary>SPEC-20261001-a2a-task-durability RF-002: in-process progress
    /// callback (per iteration/tool call) — set by transports that can stream
    /// task updates. Never serialized.</summary>
    [JsonIgnore]
    public Func<string, CancellationToken, ValueTask>? OnProgress { get; init; }
}

/// <summary>One prior conversation turn for <see cref="AgentRequest.Messages"/>.</summary>
public sealed record AgentMessage
{
    /// <summary><c>user</c> or <c>assistant</c> (anything else is treated as user).</summary>
    public required string Role { get; init; }
    public required string Content { get; init; }
}

/// <summary>One tool invocation inside the agent loop (timeline entry).</summary>
public sealed record AgentStep
{
    public required int Iteration { get; init; }
    public required string Tool { get; init; }
    public required string ArgsSummary { get; init; }
    public required bool IsError { get; init; }
    public required double ElapsedMs { get; init; }
}

/// <summary>Result of an agent run.</summary>
public sealed record AgentResponse
{
    public required string Answer { get; init; }
    public required IReadOnlyList<AgentStep> Steps { get; init; }
    public required IReadOnlyList<string> ToolCalls { get; init; }
    public required int Iterations { get; init; }
    public required double LatencyMs { get; init; }
    /// <summary>True when a hard cap (iterations/tool calls) stopped the loop.</summary>
    public required bool LimitReached { get; init; }
    /// <summary>Set when the loop suspended on a gated (mutating) tool — resume via /api/agent/resume.</summary>
    public Guid? AwaitingApprovalId { get; init; }
    public string? PendingTool { get; init; }
    public string? PendingArgsJson { get; init; }
    /// <summary>Thread this run was persisted to (when threadId/persist were used).</summary>
    public Guid? ThreadId { get; init; }
}
