# docs/architecture — index

Architecture deliverables for KnowledgeHub. Source of truth for decisions:
`AD-*.md`; rendered diagrams: `system-architecture.md` (embedded Mermaid),
`runtime-architecture.html` (interactive, archify).

## ADRs

| AD | Decision |
|---|---|
| [AD-0001](AD-0001-single-process-modular-monolith.md) | Single-process modular monolith (SPA + REST + MCP + SignalR) |
| [AD-0002](AD-0002-sqlite-first-pluggable-vector-stores.md) | SQLite-first persistence, pluggable `IVectorStore` (sqlite/sqlite-vec/pgvector) |
| [AD-0003](AD-0003-mcp-hybrid-session-mode.md) | MCP hybrid session mode (`StatefulForInitializeClients`) |
| [AD-0004](AD-0004-dual-auth-cookie-api-keys.md) | Cookie sessions + hashed `aft_*` API keys with per-key scopes |
| [AD-0005](AD-0005-hybrid-retrieval-pipeline.md) | Hybrid retrieval FTS5+vector RRF, optional rewrite/rerank |
| [AD-0006](AD-0006-graphrag-sqlite-adjacency.md) | GraphRAG on SQLite adjacency tables (dedicated graph DB deferred) |
| [AD-0007](AD-0007-partitioned-rate-limiting.md) | Partitioned rate limiting + per-API-key overrides |
| [AD-0008](AD-0008-prompt-injection-guard.md) | Prompt-injection guard (flag, exclude, provenance) |
| [AD-0009](AD-0009-opentelemetry-observability.md) | OpenTelemetry metrics/traces, opt-in OTLP/Prometheus |
| [AD-0010](AD-0010-upstream-mcp-proxies.md) | Upstream MCP proxies with encrypted per-scope secrets |
| [AD-0011](AD-0011-async-ingestion-queue.md) | Persisted async ingestion queue (202+jobId, cancel, selective reindex) |
| [AD-0012](AD-0012-hybrid-cache-l1l2-invalidation.md) | Hybrid L1/L2 cache, per-region TTLs, `kh:invalidate` pub/sub |
| [AD-0013](AD-0013-env-composed-postgres-vector-store.md) | `.env`-composed `POSTGRES_*` connection string for external/host pgvector |
| [AD-0014](AD-0014-unified-database-provider.md) | Single backend for catalog EF + vector store — `Database:Provider` auto/postgres/sqlite |
| [AD-0015](AD-0015-agentic-rag-resilience-evidence-temporal.md) | Agentic-RAG wave — resilience fallback, HMAC evidence chain, temporal/episodic KG, action bridge, ChainAst compaction |
| [AD-0016](AD-0016-a2a-task-durability-push-notifications.md) | A2A task durability — EF task store, retention purge, signed push webhooks, origin frontmatter |

## Diagrams

| File | Format | Content |
|---|---|---|
| [system-architecture.md](system-architecture.md) | Markdown + Mermaid | Context/container, MCP + A2A sequences, deployment, config surface |
| [knowledge-hub_context_container.mmd](knowledge-hub_context_container.mmd) | Mermaid | C4-style context + container view |
| [knowledge-hub_toolcall_sequence.mmd](knowledge-hub_toolcall_sequence.mmd) | Mermaid | `tools/call` sequence (local / graph / upstream) |
| [knowledge-hub_a2a_sequence.mmd](knowledge-hub_a2a_sequence.mmd) | Mermaid | A2A `message/send`→`tasks/get` sequence + push webhooks |
| [knowledge-hub_deployment.mmd](knowledge-hub_deployment.mmd) | Mermaid | Deployment view (binary/container, one port) |
| [knowledge-hub-architecture.drawio](knowledge-hub-architecture.drawio) | draw.io XML | Editable mirror of the context/container view |
| [runtime-architecture.json](runtime-architecture.json) | archify spec | Interactive runtime diagram definition — **stale**: generated 2026-09, predates A2A (#425) and task-durability (#445); regenerate via archify when available |
| [runtime-architecture.html](runtime-architecture.html) | HTML | Standalone explorable runtime diagram — **stale**: same provenance as the .json; regenerate when archify is available |
