# Screens

One page per screen of the Blazor WebAssembly admin SPA. Each doc has the route,
a screenshot (`docs/screenshots/`), and how the screen works.

| Screen | Route | What it's for |
|---|---|---|
| [Home](home.md) | `/` | Landing: MCP setup prompt + shortcuts |
| [Knowledge sources](sources.md) | `/sources` | Register and sync knowledge sources |
| [MCP monitor](mcp-monitor.md) | `/mcp-monitor` | Live JSON-RPC traffic and health |
| [Approvals](approvals.md) | `/approvals` | Human-in-the-loop tool approvals |
| [Chat](chat.md) | `/chat` | Agent threads with tool calling |
| [Playground](playground.md) | `/playground` | Call catalog tools ad-hoc |
| [Knowledge graph](graph.md) | `/graph` | Browse GraphRAG entities and edges |
| [Eval](eval.md) | `/eval` | Retrieval eval runs and baselines |
| [RAG quality](rag-quality.md) | `/rag-quality` | Answer-quality triad dashboard |
| [Flows](flows.md) | `/flows` | Agent flow canvas editor, runs, triggers |
| [API keys](api-keys.md) | `/api-keys` | `aft_*` keys and per-key overrides |
| [Settings](settings.md) | `/settings` | LLM providers, resilience, integrations |

Auth: everything except `/login` requires the cookie session (default seeded
`admin` user) — `/login` → first login forces a password change.
