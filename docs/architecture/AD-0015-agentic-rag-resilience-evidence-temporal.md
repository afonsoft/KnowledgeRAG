# AD-0015 — Agentic-RAG wave: resilience, evidence chain, temporal graph, action bridge

## Context

The 2026-09-27 wave (issues #262–#281, 16 SPECs) deepened the agentic-RAG loop
beyond retrieval quality: provider resilience, tamper-evident audit, temporal
knowledge graph, action-augmented answers, and agent-loop context management.
Each was a stacked PR (#364–#381); this ADR records the architectural shape
the wave left behind.

## Decision

- **Resilience** (`src/KnowledgeHub.Server/Resilience/`): policy engine
  (`disabled`|`observe`|`enforce`) configured via `Resilience:Fallback`.
  `ResilientChatClient` is a decorator on the scoped `IChatClient` registration
  (`KnowledgeHubServiceCollectionExtensions`) with lazy alternates per calling
  API key. `ToolCapabilityRegistry` holds the capability taxonomy
  (`WebSearch`, `DeepDocLookup`) — see gap follow-up: tool-call interception
  and the Settings UI section are pending (SPEC-20260928-resilience-tool-fallback-wiring).
- **Evidence chain** (`src/KnowledgeHub.Server/Audit/Evidence/`): append-only
  `EvidenceReceipt` rows, canonical JSON + HMAC-SHA256 keyed by `evidence:master`
  in the integration-secret store. Emission is best-effort hooks in
  `ask_knowledge`, `agent_chat` (ToolExecuted chained) and `AskEndpoints`.
  Bundle endpoint `GET /api/v1/evidence/sessions/{id}/bundle` (Operational).
  HMAC (not asymmetric) — offline verification requires the key.
- **Temporal/episodic KG** (`src/KnowledgeHub.Server/Graph/`): `KgNode`/`KgEdge`
  carry `ObservedAt`/`ValidFrom`/`ValidTo`/`EpisodeId` as `DateTime` UTC
  (DateTimeOffset doesn't translate in SQLite ORDER BY/`>=` lifted). Re-observation
  bumps `ObservedAt`; new evidence on an existing edge historicizes the previous
  row (`ValidTo`). `KgEpisode` groups one ingestion run; `TemporalGraphRetriever`
  serves window/recent/relationships(2-hop)/diverse/episode modes via
  `TemporalGraphToolsProvider` (5 MCP tools `search_graph_*`). Graph **viewer UI
  does not exist yet** — tracked as SPEC-20260928-graph-timeline-viewer.
- **Chain AST + compaction** (`src/KnowledgeHub.McpEngine/Agents/ChainAst/`):
  `ChainAstParser` sections the transcript (system/user headers + ai/tool pairs
  by CallId), `ChainAstRepair` stubs `FunctionResultContent` for orphaned tool
  calls, `ChainCompactor` folds old sections under `Agent:ContextManagement`
  limits before each LLM call in `AgentService.RunLoopAsync`.
- **Action-augmented RAG** (`src/KnowledgeHub.Server/Mcp/Bridge/`): chunks may
  carry `<!-- mcp-tool: name k="v" -->` markers; `McpDynamicRagActionBridge`
  executes nominated (or question-implied) read-only tools inside
  `ask_knowledge` (`enableLiveActions`), fusing outputs as `[Live Tool]`
  citations. `search_knowledge` surfaces the same tools as `suggestedActions`
  for next-turn calls. Caps: `Agent:MaxChainedDynamicCalls` (default 3).
- **Retrieval additions**: window expansion (`windowSize` implies
  `contextExpand=window`, gated at 80% of top normalized score), `limitMode`
  `fixed`/`autocut` (tail pruning at the N-th score drop,
  `Search:Autocut:Sensitivity`), hierarchical filter relaxation
  (`Search:Relaxation` — pathPrefix → sourceId → global, `IsRelaxed` markers),
  caller `subQueries` (≤4) as extra RRF arms, and a composite
  `(KnowledgeDocumentId, ChunkIndex)` index for window reads.
- **RAG triad eval**: `RagEvaluations` records (context relevance, groundedness,
  answer relevance) persisted on ask/agent runs and cache hits, deterministic
  sampling, 90-day retention; dashboard `/rag-quality` over
  `GET /api/v1/evaluation/stats`.
- **New connectors**: `RssFeed` (guid fingerprint), `YouTube` (transcripts),
  `GitRepository` (GitHub/GitLab/Gitea REST read-only, commit-SHA fast-path,
  SSRF guard via `WebPageConnector.GuardPublicAsync`), `UnstructuredDocument`
  (Unstructured API OCR/layout), `AudioTranscription` (AssemblyAI/Whisper).
  Per-source secrets stay in the integration-secret store
  (`restapi:{id}`/`sql:{id}`/`git:{id}`/`unstructured:{id}`/`audio:{id}`).
- **Embeddings**: `voyage` and `cohere` providers with per-section options
  (`Embeddings:Voyage`/`Embeddings:Cohere`) that fall back to the top-level
  `ApiKey`/`Model`/`Dimensions` so the settings UI works unchanged.

## Consequences

- The agentic loop is now resilient to transient provider failure (chat path)
  and self-limiting in context (ChainAst), but tool-level fallback still awaits
  interception wiring (tracked).
- Temporal graph reads are fully server-side; the UI surface is the pending
  gap — no `/graph` page exists.
- Evidence chain integrity is verifiable only while `evidence:master` remains
  secret — documented limitation of HMAC over asymmetric signing.
- `docs/{en,pt}/API.md`, README and INSTALL were synced to this wave in
  SPEC-20260928-post-pentagi-wave-docs-sync.
