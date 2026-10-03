// Package knowledgehub is the Go client SDK for the Knowledge MCP Hub.
//
// It wraps the hub's native MCP endpoint (Streamable HTTP, JSON-RPC 2.0) via
// the official mcp-go client, adds a typed facade over the stable tools
// (search_knowledge, ask_knowledge, agent_chat, read_document,
// write_knowledge, write_note, per-key settings) and REST SSE streaming for
// token-level output. The langchain subpackage adapts the dynamic tool
// catalog to langchaingo's tools.Tool interface.
//
// Usage:
//
//	hub, err := knowledgehub.New(ctx, "http://localhost:5009", "aft_...")
//	answer, err := hub.Ask(ctx, "what does the spec say about retries?")
//
// Generate an aft_* key on the hub's /api-keys page. The tool catalog is
// dynamic per key scope and registered sources — ListTools is the source of
// truth; read_document / write_note only exist when an Obsidian source is
// registered.
package knowledgehub

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"

	mcpclient "github.com/mark3labs/mcp-go/client"
	"github.com/mark3labs/mcp-go/client/transport"
	"github.com/mark3labs/mcp-go/mcp"
)

const (
	defaultMCPPath  = "/mcp"
	defaultTimeout  = 5 * time.Minute // agent loops can be slow
	sdkVersion      = "0.1.0"
	sdkClientName   = "knowledgehub-go-sdk"
	maxSSEFrameSize = 4 * 1024 * 1024
)

// Client is a Knowledge MCP Hub client.
type Client struct {
	baseURL string
	apiKey  string
	mcp     *mcpclient.Client
	http    *http.Client
}

// New connects to the hub and performs the MCP initialize handshake.
func New(ctx context.Context, baseURL, apiKey string) (*Client, error) {
	if strings.TrimSpace(baseURL) == "" {
		return nil, fmt.Errorf("knowledgehub: baseURL is required")
	}
	if strings.TrimSpace(apiKey) == "" {
		return nil, fmt.Errorf("knowledgehub: apiKey is required (aft_* from /api-keys)")
	}
	baseURL = strings.TrimSuffix(baseURL, "/")

	tr, err := transport.NewStreamableHTTP(baseURL+defaultMCPPath,
		transport.WithHTTPHeaders(map[string]string{
			"Authorization": "Bearer " + apiKey,
		}),
	)
	if err != nil {
		return nil, fmt.Errorf("knowledgehub: invalid MCP endpoint: %w", err)
	}

	mcpClient := mcpclient.NewClient(tr)
	if err := mcpClient.Start(ctx); err != nil {
		return nil, fmt.Errorf("knowledgehub: transport start failed: %w", err)
	}

	_, err = mcpClient.Initialize(ctx, mcp.InitializeRequest{
		Params: mcp.InitializeParams{
			ProtocolVersion: mcp.LATEST_PROTOCOL_VERSION,
			Capabilities:    mcp.ClientCapabilities{},
			ClientInfo:      mcp.Implementation{Name: sdkClientName, Version: sdkVersion},
		},
	})
	if err != nil {
		_ = mcpClient.Close()
		return nil, fmt.Errorf("knowledgehub: MCP initialize failed: %w", err)
	}

	return &Client{
		baseURL: baseURL,
		apiKey:  apiKey,
		mcp:     mcpClient,
		http:    &http.Client{Timeout: defaultTimeout},
	}, nil
}

// Close releases the MCP session.
func (c *Client) Close() error { return c.mcp.Close() }

// RawSession returns the underlying mcp-go client — escape hatch for
// resources, prompts and notifications.
func (c *Client) RawSession() *mcpclient.Client { return c.mcp }

