# gap-analysis run — 2026-10-02 r2 (user-invoked, post-E26 merges)

Resume state: GATE PASSED (user "Approved") → Phase 6 done — Epic [#503 (E27)](https://github.com/afonsoft/LangGraph-UI/issues/503), slices #504 (ci-ratchet), #505 (static-analysis), #506 (stale branches), #507 (branch protection decision). SPECs flipped to `Approved` + Ticket links. Phase 7 orchestrator handoff NOT requested by user — awaiting explicit go.

## Source inventory

- present: `.specs/` (200+ SPEC-*.md — grep de Status retorna só `Done`),
  `docs/` en+pt, `docs/architecture/` (AD-0001..0016), harness completo
  (`.claude/CONTEXT|MEMORY|rules|agents`, CLAUDE.md, AGENTS.md, README.md)
- absent: `ORCHESTRATOR-ROADMAP.md` (mesmo ausente no run r1 — não é gap novo)
- `gh auth status`: OK (afonsoft); tree limpa em `main` @ `01c6c7a`; sem submodules
- `gh issue list --state open`: **0 issues abertas** (E26 #494–#498 fechou as 13 `done` residuais)
- run anterior: `gap-analysis-20261002.md` (4 gaps → todos entregues, SPECs Done)

## Candidates + verdicts

| Key | Verdict | Evidence |
|---|---|---|
| GAP-automation-coverage-ratchet-broken | CONFIRMADO | Check-run annotation em `main` HEAD: "Unable to resolve action `actions/download-artifact@fc4bd39353f77d6ebe6f7a0b66a61cc069d5ae11`, unable to find version". `gh api repos/actions/download-artifact/commits/fc4bd39…` → 422 No commit found. Job falha em "Set up job" em **10+ pushes consecutivos** (runs 36955759330, 36955678739, 36955666873, 36955244738, 36953794485, 36953227109, 36952990359, 36952967153, 36952942854, 36952032405). Baseline `.ci/coverage-baseline.txt` = 20, parada desde `5a497b8` (#415). TO-BE: SPEC-20260929-observability-and-tests-residual RF-004 (ratchet sobe baseline quando cobertura melhora). Fix: SHA válido já existe no repo — `release.yml:138` usa `3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c` (v8.0.1, resolve via API). |
| GAP-tests-sonarcloud-residual | CONFIRMADO | SonarCloud API pública: 8 issues OPEN em `main` (era 1.093 antes de E25 RF-01 — exclusão AutoScan funcionou): S3776×5 CRITICAL (`Playground.razor:725` CC46, `Playground.razor:557` CC23, `NotionBlockRenderer.cs:240` CC18, `SearchService.cs:1187` CC16, `KnowledgeToolsProvider.cs:606` CC17), S1172 (`SearchService.cs:354` param `breakdowns` morto em `ApplyFinalTrimAsync` — residual do refactor E25 que corrigiu só `RerankAsync`), S107 (`SearchService.cs:22` ctor 14 params), S3358 (`NotionBlockRenderer.cs:106` ternário aninhado). Ressalva de processo: PR #490 mergeado com Quality Gate FAILED (Reliability C, S2583 `AgentService.cs:86` — já resolvido na main). |
| GAP-tests-codeql-residual | CONFIRMADO | `gh api code-scanning/alerts`: 29 open — cs/catch-of-all-exceptions×8 (StreamingEndpoints×4, IngestionService×2, IngestionWorker, AgentService:718, SettingsEndpoints:508, Chat.razor:143), cs/linq/missed-where|select×7, cs/useless-cast-to-self×4 (SearchService 979/986/1013/1035), cs/path-combine×4, cs/missed-using-statement×2, empty-catch, missed-ternary (`Login.razor:179` — mesmo site do S5146 já corrigido), **1 error**: cs/unused-collection `GitRepositoryConnector.cs:168`. Maioria note/warning (catches intencionais logam). SPEC-20260929-codeql-quality-debt está Done — isso é residual novo acumulado pós-wave. |
| GAP-operation-stale-branches | CONFIRMADO | `git branch -r --no-merged origin/main`: ~30 branches remotas (`devin/*` ×14, `docs/*` ×6, `feature/*` ×9, `chore/*` ×1) — todas de PRs squash-mergeados (git as marca unmerged porque o tip não é ancestral). +1 local `feature/Devin-20261001-a2a-task-durability`. SPECs 20260917-merged/stale-branch-cleanup existem (Done) mas a acumulação recorreu — falta automação/gate. |
| sonarcloud-gate-bypass-490 | REJEITADO | O issue S2583 específico está resolvido na main (ausente das 8 OPEN). A parte processual ("gate vermelho não bloqueia merge") é config de branch protection fora do escopo de código — registrada aqui, não vira SPEC sem decisão do usuário. |
| s2583-agentservice-86 | REJEITADO | Resolvido pós-merge (ver acima). |

## Priorities

1. `coverage-ratchet-broken` — **impacto alto** (main exibe ❌ em todo push; gate de PR compara contra baseline de 20% parada → ratchet inútil), esforço trivial (1 linha), confiança total (erro reproduzido via API).
2. `sonarcloud-residual` — impacto médio-baixo (8 issues, 5 complexidades altas), esforço médio.
3. `codeql-residual` — impacto baixo (28 notes + 1 error), esforço baixo-médio.
4. `stale-branches` — impacto baixo (higiene), esforço trivial (delete remoto) + decisão sobre automação.

## Draft SPECs (gate pending)

- `.specs/SPEC-20261002-ci-coverage-ratchet-pin.md`
- `.specs/SPEC-20261002-static-analysis-residual.md`
- `.specs/SPEC-20261002-merged-branch-recleanup.md`

## Pendencies

1. ~~GATE~~ → aprovado; Epic #503 + slices #504–#507 criadas; SPECs → `Approved`.
2. Fase 7 (orchestrator) aguardando go explícito do usuário.
3. Achados tardios do verification loop (qa-analyst): format gate não existe em CI (47 violações na main) + lock-file drift sem `--locked-mode` → comentados na #504 (mesmo escopo CI).
