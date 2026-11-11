# SPEC-20261111-agent-memory-retain

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-memory-retain` |
| Type | `Feature` |
| Stack | `.NET 10 / ASP.NET Core`, EF Core (SQLite + Postgres), MCP |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261111-agent-memory-retain` |
| Status | `Approved` |
| Ticket | [#610](https://github.com/afonsoft/KnowledgeRAG/issues/610) — Epic E29 #609 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (retain pipeline, clone `/tmp/hindsight`) |
| Depends on | — (fundation do ciclo de memória; consolidation/reflect/mental-models dependem desta) |

## 1. User Story

**As a** agente de IA consumindo o KnowledgeHub via MCP
**I want** gravar memórias duráveis em banks isolados (`memory_retain`) e recuperá-las por busca híbrida (`memory_recall`)
**So that** fatos, preferências e decisões sobrevivam entre sessões sem depender de documentos formais — o hub passa de "RAG sobre corpus" para "memória de agente".

## 2. Contexto técnico

- **Referência hindsight** (`hindsight-api-slim/hindsight_api/engine/`): `retain` executa chunking → extração LLM de fatos tipados (`world`/`experience`) + entidades + datas → resolução fuzzy de entidades → persistência de `memory_units` + `memory_links` (temporal/semantic/causal) numa única transação → gatilho de consolidação em background. Documentos guardam `content_hash` para **delta-retain** (chunks inalterados não são re-extraídos).
- **O que já temos**: `EntityExtractor`/`EntityResolver`/`TemporalDateParser`/`TemporalGraphRetriever`/`GraphEpisodeService` (`src/KnowledgeHub.Server/Graph/`), `KgNode`/`KgEdge`/`KgEpisode`/`KgAlias`, `IChatClient` (Ollama/OpenAI), `IngestionQueue`/`IngestionWorker` para jobs async `202+jobId`, CallerScope por `aft_*` key, embeddings ONNX opt-in + sqlite-vec.
- **Conceito novo**: `MemoryBank` — store isolado de memória de agente (um "cérebro" por usuário/agente/projeto), não uma `KnowledgeSource`. Isolamento = `bank_id` scoping + API key (CallerScope existente). Banks têm `disposition` (JSON: skepticism/literalism/empathy 1–5) e `mission` (texto livre usado pelo reflect — SPECs seguintes).
- Args desconhecidos já tolerados via `ToolArgs.*`; annotations MCP já emitidas (SPEC-20260926-mcp-sdk-alignment).

## 3. Requisitos

### RF-001 — Entidades de domínio `MemoryBank` + `MemoryUnit`

- **Description:** Novas entidades EF: `MemoryBank { Id, BankKey (string, unique por ApiKey scope), Name, Mission, DispositionJson, CreatedAt, ConfigJson }` e `MemoryUnit { Id, BankId, DocumentId?, ChunkIndex?, Text, FactType ('world'|'experience'|'observation'), Context?, EventDate?, OccurredStart?, OccurredEnd?, MentionedAt, Tags[], MetadataJson, ContentHash, ConsolidatedAt?, ConsolidationFailedAt?, Embedding? }`. Migration dual (SQLite + `Migrations/Postgres/`).
- **Rules:** bank scoping composto = `BankKey` + `ApiKeyId` do caller; `FactType` CHECK constraint; `ContentHash` = SHA-256 do texto normalizado.
- **Input → Output:** EF migrations aplicadas em SQLite e Postgres.

### RF-002 — `memory_retain` (tool MCP + REST)

- **Description:** Recebe `{bankKey?, content, context?, timestamp?, tags?, metadata?, mode:'sync'|'async', strategy:'extract'|'verbatim'}`. Modo `extract`: chama `IChatClient` com prompt de extração (fatos + entidades + datas + tipo world/experience, fiel ao prompt base do hindsight — mesma língua do input, resolução de coreferência, dimensões what/when/where/who/why). Modo `verbatim`: chunk vira 1 memory unit sem LLM. `async` → job na fila retornando `operationId` (reusa padrão `IngestionJob`).
- **Rules:** bank default = `default` auto-criado no primeiro retain com disposition neutra {3,3,3}; `timestamp` ancora `MentionedAt` e resolução de datas relativas → `OccurredStart/End`; `context` vira campo persistido e entra no prompt de extração; secrets scan da SPEC-20261111-memory-defense aplica-se **antes** de persistir quando entregue (acoplar via hook `IMemoryDefenseScreen` no-op por default).
- **Input → Output:** tool call → `{ bankKey, unitsCreated, operationId? }` / REST `POST /api/memory/banks/{bankKey}/memories`.

### RF-003 — Resolução de entidades e links

- **Description:** Reusar `EntityResolver` para fuzzy-merge de entidades extraídas (canonical names por bank). Criar `MemoryLink { FromUnitId, ToUnitId, LinkType ('temporal'|'semantic'|'causal'), EntityId?, Weight }`. Links temporais: mesmo fact_type, janela configurável `timeWindowHours` (default 72), bidirecional, peso `max(0.3, 1 - h/window)`, cap 20/unit. Causais: extraídos pelo LLM (≤2 por fato, apontando para fatos anteriores do mesmo chunk). Semânticos (opt-in): por similaridade de embedding ≥ threshold configurável.
- **Rules:** dedupe de links por unique index; lock ordering canônico para evitar deadlock; entity upsert por canonical name.
- **Input → Output:** unidades linkadas navegáveis pelo braço graph do recall.

### RF-004 — `memory_recall` (busca híbrida sobre units)

- **Description:** 4 braços paralelos reusando a pipeline existente: semantic (sqlite-vec/pgvector), lexical (FTS5/BM25 sobre `MemoryUnit.Text`), graph (links de RF-003 — link expansion), temporal (ativo só com `temporalWindow` ou expressão temporal na query via `TemporalDateParser`). Fusão via `RrfFuser` (k=60) → rerank opcional (`Reranking.cs`) → filtros `types`/`tags`/`tagsMatch` → budget `low|mid|high` + `maxTokens` + `minScores` (parâmetros já padronizados pela SPEC-20261001-mcp-recall-ergonomics).
- **Rules:** recall **não chama LLM** (paridade hindsight); `preferObservations` faz dedup de fatos brutos sob sua observation quando a SPEC de consolidação existir (flag aceita já, no-op sem observations); resposta inclui `armScores` por resultado (rastreabilidade).
- **Input → Output:** `{bankKey?, query, ...} → { results: [{id, text, factType, score, armScores, eventDate, tags}] }`.

### RF-005 — Delta-retain e documentos de origem

- **Description:** Opcional `documentId` agrupa units num documento lógico (`MemoryDocument { Id, BankId, Title?, ContentHash, MetadataJson }`). Re-retain com mesmo `documentId` compara hash por chunk: chunks inalterados mantêm units; alterados re-extraem (delete+insert). `mode:'append'` adiciona sem deletar.
- **Rules:** idempotente por `contentHash`; append com expected-hash guard retorna 409 em divergência.
- **Input → Output:** re-retain idempotente — segundo retain idêntico cria 0 units novas.

### RF-006 — CRUD e administração de banks/memories via MCP + REST

- **Description:** Tools: `memory_banks_list|get|create|update|delete`, `memory_list` (filtros factType/tags/dateRange/paging), `memory_get`, `memory_update` (corrige texto/metadata — re-embed), `memory_delete` (soft→hard delete + cascade links). REST espelhado em `/api/memory/*`. `memory_banks_update` edita `name`/`mission`/`disposition`.
- **Rules:** delete de bank exige confirmação explícita (`confirm:true`) e cascateia units/links/documents; tudo auditado no activity feed existente.
- **Input → Output:** superfície de gestão equivalente ao hindsight (`list_banks`, `update_bank`, `list_memories`, `update_memory`, `invalidate_memory`).

### RF-007 — Config hierarchical: global → per-key → per-bank

- **Description:** `Memory:*` em `IConfiguration` (extraction prompt variant `concise|verbose|custom`, `timeWindowHours`, `semanticLinkThreshold`, `maxUnitsPerRetain`, `llm` override). Per-key via `ApiKeyChatSettings`/settings existentes; per-bank via `MemoryBank.ConfigJson` — resolução `MemoryConfigResolver`: bank > key > global.
- **Rules:** documentar na convenção de config da SPEC-20261111-agent-rules-hardening.
- **Input → Output:** override por bank efetivo sem restart.

## 4. Fora de escopo

- Consolidação em observations (SPEC-20261111-memory-observations-consolidation).
- `memory_reflect`, dispositions aplicadas a raciocínio, directives (SPEC-20261111-memory-reflect-directives).
- Mental models/pages auto-refresh (SPEC-20261111-mental-models-pages).
- Memory Defense com políticas redact/block (SPEC-20261111-memory-defense — aqui só o hook `IMemoryDefenseScreen`).
- UI admin de banks (listagem mínima pode vir na mesma PR se trivial; playground de retain/recall fica para depois).
- Multi-tenancy por schema Postgres (mantido isolamento por key/bank).

## 5. Contrato MCP (tools novas)

```text
memory_retain(bankKey?, content, context?, timestamp?, tags?, metadata?, documentId?, mode?, strategy?)
  → { bankKey, documentId?, unitsCreated, operationId? }
memory_recall(bankKey?, query, types?, tags?, tagsMatch?, temporalWindow?, budget?, maxTokens?, minScores?, preferObservations?)
  → { results: [...], truncatedByTokens? }
memory_banks_list|get|create|update|delete
memory_list|get|update|delete
```

REST: `POST /api/memory/banks/{bankKey}/memories` (retain), `POST .../recall`, `GET|PATCH|DELETE /api/memory/banks/{bankKey}`, `GET|PATCH|DELETE /api/memory/banks/{bankKey}/memories/{id}`.

Erros esperados: `400` validação (content vazio, temporalWindow inválida, mode desconhecido), `404` bank/memory inexistente, `409` append hash divergente, `422` conteúdo bloqueado por defense.

## 6. Critérios de aceite

- [ ] **Dado** `memory_retain {content:"Alice trabalha na Google como engenheira"}` **quando** executado **então** ≥1 `MemoryUnit` com `FactType='world'` criada, entidade "Alice" e "Google" resolvidas, e `memory_recall {query:"onde Alice trabalha?"}` retorna o fato via braço semantic ou graph.
- [ ] **Dado** retain de conversa em 1ª pessoa do agente ("eu deployei a v2 ontem") **então** fato classificado `experience` e `OccurredStart` resolvido relativo ao `timestamp`.
- [ ] **Dado** dois retains no mesmo `documentId` com 1 parágrafo alterado **então** só os chunks alterados são re-extraídos (units inalteradas preservam `Id`).
- [ ] **Dado** `memory_recall {temporalWindow:{start,end}}` **então** resultados da janela recebem boost; sem janela, recall não ativa braço temporal.
- [ ] **Dado** banks `a` e `b` sob a mesma key **então** recall em `a` nunca retorna units de `b` (teste de isolamento).
- [ ] **Dado** `memory_retain {mode:'async'}` **então** retorna `operationId` e a unidade aparece após o job completar; cancelamento do job deixa 0 units parciais.
- [ ] **Edge:** retain com conteúdo > `maxUnitsPerRetain` chunks → erro 400 claro; LLM indisponível → `strategy:'extract'` falha com erro acionável, `verbatim` continua funcionando.

## 7. Arquivos

**Ler antes:**
- `src/KnowledgeHub.Server/Graph/{EntityExtractor,EntityResolver,TemporalDateParser,TemporalGraphRetriever,SqliteKnowledgeGraphStore}.cs`
- `src/KnowledgeHub.Server/Mcp/{IToolProvider.cs, ToolProviders/KnowledgeToolsProvider.cs}`
- `src/KnowledgeHub.Server/Ingestion/{IngestionQueue,IngestionWorker}.cs`
- `src/KnowledgeHub.Server/Search/{RrfFuser,Reranking,LexicalSearchService}.cs`
- `src/KnowledgeHub.Server/Domain/Entities/{KnowledgeDocument,DocumentChunk,KgNode,KgEdge}.cs`
- `/tmp/hindsight/hindsight-api-slim/hindsight_api/engine/retain/{orchestrator,fact_extraction}.py` (prompt de referência)
- `/tmp/hindsight/hindsight-api-slim/hindsight_api/engine/memories/pg/{entity_resolver,links}.py`

**Criar/modificar:**
```text
src/KnowledgeHub.Server/Domain/Entities/{MemoryBank,MemoryUnit,MemoryDocument,MemoryLink,MemoryDirective}.cs
src/KnowledgeHub.Server/Memory/{MemoryRetainService,MemoryRecallService,MemoryEntityLinker,MemoryConfigResolver,IMemoryDefenseScreen}.cs
src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs
src/KnowledgeHub.Server/Api/MemoryEndpoints.cs
src/KnowledgeHub.Server/Migrations/*_AgentMemory.cs + Migrations/Postgres/*
tests/KnowledgeHub.Tests.Unit/Memory/*
tests/KnowledgeHub.Tests.Integration/Memory/*
```

## 8. Riscos

- Custo de LLM por retain (1 call/chunk) — mitigado por `strategy:'verbatim'`, `maxUnitsPerRetain`, e chunking só acima de limiar configurável.
- Prompt de extração é a peça crítica — portar fielmente do hindsight (pt/en multilíngue, nunca traduzir fatos); teste com LLM-judge para classificação fact_type/attribution (ver convenção em SPEC-20261111-agent-rules-hardening).
- `MemoryLink` semântico pode explodir em banks grandes — default off, threshold conservador, cap por unit.
- Deadlock em links — ordering canônico de locks como no hindsight.

## 9. Task Plan + DoD

- [ ] T1: entities + migrations dual (SQLite/Postgres) + `MemoryConfigResolver`.
- [ ] T2: `MemoryRetainService` (extract/verbatim, delta-retain, entities+links) + REST.
- [ ] T3: `MemoryRecallService` 4 braços + RRF/rerank/budget + tool MCP.
- [ ] T4: CRUD banks/memories (MCP + REST) + isolamento + auditoria.
- [ ] T5: testes (unit extração-prompt/factType/delta; integração retain→recall E2E; isolamento) + `dotnet test` + `dotnet format --verify-no-changes`.
- [ ] DoD: todos RF implementados, AC cobertos por testes, coverage ≥80% nos novos serviços, guardrails do repo (branch, sem secrets, conventional commits) respeitados.