// ListTools returns the full tool catalog for this key (dynamic per
// scope/sources).
func (c *Client) ListTools(ctx context.Context) ([]ToolDescriptor, error) {
	res, err := c.mcp.ListTools(ctx, mcp.ListToolsRequest{})
	if err != nil {
		return nil, &ToolError{Tool: "tools/list", Message: rpcMessage(err), Err: err}
	}
	out := make([]ToolDescriptor, 0, len(res.Tools))
	for _, t := range res.Tools {
		var schema map[string]any
		if raw := t.RawInputSchema; len(raw) > 0 {
			_ = json.Unmarshal(raw, &schema)
		} else if b, err := json.Marshal(t.InputSchema); err == nil {
			_ = json.Unmarshal(b, &schema)
		}
		out = append(out, ToolDescriptor{
			Name:        t.Name,
			Description: t.Description,
			InputSchema: schema,
		})
	}
	return out, nil
}

// CallTool invokes any tool, returning text + structuredContent.
func (c *Client) CallTool(ctx context.Context, name string, args map[string]any) (*ToolResult, error) {
	res, err := c.mcp.CallTool(ctx, mcp.CallToolRequest{
		Params: mcp.CallToolParams{Name: name, Arguments: args},
	})
	if err != nil {
		return nil, &ToolError{Tool: name, Message: rpcMessage(err), Err: err}
	}

	var b strings.Builder
	for _, content := range res.Content {
		if tc, ok := mcp.AsTextContent(content); ok {
			if b.Len() > 0 {
				b.WriteByte('\n')
			}
			b.WriteString(tc.Text)
		}
	}
	var structured map[string]any
	if m, ok := res.StructuredContent.(map[string]any); ok {
		structured = m
	}
	return &ToolResult{Text: b.String(), Structured: structured, IsError: res.IsError}, nil
}

// Search runs search_knowledge: hybrid retrieval (FTS5 + vector + graph arms
// fused by RRF) with corrective grading.
func (c *Client) Search(ctx context.Context, query string, topK int) (*SearchResult, error) {
	args := map[string]any{"query": query, "topK": topK}
	res, err := c.CallTool(ctx, "search_knowledge", args)
	if err != nil {
		return nil, err
	}
	if err := throwIfError("search_knowledge", res); err != nil {
		return nil, err
	}
	var out SearchResult
	if err := convertStructured(res.Structured, &out); err != nil {
		return &SearchResult{}, nil
	}
	return &out, nil
}

// SearchAdvanced is Search with the full argument set.
func (c *Client) SearchAdvanced(ctx context.Context, query string, topK int,
	sourceID, mode string) (*SearchResult, error) {
	args := map[string]any{"query": query, "topK": topK}
	if sourceID != "" {
		args["sourceId"] = sourceID
	}
	if mode != "" {
		args["mode"] = mode
	}
	res, err := c.CallTool(ctx, "search_knowledge", args)
	if err != nil {
		return nil, err
	}
	if err := throwIfError("search_knowledge", res); err != nil {
		return nil, err
	}
	var out SearchResult
	if err := convertStructured(res.Structured, &out); err != nil {
		return &SearchResult{}, nil
	}
	return &out, nil
}

// Ask runs ask_knowledge: retrieval + grounded synthesis with citations.
// Set generate=false to get raw retrieved context without synthesis.
func (c *Client) Ask(ctx context.Context, question string, topK int) (*AskAnswer, error) {
	return c.AskAdvanced(ctx, question, topK, "", "", nil)
}

// AskAdvanced is Ask with the full argument set.
func (c *Client) AskAdvanced(ctx context.Context, question string, topK int,
	sourceID, mode string, generate *bool) (*AskAnswer, error) {
	args := map[string]any{"question": question, "topK": topK}
	if sourceID != "" {
		args["sourceId"] = sourceID
	}
	if mode != "" {
		args["mode"] = mode
	}
	if generate != nil {
		args["generate"] = *generate
	}
	res, err := c.CallTool(ctx, "ask_knowledge", args)
	if err != nil {
		return nil, err
	}
	if err := throwIfError("ask_knowledge", res); err != nil {
		return nil, err
	}
	if res.Structured == nil {
		// Raw-context fallback (generate=false or no provider configured).
		return &AskAnswer{Answer: res.Text, Generated: false}, nil
	}
	var out AskAnswer
	if err := convertStructured(res.Structured, &out); err != nil {
		return &AskAnswer{Answer: res.Text, Generated: false}, nil
	}
	return &out, nil
}

