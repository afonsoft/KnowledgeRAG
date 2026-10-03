# Proposta — Client SDKs KnowledgeHub (Java, .NET, Python, Go)

## Premissa

O hub já expõe um servidor MCP **spec-compliant** (`/mcp` Streamable HTTP, `/mcp/sse` legado, `tools/list` + `tools/call`, `structuredContent` com `outputSchema`, extensão de tasks para tools longas, elicitation para aprovações HITL) e A2A v1.0 (`/.well-known/agent-card.json` + `/a2a`). Auth: `Authorization: Bearer aft_*` (ou `?access_token=` no SSE).

Os SDKs **não reimplementam o protocolo** — cada um embrulha o client MCP oficial da linguagem e adiciona:

1. **Conexão** — Streamable HTTP com Bearer `aft_*`, gerenciamento de sessão (`Mcp-Session-Id`), reconexão.
2. **Core client** — `listTools()`, `callTool(name, args)` → resultado normalizado `{ text, structured: JsonObject }`; polling de tasks para tools task-eligible (`firecrawl_*`, `tavily_*`); handlers de notificações.
3. **Fachada tipada** — wrappers assinados para o conjunto estável: `search_knowledge`, `ask_knowledge`, `agent_chat`, `read_document`, `write_knowledge`, `write_note`, `find_dependencies`, `find_dependents`, `analyze_impact`, `search_graph_*`, `set_chat_settings`, `set_api_key_settings`. O catálogo completo continua descobrível via `listTools` (dinâmico por chave).
4. **Adaptadores de framework LLM** — cada SDK expõe `AsTools()` que mapeia o catálogo MCP para o modelo de tools do framework local.

## Por SDK

### .NET — `KnowledgeHub.ClientSdk` (NuGet)
- Dentro do repo, referencia `KnowledgeHub.Shared` (DTOs já existem — zero re-serialização de contratos) + `ModelContextProtocol` client oficial.
- `KnowledgeHubClient.ConnectAsync(url, apiKey)`; `SearchAsync`, `AskAsync`, `AgentChatAsync` retornando `SearchResult`/`AskAnswer` tipados.
- `client.AsAITools()` → `IReadOnlyList<AIFunction>` (Microsoft.Extensions.AI) — pluga direto em `IChatClient` **e** em Semantic Kernel (`KernelFunction` via `AIFunction` adapter).
- Streaming: `IAsyncEnumerable<AgentChatDelta>` via SSE REST `/api/agent` (streaming token-a-token já existe no server).

### Python — `knowledgehub` (PyPI)
- Embrulha `mcp` SDK oficial (`streamablehttp_client`); sync + async.
- `KnowledgeHubClient("http://host:5009", api_key="aft_...")`.
- `client.as_langchain_tools()` → `list[StructuredTool]` prontos para LangChain/LangGraph agents (equivalente ao `langchain-mcp-adapters`, mas com os helpers tipados em cima).
- `client.ask(question)` → `AskAnswer` pydantic com `answer`, `citations`, `abstained`, `grade`.

### Java — `io.github.afonsoft:knowledgerag-client` (Maven Central)
- Embrulha `io.modelcontextprotocol.sdk:mcp` (client Java oficial, mantido pela Anthropic/Spring).
- `client.asLangChain4jTools()` → `List<ToolSpecification>` + `ToolExecutor` para LangChain4j.
- API fluent: `kh.ask("...")` → `AskAnswer` records.

### Go — `github.com/afonsoft/KnowledgeRAG/sdks/go` (Go module)
- Embrulha `mark3labs/mcp-go` (client Go mais usado).
- `kh := knowledgerag.NewClient(url, knowledgerag.WithAPIKey(key))`; métodos tipados; `Tools()` retorna catálogo para mapear em langchaingo/outros frameworks.

## Layout no repositório

```
sdks/
├── dotnet/KnowledgeHub.ClientSdk/      # csproj → adicionado na slnx
│   └── tests em tests/KnowledgeHub.Tests.Unit/Sdk/
├── python/knowledgehub/                # pyproject + src layout
├── java/knowledgerag-client/           # mvn pom.xml
└── go/knowledgerag/                    # go.mod
```

Trade-off decidido: **monorepo** — versionamento alinhado ao servidor, contracts .NET reutilizados, release tag-only já existente. Alternativa (repos separados) só faz sentido se o ritmo de release divergir.

## O que é possível / limites honestos

| Capacidade | Status |
|---|---|
| Tools de leitura (search/ask/read/graph) | Pleno — `callTool` + `structuredContent` |
| `agent_chat` | Pleno (longa duração → task-eligible via extensão MCP tasks; SDK faz poll transparente) |
| Streaming token-a-token | Via REST SSE `/api/agent` (MCP não streama texto, só progress) — SDK expõe os dois modos |
| Write tools (write_knowledge/write_note) | Pleno **mas** requer escopo de escrita na chave e pode pausar em aprovação HITL — SDK expõe `needsApproval` e o fluxo de elicitation |
| Catálogo dinâmico (McpProxy/upstream) | Via `listTools` — fachada tipada só cobre o núcleo estável |
| A2A | Opcional — SDKs focam MCP; um `A2aClient` fino pode vir depois (a card já é estável) |
| Publish automático NuGet/PyPI/Maven/Go | **Bloqueado**: `.github/workflows/` protegido — releases ficam manuais (`dotnet pack`, `twine`, `mvn deploy`) ou precisam de autorização sua pra criar workflow |

## Plano de execução sugerido

- **Fase 1 (este PR):** SDK .NET completo (cliente + facade tipada + `AsAITools()` + SSE streaming + testes de integração contra TestServer) + SDK Python (cliente async/sync + pydantic models + adaptador LangChain + testes com servidor fake). São os dois de maior reuso imediato.
- **Fase 2 (PR seguinte):** Java (LangChain4j) + Go (mcp-go), mesma fachada.
- **Docs:** `sdks/README.md` + seção "Client SDKs" nos dois READMEs com snippets por linguagem.
