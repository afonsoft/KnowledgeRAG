package knowledgehub

import "fmt"

// ToolError is raised when a hub tool call fails — either a remote JSON-RPC
// error (unknown tool, invalid arguments) or a tool result with
// IsError: true, or a non-2xx response from a REST streaming endpoint.
type ToolError struct {
	Tool    string
	Message string
	Err     error
}

func (e *ToolError) Error() string {
	if e.Tool != "" {
		return fmt.Sprintf("tool %q failed: %s", e.Tool, e.Message)
	}
	return e.Message
}

func (e *ToolError) Unwrap() error { return e.Err }
