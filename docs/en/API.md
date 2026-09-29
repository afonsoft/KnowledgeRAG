# REST API

All `/api/*` endpoints require authentication (cookie session or `Authorization: Bearer aft_...` API key) except `POST /api/auth/login` and `/health*`. Rate limits apply (`llm`/`sync`/`general` policies; 429 + `Retry-After` when exceeded).

## Auth

| Route | Purpose |
|---|---|
| `POST /api/auth/login` | `{username, password}` → session cookie |
| `GET /api/auth/me` | current user + `mustChangePassword` |
| `POST /api/auth/logout` | clear session |
| `POST /api/auth/change-password` | `{currentPassword, newPassword}` → 204 |

## API keys (cookie session only)

| Route | Purpose |
|---|---|
| `GET/POST /api/apikeys` · `DELETE /api/apikeys/{id}` | manage keys (secret shown at creation) |
| `GET /api/apikeys/{id}/usage` · `GET /api/apikeys/{id}/secret` | per-key usage audit feed · secret reveal when `canReveal` (DataProtection-protected copy) |
| `PUT/DELETE /api/api-keys/{id}/rate-limit` | per-key LLM/sync rate-limit overrides (`null` field = inherit global) |
| `PUT/DELETE /api/api-keys/{id}/scopes` | restrict key to allowed sources/tools |
| `GET/PUT/DELETE /api/api-keys/{id}/settings/chat` | per-key chat endpoint/model/key |
| `PUT/DELETE /api/api-keys/{id}/settings/integrations/{provider}` | per-key integration secrets |

## Sources & ingestion

| Route | Purpose |
|---|---|
| `GET/POST /api/sources` · `GET/PUT/DELETE /api/sources/{id}` | source CRUD (configs redacted) |
| `POST /api/sources/{id}/sync` | **async** — `202 {jobId,status,existing}`; `?wait=true` keeps the legacy synchronous `SyncResultDto` |
| `POST /api/sources/{id}/reindex` | `202` — forces re-chunk/re-embed even for unchanged content |
| `POST /api/sources/{id}/activate` · `/deactivate` | activation |
| `GET /api/sources/{id}/documents` · `/usage` | documents + usage stats |

### Ingestion jobs

| Route | Purpose |
|---|---|
| `GET /api/ingestion/jobs?sourceId=&status=&limit=` | list jobs (source filter + in-memory status filter) |
| `GET /api/ingestion/jobs/{id}` | one job — status, per-doc counters, error |
| `POST /api/ingestion/jobs/{id}/cancel` | cancel a queued job (`409` when already running/terminal) |

## Search, answers, agent

| Route | Purpose |
|---|---|
| `GET/POST /api/search` | hybrid search (`mode`, `topK`, filters `sourceType`/`pathPrefix`/`indexedAfter`/`language`, `expand`, `contextExpand`, `useGraph`) |
| `POST /api/ask` · `POST /api/ask/stream` | cited answer; SSE stream |
| `POST /api/agent` · `POST /api/agent/stream` | tool-calling agent loop; SSE (`token`/`tool_start`/`tool_end`/`awaiting_approval`/`done`/`error`, 15 s heartbeat) |
| `POST /api/agent/resume` | resume a paused agent run after an approval decision (`{approvalId}`) |
| `GET/POST /api/threads` · `GET/PUT/DELETE /api/threads/{id}` | conversation threads (list, create, get, rename, delete) |
| `GET/POST /api/threads/{id}/messages` | thread messages — history and append |

## Approvals & tools

| Route | Purpose |
|---|---|
| `GET /api/approvals` · `POST /api/approvals/{id}/approve|deny` | HITL approval queue |
| `GET /api/tools` · `POST /api/tools/{name}` | REST façade over the live MCP catalog (same tools as `tools/list`/`tools/call`) |

## Settings (Operational policy)

| Route | Purpose |
|---|---|
| `GET/PUT/DELETE /api/settings/chat` · `POST /api/settings/chat/test` · `DELETE /api/settings/chat/apikey` | persisted chat config + connectivity test; `/apikey` clears only the stored key |
| `GET/PUT/DELETE /api/settings/embeddings` · `DELETE /api/settings/embeddings/apikey` | persisted embeddings config — GET stamps `stampedModelId`, `providerError`, `storeDimensions`; `/apikey` clears only the stored key |
| `GET/PUT/DELETE /api/settings/graph` | GraphRAG `enabled`, `maxChunksPerSync`, `maxChunkChars`, `maxResults` — applies without restart |
| `GET/PUT/DELETE /api/settings/integrations/{provider}` | masked integration keys (firecrawl, deepwiki, tavily, context7) |
| `GET /api/settings/cache` · `POST /api/settings/cache/clear` · `DELETE /api/settings/cache/keys/{*key}` | cache stats (per-process tracked keys + Redis server SCAN/INFO overlay — `serverReported`/`partial`) + clear all regions + delete one key pattern |
| `GET /api/settings/database` | database stats (table counts, sizes, vector-store summary) |
| `GET/PUT /api/settings/log-level` | runtime log level (`LoggingLevelSwitch`); `PUT {level, minutes}` — `minutes` 0–120 schedules automatic reset to the configured level |

