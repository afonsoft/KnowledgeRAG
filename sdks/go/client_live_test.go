package knowledgehub_test

import (
	"context"
	"os"
	"testing"
	"time"

	knowledgehub "github.com/afonsoft/KnowledgeRAG/sdks/go"
	"github.com/afonsoft/KnowledgeRAG/sdks/go/langchain"
)

// Live E2E against a running hub. Enable with:
//
//	KNOWLEDGEHUB_URL=http://localhost:5009 KNOWLEDGEHUB_KEY=aft_... go test ./...
func liveClient(t *testing.T) *knowledgehub.Client {
	t.Helper()
	url, key := os.Getenv("KNOWLEDGEHUB_URL"), os.Getenv("KNOWLEDGEHUB_KEY")
	if url == "" || key == "" {
		t.Skip("KNOWLEDGEHUB_URL/KNOWLEDGEHUB_KEY not set")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	t.Cleanup(cancel)
	c, err := knowledgehub.New(ctx, url, key)
	if err != nil {
		t.Fatalf("connect: %v", err)
	}
	t.Cleanup(func() { _ = c.Close() })
	return c
}

func TestLive_ListTools(t *testing.T) {
	ctx := context.Background()
	c := liveClient(t)
	tools, err := c.ListTools(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if len(tools) == 0 {
		t.Fatal("empty catalog")
	}
	found := false
	for _, d := range tools {
		if d.Name == "search_knowledge" {
			found = true
		}
	}
	if !found {
		t.Fatal("search_knowledge missing from catalog")
	}
}

func TestLive_CallTool_UnknownRaisesCleanError(t *testing.T) {
	ctx := context.Background()
	c := liveClient(t)
	_, err := c.CallTool(ctx, "no_such_tool_xyz", nil)
	if err == nil {
		t.Fatal("expected error")
	}
	if e, ok := err.(*knowledgehub.ToolError); !ok || e.Tool != "no_such_tool_xyz" {
		t.Fatalf("expected ToolError with tool name, got %v", err)
	}
}

func TestLive_SearchAndAsk(t *testing.T) {
	ctx := context.Background()
	c := liveClient(t)
	sr, err := c.Search(ctx, "knowledge hub", 5)
	if err != nil {
		t.Fatal(err)
	}
	if sr.Results == nil {
		t.Fatal("nil results")
	}
	ans, err := c.Ask(ctx, "what is this knowledge base about?", 5)
	if err != nil {
		t.Fatal(err)
	}
	if ans.Answer == "" {
		t.Fatal("empty answer")
	}
}

func TestLive_LangchainTools(t *testing.T) {
	ctx := context.Background()
	c := liveClient(t)
	tools, err := langchain.AsTools(ctx, c)
	if err != nil {
		t.Fatal(err)
	}
	if len(tools) == 0 {
		t.Fatal("no tools")
	}
	out, err := tools[0].Call(ctx, `{"query":"knowledge"}`)
	if err != nil {
		t.Fatal(err)
	}
	_ = out
}

func TestLive_StreamAsk(t *testing.T) {
	ctx := context.Background()
	c := liveClient(t)
	events, err := c.StreamAsk(ctx, "what is this about?", 0, "")
	if err != nil {
		// A hub without a configured chat provider answers 400 — the SDK must
		// surface that as a clean ToolError (which is itself the contract).
		var te *knowledgehub.ToolError
		if e, ok := err.(*knowledgehub.ToolError); ok {
			te = e
		}
		if te == nil || te.Message == "" {
			t.Fatalf("expected clean ToolError, got %v", err)
		}
		return
	}
	var last string
	count := 0
	for ev := range events {
		last = ev.Type
		count++
	}
	if count == 0 || last != "done" {
		t.Fatalf("expected done as last event, got %d events, last=%q", count, last)
	}
}
