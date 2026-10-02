# SPEC-20261002-runtime-archify-stale — Regenerate or retire stale archify runtime artifacts

| Campo | Valor |
|-------|-------|
| Status | `Approved` |
| Ticket | Epic [#494 (E26)](https://github.com/afonsoft/LangGraph-UI/issues/494) — slice [#497](https://github.com/afonsoft/LangGraph-UI/issues/497) |
| Origem | /gap-analysis run — audit docs↔code |
| Tipo | Docs |

## 1. User Story

As a docs consumer, I want `docs/architecture/runtime-architecture.{json,html}` to either reflect the current architecture or stop existing, so the directory contains no misleading artifacts.

## 2. Scope

In scope: `docs/architecture/runtime-architecture.json` + `runtime-architecture.html` (archify 2.17.0-dev.1 generated; zero A2A nodes — grep confirms no `a2a`/`A2A` references).
Out of scope: installing archify in CI/blueprints (skill policy: never install at runtime).

## 3. Technical Context

AS-IS: artifacts predate A2A (#425) + durability (#445) waves; regeneration requires the `archify` binary which is absent from the session VM and not in any blueprint.
TO-BE: README.md lists them as "Interactive runtime diagram" — the claim only holds if content is current.

## 4. Requirements

- RF-01: Decision taken (user, 2026-10-02): **keep** the artifacts and regenerate later via `architecture`/`archify` when the binary is available — never install it at runtime.
- RF-02: Add a staleness note in `docs/architecture/README.md` (runtime-architecture rows): "generated 2026-09, predates A2A (#425) and durability (#445) — regenerate via archify when available".
- RF-03: When regeneration happens, validate the artifact contains the A2A surface before committing.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- Given `docs/architecture/README.md`, the runtime-architecture rows carry an explicit staleness note.

## 7. Task Plan

1. Add the staleness note to the two README.md rows; PR.

## 8. Organization Guardrails

Docs-only; commit `docs(architecture): flag stale archify runtime artifacts`.

## 9. Definition of Done

README index notes the artifacts are stale pending regeneration; PR merged.
