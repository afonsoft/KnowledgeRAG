# SPEC-20261002-ci-coverage-ratchet-pin — Fix dead download-artifact pin in CI ratchet

| Campo | Valor |
|-------|-------|
| Status | `In Review` — PR [#509](https://github.com/afonsoft/LangGraph-UI/pull/509) |
| Ticket | Epic [#503 (E27)](https://github.com/afonsoft/LangGraph-UI/issues/503) — slice [#504](https://github.com/afonsoft/LangGraph-UI/issues/504) (RF-001..003) |
| Origem | gap-analysis — relatório `.claude/memory/gap-analysis-20261002-r2.md` |

## Contexto

O job `Coverage Baseline Ratchet` (`.github/workflows/ci-build-test.yml:198-231`)
falha em "Set up job" em **todos os pushes à main** (10+ runs consecutivos).
Causa raiz confirmada via check-run annotation e GitHub API: o pin
`actions/download-artifact@fc4bd39353f77d6ebe6f7a0b66a61cc069d5ae11`
(`ci-build-test.yml:211`, anotado `# v8`) **não resolve** — o commit não existe
no repo `actions/download-artifact` (HTTP 422).

Consequências:
- Workflow `🚀 CI Build & Test` exibe ❌ em todo commit de `main` (falso negativo crônico).
- `.ci/coverage-baseline.txt` está parada em `20` desde `5a497b8` (PR #415) —
  o ratchet nunca executou. O gate de PR que compara cobertura contra a
  baseline usa um valor defasado e excessivamente permissivo.

O SHA correto já está em uso no próprio repo: `release.yml:138` pinna
`actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c` (v8.0.1),
que resolve via API (`gh api repos/actions/download-artifact/commits/3e5f45b…` → 200).

## Requisitos funcionais

### RF-001 — Substituir o pin quebrado

- [ ] `.github/workflows/ci-build-test.yml:211`: trocar
      `actions/download-artifact@fc4bd39353f77d6ebe6f7a0b66a61cc069d5ae11   # v8`
      por `actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c   # v8.0.1`
      (mesmo SHA já usado em `release.yml:138` — consistência de pins).

### RF-002 — Validar o ratchet de ponta a ponta

- [ ] Após merge, o próximo push à `main` deve executar `Coverage Baseline
      Ratchet` até o fim (não falhar em "Set up job"); log "measured X% vs
      baseline 20%" visível no job.
- [ ] Se a cobertura medida > 20%, o job commita a nova baseline em
      `.ci/coverage-baseline.txt` com `[skip ci]` (comportamento existente,
      `.github/workflows/ci-build-test.yml:222-228`).

### RF-003 — Sweep de pins mortos (prevenção)

- [ ] Verificar todos os pins SHA em `.github/workflows/*.yml` resolvem
      (`gh api repos/{owner}/{action}/commits/{sha}`). Hoje confirmados OK:
      `actions/checkout@3d3c42e5`, `actions/upload-artifact@043fb46`,
      `actions/download-artifact@3e5f45b`. Registrar qualquer outro morto.

## Critérios de aceite

- `gh run list --branch main` mostra `🚀 CI Build & Test` com `success` no
  primeiro push pós-merge, incluindo o job `Coverage Baseline Ratchet`.
- `.ci/coverage-baseline.txt` ≥ taxa real de cobertura atual (nunca rebaixada).

## Fora de escopo

- Alterar política de branch protection para bloquear merges com SonarCloud
  gate FAILED (decisão separada, ver relatório de gaps).
- Elevar a baseline manualmente — o ratchet faz isso sozinho quando consertado.
