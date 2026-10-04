# Home

**Route:** `/`

![Home](../../screenshots/home.png)

## How it works

Landing page (`Home.razor`) — the onboarding entry point for connecting an AI
agent to the hub:

- **MCP setup prompt card** — a ready-made prompt describing the hub's MCP
  server for an LLM agent; the copy button puts it on the clipboard so you can
  paste it into Claude/OpenCode/Cursor. The help text points to
  [`/api-keys`](api-keys.md) to mint an `aft_*` key for the `Authorization:
  Bearer` header.
- **Shortcut cards** — quick links to [sources](sources.md),
  [mcp-monitor](mcp-monitor.md) and [playground](playground.md).

Useful endpoints for the prompt: `/mcp` (Streamable HTTP) and `/mcp/sse`
(legacy SSE).
