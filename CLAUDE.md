# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Estado Atual

Este repositório contém o **Knowledge MCP Hub** — plataforma standalone .NET 10: SPA Blazor WebAssembly + REST management API + MCP server nativo (Streamable HTTP híbrido `StatefulForInitializeClients` — clients `initialize`/SSE legado com sessão completa, clients `2026-07-28` stateless — configurável via `Mcp:SessionMode`) num único processo Kestrel. RAG agêntico sobre fontes de conhecimento registradas pelo usuário (Obsidian vaults, web pages, documentos, APIs, SQL).

Features implementadas (a maioria das SPECs em `.specs/` está `Done` — SPECs em andamento têm Issue aberta no GitHub): catálogo dinâmico de tools MCP, ingestão com conectores (Obsidian — vault local ou via WebDAV, WebPage, DocumentFile, Notion — REST read-only com token criptografado, descoberta via `/search` ou roots, sync incremental por `last_edited_time`; RestApi — `GET` JSON com `itemsPath` dot-path, mapeamento `titleField`/`contentFields`/`idField`/`urlField`, paginação `pageParam`/`maxPages`, headers no secret store `restapi:{id}` (SPEC-20260927-restapi-sqldatabase-connectors); SqlDatabase — `sqlite`/`postgres` com query SELECT-only validada por `SqlQueryGuard` (sem keywords de escrita fora de literais/comentários), `Mode=ReadOnly` no SQLite, transação `READ ONLY`+rollback no Postgres, connection string no secret store `sql:{id}`, mapeamento `idColumn`/`titleColumn`/`contentColumns`, `maxRows`/`Truncated`), retrieval híbrido FTS5+RRF, síntese de respostas via `IChatClient` (Ollama/OpenAI), loop de agente com tool-calling, aprovações HITL, threads de conversação com sumarização, endpoints SSE de streaming, auth (login por cookie + API keys `aft_*` com auditoria de uso e policies), proxies MCP upstream (DeepWiki público/privado, Firecrawl, Tavily, Context7 — `resolve-library-id`/`query-docs` via `ctx7sk-*`) com secrets por integração — incluindo `McpProxy` como `SourceType` de catálogo para MCP servers arbitrários e `tools/list` passthrough (tools privadas `devin_*` quando ApiKey configurada), provider de embeddings local ONNX Runtime (all-MiniLM-L6-v2, opt-in, `models/` gitignored), vector store sqlite-vec com KNN nativo (opt-in), cache distribuído opt-in `IDistributedCache` (memory|redis) para busca/embeddings, health checks, ProblemDetails, validação de config no startup, graceful shutdown, migrations EF Core com guard de dimensão de embeddings, UI admin com monitor MCP em tempo real (SignalR), edição de sources e playground, PWA instalável (tema claro, manifest + service worker) com sidebar icon-rail colapsável, transporte MCP híbrido (`Mcp:SessionMode`, default `StatefulForInitializeClients` — clients `initialize`/SSE legado ≤2025-11-25 mantêm sessão; clients `2026-07-28` atendidos stateless; `?access_token=` aceito no `/mcp/sse` para clients sem header), e scripts de backup/restore + packaging standalone com `./install.sh --host --systemd` verificado em host real. Capabilities adicionais do ciclo RAG/knowledge (SPECs 2026-09-24/25): MMR + quota/doc + score floor, corrective-RAG (grading → retry → abstenção), rewrite history-aware, embeddings assimétricos (query/document `input_type` + marcador `+asym`), contextual chunk enrichment (`SectionPath`/`EnrichedText`), query expansion multi-query + HyDE, expansão hierárquica de contexto (`contextExpand`), chunking semântico por breakpoints de embeddings (opt-in por fonte via `{"chunking":"semantic"}` no dialog de fontes), braço de knowledge-graph no RRF (`useGraph`), fila de ingestão assíncrona com jobs persistidos (`202+jobId`, `?wait=true` legado, cancel, reindex seletivo por chunker-version), gates de eval com baselines nomeados + latências p50/p95/p99 + agendamento, e conectores de cloud storage (AWS S3, Azure Files, OCI Object Storage) com staging local incremental por ETag e secrets no `IIntegrationSecretStore`. Capabilities de observabilidade/infra (waves 2026-09-25/26): Serilog request logging estruturado (health/static→Debug, 5xx→Error, `x-request-id`/`RequestId`), file sink rolling diário 14d no volume `./logs` + enricher de redaction (`***REDACTED***` em keys/tokens/secrets/connstrings e padrões `aft_*`/`ctx7sk-*`/`sk-*`), sink OTLP opt-in, `GET/PUT /api/settings/log-level` (`LoggingLevelSwitch` + auto-reset 0–120min), cache híbrido L1(in-process)→L2(redis) com TTL por região (`Cache:RegionTtlMinutes`) e invalidação distribuída via pub/sub `kh:invalidate`, health checks de Redis/vector store + `GET /api/diagnostics/vectorstore`, métricas `vector.errors`/`vector_upsert.duration`, spans OTel na pipeline (`search.rewrite/rrf/mmr/rerank`, `ingestion.job/embed`), e pgvector avançado (iterative filtered scan opt-in `pgvector≥0.8`, storage `halfvec` opt-in `pgvector≥0.7` com gate `AllowStorageMigration`, pooling, ANALYZE pós-bulk, cascade delete por fonte). Capabilities do ciclo agentic-RAG (SPECs 2026-09-27, issues #262–#281): conectores RSS/Atom (fingerprint por `guid`), YouTube (transcrições com `includeAutoCaptions`/`includeTimestamps`), `GitRepository` (GitHub/GitLab/Gitea REST read-only, fingerprint commit-SHA+por-blob, SSRF guard `allowPrivateHosts`, PAT `git:{id}`), `UnstructuredDocument` (OCR/layout via Unstructured API, `strategy`, `unstructured:{id}`), `AudioTranscription` (AssemblyAI ou Whisper self-hosted, capítulos + diarization, `audio:{id}`); embeddings `voyage`/`cohere` (`Embeddings:Voyage|Cohere` com fallback para top-level ApiKey/Model); avaliação da tríade RAG (context relevance, groundedness, answer relevance — `RagEvaluations`, dashboard `/rag-quality`, `GET /api/v1/evaluation/stats`, retenção 90d); cadeia de evidências HMAC-SHA256 append-only (`Audit/Evidence/`, `GET /api/v1/evidence/sessions/{id}/bundle`); Chain AST + repair de tool calls órfãs + compactor de histórico (`Agents/ChainAst/`, `Agent:ContextManagement`); motor de resiliência `Resilience:Fallback` (`disabled`/`observe`/`enforce`, `ResilientChatClient` decora `IChatClient` com alternates por key, `ToolCapabilityRegistry` para capacidades de tools); grafo temporal/episódico (`KgNode.ObservedAt/ValidFrom/ValidTo/EpisodeId`, `KgEpisode`, `TemporalGraphRetriever` com 5 tools `search_graph_*`, `TemporalDateParser`, `DiversityRanker`); action-augmented RAG (`Mcp/Bridge/` — markers `<!-- mcp-tool: -->` em chunks, `ask_knowledge.enableLiveActions`, `search_knowledge.suggestedActions`, cap `Agent:MaxChainedDynamicCalls`); window retrieval + autocut (`windowSize`/`limitMode`/`autocutSensitivity`, `Search:LimitMode`, gate 80% score); e relaxamento hierárquico de filtros + `subQueries` multi-query via RRF (`Search:Relaxation`, `IsRelaxed`/`appliedFilter`/`filterRelaxed`).

## Estrutura

```
.
├── KnowledgeHub.slnx          # solution
├── src/
│   ├── KnowledgeHub.Shared/      # DTOs, contratos JSON-RPC 2.0 / MCP, enums
│   ├── KnowledgeHub.Client/      # Blazor WASM SPA (BootstrapBlazor)
│   ├── KnowledgeHub.Server/      # host Kestrel, Minimal APIs, EF Core SQLite, sync
│   └── KnowledgeHub.McpEngine/   # MCP nativo: sessões SSE, dispatcher JSON-RPC
├── tests/
│   ├── KnowledgeHub.Tests.Unit/
│   └── KnowledgeHub.Tests.Integration/
├── .specs/                     # SPECs SDD — fonte da verdade para features
├── .claude/skills/             # skills versionadas (afonsoft/skills)
├── skills-lock.json            # hash SHA-256 de cada skill
├── backup.sh / restore.sh      # backup/restore do SQLite + uploads
├── docker-compose.yml          # deploy containerizado
└── LICENSE                     # MIT — Afonso Dutra Nogueira Filho, 2026
```

## Skills Registradas

As skills são gerenciadas pelo CLI `skills` (skills.sh): o store canônico fica em `.agents/skills/<skill-name>/` (gitignored) e `.claude/skills/<skill-name>` é um symlink para ele. O `skills-lock.json` fixa cada skill com `computedHash` SHA-256 (origem: `https://github.com/afonsoft/skills`, branch `skills/...`). Para atualizar: `npx skills update -p -y` (existentes) e `npx skills add afonsoft/skills -s <nome> -y` (novas); para restaurar após clone: `npx skills experimental_install`.

## Workflow Recomendado

O fluxo do agent-loop definido em `.claude/skills/write-specs/` → `execute-specs/` → `create-issues/` é o caminho canônico para novas funcionalidades:

1. `/write-specs` — estabelecer linguagem compartilhada, domínio e SPEC SDD.
2. `/execute-specs` — executar tarefas a partir do SPEC aprovado.
3. `/create-issues` — quando o trabalho precisa ser fragmentado em tickets.

Para revisão: `/code-review`, `/simplify` ou invoque `code-review-and-quality` / `quality-test-implementation` / `qa-analyst` diretamente. Para arquitetura: `drawio-architecture` ou `mermaid-architecture`.

## Memory Protocol

- **State** (short-term): `.claude/memory/memory.md` — overwritten every session, max 100 lines.
- **History** (long-term): `.claude/memory/{YYYYMMDD}-memory.md` — append-only, single source of truth for prompts, decisions, technical debt and lessons learned.
- **Knowledge** (durable): `.claude/knowledge/{slug}.md` — reusable facts and patterns promoted out of memory.
- **Protocol docs** (on-demand): `.claude/MEMORY.md` — reference only, no state or history.

Save everything, always. Read `memory.md` and the 3 most recent long-term files at session start. Log a one-line summary of every user prompt or instruction under `## Prompts`, each verified checkpoint, decision, mistake or discovery under its section, and a `## Session summary` — outcome and where work stopped — before compaction, context reset or any possible end of session. Promote reusable knowledge to `.claude/knowledge/`. Nothing survives only in context.

## MCP Server & Como Usar

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

### Recomendações para Agentes

- **Grounding First**: Sempre consulte `search_knowledge` antes de supor regras de negócio ou duplicar classes utilitárias já existentes no repositório.
- **Onboarding do repositório**: Na primeira sessão, pergunte sobre o repositório com `ask_question` consultando a documentação via `read_wiki_contents` antes de implementar.
- **Registro de trabalho**: Sempre registre o que foi feito com `write_note`; quando precisar criar conhecimento ou memória persistente, use `write_knowledge`.
- **Análise de Impacto**: Execute `analyze_impact` antes de refatorar contratos ou tipos compartilhados em `KnowledgeHub.Shared`.
- **Segurança**: Nunca realize commit ou emita em logs chaves `aft_*`, credenciais ou segredos presentes em `.env`.

## Convenções

- **Branches**: `feature/{AgentLLM}-{YYYYMMDD}-{descricao-curta}` baseada em `main`. Nunca commitar em `main`, `master` ou `develop`.
- **Branch protection**: `main` tem proteção ativa no GitHub — PR obrigatório + status checks (`Build KnowledgeHub (.NET 10)`, `Unit Tests (xUnit)`, `Integration Tests (SQLite)`, `Blazor WASM Client Validation`, `Docker Image Build`); force-push e delete bloqueados. `enforce_admins=false` (owner mantém bypass de emergência — usar só com justificativa).
- **Workflows**: `.github/workflows` é protegido — qualquer alteração é bloqueada pela proteção de branch.
- **Specs**: `.specs/SPEC-*.md` aprovadas são a fonte da verdade; manter `Status`/`Ticket` sincronizados com a implementação.
- **Secrets**: nunca commitar `.env`, `*.key`, `*.pem`. API keys via variáveis de ambiente (`Chat__ApiKey`, `Embeddings__ApiKey`).
- **Commits**: Conventional Commits (`feat:`, `fix:`, `docs:`, `refactor:`).

## Comandos

```bash
dotnet build KnowledgeHub.slnx                    # build all
dotnet test                                       # unit + integration tests
dotnet format KnowledgeHub.slnx --verify-no-changes  # formatting gate
dotnet run --project src/KnowledgeHub.Server      # serve http://localhost:5000
dotnet ef database update -p src/KnowledgeHub.Server  # apply EF migrations
```

Para inspecionar/atualizar o lockfile de skills manualmente, use ferramentas Git padrão (ex.: `git diff skills-lock.json` para auditar mudanças de hash).

## Licença

MIT — ver `LICENSE`. Atribuição a terceiros (skills de `afonsoft/skills`) deve respeitar a licença de cada skill; cada `SKILL.md` declara a própria licença no frontmatter.
