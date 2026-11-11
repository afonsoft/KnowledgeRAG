# SPEC-20261111-mcp-ergonomics-wave2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `mcp-ergonomics-wave2` |
| Type | `Feature` |
| Stack | `.NET 10`, MCP (Streamable HTTP + SSE) |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261111-mcp-ergonomics-w2` |
| Status | `In implementation` |
| Ticket | [#614](https://github.com/afonsoft/KnowledgeRAG/issues/614) — Epic E29 #609 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (`mcp_tools.py`, 39 tools) — 2ª onda após SPEC-20261001-mcp-recall-ergonomics (Done) |

## 1. User Story

**As a** agente de IA consumindo o KnowledgeHub via MCP
**I want** uma superfície de tools completa (gestão de operações, CRUD de documentos, escopo por bank) e tolerante aos erros típicos de LLM
**So that** consigo operar o hub inteiro via MCP sem cair para REST, e erros de serialização do meu lado não quebram chamadas.

## 2. Contexto técnico

- **Referência hindsight** (`mcp_tools.py`, 39 tools): CRUD completo de todos os recursos + `list_operations|get_operation|cancel_operation` (jobs async de 1ª classe), endpoints **por bank** (`/mcp/{bank_id}/`, `X-Bank-Id` header, multi-bank `/mcp/`), `_make_tools_tolerant` (strip args desconhecidos + **coerção de JSON stringificado** — LLM manda `'{"a":1}'` string onde devia ser objeto), `HINDSIGHT_API_MCP_INSTRUCTIONS` anexa notas de deploy às descrições.
- **Já entregue** (não repetir): annotations `readOnlyHint`/etc. (SPEC-20260926-mcp-sdk-alignment), tolerância a args **desconhecidos** via `ToolArgs.*`, `budget`/`maxTokens`/`minScores`/`temporalWindow` (SPEC-20261001, Done #441), tool filtering per-key, audit feed.
- **Gaps reais**: (a) **coerção de JSON stringificado** — string com JSON válido onde schema pede object/array não é desserializada hoje; (b) sem tools de **operations** (IngestionJob existe em REST com cancel; MCP não expõe list/get/cancel); (c) sem **update/delete** de documento/memória via MCP; (d) sem **endpoint/header de escopo** (bank implícito — hoje bank vai sempre por arg); (e) sem `Mcp:Instructions` env.

## 3. Requisitos

### RF-001 — Coerção de args JSON-stringificados

- **Description:** No `ToolArgs`/binder: quando schema declara `object`/`array`/`number`/`boolean` e o valor chega como `string` contendo JSON válido, desserializar antes de validar. `'{"start":"2026-01-01"}'` → objeto; `"5"` para int → 5; `"true"` → bool. Falha de parse → erro claro `invalid argument type for <name>` (não crash).
- **Rules:** aplica-se a todas as tools automaticamente no dispatcher central (não por-provider); string que não é JSON → erro de validação normal; log Debug quando coerção ocorre (observabilidade de clientes ruins).
- **Input → Output:** chamadas LLM "imperfeitas" passam sem retry manual.

### RF-002 — Tools de operations (jobs async)

- **Description:** `operations_list(status?, type?, limit?, offset?)`, `operations_get(id)`, `operations_cancel(id)` expondo `IngestionJob` (e futuros jobs de memória/consolidação) via MCP. Escopo: caller só vê jobs das suas keys/sources (CallerScope).
- **Rules:** cancel cooperativo (marca `Cancelled`, worker respeita no próximo checkpoint — reusar mecanismo existente); terminal state → `409`; retry de job `failed|cancelled` via `operations_retry(id)` requeue.
- **Input → Output:** polling determinístico de jobs longos pelo MCP (paridade com REST `/api/.../jobs`).

### RF-003 — CRUD gaps: update/delete via MCP

- **Description:** `document_update(id|path, {title?, tags?, metadata?})`, `document_delete(id|path, confirm)` para `KnowledgeDocument` (re-movendo chunks derivados na re-ingestão) — paridade com as tools de memória da SPEC-20261111-agent-memory-retain. `source_rescan(sourceId)` dispara re-sync on-demand.
- **Rules:** deletes exigem `confirm:true`; annotations `destructiveHint:true` já saem de graça; escopo por source/key.
- **Input → Output:** gestão do corpus 100% via MCP.

### RF-004 — Escopo por bank: header + endpoint

- **Description:** Com a camada de memória (SPECs 20261111-*), bank resolvido nesta ordem: path `/mcp/banks/{bankKey}/` → header `X-Memory-Bank` → arg `bankKey` → `default`. Endpoint por bank opcional (`Mcp:BankScopedEndpoints`, default on quando a feature de memória existir) — tools de memória chamadas no endpoint scoped têm `bankKey` implícito.
- **Rules:** resolução documentada no `initialize`/instructions; bank inexistente no header → `400` (não cria implicitamente — criação só via `memory_retain`/`memory_banks_create`).
- **Input → Output:** clients pinam um bank por conexão (paridade hindsight `/mcp/{bank_id}/`).

### RF-005 — `Mcp:Instructions` deployment notes

- **Description:** Config `Mcp:Instructions` (string) anexada às `instructions` do `initialize` result e ao final da description das tools principais — admins documentam deployment (ex.: "este hub é read-mostly; use bank=projeto-x").
- **Rules:** sanitizado (sem HTML/segredos; max 2000 chars, trunca); refletido no playground.
- **Input → Output:** operação documenta convenções locais sem rebuild.

### RF-006 — Descrições e examples consistentes

- **Description:** Pass sobre descriptions das tools nativas: 1ª linha = o que faz; `when_to_use` explícito; `examples` JSON no schema (padrão já usado em `agent_chat`). Adicionar aos que faltam (graph tools, flows, context).
- **Rules:** sem mudança de comportamento; lint de schema: toda tool tem `description` + ao menos 1 `example` (teste de catálogo).
- **Input → Output:** catálogo auto-explicativo como o do hindsight.

## 4. Fora de escopo

- Annotations (Done), tolerância a args desconhecidos (Done), budget/minScores/temporalWindow (Done).
- Novas tools de memória (SPECs 20261111-*).
- Auth por endpoint scoped (permanece `aft_*`/query token atual).
- Tools de upstream proxy (passthrough já existe).
- Refatorar transporte (híbrido stateful/stateless já coberto).

## 5. Critérios de aceite

- [ ] **Dado** `memory_recall` com `temporalWindow:"{\"start\":\"2026-01-01\",\"end\":\"2026-02-01\"}"` (string) **então** desserializa e executa normalmente.
- [ ] **Dado** `operations_list` após um reindex **então** retorna o job com status; `operations_cancel` num job running → `Cancelled`; num `completed` → 409.
- [ ] **Dado** `document_delete {id, confirm:true}` **então** documento e chunks somem de `search_knowledge`; sem `confirm` → 400.
- [ ] **Dado** client em `/mcp/banks/projeto-x/` chamando `memory_recall` sem `bankKey` **então** usa `projeto-x`; header `X-Memory-Bank` divergente do path → path vence (ordem documentada).
- [ ] **Dado** `Mcp:Instructions="Bank padrão: projeto-x"` **então** `initialize` carrega o texto e tool descriptions principais o sufixam.
- [ ] **Edge:** coerção com string `"abc"` para int → erro de validação legível; `operations_get` de job de outra key → 404 (não vaza existência).

## 6. Arquivos

**Ler antes:**
- `src/KnowledgeHub.Server/Mcp/{IToolProvider.cs, ToolProviders/*.cs, CallerIdentity.cs}`
- `src/KnowledgeHub.McpEngine/` (dispatcher, `McpEndpointExtensions.cs`)
- `src/KnowledgeHub.Server/Ingestion/{IngestionJob,IngestionQueue}.cs` (entity `IngestionJob` em `Domain/Entities/`)
- `/tmp/hindsight/hindsight-api-slim/hindsight_api/mcp_tools.py` (`_make_tools_tolerant`, `_resolve_bank`, ops tools)

**Criar/modificar:**
```text
src/KnowledgeHub.McpEngine/ (binder/coercion — localizar ponto único de desserialização de args)
src/KnowledgeHub.Server/Mcp/ToolProviders/OperationsToolsProvider.cs
src/KnowledgeHub.Server/Mcp/ToolProviders/ObsidianToolsProvider.cs (document_update/delete)
src/KnowledgeHub.Server/Api/MemoryEndpoints.cs / Mcp routing (bank-scoped endpoint)
tests/KnowledgeHub.Tests.Unit/Mcp/*
tests/KnowledgeHub.Tests.Integration/Mcp/*
```

## 7. Riscos

- Coerção automática pode mascarar client bug — mitigado por log Debug + erro claro quando parse falha.
- `operations_*` expõe metadata de jobs — escopo CallerScope garante que key A não vê jobs de key B (teste obrigatório).
- Endpoint por bank multiplica rotas — opcional por config; default sensato documentado.
- `document_delete` via MCP é destrutivo — `confirm:true` + `destructiveHint` + auditoria.

## 8. Task Plan + DoD

- [ ] T1: coerção JSON-stringified no binder + testes parametrizados.
- [ ] T2: `operations_list|get|cancel|retry` (IngestionJob) + escopo.
- [ ] T3: `document_update|delete` + `source_rescan`.
- [ ] T4: resolução de bank (path→header→arg→default) + endpoints scoped + `Mcp:Instructions`.
- [ ] T5: pass de descriptions/examples + teste de catálogo (toda tool com example).
- [ ] DoD: RF implementados, AC cobertos, sem regressão `dotnet test`, `dotnet format --verify-no-changes`, coverage ≥80% nos novos trechos.
