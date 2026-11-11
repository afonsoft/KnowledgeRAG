# SPEC-20261111-memory-observations-consolidation

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `memory-observations-consolidation` |
| Type | `Feature` |
| Stack | `.NET 10`, EF Core, background worker |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261111-memory-consolidation` |
| Status | `Approved` |
| Ticket | [#611](https://github.com/afonsoft/KnowledgeRAG/issues/611) — Epic E29 #609 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (`engine/consolidation/`) |
| Depends on | `SPEC-20261111-agent-memory-retain` (entidades `MemoryUnit`/`MemoryBank`) |

## 1. User Story

**As a** agente de IA com centenas de memórias acumuladas num bank
**I want** que fatos relacionados se consolidem em observações deduplicadas com evidência
**So that** o recall responda com crenças refinadas ("usuário prefere sync → migrou para async") em vez de listar fatos brutos contraditórios.

## 2. Contexto técnico

- **Referência hindsight** (`engine/consolidation/consolidator.py`): worker async seleciona `memory_units` onde `consolidated_at IS NULL AND consolidation_failed_at IS NULL AND fact_type IN ('world','experience')`, agrupa por bank, e chama o LLM por batch. Por fato o LLM decide: `create` (nova observation + evidence quotes), `update` (refina observation existente + anexa fontes), `delete`, `skip`. Observations são `memory_units` com `fact_type='observation'`, `proof_count` (nº de fatos-suporte), `source_memory_ids` e histórico em `observation_history` — **refine, nunca sobrescreve**. Dedup adjudicado por LLM (`_dedup_adjudicate`); decision model barato tria casos óbvios (`restate`→fold evidence, `chatter`→skip) economizando ~40% das chamadas LLM; falha do decision model → fallback LLM.
- **O que já temos**: `IngestionWorker`/`IngestionQueue` (padrão de job persistido), `BackgroundServices/` (Maintenance/ScheduledSync/VaultWatcher), `IChatClient`, RRF/rerank para candidatos de dedup, `preferObservations` já previsto na SPEC de retain.
- Novo: `MemoryConsolidationWorker` (BackgroundService ou job na fila existente), campos `ConsolidatedAt`/`ConsolidationFailedAt`/`ProofCount`/`SourceMemoryIds`/`ObservationScopes` em `MemoryUnit` (da SPEC anterior) + tabela `ObservationHistory`.

## 3. Requisitos

### RF-001 — Worker de consolidação

- **Description:** `MemoryConsolidationService` disparado (a) após retain bem-sucedido (gatilho pós-commit do job), (b) por schedule configurável `Memory:Consolidation:Interval` (default 15min), (c) manual via `memory_consolidate` tool/REST. Processa por bank, batch size configurável (`Memory:Consolidation:BatchSize`, default 50).
- **Rules:** um run por bank por vez (`serialization_key` por bank — reuse do padrão de queue); cancelamento cooperativo entre batches; falha marca `ConsolidationFailedAt` (retryable) e não reprocessa no próximo tick sem backoff.
- **Input → Output:** job run → `{ bankKey, processed, created, updated, skipped, failed }`.

### RF-002 — Consolidação LLM por batch

- **Description:** Prompt portado do hindsight: para cada fato novo + observations candidatas (recuperadas por recall restrito a `fact_type='observation'`, top-k configurável), decide `create|update|delete|skip`. `create` exige **evidence quotes** (trechos exatos dos fatos-fonte). `update` reescreve o texto da observation e **anexa** novos `source_memory_ids` + incrementa `proof_count` — nunca remove evidência.
- **Rules:** fatos-fonte recebem `ConsolidatedAt`; observations consolidadas não entram no lote seguinte (já têm `ConsolidatedAt` — na verdade observations nunca são re-consolidadas: filtro por `fact_type IN world/experience`); resposta do LLM validada por schema (structured output — `Microsoft.Extensions.AI` JSON schema); falha de parse → fato marcado `skip` (não `failed`).
- **Input → Output:** batch de units → conjunto de ações aplicadas transacionalmente.

### RF-003 — `ObservationHistory` (auditoria de evolução)

- **Description:** Tabela `ObservationHistory { Id, ObservationUnitId, Version, Text, AddedSourceIds, ProofCount, Action, CreatedAt }` — uma linha por create/update. Permite ver como uma crença evoluiu.
- **Rules:** append-only; `GET /api/memory/observations/{id}/history` + tool `memory_observation_history`.
- **Input → Output:** trilha completa de refinamento por observation.

### RF-004 — Dedup adjudicado

- **Description:** Antes de `create`, candidatos com similaridade de embedding ≥ `Memory:Consolidation:DedupThreshold` (default 0.92) são enviados ao LLM numa chamada de adjudicação: `same` (fold em observation existente como nova evidência) ou `distinct` (cria nova). Evita explosão de observations quase idênticas.
- **Rules:** sem embeddings habilitados → fallback para similaridade lexical (FTS overlap ≥ threshold); adjudicação é uma chamada única por batch (não por par).
- **Input → Output:** observations deduplicadas com fold de evidência.

### RF-005 — Decision-model triage (opt-in)

- **Description:** Se `Memory:Consolidation:Triage=decision-model` e um provider barato estiver configurado (reusa `ModelProfile`/chat settings), tria cada fato: `restate` (idêntico a observation existente → fold direto, sem LLM), `chatter` (não-fato → `ConsolidatedAt` sem ação), `new|changes` → vai ao LLM. Falha do triage → tudo vai ao LLM (fallback transparente).
- **Rules:** opt-in (default `off`); métricas de redução de chamadas LLM expostas no activity feed/health.
- **Input → Output:** redução de custo LLM mensurável em banks ativos.

### RF-006 — `preferObservations` efetivo no recall

- **Description:** Com observations existentes, `memory_recall {preferObservations:true}` (default true quando há observations) remove fatos brutos cujo `Id` está em `SourceMemoryIds` de uma observation retornada — a observation "supersede" suas fontes no output.
- **Rules:** `memory_get` do fato bruto continua acessível (dedup só no ranking); observation carrega `sourceFacts` expandidos quando `includeSourceFacts:true`.
- **Input → Output:** respostas consolidadas sem redundância.

## 4. Fora de escopo

- Refresh de mental models pós-consolidação (SPEC-20261111-mental-models-pages — aqui só emitir o gatilho `MemoryConsolidated` via interface `IMemoryConsolidationHook`).
- Decision model dedicado treinado (`hs-decide` equivalente) — usamos LLM genérico barato via ModelProfile.
- Consolidação cross-bank ou de documentos `KnowledgeDocument` (só `MemoryUnit`).
- UI de observations (read via MCP/REST basta nesta onda).

## 5. Contrato MCP/REST (incrementos)

```text
memory_consolidate(bankKey?, wait?) → { operationId | summary }
memory_observation_history(bankKey?, observationId) → { versions: [...] }
```

REST: `POST /api/memory/banks/{bankKey}/consolidate`, `GET /api/memory/observations/{id}/history`.
Erros: `404` bank/observation inexistente; `409` consolidação já em andamento para o bank.

## 6. Critérios de aceite

- [ ] **Dado** 3 retains "Alice é engenheira", "Alice é engenheira sênior", "Alice foi promovida a staff" **quando** consolidação roda **então** 1 observation "Alice é staff engineer (progressão: engenheira → sênior → staff)" com `ProofCount=3` e 3 `SourceMemoryIds`, e os fatos brutos marcados `ConsolidatedAt`.
- [ ] **Dado** fato novo que contradiz observation existente **então** `update` refina o texto (não sobrescreve cegamente) e `ObservationHistory` registra a versão anterior.
- [ ] **Dado** `preferObservations` no recall **então** a observation retorna e seus fatos-fonte são suprimidos do output.
- [ ] **Dado** falha do LLM no batch **então** fatos marcados `ConsolidationFailedAt`, retry no próximo tick com backoff, e nada é parcialmente commitado.
- [ ] **Dado** 2 retains idênticos em sequência **então** dedup adjudicado faz fold — uma só observation com `ProofCount=2`.
- [ ] **Edge:** bank sem fatos novos → run vazio `processed:0` sem chamada LLM; fato `verbatim` participa da consolidação normalmente.

## 7. Arquivos

**Ler antes:**
- `src/KnowledgeHub.Server/BackgroundServices/{MaintenanceBackgroundService,ScheduledSyncBackgroundService}.cs`
- `src/KnowledgeHub.Server/Ingestion/{IngestionQueue,IngestionWorker}.cs`
- `src/KnowledgeHub.Server/Settings/` (per-key settings para triage model)
- `/tmp/hindsight/hindsight-api-slim/hindsight_api/engine/consolidation/{consolidator,prompts,triage}.py`

**Criar/modificar:**
```text
src/KnowledgeHub.Server/Domain/Entities/ObservationHistory.cs
src/KnowledgeHub.Server/Memory/Consolidation/{MemoryConsolidationWorker,ConsolidationPromptBuilder,DedupAdjudicator,ConsolidationTriage,IMemoryConsolidationHook}.cs
src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs (tools novas)
src/KnowledgeHub.Server/Api/MemoryEndpoints.cs
tests/KnowledgeHub.Tests.Unit/Memory/Consolidation/*
tests/KnowledgeHub.Tests.Integration/Memory/*
```

## 8. Riscos

- Consolidação LLM por batch pode ser lenta/cara em banks grandes — mitigado por batch size, triage, schedule e `wait:false`.
- Refinamento pode degradar texto da observation (LLM drift) — mitigado por history append-only + evidence quotes obrigatórias + proof_count auditável.
- Loop de consolidação sobre observations — garantido pelo filtro `fact_type IN ('world','experience')`.
- Prompt em pt/en — mesmo princípio do retain: observation na língua dominante dos fatos-fonte, nunca traduzir.

## 9. Task Plan + DoD

- [ ] T1: `ObservationHistory` entity + campos em `MemoryUnit` + migration dual.
- [ ] T2: `MemoryConsolidationWorker` + prompts (create/update/delete/skip + adjudicação) + transação.
- [ ] T3: Triage opt-in + métricas.
- [ ] T4: `preferObservations` no recall + `memory_consolidate`/`memory_observation_history` tools + REST.
- [ ] T5: testes (unit: prompt/ações/dedup/decisões; integração: retain→consolidate→recall E2E; history) + gates de build/format.
- [ ] DoD: RF implementados, AC cobertos, ≥80% coverage nos novos serviços, histórico auditável, sem regressão na suíte.
