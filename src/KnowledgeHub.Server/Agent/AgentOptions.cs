namespace KnowledgeHub.Server.Agent;

/// <summary>Loop bounds for the agentic tool-calling loop (SPEC-20260914-agent-chat-loop RF-001/RNF).</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Hard cap on model→tools→model rounds.</summary>
    public int MaxIterations { get; set; } = 10;

    /// <summary>Hard cap on total tool invocations across all iterations.</summary>
    public int MaxToolCalls { get; set; } = 20;

    /// <summary>Tool results are truncated to this many chars before going back to the model.</summary>
    public int MaxToolResultChars { get; set; } = 4000;

    /// <summary>
    /// Tool names that require human approval when invoked inside the agent loop.
    /// "*" (default) gates every non-readOnly tool. (SPEC-20260914-hitl-tool-approval RF-001)
    /// </summary>
    public List<string> RequireApprovalFor { get; set; } = ["*"];

    /// <summary>Minutes a pending approval stays valid before it expires.</summary>
    public int ApprovalTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Estimated token budget for conversation history (chars/4). Older thread
    /// messages beyond the window are condensed into a rolling summary.
    /// (SPEC-20260914-conversation-threads RF-002)
    /// </summary>
    public int MaxContextTokens { get; set; } = 8000;

    /// <summary>Chain AST compaction + repair policy
    /// (<c>Agent:ContextManagement</c>, SPEC-20260927-chain-ast-thread-compactor).</summary>
    public McpEngine.Agents.ChainAst.ChainCompactionOptions ContextManagement { get; set; } = new();

    /// <summary>
    /// SSE event buffer capacity for <c>agent_chat</c> streaming
    /// (SPEC-20260923-agent-runtime-hardening RF-001): bounded with
    /// <see cref="BoundedChannelFullMode.Wait"/> — a slow client back-pressures
    /// the run instead of growing memory unboundedly; events are never dropped.
    /// </summary>
    public int SseChannelCapacity { get; set; } = 256;

    /// <summary>SPEC-20260924-conversational-query-context: history-aware query
    /// rewriting for retrieval tools invoked inside a thread.</summary>
    public QueryContextOptions QueryContext { get; set; } = new();

    /// <summary>SPEC-20260927-mcp-dynamic-rag-action-bridge: action-augmented
    /// RAG — retrieved chunks may nominate live MCP tools (markers or explicit
    /// mentions) which are executed and fused into the answer.</summary>
    public bool EnableDynamicActionBridge { get; set; } = true;

    /// <summary>Hard cap on chained live-tool executions per ask (RF-003).</summary>
    public int MaxChainedDynamicCalls { get; set; } = 3;

    public sealed class QueryContextOptions
    {
        /// <summary>Pass the conversation snapshot to retrieval tools.</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>How many recent turns feed the rewriter context.</summary>
        public int HistoryMessages { get; set; } = 4;
    }
}
