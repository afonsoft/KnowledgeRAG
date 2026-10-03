package knowledgehub

// ToolResult is the normalized result of an MCP tools/call.
type ToolResult struct {
	// Text is the concatenated text content blocks (what LLMs consume).
	Text string
	// Structured is the raw structuredContent payload when the tool emitted one.
	Structured map[string]any
	// IsError is true when the tool reported an execution error in-band.
	IsError bool
}

// ToolDescriptor is a discovered MCP tool (name + JSON-Schema inputSchema).
type ToolDescriptor struct {
	Name        string         `json:"name"`
	Description string         `json:"description,omitempty"`
	InputSchema map[string]any `json:"inputSchema,omitempty"`
}

// Citation links an answer marker [n] to a retrieved chunk.
type Citation struct {
	Index          int      `json:"index"`
	Source         string   `json:"source"`
	Title          string   `json:"title"`
	URI            string   `json:"uri"`
	Path           string   `json:"path,omitempty"`
	Score          float64  `json:"score"`
	SuspicionFlags string   `json:"suspicionFlags,omitempty"`
	Components     []string `json:"components,omitempty"`
}

// SearchHit is one ranked chunk from search_knowledge.
type SearchHit struct {
	ChunkText      string   `json:"chunkText"`
	DocumentTitle  string   `json:"documentTitle"`
	SourceName     string   `json:"sourceName"`
	SourceID       string   `json:"sourceId"`
	Score          float64  `json:"score"`
	URIReference   string   `json:"uriReference"`
	SuspicionFlags string   `json:"suspicionFlags,omitempty"`
	SectionPath    string   `json:"sectionPath,omitempty"`
	Context        string   `json:"context,omitempty"`
	Components     []string `json:"components,omitempty"`
	IsRelaxed      bool     `json:"isRelaxed"`
}

// SearchResult is the result of search_knowledge (hybrid RRF retrieval +
// corrective grading).
type SearchResult struct {
	Results []SearchHit `json:"results"`
	// Grade is the corrective-RAG grade: sufficient | weak | insufficient
	// (empty when grading is off).
	Grade            string   `json:"grade,omitempty"`
	Retried          bool     `json:"retried"`
	TotalMatches     int      `json:"totalMatches"`
	FilterRelaxed    bool     `json:"filterRelaxed"`
	TruncatedByToken bool     `json:"truncatedByTokens"`
	Warnings         []string `json:"warnings,omitempty"`
}

// AskAnswer is the result of ask_knowledge (retrieval + grounded synthesis).
type AskAnswer struct {
	Answer    string     `json:"answer,omitempty"`
	Citations []Citation `json:"citations,omitempty"`
	Model     string     `json:"model,omitempty"`
	Generated bool       `json:"generated"`
	// InsufficientEvidence is true when the retriever graded evidence
	// insufficient and abstained.
	InsufficientEvidence bool   `json:"insufficientEvidence"`
	RetrievalGrade       string `json:"retrievalGrade,omitempty"`
	Retried              bool   `json:"retried"`
	Cached               bool   `json:"cached"`
	TruncatedByTokens    bool   `json:"truncatedByTokens"`
}

// AgentStep is one tool-call step taken by the agent loop.
type AgentStep struct {
	Iteration   int     `json:"iteration"`
	Tool        string  `json:"tool"`
	ArgsSummary string  `json:"argsSummary"`
	IsError     bool    `json:"isError"`
	ElapsedMs   float64 `json:"elapsedMs"`
}

// AgentResult is the result of agent_chat — the completed (or HITL-paused) run.
type AgentResult struct {
	Answer    string      `json:"answer"`
	Steps     []AgentStep `json:"steps,omitempty"`
	ToolCalls []string    `json:"toolCalls,omitempty"`
	Iterations int        `json:"iterations"`
	LatencyMs  float64    `json:"latencyMs"`
	LimitReached bool     `json:"limitReached"`
	// AwaitingApprovalID is set when the run paused for human approval of a
	// write tool.
	AwaitingApprovalID string `json:"awaitingApprovalId,omitempty"`
	PendingTool        string `json:"pendingTool,omitempty"`
	ThreadID           string `json:"threadId,omitempty"`
}

// StreamEvent is one Server-Sent-Events frame from /api/ask/stream or
// /api/agent/stream. Type is one of meta | token | tool_start | tool_end |
// awaiting_approval | abstain | done | error.
type StreamEvent struct {
	Type string         `json:"type"`
	Data map[string]any `json:"data"`
}

// AgentMessage is a chat message for the agent REST streaming endpoint.
type AgentMessage struct {
	Role    string `json:"role"`
	Content string `json:"content"`
}
