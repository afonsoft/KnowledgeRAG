# KnowledgeHub SDK — Go

Client SDK for the Knowledge MCP Hub: wraps the hub's native MCP endpoint
(Streamable HTTP, `/mcp`) via the official
[`mcp-go`](https://github.com/mark3labs/mcp-go) client, with a typed facade
plus a `langchain` subpackage that exposes the hub's dynamic tool catalog as
`tools.Tool` for [langchaingo](https://github.com/tmc/langchaingo).

## Install

```bash
go get github.com/afonsoft/KnowledgeRAG/sdks/go@latest
```

Module path: `github.com/afonsoft/KnowledgeRAG/sdks/go` — Go modules resolve
it straight from this repo (release tags `sdks/go/v*.*.*`).

## Usage

```go
hub, err := knowledgehub.New(ctx, "http://localhost:5009", "aft_...")
if err != nil { log.Fatal(err) }
defer hub.Close()

tools,  _ := hub.ListTools(ctx)                    // dynamic catalog
search, _ := hub.Search(ctx, "deployment options", 10)
answer, _ := hub.Ask(ctx, "how do I configure postgres?", 5)  // citations + grade
agent,  _ := hub.AgentChat(ctx, "summarize the runbook")      // agent loop

events, _ := hub.StreamAsk(ctx, "status?", 0, "")  // SSE token stream
for ev := range events {
    fmt.Println(ev.Type, ev.Data)
}
```

langchaingo:

```go
hubTools, err := langchain.AsTools(ctx, hub)       // []tools.Tool
agentExecutor := agents.NewExecutor(
    agents.NewOneShotAgent(llm, hubTools,
        agents.WithMaxIterations(5)),
    hubTools)
```

Notes:

- **Dynamic catalog** — `ListTools` is the source of truth;
  `read_document`/`write_note` exist only with an Obsidian source;
  upstream tools (deepwiki/tavily/…) only with configured integrations.
- **Errors** — remote JSON-RPC errors, `IsError` results and non-2xx SSE
  responses surface as `*knowledgehub.ToolError` (use `errors.As`).
- **Timeouts** — 5 min for REST streaming; MCP calls inherit ctx deadlines —
  agent loops can be slow, use a generous `context.WithTimeout`.

## Tests

`go test ./...` — unit tests always run; live E2E activates with
`KNOWLEDGEHUB_URL` + `KNOWLEDGEHUB_KEY` env vars.
