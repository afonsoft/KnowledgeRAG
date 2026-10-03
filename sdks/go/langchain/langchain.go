// Package langchain adapts the KnowledgeHub MCP tool catalog to
// langchaingo's tools.Tool interface, so agents built on langchaingo can
// call the hub's tools natively (search_knowledge, ask_knowledge,
// agent_chat, etc.).
//
// Usage:
//
//	hub, _ := knowledgehub.New(ctx, "http://localhost:5009", "aft_...")
//	hubTools, _ := langchain.AsTools(ctx, hub)
//	agentExecutor := agents.NewExecutor(
//	    agents.NewOneShotAgent(llm, hubTools, ...), hubTools)
package langchain

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/tmc/langchaingo/tools"

	knowledgehub "github.com/afonsoft/KnowledgeRAG/sdks/go"
)

var _ tools.Tool = (*Tool)(nil)

// Tool is a langchaingo tools.Tool backed by a KnowledgeHub MCP tool.
type Tool struct {
	client      *knowledgehub.Client
	name        string
	description string
}

// AsTools maps every tool in the hub's dynamic catalog (for this API key)
// to a langchaingo tool that routes through tools/call.
func AsTools(ctx context.Context, client *knowledgehub.Client) ([]tools.Tool, error) {
	descriptors, err := client.ListTools(ctx)
	if err != nil {
		return nil, err
	}
	out := make([]tools.Tool, 0, len(descriptors))
	for _, d := range descriptors {
		out = append(out, &Tool{
			client:      client,
			name:        d.Name,
			description: buildDescription(d),
		})
	}
	return out, nil
}

func (t *Tool) Name() string { return t.name }

func (t *Tool) Description() string { return t.description }

// Call executes the hub tool. langchaingo passes the LLM's arguments as a
// JSON string.
func (t *Tool) Call(ctx context.Context, input string) (string, error) {
	args := map[string]any{}
	if input != "" {
		if err := json.Unmarshal([]byte(input), &args); err != nil {
			return "", fmt.Errorf("invalid tool arguments %q: %w", input, err)
		}
	}
	result, err := t.client.CallTool(ctx, t.name, args)
	if err != nil {
		return "", err
	}
	if result.IsError {
		return "tool error: " + result.Text, nil
	}
	return result.Text, nil
}

// buildDescription appends a compact argument summary to the tool
// description so the LLM knows what to pass (langchaingo's Tool interface
// has no schema field).
func buildDescription(d knowledgehub.ToolDescriptor) string {
	desc := d.Description
	props, _ := d.InputSchema["properties"].(map[string]any)
	if len(props) == 0 {
		return desc
	}
	required := requiredSet(d.InputSchema)
	names := make([]string, 0, len(props))
	for name, spec := range props {
		names = append(names, argSummary(name, propType(spec), required[name]))
	}
	return desc + " Arguments (JSON object): " + joinComma(names) + "."
}

func requiredSet(schema map[string]any) map[string]bool {
	required := map[string]bool{}
	req, _ := schema["required"].([]any)
	for _, r := range req {
		if s, ok := r.(string); ok {
			required[s] = true
		}
	}
	return required
}

// propType resolves a JSON-schema property spec to a display type — the
// first non-null member for union types like ["string","null"].
func propType(spec any) string {
	m, ok := spec.(map[string]any)
	if !ok {
		return "any"
	}
	switch t := m["type"].(type) {
	case string:
		return t
	case []any:
		for _, candidate := range t {
			if s, ok := candidate.(string); ok && s != "null" {
				return s
			}
		}
	}
	return "any"
}

func argSummary(name, typ string, required bool) string {
	if required {
		return name + " (" + typ + ", required)"
	}
	return name + " (" + typ + ")"
}

func joinComma(parts []string) string {
	out := ""
	for i, p := range parts {
		if i > 0 {
			out += ", "
		}
		out += p
	}
	return out
}
