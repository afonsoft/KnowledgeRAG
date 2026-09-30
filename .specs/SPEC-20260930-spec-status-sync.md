# SPEC-20260930-spec-status-sync

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `spec-status-sync` |
| Type | `Docs` (processo) |
| Stack | `.specs/` metadata |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260930-spec-status-sync` |
| Status | `Done` |
| Source | `GAP-documentation-spec-status-drift` (gap-analysis-20260930) |

## 1. User Story

**As a** mantenedor
**I want** que SPECs cujas implementações já mergearam estejam `Done`
**So that** `Status` reflita a realidade — hoje 15 SPECs `Approved` têm código já em produção (drift documentado).

## 2. Findings

- 🟡 AS-IS: `grep 'Status | Approved'` retorna 15 SPECs cujas implementações já foram mergeadas:
  - wave 20260929 (10): `search-scope-pipeline-hardening` (#407), `connector-security-sync-safety` (#408), `resilience-fallback-hardening` (#409), `agent-context-window-compaction` (#410), `evidence-chain-integrity` (#411), `temporal-graph-correctness` (#412), `live-actions-bridge-hardening` (#413), `codeql-quality-debt` (#414), `observability-and-tests-residual` (#415), `docs-and-ux-nits` (#417) — issues #397–#406 fechadas.
  - wave 20260928 (5): `graph-timeline-viewer`, `observability-followups`, `post-pentagi-wave-docs-sync`, `resilience-tool-fallback-wiring`, `test-reliability-and-coverage-gate`.
- 🟡 TO-BE: `Status` sincronizado — `Done` quando implementado/mergeado. Regra já existe no `CLAUDE.md` ("manter Status/Ticket sincronizados") mas não foi aplicada no fechamento das waves.

## 3. Requirements

1. **RF-001** — Para cada SPEC `Approved` listada, verificar evidência de entrega (PR mergeado ou feature presente em `main`); se entregue, `Status → Done` e registrar o PR/evidência no campo `Source`/`Ticket` quando ausente.
2. **RF-002** — SPECs sem evidência de entrega permanecem `Approved` e são listadas na seção de achados do PR como pendências reais (não-inferir Done).
3. **RF-003** — Incluir os SPECs Draft desta análise (`pwa-stale-cache-eviction`, `migration-populated-db-tests`) no mesmo sweep apenas se aprovadas pelo usuário — caso contrário mantê-las `Draft`.
4. **RF-004** — Atualizar `.claude/memory/memory.md` com o resultado.

## 4. Acceptance Criteria

- `grep -c 'Status | .Approved' .specs/*.md` retorna apenas SPECs genuinamente não-implementadas.
- Nenhum SPEC marcado `Done` sem evidência citada.

## 5. Task Plan

1. Para cada um dos 15 SPECs: `gh pr list --search` / verificar feature no código → flip status + anotar evidência.
2. Commit docs → PR → merge.