// AgentChat runs agent_chat: the tool-calling agent loop. May pause for HITL
// approval — AwaitingApprovalID is then set on the result.
func (c *Client) AgentChat(ctx context.Context, prompt string) (*AgentResult, error) {
	return c.AgentChatAdvanced(ctx, prompt, nil, 0, false, "", false)
}

// AgentChatAdvanced is AgentChat with the full argument set.
func (c *Client) AgentChatAdvanced(ctx context.Context, prompt string, tools []string,
	maxIterations int, allowWrite bool, threadID string, persist bool) (*AgentResult, error) {
	args := map[string]any{"prompt": prompt}
	if len(tools) > 0 {
		args["tools"] = tools
	}
	if maxIterations > 0 {
		args["maxIterations"] = maxIterations
	}
	if allowWrite {
		args["allowWrite"] = true
	}
	if threadID != "" {
		args["threadId"] = threadID
	}
	if persist {
		args["persist"] = true
	}
	res, err := c.CallTool(ctx, "agent_chat", args)
	if err != nil {
		return nil, err
	}
	if err := throwIfError("agent_chat", res); err != nil {
		return nil, err
	}
	var out AgentResult
	if err := convertStructured(res.Structured, &out); err != nil {
		return &AgentResult{Answer: res.Text}, nil
	}
	return &out, nil
}

// ReadDocument returns the full text of a document (a citation's Path or
// URI). Only in the catalog with an Obsidian source registered.
func (c *Client) ReadDocument(ctx context.Context, path string) (string, error) {
	res, err := c.CallTool(ctx, "read_document", map[string]any{"path": path})
	if err != nil {
		return "", err
	}
	if err := throwIfError("read_document", res); err != nil {
		return "", err
	}
	return res.Text, nil
}

// WriteKnowledge registers a new document. Requires a key with write scope;
// may trigger an HITL approval.
func (c *Client) WriteKnowledge(ctx context.Context, title, content string) (*ToolResult, error) {
	return c.CallTool(ctx, "write_knowledge",
		map[string]any{"title": title, "content": content})
}

// WriteNote appends a note into the vault at an optional path.
func (c *Client) WriteNote(ctx context.Context, title, content, path string) (*ToolResult, error) {
	args := map[string]any{"title": title, "content": content}
	if path != "" {
		args["path"] = path
	}
	return c.CallTool(ctx, "write_note", args)
}

// SetChatSettings pins a chat LLM endpoint/model for this key's sessions.
func (c *Client) SetChatSettings(ctx context.Context, endpoint, model, apiKey string) (*ToolResult, error) {
	args := map[string]any{}
	if endpoint != "" {
		args["endpoint"] = endpoint
	}
	if model != "" {
		args["model"] = model
	}
	if apiKey != "" {
		args["apiKey"] = apiKey
	}
	return c.CallTool(ctx, "set_chat_settings", args)
}

// SetAPIKeySettings configures an upstream integration key
// (firecrawl|deepwiki|tavily|context7) for this key's sessions.
func (c *Client) SetAPIKeySettings(ctx context.Context, provider, apiKey string) (*ToolResult, error) {
	return c.CallTool(ctx, "set_api_key_settings",
		map[string]any{"provider": provider, "apiKey": apiKey})
}

