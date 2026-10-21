# gap-analysis — 2026-10-21 — context-snapshots post-merge audit

Branch: `feature/Devin-20261021-gap-analysis-context`. Target: SPEC-20261004-context-snapshots (merged as PR #586, issue #585 closed). gh auth: ok (afonsoft).

## Source inventory
present: .specs/, docs/, docs/architecture/, .claude/{CONTEXT,MEMORY,memory,rules,agents}, CLAUDE.md, AGENTS.md, README.md — absent: ORCHESTRATOR-ROADMAP.md.

## Verdicts

### CONFIRMADO
| Key | Gap | Evidence |
|---|---|---|
| GAP-impl-rest-source-tag | REST saves gravam `Source="mcp"` — `ContextToolsProvider.SourceTag` hardcoded; `ContextEndpoints.InvokeAsync` reusa o handler sem trocar a origem (RF-002 enum `agent|mcp|rest`) | `Mcp/ToolProviders/ContextToolsProvider.cs` SourceTag; `Api/ContextEndpoints.cs` InvokeAsync |
| GAP-impl-rest-status-201 | `POST /api/context/snapshots` retorna 200; contrato da SPEC pede 201 (API table) | `ContextEndpoints.InvokeAsync` → `Results.Ok` |
| GAP-impl-compact-outputschema | `context_compact.OutputSchema = RecoveryOutputSchema` exige `items`+`stats`; a resposta real emite `compacted`+`stats`+`savedVersion` — drift de contrato | `ContextToolsProvider.cs` CompactSchema/OutputSchema |
| GAP-tests-concurrency | AC "5 context_save em paralelo → versões 1..5" sem teste | SPEC linha ~288; ausente em tests/ |
| GAP-tests-postgres-conditional | SPEC exige teste Postgres condicional (`Postgres:ConnectionString`); existe pattern `PostgresVectorStoreLiveTests` (Testcontainers) mas nada para ContextSnapshots | SPEC linhas 90/362; `tests/.../PostgresVectorStoreLiveTests.cs` |
| GAP-ops-eval-matrix | T12 eval matrix (strategy × tokenBudget, gate faithfulness) não implementada; `elision+summary` segue opt-in | SPEC T12; nenhum harness eval de contexto |
| GAP-docs-spec-status | SPEC status `In implementation` após merge — deve virar `Done` quando pendências fecharem | `.specs/SPEC-20261004-context-snapshots.md:13` |
| GAP-ops-rest-ratelimit | `POST /api/context/compact` dispara summarization LLM sob `GeneralPolicy` (300/min); partição `context` 60/min só vale no dispatcher MCP | `Program.cs:521`; `McpToolRateLimiter` |

### REJEITADO
- Isolamento por repo (qualquer owner/name válido — decisão Q2) — teste `Recovery_OtherRepo_NotFound` cobre.
- `withinBudget=false` sem erro — `OnlyProtectedRemain_OverBudget_WithinBudgetFalse` cobre.
- Secret scan/kinds/limites — `ContextPayloadTests` cobre.
- Cache hit/invalidation/tag-isolation — `Cache_SecondRecovery_ReportsHit_AndSaveInvalidates` cobre; `context_` excluído do `IToolCacheService` (D-1).
- jsonb Postgres — migration `20261021013843` usa `type: "jsonb"`.
- IdempotencyKey — `IdempotencyKey_SameKeyReturnsSameVersion` cobre.
- `ElisionStub` inexistente — decisão registrada na SPEC (código novo).

## Prioridade
1. GAP-impl-* (P1: contrato público REST/MCP errado)
2. GAP-tests-* (P2: ACs sem cobertura)
3. GAP-ops-eval-matrix (P2: bloqueia default `elision+summary`)
4. GAP-ops-rest-ratelimit (P3)
5. GAP-docs-spec-status (P3: mecânico, ao fechar pendências)

## Draft SPECs
- `.specs/SPEC-20261021-context-rest-contract-fixes.md`
- `.specs/SPEC-20261021-context-test-coverage.md`
- `.specs/SPEC-20261021-context-eval-matrix.md`

## Resultados de implementação (2026-10-21, Epic #587)

Todos os gaps confirmados foram corrigidos na branch `feature/Devin-20261021-gap-analysis-context`:

- **GAP-1 (Source)**: `ToolCallContext.Transport` adicionado; REST passa `"rest"`, MCP mantém `"mcp"` via `SourceOf(ctx)`. Teste `Rest_Save_Returns201_AndSourceIsRest` cobre.
- **GAP-2 (201)**: `POST /api/context/snapshots` → `201 Created` + `Location`. Teste atualizado.
- **GAP-3 (OutputSchema)**: `context_compact` declara `CompactOutputSchema` próprio (`compacted`, `droppedCount`, `elidedCount`, `savedVersion`, `withinBudget`).
- **GAP-4 (concorrência)**: `Concurrency_FiveSaves_ProduceVersions1To5` — 5 saves paralelos via MCP → versões 1..5.
- **GAP-5 (Postgres)**: `ContextToolsPostgresTests` com fixture Testcontainers (skip sem Docker).
- **GAP-6 (eval matrix)**: `ContextEvalMatrixTests` — 3 estratégias × 4 budgets × {com,sem} LLM = 24 casos; resultados publicados em `docs/en/CONTEXT-EVAL-MATRIX.md`. Decisão RF-3: manter default `elision+summary`.
- **GAP-7 (rate-limit REST)**: policy `context` HTTP (60/60s via `ContextPermitLimit`/`ContextWindowSeconds`) aplicada a `POST /api/context/compact`.
- **GAP-8 (SPEC status)**: SPEC-20261004 → `Done`.
