# SPEC-20261112-memory-prompt-provenance

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `memory-prompt-provenance` |
| Type | `Feature` |
| Stack | `.NET 10`, EF Core (migration dual SQLite+Postgres), MCP/REST |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/devin-20261112-agent-memory-e30` |
| Status | `In implementation` |
| Ticket | #621 — Epic #619 |
| Source | Benchmark analysis: `Gentleman-Programming/gentle-ai` (`engram` — `mem_save_prompt`, `capture_prompt`) |
| Depends on | `SPEC-20261111-agent-memory-retain` (#610), `SPEC-20261111-memory-defense` (#615) |

## 1. User Story

**As a** agente de IA que retém fatos ao longo de uma sessão
**I want** que cada retain carregue o prompt/sessão que o originou, com dedupe por prompt
**So that** o recall responda "de onde veio esse fato", eu possa consultar "o que aprendemos na sessão X", e retains repetidos do mesmo prompt não re-extraiam nem dupliquem memória — como o `mem_save_prompt`/`capture_prompt` do Engram.

## 2. Contexto técnico

- **Referência gentle-ai/engram**: `mem_save_prompt` registra o prompt real do usuário e alimenta o contexto da sessão; `mem_save` posterior anexa o prompt capturado e deduplica observações derivadas do mesmo prompt. `capture_prompt:false` nos saves automáticos (caches, artifacts de skill-registry) separa conteúdo humano de gerado.
- **O que já temos**:
  - `MemoryDocument` — container de um retain batch (1 chamada → N units) com `ContentHash`/`CreatedAt` — **o nível certo para proveniência** (o prompt pertence ao batch, não a cada unit);
  - `MemoryUnit.DocumentId` + nav `Document` — units já herdam proveniência via navegação;
  - Delta-retain dedupe por `ContentHash` — dedup de *conteúdo*, não de *causa*;
  - `MemoryDefense`/`IMemoryDefenseScreen` — `sourcePrompt` é conteúdo e passa pelo mesmo screen;
  - `agent_chat.threadId` — id de sessão natural para mapear em `SessionId`.
- **Decisão rejeitada:** tool separada `memory_prompt_save` (análogo direto do Engram) — adiciona uma chamada extra por turno para benefício marginal; os campos opcionais no `memory_retain` cobrem o caso numa única chamada.

## 3. Requisitos

### RF-001 — Campos de proveniência em `MemoryDocument`

- **Description:** Três colunas novas (migration dual): `SourcePrompt` (TEXT NULL, truncado a ~2000 chars), `SourceSessionId` (TEXT NULL, cap 128), `Origin` (TEXT NOT NULL default `'explicit'` — valores `explicit|passive|import|system`).
- **Rules:** `Origin` validado por check constraint (SQLite + Postgres em paridade, via `MigrationParityTests`); `SourceSessionId` indexado com `BankId` — `(BankId, SourceSessionId)` — para queries por sessão; null = retenção sem proveniência declarada (retrocompatível).
- **Input → Output:** migration `MemoryPromptProvenance` + contraparte Postgres.

### RF-002 — `memory_retain` aceita proveniência

- **Description:** Args opcionais `sourcePrompt` (string), `sessionId` (string), `origin` (enum) — gravados no `MemoryDocument` do batch. Schema MCP documenta o mapeamento sugerido: `sessionId` ↔ `threadId` do `agent_chat`.
- **Rules:** `sourcePrompt` passa pelo `IMemoryDefenseScreen` como conteúdo (prompts carregam secrets — block/redact/allow seguem a política do bank/source); `origin` fora do enum → 400/InvalidParams; truncamento de `sourcePrompt` registrado em `MetadataJson` (`promptTruncated:true`).
- **Input → Output:** retain existente + campos novos; comportamento inalterado quando ausentes.

### RF-003 — Dedupe por prompt

- **Description:** Hash SHA-256 de `normalize(sourcePrompt)` gravado como `PromptHash` no `MemoryDocument`. Retain com mesmo `(BankId, SessionId, PromptHash)` já persistido → resposta marca `promptSeen:true` e **pula a extração** (dedup da causa, antes do dedup de conteúdo).
- **Rules:** só aplica quando os 3 componentes presentes; `promptSeen` ≠ erro — retorna `unitsCreated:0` + `promptSeen:true` + referência ao `documentId` original; retains sem `sourcePrompt`/`sessionId` comportamento inalterado.
- **Input → Output:** retains duplicados de um mesmo prompt → no-op idempotente com sinal explícito.

### RF-004 — Proveniência na leitura

- **Description:** `memory_get`/`memory_list`/`memory_recall`/`memory_timeline` (SPEC-20261112-memory-context-timeline-stats) expõem `sourcePrompt`, `sessionId`, `origin` quando presentes (via `Document`); `memory_recall`/`memory_list` aceitam filtro `sessionId` — "o que aprendemos na sessão X".
- **Rules:** `sourcePrompt` na resposta truncado a 500 chars (preview); filtro `sessionId` casa exato (case-sensitive); unidades sem `Document` retornam campos null — sem erro.
- **Input → Output:** respostas de leitura enriquecidas + filtro por sessão.

### RF-005 — Ergonomia/contrato

- **Description:** `memory_retain` schema atualizado (contract pin); `.env.example`/appsettings sem novas chaves (nada configurável por default — `Memory:Provenance:MaxPromptLength` opcional, default 2000); `skills/knowledge-hub/SKILL.md` documenta o padrão "passe `sessionId` do thread corrente".
- **Rules:** sem mudança em tools existentes além dos args; REST `POST /api/memory/banks/{bankKey}/retain` aceita os mesmos campos.
- **Input → Output:** contrato + docs consistentes.

## 4. Fora de escopo

- Captura passiva (`mem_capture_passive` — extração automática do turno sem chamada explícita) → proposta separada Epic E30.
- Session lifecycle (`mem_session_start|end`) — snapshots de contexto já existem; aprofundar depois.
- Merge de banks por name-drift → proposta separada.

## 5. Mudanças técnicas

```text
memory_retain(..., sourcePrompt?, sessionId?, origin?) → { unitsCreated, promptSeen?, documentId }
memory_recall(..., sessionId?) → results[] + provenance
memory_get / memory_list / memory_timeline → + sourcePrompt|sessionId|origin
```

REST: `POST /api/memory/banks/{bankKey}/retain` (campos novos no body), `GET /api/memory/banks/{bankKey}/units?sessionId=`.
Erros: `400` origin inválido / sourcePrompt > cap configurado sem truncate; `422` defense block.

## 6. Critérios de aceite

- [ ] **Dado** retain com `sourcePrompt="prefiro async"`, `sessionId="t1"` **então** `MemoryDocument` persiste os 3 campos e `memory_get` da unit devolve `sourcePrompt/sessionId/origin`.
- [ ] **Dado** 2 retains idênticos (mesmo bank+sessionId+sourcePrompt) **então** o segundo retorna `promptSeen:true`, `unitsCreated:0`, sem nova extração LLM.
- [ ] **Dado** retain sem proveniência **então** comportamento idêntico ao atual (campos null, dedup por `ContentHash` normal).
- [ ] **Dado** `origin:"bogus"` **então** 400/InvalidParams — e nada persiste.
- [ ] **Dado** `sourcePrompt` com secret **então** o screen aplica a política do bank (block → 422; redact → persiste redigido) — nunca valor bruto.
- [ ] **Dado** `memory_recall sessionId="t1"` **então** só units da sessão t1 retornam.
- [ ] **Edge:** `sourcePrompt` de 5KB → persistido truncado a 2000 + `promptTruncated` no metadata.

## 7. Arquivos

**Ler antes:**
- `src/KnowledgeHub.Server/Domain/Entities/{MemoryDocument,MemoryUnit}.cs`
- `src/KnowledgeHub.Server/Memory/{MemoryRetainService,MemoryRecallService}.cs`
- `src/KnowledgeHub.Server/Security/{DefensePolicy,IMemoryDefenseScreen}.cs`
- `docs/engram.md` em `/tmp/gentle-ai` (referência)

**Criar/modificar:**
```text
src/KnowledgeHub.Server/Domain/Entities/MemoryDocument.cs (+3 campos +PromptHash)
src/KnowledgeHub.Server/Migrations/*MemoryPromptProvenance* (+Postgres/*)
src/KnowledgeHub.Server/Memory/MemoryRetainService.cs (args, dedupe por prompt)
src/KnowledgeHub.Server/Memory/MemoryRecallService.cs (filtro sessionId + campos na resposta)
src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs (schema + handlers)
src/KnowledgeHub.Server/Api/MemoryEndpoints.cs
tests/KnowledgeHub.Tests.Unit/Memory/MemoryProvenanceTests.cs
tests/KnowledgeHub.Tests.Integration/McpContractTests.cs (pin retain schema)
```

## 8. Riscos

- `sourcePrompt` pode vazar contexto da conversa para quem lê o bank — mitigado por preview truncado e documentação de que proveniência é metadado de auditoria, não canal de compartilhamento.
- Dedupe por prompt pode suprimir retains legítimos quando dois prompts textualmente iguais têm intenções diferentes — aceitável: mesmo bank + mesma sessão + mesmo texto é a definição de duplicado; cross-session sempre retém.
- Migration em tabela potencialmente grande — colunas NULLable (sem backfill), custo baixo nos dois dialetos.

## 9. Task Plan + DoD

- [ ] T1: `MemoryDocument` +3 campos + `PromptHash` + migration dual + constraint de `Origin`.
- [ ] T2: retain — args, screen de `sourcePrompt`, truncamento, `promptSeen` no response.
- [ ] T3: dedupe por `(BankId, SessionId, PromptHash)` — pula extração, sinal idempotente.
- [ ] T4: leitura — campos em get/list/recall/timeline + filtro `sessionId` + MCP/REST + contract pin.
- [ ] T5: testes unit + integração (provenance round-trip, dedupe, defense, session filter) + gates.
- [ ] DoD: RF implementados, AC cobertos, migration dual em paridade, retrocompatível, secrets nunca em claro.
