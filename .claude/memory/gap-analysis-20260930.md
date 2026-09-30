# Gap Analysis — 2026-09-30

Run da skill `gap-analysis` (afonsoft/skills) sobre `afonsoft/LangGraph-UI`, branch `feature/Devin-20260930-sonarcloud-gate-fixes`.

## Source inventory

`.specs/` 179 SPECs (9 sem `Status` tabular → 2 confirmados sem campo, resto `Done`), `docs/` EN+PT, `docs/architecture/`, `.claude/{CONTEXT,MEMORY,memory,rules,agents}`, `CLAUDE.md`, `AGENTS.md`, `README.md` — todos PRESENT. `gh` autenticado (afonsoft). Submodules: none.

Trigger da sessão: incidentes de produção — página não abria após deploy (SW antigo vs assets .NET 10 novos), 502 em `rag.afonsoft.dev` (`.env` deletado → porta 5000 em vez de 5550), startup crash `23502` na migration `Labels` (corrigido em PR #425).

## Candidatos e verdicts

| Key | Categoria | Verdict | Evidência |
| --- | --- | --- | --- |
| GAP-documentation-spec-status-drift | documentation | **CONFIRMADO** | 15 SPECs `Approved` com impl. mergeada (PRs #407–#417 + wave 09-28); `CLAUDE.md` exige sync |
| GAP-implementation-pwa-stale-cache-eviction | implementation | **CONFIRMADO** | `service-worker.published.js:51-56` deleta todas as gerações anteriores em `onActivate`; logs de 404 `blazor.boot`/`dotnet.wasm` de browser real pós-deploy |
| GAP-tests-migration-populated-db | tests | **CONFIRMADO** | Único teste de migration (`UnifiedDatabaseProviderTests`) roda em DB vazio; bug `Labels` passou por CI+Sonar |
| GAP-operation-env-required-false | operation | **INCONCLUSIVO** | `env_file required:false` é decisão deliberada (comentário no compose) mas `.env` deletado degradou prod silenciosamente (porta errada → 502). Tradeoff prod-vs-dev precisa decisão do usuário |
| GAP-operation-agent-card-wellknown-edge | operation | **INCONCLUSIVO** | `/.well-known/agent-card.json` → 404 via Cloudflare (request nunca chega ao container; local 200). Fix é config de zona fora do repo — ação do usuário; repo pode apenas documentar |
| GAP-automation-code-smell-backlog | automation | **DUPLICADO** | 121 CODE_SMELL new code já tracked pelo SonarCloud + nota em SPEC-20260930-sonarcloud-gate-blockers |
| GAP-implementation-addcolumn-defaults | implementation | **REJEITADO** | Scan de todas as migrations Postgres: nenhum outro `nullable:false` sem default em tabela existente (coberto pelo novo teste GAP-tests-*) |
| GAP-implementation-framework-assets-legacy | implementation | **REJEITADO** | Mirror `/framework-assets` para nomes .NET antigos removido por design (.NET 10 mudou o naming) — clientes antigos devem hard-refresh |

## SPECs Draft gerados

- `.specs/SPEC-20260930-pwa-stale-cache-eviction.md` (GAP-implementation-pwa-stale-cache-eviction)
- `.specs/SPEC-20260930-migration-populated-db-tests.md` (GAP-tests-migration-populated-db)
- `.specs/SPEC-20260930-spec-status-sync.md` (GAP-documentation-spec-status-drift)

## Pendências / gate

- Aguardando aprovação do usuário: marcar SPECs Approved + criar Epic `gap-analysis-20260930` + slice issues (create-issues) + handoff orchestrator.
- INCONCLUSIVOs requerem decisão: `env_file.required` em prod; regra Cloudflare para `/.well-known/*`.
- Sonar autofix em paralelo: PR #426 (11 gate-blockers) aberto; 121 smells ficam como backlog registrado.