## Security & eval

| Route | Purpose |
|---|---|
| `GET /api/security/events` | prompt-injection flag audit (metadata only — never raw content) |
| `POST /api/eval/run` · `GET /api/eval/runs` · `GET /api/eval/runs/{id}` | retrieval eval harness (Recall@K/P@K/MRR/faithfulness, p50/p95/p99 latency) — request accepts `baseline` (name) and `gate` (rules `[{metric,direction,threshold}]`), report carries `gateResult` + `topRegressions` |
| `GET/POST /api/eval/baselines` | list named baselines; `POST {name, runId}` promotes a run |
| `GET /api/v1/evaluation/stats` | RAG quality triad stats — context relevance, groundedness, answer relevance averages, hallucination rate and recent flagged queries (last 7 days); backs the `/rag-quality` dashboard |
| `GET /api/v1/evidence/sessions/{sessionId}/bundle` | tamper-evident evidence bundle for a session — append-only `EvidenceReceipt` chain signed with HMAC-SHA256 (`evidence:master` secret); Operational policy |

### Search/ask tool args

`search_knowledge` and `ask_knowledge` (MCP tools and REST façade) accept, besides
`mode`/`topK`/metadata filters:

| Arg | Values |
|---|---|
| `expand` | `off` (default) · `multi` (N query rewrites fused via RRF) · `hyde` (hypothetical doc on the vector arm) · `both` |
| `contextExpand` | `none` · `window` (neighbouring chunks) · `section` (parent section) — attaches `context` to each hit without changing ranking |
| `useGraph` | bool — enables the knowledge-graph retrieval arm (entity linking + 1-hop evidence) |
| `subQueries` | string[] ≤4 — extra query variants searched in parallel and fused via RRF (multi-query); blanks filtered |
| `windowSize` | int 0–3 — neighbour-chunk window expansion; `>0` implies `contextExpand=window`; hits below 80% of the top normalized score don't expand (`Search:Expansion:WindowThresholdPercent`) |
| `limitMode` | `fixed` (classic topK) · `autocut` (default `Search:LimitMode` — prunes the long tail at the N-th abrupt score drop) |
| `autocutSensitivity` | int 1–3 — autocut sensitivity (N-th drop), clamped by `Search:Autocut:MaxClamp` |
| `enableLiveActions` | bool — `ask_knowledge` only: Action-Augmented RAG executes live MCP tools nominated by retrieved chunks (`<!-- mcp-tool: -->` markers) or the question; outputs fuse as `[Live Tool]` citations and `liveToolExecutions` |

Responses may carry `suggestedActions` (search — tool calls the model can issue next turn), `expandedChunkIndices`/`windowExpanded`, and filter-relaxation fields `isRelaxed`/`relaxedScope`/`appliedFilter`/`originalFilter`/`filterRelaxed` when `Search:Relaxation` drops a filter level.

### Graph — MCP tools

| Tool | Args | Purpose |
|---|---|---|
| `search_graph_temporal` | `query`, `timeStart`/`timeEnd` (RFC3339), `topK` | Time-windowed entity/edge retrieval over `ObservedAt`/`ValidFrom`/`ValidTo` |
| `search_graph_recent` | `query`, `window` (`1h`/`6h`/`24h`/`7d`), `topK` | Recent-window graph retrieval |
| `search_graph_relationships` | `entity`, `depth` | 2-hop relationship expansion |
| `search_graph_diverse` | `query`, `diversity` (low/med/high) | Cluster-diversified graph results |
| `search_graph_episode` | `episodeId` | Retrieval scoped to one ingestion episode (`KgEpisode`) |

### Graph — REST endpoints

| Route | Purpose |
|---|---|
| `GET /api/graph/nodes` | Node listing/filtering for the `/graph` viewer |
| `GET /api/graph/nodes/{id}/edges` | Edges of a node (evidence per edge) |
| `GET /api/graph/episodes` | Ingestion episodes (`KgEpisode`) |
| `GET /api/graph/timeline` | Temporal timeline for the viewer presets (1h/6h/24h/7d) |

## Other

| Route | Purpose |
|---|---|
| `GET /api/diagnostics/vectorstore` | vector store diagnostics — provider, host, database, dimension, chunk count, storage type (`vector`\|`halfvec`), index state |
| `GET /api/mcp/capabilities` | advertised MCP session mode (`sessionMode`, `legacySse`) — anonymous |
| `/mcp` (+ `/mcp/sse`, `/mcp/message`) | MCP transports — see README |
| `/hubs/mcp` | SignalR activity feed |
| `/metrics` | Prometheus scrape endpoint (`Telemetry:Metrics:Prometheus=true`) |
| `/framework-assets/{stem}/{ext}` | proxy-safe `_framework` asset mirror |
