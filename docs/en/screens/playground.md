# Playground

**Route:** `/playground`

![Playground](../../screenshots/playground.png)

## How it works

`Playground.razor` lets you invoke the live tool catalog directly — same tools
MCP clients see:

- **Tool picker** — the dynamic catalog (built-ins + flows + upstream proxies);
  write tools carry a `write` badge with a warning, read-only ones a
  `read-only` badge.
- **Argument form** — generated from the tool's `InputSchema`; required fields
  are marked, placeholders show examples, and "fill example" seeds a sane call.
- **Run** — executes the call and renders `structuredContent` plus the
  `citations` block (the chunks the answer used); failures surface the MCP
  error verbatim.
- **A2A task card** — sends a task to a hub skill (`ask_knowledge`,
  `search_knowledge`, `agent_chat`, `read_document`) through the `/a2a`
  endpoint and shows the task steps/state.
