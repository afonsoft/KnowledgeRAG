# MCP monitor

**Route:** `/mcp-monitor`

![MCP monitor](../../screenshots/monitor.png)

## How it works

`McpMonitor.razor` is a live console over the native MCP server — events arrive
via SignalR, no refresh needed:

- **Sessions card** — active MCP/A2A client sessions; empty state shows a
  connect hint with the `/mcp` endpoint.
- **Activity stream** — every JSON-RPC message (tool calls, results, errors)
  with timestamp, kind, session and outcome badges.
- **Filters** — kind (tools/list, tools/call, …), outcome (all/ok/errors),
  free-text search, "since" window; non-matching events are hidden client-side.
- **Stats** — average latency and top tools cards; error health badge.
- **Actions** — reconnect when the SignalR link drops, and **Export CSV** to
  dump the filtered activity for analysis.
