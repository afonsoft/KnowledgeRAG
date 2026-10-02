# gap-analysis run — 2026-10-02 (invoked by /architecture after #493)

Resume state: SPECs Draft written, AWAITING GATE (issue creation approval).

## Source inventory

- present: `.specs/` (100+ SPEC-*.md, all `Done`), `docs/` (en+pt mirrors), `docs/architecture/` (AD-0001..0016, 4 .mmd, .drawio, archify pair), `.claude/CONTEXT.md`/`MEMORY.md`/`memory/`/`rules/`/`agents/`, `CLAUDE.md`, `AGENTS.md`, `README.md`
- absent: `ORCHESTRATOR-ROADMAP.md`
- `gh auth status`: OK (devin-ai-integration[bot]); clean tree on `main`; no submodules

## Candidates + verdicts

| Key | Verdict | Evidence |
|---|---|---|
| GAP-documentation-architecture-embedded-drift | CONFIRMADO | `system-architecture.md:144` "(catalog stays in SQLite)" vs `CatalogDatabase.cs:82` (postgres provider moves catalog — AD-0014); embedded deployment mermaid missing `/framework-assets` (`FrameworkAssetsEndpoints.cs:23`, .mmd R8 has it) and claims dev `:5000` vs launchSettings `:5009` (only `A2aFallbackBaseUrl` uses 5000 — `Program.cs:234`) |
| GAP-documentation-api-a2a-durability | CONFIRMADO | `docs/en/API.md` + `docs/pt/API.md` §A2A document card/send/tasks only — no `tasks/pushNotificationConfig` CRUD, `X-KH-Signature`, persisted tasks, `A2a:TaskRetentionHours`, `origin` frontmatter (all shipped in #445: `EfA2aTaskStore.cs`, `KnowledgeHubA2AServer.cs`, `A2aPushNotifier.cs`) |
| GAP-documentation-runtime-archify-stale | CONFIRMADO | `runtime-architecture.json/.html` (archify 2.17.0-dev.1) contain zero A2A references — generated pre-#425; regeneration blocked (archify absent, skill forbids install). Disclosed as stale in PR #493 |
| GAP-operation-done-issues-open | CONFIRMADO | `gh issue list --state open`: 13 Issues with `done` label still open — #451, #452, #453, #458, #480, #481–#486 |
| mmd-toolcall-sequence-mcp-only | REJEITADO | covered by new `knowledge-hub_a2a_sequence.mmd` (#493) |
| bilingual-docs-drift | REJEITADO | `docs/pt/` mirrors `docs/en/` — same commit `bd55dc7`, API.md 148 lines both |
| allowed-hosts-localhost-claim | REJEITADO | `appsettings.json:40` pins localhost; `docker-compose.yml:33-36` override documented inline |

## Priorities

All CONFIRMADO: low effort (docs/ops), low risk. Highest signal: api-docs-a2a-durability (integrator-facing).

## Draft SPECs (gate pending)

- `.specs/SPEC-20261002-architecture-embedded-drift.md`
- `.specs/SPEC-20261002-api-docs-a2a-durability.md`
- `.specs/SPEC-20261002-runtime-archify-stale.md` (decision a/b pending — recommended: retire)
- `.specs/SPEC-20261002-close-done-issues.md`

## Pendencies

1. GATE: user approval → Phase 6 create-issues (epic `gap-analysis-20261002` + 4 slices) → Phase 7 orchestrator handoff.
2. runtime-archify-stale needs the (a) regenerate / (b) retire decision.
