package knowledgehub

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestUnwrapSSEData_Envelope(t *testing.T) {
	got := unwrapSSEData(`{"seq":3,"data":{"text":"hi"}}`)
	if got["text"] != "hi" {
		t.Fatalf("expected unwrapped payload, got %v", got)
	}
}

func TestUnwrapSSEData_BarePayload(t *testing.T) {
	got := unwrapSSEData(`{"text":"x"}`)
	if got["text"] != "x" {
		t.Fatalf("expected bare payload, got %v", got)
	}
}

func TestUnwrapSSEData_NotJSON(t *testing.T) {
	got := unwrapSSEData("not json")
	if got["raw"] != "not json" {
		t.Fatalf("expected raw fallback, got %v", got)
	}
}

func TestConvertStructured_SearchResult(t *testing.T) {
	structured := map[string]any{
		"results": []any{map[string]any{
			"chunkText":     "hello",
			"documentTitle": "doc",
			"sourceName":    "src",
			"sourceId":      "11111111-1111-1111-1111-111111111111",
			"score":         0.9,
			"uriReference":  "obsidian://x",
		}},
		"grade":        "sufficient",
		"totalMatches": 3,
	}
	var out SearchResult
	if err := convertStructured(structured, &out); err != nil {
		t.Fatal(err)
	}
	if len(out.Results) != 1 || out.Results[0].ChunkText != "hello" {
		t.Fatalf("unexpected parse: %+v", out)
	}
	if out.Grade != "sufficient" || out.TotalMatches != 3 {
		t.Fatalf("unexpected fields: %+v", out)
	}
}

func TestThrowIfError(t *testing.T) {
	err := throwIfError("read_document", &ToolResult{Text: "unknown tool", IsError: true})
	if err == nil {
		t.Fatal("expected error")
	}
	var te *ToolError
	if !asToolError(err, &te) || te.Tool != "read_document" {
		t.Fatalf("expected ToolError with tool name, got %v", err)
	}
}

func asToolError(err error, target **ToolError) bool {
	if e, ok := err.(*ToolError); ok {
		*target = e
		return true
	}
	return false
}

func TestStream_ParsesFrames(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/ask/stream" {
			t.Fatalf("unexpected path %s", r.URL.Path)
		}
		if r.Header.Get("Authorization") != "Bearer aft_x" {
			t.Fatal("missing auth header")
		}
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = w.Write([]byte("event: meta\ndata: {\"seq\":0,\"data\":{\"model\":\"m\"}}\n\n" +
			"event: token\ndata: {\"seq\":1,\"data\":{\"text\":\"hello\"}}\n\n" +
			"event: done\ndata: {\"seq\":2,\"data\":{\"answer\":\"hi\"}}\n\n"))
	}))
	defer server.Close()

	c := &Client{baseURL: server.URL, apiKey: "aft_x", http: server.Client()}
	events, err := c.StreamAsk(context.Background(), "q", 0, "")
	if err != nil {
		t.Fatal(err)
	}
	var got []StreamEvent
	for ev := range events {
		got = append(got, ev)
	}
	if len(got) != 3 {
		t.Fatalf("expected 3 events, got %d", len(got))
	}
	if got[0].Type != "meta" || got[0].Data["model"] != "m" {
		t.Fatalf("bad meta frame: %+v", got[0])
	}
	if got[2].Type != "done" || got[2].Data["answer"] != "hi" {
		t.Fatalf("bad done frame: %+v", got[2])
	}
}

func TestStream_Non2xxRaisesToolError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
	}))
	defer server.Close()

	c := &Client{baseURL: server.URL, apiKey: "aft_x", http: server.Client()}
	_, err := c.StreamAsk(context.Background(), "q", 0, "")
	if err == nil {
		t.Fatal("expected error")
	}
	if e, ok := err.(*ToolError); !ok || e.Message == "" {
		t.Fatalf("expected ToolError, got %v", err)
	}
}

func TestToolDescriptorJSON(t *testing.T) {
	d := ToolDescriptor{Name: "search_knowledge", Description: "x",
		InputSchema: map[string]any{"type": "object"}}
	b, _ := json.Marshal(d)
	if !json.Valid(b) {
		t.Fatal("invalid json")
	}
}
