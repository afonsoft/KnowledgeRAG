# KnowledgeHub Client SDKs

Client libraries that wrap the hub's MCP server (Streamable HTTP, `/mcp`) so other LLM-powered systems can embed the hub's tools — `search_knowledge`, `ask_knowledge`, `agent_chat`, knowledge-graph, upstream providers — LangChain-style: a typed facade for the stable tools plus a live catalog that plugs straight into the LLM framework of each language.

| SDK | Package | LLM adapter | Status |
|-----|---------|-------------|--------|
| [.NET](dotnet/) | `KnowledgeHub.Sdk` (NuGet, net8.0) | `AIFunction` → Microsoft.Extensions.AI / Semantic Kernel | ✅ Phase 1 |
| [Python](python/) | `knowledgehub-sdk` (PyPI, ≥3.10) | `StructuredTool` → LangChain / LangGraph | ✅ Phase 1 |
| Java | `io.github.afonsoft:knowledgehub` (Maven) | `ToolSpecification`/`ToolExecutor` → LangChain4j | 🔜 Phase 2 |
| Go | `github.com/afonsoft/KnowledgeRAG/sdks/go` | `tools.Tool` → langchaingo | 🔜 Phase 2 |

## Design

- **No protocol reimplementation** — each SDK wraps the language's official MCP client SDK (`ModelContextProtocol` for .NET, `mcp` for Python, `io.modelcontextprotocol` for Java, `mcp-go` for Go). The hub is spec-compliant MCP, so everything rides on `tools/list` + `tools/call` over Streamable HTTP.
- **Auth** — `Authorization: Bearer aft_*` (create in `/api-keys`). The catalog is dynamic per key scope and registered sources — `list_tools` is the source of truth; `read_document`/`write_note` exist only with an Obsidian source, upstream tools only with configured integrations.
- **Facade** — typed methods for the stable tools with real models (citations, grades, agent steps, HITL pause) + `call_tool` escape hatch for the dynamic catalog.
- **Streaming** — `stream_ask` / `stream_agent` over the REST SSE endpoints (`/api/ask/stream`, `/api/agent/stream`): `meta`/`token`/`tool_start`/`tool_end`/`awaiting_approval`/`abstain`/`done`/`error`.
- **Errors** — JSON-RPC tool errors and `isError` results surface as `KnowledgeHubToolException` / `KnowledgeHubError`.

## Releasing

CI (`.github/workflows/sdk-ci.yml`) builds and tests each SDK on `sdks/**` changes.
Publishing runs on tags `sdk-v*.*.*` (`.github/workflows/sdk-release.yml`):
`git tag sdk-v0.1.0 && git push --tags` — NuGet push gated on `NUGET_API_KEY`, PyPI push on `PYPI_API_TOKEN` (same opt-in secret pattern as `DOCKERHUB_*`).

## Quickstart

```csharp
// .NET
await using var kh = await KnowledgeHubClient.ConnectAsync("http://localhost:5009", "aft_...");
var tools = await kh.AsAIToolsAsync();          // → ChatOptions.Tools
var answer = await kh.AskAsync("…");
```

```python
# Python
async with KnowledgeHubClient("http://localhost:5009", api_key="aft_...") as kh:
    tools = await kh.as_langchain_tools()        # → StructuredTool list
    answer = await kh.ask("…")
```
