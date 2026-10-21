# SPEC-20261021-model-capability-profiles

| Campo | Valor |
|---|---|
| Status | In implementation |
| Ticket | [#594](https://github.com/afonsoft/KnowledgeRAG/issues/594) |
| Escopo | Store de capacidades por modelo (`ModelProfiles`) — context window, tool support, structured output, reasoning — para dirigir budgets e validação, inspirado no `langchain-model-profiles` |

## Contexto técnico

Chat settings hoje são strings livres: `ChatSettings.Endpoint`/`Model` (global) e `ApiKeyChatSettings` (per-key). Nada sabe que `gpt-4o` aceita tools+structured output e `llama3.2:1b` não — `AgentOptions.MaxIterations`, `ContextManagement.MaxTotalHistoryBytes` são fixos para qualquer modelo.

Arquivos-chave: `src/KnowledgeHub.Server/Domain/Entities/ChatSettings.cs`, `ApiKeyChatSettings.cs`, `src/KnowledgeHub.Server/Agent/AgentOptions.cs`, `src/KnowledgeHub.Server/Services/AgentService.cs` (chat options build), `src/KnowledgeHub.Server/Api/` (settings endpoints), DbContext + migrations EF Core (SQLite + Postgres — guard de paridade).

## Requisitos

- **RF-1 — Entidade `ModelProfile`.** `{ ModelPattern (ex: "gpt-4o*", "llama3*"), ContextWindowTokens, MaxOutputTokens?, SupportsToolCalls, SupportsStructuredOutput, SupportsReasoning, Source (builtin|user), UpdatedAt }`. Tabela EF Core nas duas migrations (SQLite + Postgres).
- **RF-2 — Catálogo builtin.** Seed em código com perfis conhecidos: `gpt-4o*` (128k, tools, structured, no reasoning), `gpt-5*`/`o1*`/`o3*` (tools, reasoning), `claude-*` (200k, tools), `llama3*`/`mistral*`/`qwen*` (Ollama comuns), `nomic-embed*`/`all-minilm*` (embedding — sem tool/reasoning). Pattern matching por prefixo/`fnmatch` simples, mais específico ganha.
- **RF-3 — API.** `GET /api/settings/model-profiles` (lista merged builtin+user), `PUT /api/settings/model-profiles/{pattern}` (override user), `DELETE` (remove user override). Cookie admin only.
- **RF-4 — Consumo no agente.** Quando o chat client é criado para um model com profile: `SupportsToolCalls=false` → request falha com erro claro antes do loop; `ContextWindowTokens` alimenta o budget de compaction (`MaxBodyPairBytes`/`MaxTotalHistoryBytes` viram cap calculado = min(config, profile-derived)) — opt-in via `Agent:UseModelProfiles` (default false para não mudar comportamento).
- **RF-5 — Consumo em structured output/streaming.** `SupportsStructuredOutput`/`SupportsReasoning` expostos no response de `GET` e consultáveis por features futuras (SPEC-20261021-agent-structured-output pode consultar).
- **RF-6 — Testes.** Unit: match de pattern (exato > prefixo > catch-all), merge builtin/user, budget derivado com cap. Integration: endpoints + flag off = comportamento atual idêntico.

## Fora de escopo

Descoberta automática de capacidades via provider API (Ollama `/api/show`, OpenAI model list), UI de edição de profiles, custo/pricing por modelo.

## Validação

`dotnet test` + `dotnet ef migrations script` para ambos providers; flag default-off garante zero mudança observável.