// StreamAsk streams POST /api/ask/stream — meta/token/abstain/done/error
// events. Returns a channel closed when the stream ends.
func (c *Client) StreamAsk(ctx context.Context, question string, topK int,
	mode string) (<-chan StreamEvent, error) {
	body := map[string]any{"question": question}
	if topK > 0 {
		body["topK"] = topK
	}
	if mode != "" {
		body["mode"] = mode
	}
	return c.stream(ctx, "/api/ask/stream", body)
}

// StreamAgent streams POST /api/agent/stream — meta/token/tool_start/
// tool_end/awaiting_approval/done/error events.
func (c *Client) StreamAgent(ctx context.Context, prompt string, messages []AgentMessage,
	maxIterations int, allowWrite bool) (<-chan StreamEvent, error) {
	body := map[string]any{"prompt": prompt}
	if len(messages) > 0 {
		body["messages"] = messages
	}
	if maxIterations > 0 {
		body["maxIterations"] = maxIterations
	}
	if allowWrite {
		body["allowWrite"] = true
	}
	return c.stream(ctx, "/api/agent/stream", body)
}

func (c *Client) stream(ctx context.Context, path string, body map[string]any) (<-chan StreamEvent, error) {
	payload, err := json.Marshal(body)
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.baseURL+path,
		bytes.NewReader(payload))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+c.apiKey)
	req.Header.Set("Accept", "text/event-stream")
	req.Header.Set("Content-Type", "application/json")

	resp, err := c.http.Do(req)
	if err != nil {
		return nil, &ToolError{Message: fmt.Sprintf("%s request failed: %v", path, err), Err: err}
	}
	if resp.StatusCode >= 400 {
		defer resp.Body.Close()
		errBody, _ := io.ReadAll(io.LimitReader(resp.Body, 64*1024))
		return nil, &ToolError{Message: fmt.Sprintf("%s returned %d: %s",
			path, resp.StatusCode, strings.TrimSpace(string(errBody)))}
	}

	out := make(chan StreamEvent, 8)
	go func() {
		defer close(out)
		defer resp.Body.Close()
		event, data := "message", strings.Builder{}
		scanner := bufio.NewScanner(resp.Body)
		scanner.Buffer(make([]byte, 64*1024), maxSSEFrameSize)
		emit := func() {
			if data.Len() == 0 {
				return
			}
			payload := unwrapSSEData(data.String())
			select {
			case out <- StreamEvent{Type: event, Data: payload}:
			case <-ctx.Done():
			}
		}
		for scanner.Scan() {
			line := scanner.Text()
			switch {
			case line == "":
				emit()
				event, data = "message", strings.Builder{}
			case strings.HasPrefix(line, "event:"):
				event = strings.TrimSpace(strings.TrimPrefix(line, "event:"))
			case strings.HasPrefix(line, "data:"):
				if data.Len() > 0 {
					data.WriteByte('\n')
				}
				data.WriteString(strings.TrimSpace(strings.TrimPrefix(line, "data:")))
			}
		}
		emit()
	}()
	return out, nil
}

// unwrapSSEData unwraps {"seq": n, "data": {...}} envelopes into the payload.
func unwrapSSEData(raw string) map[string]any {
	var parsed map[string]any
	if err := json.Unmarshal([]byte(raw), &parsed); err != nil {
		return map[string]any{"raw": raw}
	}
	if _, hasSeq := parsed["seq"]; hasSeq {
		if inner, ok := parsed["data"].(map[string]any); ok {
			return inner
		}
	}
	return parsed
}

func throwIfError(tool string, res *ToolResult) error {
	if res.IsError {
		return &ToolError{Tool: tool, Message: res.Text}
	}
	return nil
}

func convertStructured(structured map[string]any, target any) error {
	if structured == nil {
		return fmt.Errorf("no structuredContent")
	}
	b, err := json.Marshal(structured)
	if err != nil {
		return err
	}
	return json.Unmarshal(b, target)
}

func rpcMessage(err error) string {
	if err == nil {
		return ""
	}
	return err.Error()
}
