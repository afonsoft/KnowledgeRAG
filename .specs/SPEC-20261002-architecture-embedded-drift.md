# SPEC-20261002-architecture-embedded-drift — Sync embedded copies in system-architecture.md

| Campo | Valor |
|-------|-------|
| Status | Done |
| Ticket | Epic [#494 (E26)](https://github.com/afonsoft/LangGraph-UI/issues/494) — slice [#495](https://github.com/afonsoft/LangGraph-UI/issues/495) |
| Origem | /gap-analysis run — audit docs↔code |
| Tipo | Docs |

## 1. User Story

As a reader of `docs/architecture/system-architecture.md`, I want the embedded claims and diagrams to match the code and the standalone `.mmd` mirrors, so that the doc is trustworthy.

## 2. Scope

In scope: three stale statements in `docs/architecture/system-architecture.md` on `main` (post-#493).
Out of scope: `.mmd` mirrors (already correct), code changes, other docs.

## 3. Technical Context

File: `docs/architecture/system-architecture.md`.
Mirror source of truth: `knowledge-hub_deployment.mmd` (R8 `/framework-assets`), `AD-0014`.

## 4. Requirements

- RF-01: Storage row (line ~144): "(catalog stays in SQLite)" → rewrite to reflect AD-0014 unified provider — `Database:Provider` puts catalog AND vector store on the same backend (sqlite | postgres). Evidence: `src/KnowledgeHub.Server/Data/CatalogDatabase.cs:82` (postgres requires `Database:ConnectionString`; catalog EF moves with the provider).
- RF-02: Embedded deployment mermaid (section 5): add `/framework-assets/{stem}/{ext}` route row, matching `knowledge-hub_deployment.mmd` R8. Evidence: `src/KnowledgeHub.Server/Api/FrameworkAssetsEndpoints.cs:23` (`MapGroup("/framework-assets").AllowAnonymous()`).
- RF-03: Same mermaid: ":5000 dev" → ":5009 dev" (launchSettings.json `applicationUrl` `http://localhost:5009`); or drop the dev-port claim entirely — the only `:5000` in code is `A2aFallbackBaseUrl` (Program.cs:234), a client-facing default, not the Kestrel dev port.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- Given `system-architecture.md` on main, when compared to `CatalogDatabase.cs`, `FrameworkAssetsEndpoints.cs` and `launchSettings.json`, then no claim contradicts code.
- Given the embedded deployment mermaid and `knowledge-hub_deployment.mmd`, then both list the same routes.

## 7. Task Plan

1. Edit the 3 spots in `system-architecture.md`. Validate: extract embedded mermaid → `npx -y @mermaid-js/mermaid-cli` renders clean.

## 8. Organization Guardrails

Docs-only commit `docs(architecture):`, PR into `main`.

## 9. Definition of Done

All RF checkboxes verified against cited evidence; mermaid blocks validate; PR merged.
