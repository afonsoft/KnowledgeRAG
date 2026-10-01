namespace KnowledgeHub.Shared.Contracts;

/// <summary>POST /api/ask request (SPEC-20260914-llm-answer-synthesis RF-003).</summary>
public sealed record AskRequest
{
    public required string Question { get; init; }
    public int? TopK { get; init; }
    public Guid? SourceId { get; init; }
    /// <summary>hybrid | semantic | lexical — default semantic (same as /api/search).</summary>
    public string? Mode { get; init; }
    /// <summary>Override LLM generation; default true when a chat provider is configured.</summary>
    public bool? Generate { get; init; }
    /// <summary>Optional metadata filters (SPEC-20260923-retrieval-quality RF-003).</summary>
    public SearchFilter? Filters { get; init; }
}

/// <summary>One citation linking an answer marker [n] to a retrieved chunk.</summary>
public sealed record CitationDto
{
    public required int Index { get; init; }
    public required string Source { get; init; }
    public required string Title { get; init; }
    public required string Uri { get; init; }
    /// <summary>Vault-relative file path accepted by read_document; null for
    /// non-file-backed sources (SPEC-20260922-tool-descriptions-en-us RF-003).</summary>
    public string? Path { get; init; }
    public required double Score { get; init; }
    /// <summary>Suspicion flags on the cited chunk when it survived exclusion
    /// (SPEC-20260923-flagged-chunk-badge RF-002); null = clean.</summary>
    public string? SuspicionFlags { get; init; }
    /// <summary>Knowledge-graph entity names evidenced by the cited chunk —
    /// usable as `component`/`a`/`b` args of the find_* tools
    /// (SPEC-20260924-graph-tool-discovery RF-002).</summary>
    public IReadOnlyList<string>? Components { get; init; }
}

/// <summary>Synthesized answer (or raw context when generation is off/unavailable).</summary>
public sealed record AskResponse
{
    public required string? Answer { get; init; }
    public required IReadOnlyList<CitationDto> Citations { get; init; }
    public required double LatencyMs { get; init; }
    /// <summary>Model that produced the answer; null when generated=false.</summary>
    public required string? Model { get; init; }
    /// <summary>True when the answer was synthesized by the configured chat provider.</summary>
    public required bool Generated { get; init; }
    /// <summary>SPEC-20260924-corrective-rag RF-003: true when retrieval grading
    /// found insufficient evidence and no synthesis was attempted.</summary>
    public bool InsufficientEvidence { get; init; }
    /// <summary>Retrieval grade that grounded (or blocked) this answer:
    /// sufficient|weak|insufficient. Null when grading is off.</summary>
    public string? RetrievalGrade { get; init; }
    /// <summary>True when weak evidence triggered a corrective re-search.</summary>
    public bool Retried { get; init; }
    /// <summary>True when served from the answer cache (SPEC-20260923
    /// agent-runtime-hardening RF-003 — opt-in via Cache:AnswerCache:Enabled).</summary>
    public bool Cached { get; init; }
    /// <summary>Raw context used for the answer (when generated=false).</summary>
    public IReadOnlyList<SearchResultItem>? Context { get; init; }
    /// <summary>SPEC-20260927-mcp-dynamic-rag-action-bridge RF-001: live MCP
    /// tool executions fused into this answer — empty/null when the bridge
    /// produced none or was disabled.</summary>
    public IReadOnlyList<LiveToolExecution>? LiveToolExecutions { get; init; }
    /// <summary>SPEC-20261001-mcp-recall-ergonomics RF-002: the retrieved
    /// context was cut to fit the caller's maxTokens budget.</summary>
    public bool TruncatedByTokens { get; init; }
}
