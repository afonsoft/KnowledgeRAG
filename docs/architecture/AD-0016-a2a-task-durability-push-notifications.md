# AD-0016 — A2A task durability: EF-persisted tasks, retention purge, signed push notifications

## Context

The A2A v1.0 surface (AD recorded in the 2026-09-26 wave, surfaces shipped in
#425) initially used the SDK's in-memory `InMemoryTaskStore`: every `message/send`
task vanished on restart, so a client could not `tasks/get` a long-running job
after a deploy, and long-lived tasks had no completion channel other than
client polling. Delegated writes (`write_knowledge`/`write_note` invoked via
A2A or MCP) also left no provenance trail distinguishing agent traffic from UI
writes.

SPEC-20261001-a2a-task-durability (issue #442, epic #440, PR #445) hardened the
surface for production use.

## Decision

- **EF Core task store.** `EfA2aTaskStore` replaces `InMemoryTaskStore` and
  persists full `Task` payloads (artifacts, history, metadata) to the catalog
  database — SQLite or Postgres depending on `Database__Provider` (AD-0014).
  Task rows carry a `contextId` that is **not** mapped to `agent_chat`
  threadIds (A2A contexts are hex-32 protocol identifiers, not chat threads).
- **Bounded retention.** `MaintenanceBackgroundService` purges tasks older
  than `A2a:TaskRetentionHours` (default 72h); task volume is a debug/trace
  surface, not a document store.
- **Push notifications.** `KnowledgeHubA2AServer` implements the push-config
  CRUD plus inline `SendMessageConfiguration.PushNotificationConfig`.
  `A2aPushNotifier` POSTs task updates to the registered webhook URL with an
  `X-KH-Signature` HMAC header (key `evidence:master`), 3 retries with
  exponential backoff. Webhook URLs are validated through
  `EgressPolicyHandler.IsBlockedAsync` before any outbound call (SSRF guard).
  The Agent Card exposes `capabilities.pushNotifications` only when enabled.
- **Origin frontmatter.** `WriteOriginContext` stamps `origin:
  {channel: mcp|a2a, keyId, agentName, at}` into the frontmatter of delegated
  `write_knowledge`/`write_note` calls, so writes made by agents are
  distinguishable and auditable (complements the EvidenceChain trail, AD-0015).
- **Progressive reporting.** `ToolCallContext.OnProgress` flows through
  `AgentRequest.OnProgress` into `TaskUpdater` working-state events with a 2s
  heartbeat, so A2A clients observe incremental progress, not just the
  terminal state.

Rejected alternatives: keeping the in-memory store + documenting restart
loss (breaks long-running ingestion/answer tasks across deploys); separate
task database (violates the single-store philosophy, AD-0002/AD-0014);
unsigned push (spoofable task-state injection into client agents).

## Consequences

- Positive: tasks survive restarts and deploys; clients can rely on
  `tasks/get` and webhooks instead of long-polling; delegated writes are
  attributable; push URLs cannot be pointed at internal addresses.
- Trade-off: task table grows between purges (bounded by 72h retention);
  webhook secrets are symmetric (rotated via settings); one more EF entity
  in the catalog DB adds a small migration surface per provider.

## Related SPEC

- [.specs/SPEC-20261001-a2a-task-durability.md](../../.specs/SPEC-20261001-a2a-task-durability.md)
