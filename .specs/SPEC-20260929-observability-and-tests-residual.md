# SPEC-20260929-observability-and-tests-residual

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `observability-and-tests-residual` |
| Type | `Fix` (residual das slices S4/S5 + testes rasos) |
| Stack | `.NET 10 / C#` — ingestion feed, telemetry, coverage gate |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-observability-residual` |
| Status | `Draft` |
| Source | Comentários `devin-ai-integration` em PRs #389, #393, #394 |

## 1. User Story

**As a** mantenedor
**I want** os resíduos de observabilidade/test-depth corrigidos
**So that** o evento de job órfão alcance clientes que reconectam após restart e os novos spans marquem erro corretamente.

## 2. Findings

1. 🟡 `FailOrphanedJobsAsync` publica `IngestionProgressEvent` mas quem conecta no SSE/SignalR depois do restart nunca vê — o feed é in-memory-only; precisa de replay do último estado por job (persistir último evento ou re-emitir on-subscribe).
2. 🟡 Falhas de live tool (`IsError=true` resultado, não exceção) aparecem como spans de sucesso — `search.live_actions` marca Error só no `catch`; `CallToolResult.IsError` deve marcar Error + `otel.status_code=ERROR` mesmo sem exception.
3. 🟡 Falhas de `EvidenceEmission`/`AppendAsync` são swallowed em `LogWarning` sem marcar o span `evidence.emit` como Error.
4. 🟡 Coverage gate usa floor fixo (20) — regressões pequenas passam. Melhorar para: falhar se `line-rate` do PR < `line-rate` de `main` (diff coverage) ou elevar a cada merge.
5. 🔍 `TelemetryTests` não comprova a hierarquia dos novos spans (parent correto em `search.temporal_graph`/`evidence.emit`/`live_actions`) — falta assert `ParentSpanId`.
6. 🔍 Teste REST de integração com DB vazio não exercita poda/expansão (`AutocutSearchApiTests` e similares).

## 3. Requirements

- RF-001: `IngestionProgressFeed` mantém último evento por `JobId` e re-emite para novos subscribers (ou o endpoint de feed inclui `LastEvent` por job ativo/falho).
- RF-002: `McpDynamicRagActionBridge` marca o span `Error` quando `result.IsError == true` (não só em catch).
- RF-003: `EvidenceEmission` marca span `Error` no catch (antes de LogWarning).
- RF-004: Coverage gate compara com baseline armazenado (coverage de main publicado como artifact + download no PR) — ou, no mínimo, `COVERAGE_MIN` incrementa automaticamente quando main sobe.
- RF-005: Telemetry tests validam `ParentSpanId` == root da request para spans novos.
- RF-006: Teste de integração dos endpoints de busca/expansão com DB seedado (não vazio).

## 4. Acceptance Criteria

- AC-1: subscriber tardio vê o evento terminal de job órfão — teste.
- AC-2: `IsError=true` → span com `Status=Error` — teste.
- AC-3: `ParentSpanId` assert presente nos testes novos.
- AC-4: coverage não regride silenciosamente — gate comparado à baseline.
