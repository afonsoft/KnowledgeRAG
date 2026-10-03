using System.Text.Json;

namespace KnowledgeHub.Sdk;

/// <summary>Normalized result of an MCP <c>tools/call</c>.</summary>
public sealed record HubToolResult
{
    /// <summary>Concatenated text content blocks (what LLMs consume).</summary>
    public required string Text { get; init; }

    /// <summary>Raw <c>structuredContent</c> payload when the tool emitted one.</summary>
    public JsonElement? Structured { get; init; }

    /// <summary>True when the tool reported an execution error in-band.</summary>
    public bool IsError { get; init; }
}

/// <summary>One citation linking an answer marker [n] to a retrieved chunk.</summary>
public sealed record HubCitation
{
    public int Index { get; init; }
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Uri { get; init; } = "";
    /// <summary>Vault-relative path accepted by <see cref="KnowledgeHubClient.ReadDocumentAsync"/>.</summary>
    public string? Path { get; init; }
    public double Score { get; init; }
    public string? SuspicionFlags { get; init; }
    public IReadOnlyList<string>? Components { get; init; }
}

/// <summary>One ranked chunk hit from <c>search_knowledge</c>.</summary>
public sealed record HubSearchHit
{
    public string ChunkText { get; init; } = "";
    public string DocumentTitle { get; init; } = "";
    public string SourceName { get; init; } = "";
    public Guid SourceId { get; init; }
    public double Score { get; init; }
    public string UriReference { get; init; } = "";
    public string? SuspicionFlags { get; init; }
    public string? SectionPath { get; init; }
    public string? Context { get; init; }
    public IReadOnlyList<string>? Components { get; init; }
    public bool IsRelaxed { get; init; }
}

/// <summary>Result of <c>search_knowledge</c>.</summary>
public sealed record HubSearchResult
{
    public required IReadOnlyList<HubSearchHit> Results { get; init; }
    /// <summary>Corrective-RAG grade: sufficient | weak | insufficient (null when grading is off).</summary>
    public string? Grade { get; init; }
    public bool Retried { get; init; }
    public int TotalMatches { get; init; }
    public bool FilterRelaxed { get; init; }
    public bool TruncatedByTokens { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Result of <c>ask_knowledge</c>.</summary>
public sealed record HubAskAnswer
{
    public string? Answer { get; init; }
    public IReadOnlyList<HubCitation> Citations { get; init; } = [];
    public string? Model { get; init; }
    public bool Generated { get; init; }
    /// <summary>True when the retriever graded evidence insufficient and abstained.</summary>
    public bool InsufficientEvidence { get; init; }
    public string? RetrievalGrade { get; init; }
    public bool Retried { get; init; }
    public bool Cached { get; init; }
    public bool TruncatedByTokens { get; init; }
}

/// <summary>One tool-call step taken by the agent loop.</summary>
public sealed record HubAgentStep
{
    public int Iteration { get; init; }
    public string Tool { get; init; } = "";
    public string ArgsSummary { get; init; } = "";
    public bool IsError { get; init; }
    public double ElapsedMs { get; init; }
}

/// <summary>Result of <c>agent_chat</c> (MCP) — the completed run.</summary>
public sealed record HubAgentResult
{
    public string Answer { get; init; } = "";
    public IReadOnlyList<HubAgentStep> Steps { get; init; } = [];
    public IReadOnlyList<string> ToolCalls { get; init; } = [];
    public int Iterations { get; init; }
    public double LatencyMs { get; init; }
    public bool LimitReached { get; init; }
    /// <summary>Set when the run paused for human approval of a write tool.</summary>
    public Guid? AwaitingApprovalId { get; init; }
    public string? PendingTool { get; init; }
    public Guid? ThreadId { get; init; }
}

/// <summary>One Server-Sent-Events frame from <c>/api/ask/stream</c> or
/// <c>/api/agent/stream</c>. <see cref="Data"/> is the raw event payload;
/// <see cref="Type"/> is one of meta | token | tool_start | tool_end |
/// awaiting_approval | abstain | done | error.</summary>
public sealed record HubStreamEvent(string Type, JsonElement Data);

/// <summary>A chat message for the agent REST streaming endpoint.</summary>
public sealed record HubAgentMessage(string Role, string Content);
