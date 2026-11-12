---
name: knowledge-hub-docs
license: MIT
description: Knowledge MCP Hub operational documentation for agents — MCP/REST endpoints, aft_* auth, tool catalog, transports, jobs and troubleshooting. Use when wiring the hub, calling tools, or debugging 401/409/session errors.
metadata:
  version: "1.0.0"
  visibility: public
  author: afonsoft
  url: https://github.com/afonsoft/KnowledgeRAG
generated-from:
  - CLAUDE.md
  - docs/en/API.md
---

# Knowledge MCP Hub — operational docs

> GENERATED FILE — edit the `skill-docs` sections in CLAUDE.md / docs/en/API.md
> and run `scripts/generate-skills.sh`. Direct edits are overwritten.

The hub is a single Kestrel process exposing the admin SPA, the REST API and
the native MCP server. Connect over Streamable HTTP at `/mcp` (preferred) or
legacy SSE at `/mcp/sse`; agents authenticate with `Authorization: Bearer aft_*`
(API keys are created in the hub UI under API Keys — never commit one).

O Knowledge MCP Hub disponibiliza um servidor MCP nativo para agentes de IA e clientes externos:

- **Endpoints de Transporte**:
  - Streamable HTTP (especificação `2026-07-28` / C# SDK 2.2): `/mcp`
  - SSE legado: `/mcp/sse`
  - A2A v1.0 (agent-to-agent): Agent Card anônimo em `/.well-known/agent-card.json`; execução em `/a2a` (JSON-RPC 2.0 e HTTP+JSON `/a2a/message:send`). Skills expostas: `ask_knowledge` (default), `search_knowledge`, `agent_chat`, `read_document` — selecionadas via metadata `{"skill": "<nome>"}` na message. Reutiliza o mesmo Bearer `aft_*` e o escopo da chave (tools/fontes/rate limit).
- **Autenticação**:
  - Header HTTP `Authorization: Bearer aft_SUA_CHAVE` (gerada na tela `/api-keys`).
  - Clients SSE que não enviam headers customizados aceitam query string: `/mcp/sse?access_token=aft_SUA_CHAVE`.

### Catálogo de Ferramentas Principais

1. **RAG & Conhecimento**:
   - `search_knowledge(query, topK)`: Busca híbrida (FTS5 + SQLite-Vec/PgVector) com RRF e diversificação MMR. Use antes de implementar código para verificar especificações e convenções existentes.
   - `ask_knowledge(question, topK)`: Síntese de resposta fundamentada em citações de chunks.
   - `agent_chat(message, threadId)`: Loop de raciocínio reativo com chamadas encadeadas de tools.
   - `read_document(path)`: Lê o conteúdo integral de um documento cadastrado.
   - `write_knowledge(title, content)` / `write_note(title, content, path)`: Registra novos documentos ou notas no Obsidian vault conectado.
2. **GraphRAG & Relações**:
   - `find_dependencies(entity)` / `find_dependents(entity)`: Localiza dependências diretas e reversas no grafo de conhecimento.
   - `analyze_impact(entity)`: Avalia o impacto arquitetural e componentes afetados por mudanças em um nó.
3. **Configurações per-key**:
   - `set_chat_settings(endpoint, model, apiKey)`: Configura modelo LLM e endpoint específicos para a sessão da chave chamadora.
   - `set_api_key_settings(provider, apiKey)`: Configura chaves de integração upstream (`firecrawl`, `deepwiki`, `tavily`, `context7`) para a sessão da chave chamadora.
4. **Context snapshots** (SPEC-20261004): snapshots de contexto de trabalho versionados por repositório (`repo` obrigatório, `owner/name` ou URL do GitHub, normalizado lowercase). `context_save` persiste itens tipados (decision/note/passage/tool_output/summary, scan de secrets, idempotencyKey); `context_recovery` retorna o snapshot mais recente/versão/label já compactado ao `tokenBudget`; `context_compact` compacta itens ou um snapshot (elision determinística + summarization com fallback, nunca falha) e pode persistir via `save=true`. Espelho REST em `/api/context/*`. Nunca grava no vault nem em `ConversationMessage`.
5. **Memória persistente** (Epic E29 — SPECs 20261111): ciclo `memory_retain → memory_consolidate → memory_recall/memory_reflect` por `MemoryBank` (escopo via `/mcp/banks/{key}` ou header `X-Memory-Bank`). `memory_retain` (verbatim|extract, async via operations) grava fatos tipados; `memory_consolidate` refina fatos em observações com evidência; `memory_recall` busca híbrida 4 braços; `memory_reflect` responde com citações validadas sob diretivas do bank; `memory_mental_models_*`/`memory_pages_*` mantêm páginas auto-refresh projetadas no vault; `memory_directives_*` configuram o reflect. Epic E30 (SPEC-20261112): `memory_context` (digest determinístico de session-start), `memory_timeline` (buckets cronológicos), `memory_stats` (saúde por bank/agregado). Writes passam pelo secret-scan (`422 blocked` quando a política é `block`).
6. **Operações & documentos** (SPEC-20261111-mcp-ergonomics-wave2): `operations_list`/`operations_get`/`operations_cancel`/`operations_retry` cobrem jobs de ingestão e operações de memória async; `document_update`/`document_delete` completam o CRUD de documentos.

### Recomendações para Agentes

- **Grounding First**: Sempre consulte `search_knowledge` antes de supor regras de negócio ou duplicar classes utilitárias já existentes no repositório.
- **Onboarding do repositório**: Na primeira sessão, pergunte sobre o repositório com `ask_question` consultando a documentação via `read_wiki_contents` antes de implementar.
- **Registro de trabalho**: Sempre registre o que foi feito com `write_note`; quando precisar criar conhecimento ou memória persistente, use `write_knowledge`.
- **Análise de Impacto**: Execute `analyze_impact` antes de refatorar contratos ou tipos compartilhados em `KnowledgeHub.Shared`.
- **Segurança**: Nunca realize commit ou emita em logs chaves `aft_*`, credenciais ou segredos presentes em `.env`.

## REST API surface (excerpt)

Full route tables live in `docs/en/API.md`. Key groups:

All `/api/*` endpoints require authentication (cookie session or `Authorization: Bearer aft_...` API key) except `POST /api/auth/login` and `/health*`. Rate limits apply (`llm`/`sync`/`general` policies; 429 + `Retry-After` when exceeded).

### Auth & keys

| Route | Purpose |
|---|---|
| `POST /api/auth/login` | `{username, password}` → session cookie |
| `GET /api/auth/me` | current user + `mustChangePassword` |
| `POST /api/auth/change-password` | `{currentPassword, newPassword}` → 204 |
| `GET/POST /api/apikeys` · `DELETE /api/apikeys/{id}` | manage `aft_*` keys (cookie session only; secret shown at creation) |
| `PUT/DELETE /api/api-keys/{id}/scopes` | restrict key to allowed sources/tools |
| `PUT/DELETE /api/api-keys/{id}/rate-limit` | per-key LLM/sync rate-limit overrides |

### Ingestion jobs & memory operations

| Route | Purpose |
|---|---|
| `GET /api/ingestion/jobs?sourceId=&status=&limit=` | list jobs |
| `GET /api/ingestion/jobs/{id}` · `POST /api/ingestion/jobs/{id}/cancel` | job detail / cancel (`409` when running) |
| `GET/POST /api/memory/...` | memory banks, units, recall/reflect, consolidation, mental models & pages — mirror of the `memory_*` MCP tools |

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `401` on `/mcp` | missing/invalid `aft_*` key | `Authorization: Bearer aft_...` header (or `?access_token=` on `/mcp/sse`) |
| `tools/list` empty | key scope restricts tools | check key scopes in the hub UI |
| `409` on job cancel | job already running/terminal | poll `operations_get`, wait for finish |
| `notifications/initialized` → 400 | stateless session mode | omit the notification or use `Mcp:SessionMode=StatefulForInitializeClients` |
| `422 blocked` on writes | secret scan block policy | remove the credential — findings list pattern names only |
| SSE refused | server in `Stateless` mode | use Streamable HTTP `/mcp` |

