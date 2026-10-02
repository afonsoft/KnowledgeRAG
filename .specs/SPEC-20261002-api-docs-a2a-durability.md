# SPEC-20261002-api-docs-a2a-durability — Document A2A durability surface in API.md (en+pt)

| Campo | Valor |
|-------|-------|
| Status | Done |
| Ticket | Epic [#494 (E26)](https://github.com/afonsoft/LangGraph-UI/issues/494) — slice [#496](https://github.com/afonsoft/LangGraph-UI/issues/496) |
| Origem | /gap-analysis run — audit docs↔code |
| Tipo | Docs |

## 1. User Story

As an A2A integrator, I want `docs/en/API.md` (and the pt mirror) to document the durability surface shipped in #445, so I can use persisted tasks and push webhooks instead of polling.

## 2. Scope

In scope: `docs/en/API.md` §A2A + `docs/pt/API.md` §A2A (bilingual mirror rule — `docs/en/CONTRIBUTING.md:32`).
Out of scope: architecture docs (done via #493), code changes.

## 3. Technical Context

AS-IS: API.md §A2A covers card, `message/send`, `tasks/get|list|cancel` only — no durability.
TO-BE evidence (code):
- `src/KnowledgeHub.Server/A2A/EfA2aTaskStore.cs` — tasks persist in catalog DB.
- `src/KnowledgeHub.Server/A2A/KnowledgeHubA2AServer.cs` — `tasks/pushNotificationConfig` CRUD + inline `SendMessageConfiguration.PushNotificationConfig`.
- `src/KnowledgeHub.Server/A2A/A2aPushNotifier.cs` — webhook POST + `X-KH-Signature` HMAC, retry.
- `A2a:TaskRetentionHours` (72 h default) purge; `WriteOriginContext` frontmatter on delegated writes.

## 4. Requirements

- RF-01: Document task persistence (`tasks/get` survives restarts; retention `A2a:TaskRetentionHours` default 72 h).
- RF-02: Document push notifications: `tasks/pushNotificationConfig/{get,set,delete}` (JSON-RPC method names as implemented) + `SendMessageConfiguration.PushNotificationConfig`; `X-KH-Signature` HMAC signature header; egress-validated URLs.
- RF-03: Mirror everything in `docs/pt/API.md`.
- RF-04: Document `origin` frontmatter on `write_knowledge`/`write_note` delegated via A2A/MCP.

## 5. API Contract

Extend the existing §A2A subsection — JSON-RPC method names must match `KnowledgeHubA2AServer` registrations exactly.

## 6. Acceptance Criteria

- Given the API.md A2A section, an integrator can configure push notifications and understand task lifecycle without reading code.
- Given `docs/en/API.md` and `docs/pt/API.md`, both contain the new subsections.

## 7. Task Plan

1. Read `KnowledgeHubA2AServer.cs` for exact method names + payload shapes.
2. Write en section; mirror pt.
3. Validate JSON examples are syntactically valid.

## 8. Organization Guardrails

Docs-only commit `docs(api):`, one PR touching both en+pt per the bilingual rule.

## 9. Definition of Done

RF-01..RF-04 verified against code; both mirrors updated; PR merged.
