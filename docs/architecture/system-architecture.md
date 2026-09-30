# KnowledgeHub — System Architecture

All-in-one standalone .NET 10 platform: a **single Kestrel process** serves the Blazor WebAssembly admin SPA, the REST management API, a native **MCP server** (Streamable HTTP in hybrid session mode — `initialize` clients get sessions, `2026-07-28` clients are served statelessly — plus legacy HTTP/SSE) and a SignalR monitor hub. Agentic RAG over user-registered knowledge sources (Obsidian vaults, web pages, document files, Notion, REST APIs, SQL, cloud storage — AWS S3, Azure Files, OCI Object Storage, Google Drive) ingested through a **persisted async job queue**, with **GraphRAG** entity/relation extraction, prompt-injection defense, partitioned rate limiting, hybrid L1/L2 distributed cache, structured Serilog logging, and OpenTelemetry observability.

> Source of truth: `.specs/` (SPEC-*, all `Done`). Diagram sources: `*.mmd` (Mermaid) + `knowledge-hub-architecture.drawio` (editable XML). Decisions: `AD-*.md` in this directory.

## 1. Context & Container view

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart TB
    subgraph Clients["Consumers"]
        UI["Browser Admin<br/>(Blazor WASM SPA / PWA)"]
        CUR["Cursor / Claude Desktop<br/>(legacy SSE)"]
        AG["MCP agents / LangGraph<br/>(Streamable HTTP)"]
        A2AAG["A2A agents (v1.0)<br/>(JSON-RPC / HTTP+JSON)"]
    end

    subgraph KH["KnowledgeHub — single Kestrel process (.NET 10)"]
        direction TB
        SPA["Static host<br/>index.html + WASM assets"]
        API["REST API<br/>/api/* (Minimal APIs)"]
        MCP["MCP Server<br/>/mcp (Streamable HTTP, hybrid sessions)<br/>/mcp/sse + /mcp/message (legacy)"]
        A2A["A2A Server<br/>/.well-known/agent-card.json<br/>/a2a (JSON-RPC + HTTP+JSON)"]
        HUB["SignalR Hub<br/>/hubs/mcp"]
        AUTH["Auth<br/>cookie session + aft_* API keys"]
        RL["Rate limiter<br/>key → user → IP partitions"]
        CAT["DynamicToolCatalog<br/>IToolProviders (live)"]
        QUEUE["IngestionQueue +<br/>IngestionWorker<br/>persisted jobs · cancel · reindex"]
        ING["IngestionService<br/>connectors · chunkers · watcher"]
        SEC["ContentSanitizer<br/>prompt-injection scan"]
        GRAPH["EntityExtractor +<br/>IKnowledgeGraphStore"]
        SRCH["SearchService<br/>FTS5+vector RRF · MMR · corrective<br/>expand · contextExpand · useGraph"]
        ANS["AnswerService /<br/>AgentService<br/>(bounded SSE, HITL)"]
        EMB["IEmbeddingProvider<br/>deterministic · ollama · openai · onnx"]
        VEC["IVectorStore<br/>sqlite · sqlite-vec · pgvector"]
        CACHE["HybridCache L1→L2<br/>region TTLs · kh:invalidate"]
        LOG["Serilog<br/>request log · file sink<br/>redaction · LogLevelSwitch"]
        TEL["OpenTelemetry<br/>metrics + traces"]
        FEED["IMcpActivityFeed<br/>ring buffer 500"]
    end

    subgraph Stores["Storage"]
        SQL[("SQLite<br/>knowledgehub.db<br/>catalog + docs + vectors<br/>+ graph + settings")]
        PG[("PostgreSQL + pgvector<br/>(optional, HNSW)")]
        VAULT["Obsidian vault<br/>(.md on disk)"]
        REDIS[("Redis<br/>(optional cache)")]
    end

    UP["Upstream MCP<br/>DeepWiki · Firecrawl · Tavily · Context7"]
    LLM["AI endpoints<br/>Ollama · OpenAI-compat"]
    CLOUD["Cloud storage<br/>AWS S3 · Azure Files · OCI · Google Drive"]

    UI -->|HTTPS+WS| SPA
    UI -->|REST| API
    UI -->|SignalR| HUB
    CUR -->|SSE+POST| MCP
    AG -->|JSON-RPC| MCP
    A2AAG -->|Agent Card + message/send| A2A

    AUTH --> API
    AUTH --> MCP
    AUTH --> A2A
    RL --> API
    RL --> MCP
    RL --> A2A
    MCP --> CAT
    A2A -->|delegates to live catalog| CAT
    MCP --> FEED
    FEED --> HUB
    CAT --> SRCH
    CAT --> ANS
    CAT --> ING
    CAT --> GRAPH
    API --> SRCH
    API --> ANS
    API --> QUEUE
    QUEUE --> ING
    ING --> SEC
    ING --> EMB
    ING --> GRAPH
    ING --> VAULT
    ING --> CLOUD
    ING --> VEC
    ING --> SQL
    SRCH --> VEC
    SRCH -.-> CACHE
    EMB -.-> CACHE
    ANS --> SRCH
    ANS --> LLM
    EMB --> LLM
    VEC --> SQL
    GRAPH --> SQL
    QUEUE --> SQL
    VEC --> PG
    CACHE -.-> REDIS
    CAT -.->|upstream tools passthrough| UP
    API --> TEL
    MCP --> TEL
    API -.-> LOG
    MCP -.-> LOG

    classDef client fill:#dae8fc,stroke:#6c8ebf,stroke-width:2px,color:darkblue
    classDef core fill:#d5e8d4,stroke:#82b366,stroke-width:2px,color:darkgreen
    classDef store fill:#f5f5f5,stroke:#666666,stroke-width:2px,color:black
    classDef ext fill:#ffe6cc,stroke:#d79b00,stroke-width:2px,color:#7f4d00
    class UI,CUR,AG,A2AAG client
    class SPA,API,MCP,A2A,HUB,AUTH,RL,CAT,QUEUE,ING,SEC,GRAPH,SRCH,ANS,EMB,VEC,CACHE,LOG,TEL,FEED core
    class SQL,PG,VAULT,REDIS store
    class UP,LLM,CLOUD ext
```

| Layer | Component | Responsibility |
|---|---|---|
| Consumers | Browser (Blazor WASM) | `/sources`, `/mcp-monitor`, `/playground`, `/chat`, `/settings`, `/api-keys`, `/approvals`, `/eval` |
| | MCP clients | Streamable HTTP `/mcp` or legacy SSE `/mcp/sse` + `/mcp/message` |
| | A2A agents | Agent Card `/.well-known/agent-card.json` (anonymous); execution `/a2a` JSON-RPC + HTTP+JSON under the caller's `aft_*` scope (Operational policy + `llm` rate limit); delegates to `DynamicToolCatalog` skills `ask_knowledge`/`search_knowledge`/`agent_chat`/`read_document` |
| Edge | Static host | `index.html` + WASM assets, SPA fallback |
| | REST API | Minimal APIs under `/api/*` — see `docs/en/API.md` |
| | MCP transport | `ModelContextProtocol.AspNetCore` 2.2.0, `Mcp:SessionMode` hybrid |
| | SignalR hub | `/hubs/mcp` — live sessions + call log |
| Security | Auth | Cookie session (12 h sliding, lockout) + `aft_*` API keys (SHA-256 hash only) |
| | Rate limiting | `System.Threading.RateLimiting` — partitions `key:`/`user:`/`anon:`; per-key overrides via `IApiKeyRateLimitResolver` |
| | `ContentSanitizer` | Heuristic injection scan at ingest → `DocumentChunk.SuspicionFlags` + `security_events`; exclusion default-on |
| | Source authorization | Per-key `AllowedSourceIdsJson`/`AllowedToolsJson` enforcement in `SearchService` |
| Core | `DynamicToolCatalog` | Live tool resolution per `tools/list`; `IToolCatalogChangeNotifier` pushes `tools/list_changed` |
| | `IngestionQueue` + `IngestionWorker` | Persisted async sync jobs (`202+jobId`) — per-doc counters, cancel, selective reindex by chunker version, auto-sync routed through the queue |
| | `IngestionService` | Connector (Obsidian/WebDAV, web, docs, Notion, REST, SQL, S3, Azure Files, OCI, Google Drive) → chunker (markdown/code/config, semantic opt-in) → injection scan → embed → vector upsert → optional graph extraction |
| | `SearchService` | Hybrid FTS5 + vector (RRF), MMR + per-doc quota + score floor, corrective-RAG, query rewriting + expansion (multi-query/HyDE) + LLM reranker, contextual enrichment + `contextExpand`, `useGraph` arm, metadata filters, source authz, flagged-chunk exclusion |
| | `AnswerService` / `AgentService` | LLM synthesis with `[n]` citations; agent loop with tool-calling, HITL approvals, bounded SSE, conversation threads + summarization |
| | `EntityExtractor` + `IKnowledgeGraphStore` | LLM entity/relation extraction → `KgNodes`/`KgEdges`/`KgAliases` (SQLite adjacency); tools `find_dependencies`/`find_dependents`/`find_path`/`analyze_impact` |
| | `IEmbeddingProvider` | `deterministic` · `ollama` · `openai` · `onnx` (local all-MiniLM-L6-v2) |
| | `IVectorStore` | `sqlite` (chunk columns) · `sqlite-vec` (KNN) · `postgres`/pgvector (HNSW, batched upserts, metadata jsonb) |
| | `EvalRunner` | Versioned eval datasets → Recall@K / P@K / MRR / faithfulness reports |
| | `IDistributedCache` | `memory` (default) · `redis` — hybrid L1(in-process)→L2(Redis), per-region TTLs, `mcp:tool` region, pub/sub invalidation (`kh:invalidate`) |
| | Serilog | Structured request logging (health/static → Debug, 5xx → Error, `x-request-id`), daily rolling file sink (14 d) + opt-in OTLP sink, secret redaction (`***REDACTED***`), runtime `LoggingLevelSwitch` via `/api/settings/log-level` (auto-reset 0–120 min) |
| | `IMcpActivityFeed` | Ring buffer (500) → SignalR broadcast |
| | Telemetry | `System.Diagnostics` `Meter`/`ActivitySource` → OTLP/Prometheus opt-in |
| Storage | SQLite `knowledgehub.db` | Catalog, documents, chunks (FTS5), vectors (default), knowledge graph, settings, eval runs, security events — beside the executable |
| | PostgreSQL + pgvector | Optional vector backend (catalog stays in SQLite) — HNSW, `halfvec` storage, iterative filtered scans; host/external instances reached via `.env`-composed `POSTGRES_*` connection string |
| | Redis | Optional `IDistributedCache` backend |
| External | Upstream MCP proxies | DeepWiki (public + private), Firecrawl, Tavily, Context7 — secrets encrypted at rest |
| | Cloud storage | AWS S3, Azure Files, OCI Object Storage (S3-compatible), Google Drive — staged ingestion with ETag diffs, secrets in the integration-secret store |
| | Ollama / OpenAI-compat | Chat + embedding generation endpoints (HTTP resilience: retry + circuit breaker) |

## 2. MCP request flow (tools/call)

```mermaid
%%{init: {'theme':'neutral'}}%%
sequenceDiagram
    autonumber
    participant C as MCP Client
    participant K as /mcp (SDK transport)
    participant F as Filters + rate limit
    participant CAT as DynamicToolCatalog
    participant S as SearchService
    participant V as IVectorStore
    participant U as Upstream MCP proxy

    C->>K: POST initialize
    K-->>C: 200 + Mcp-Session-Id
    C->>K: notifications/initialized
    C->>K: tools/list
    K->>CAT: GetToolsAsync (live)
    CAT-->>K: local + graph + query_* + upstream tools
    K-->>C: tools[]

    alt Local tool (search_knowledge)
        C->>K: tools/call search_knowledge
        K->>F: rate limit (llm policy) + telemetry
        F-->>C: isError + retryAfter when over limit
        K->>CAT: resolve + invoke
        CAT->>S: SearchAsync(query, topK, filters)
        S->>V: hybrid FTS5+vector → RRF → rerank?
        V-->>S: ranked hits
        S->>S: source authz + flagged-chunk exclusion
        S-->>CAT: items (+suspicionFlags when kept)
        CAT-->>K: CallToolResult(text + structuredContent)
        K-->>C: result
    else Graph tool (find_dependencies)
        C->>K: tools/call find_dependencies
        K->>CAT: resolve → GraphToolsProvider
        CAT->>CAT: KgNode resolve → BFS (depth ≤3) → evidence edges
        CAT-->>K: CallToolResult(subgraph JSON)
        K-->>C: result
    else Upstream proxy (firecrawl_search)
        C->>K: tools/call firecrawl_search
        K->>CAT: resolve → upstream client
        CAT->>U: McpClient tools/call (Streamable HTTP)
        U-->>CAT: CallToolResult pass-through
        CAT-->>K: result
        K-->>C: result
    end
```

- `tools/list` resolves **live** per call; source/settings mutations broadcast `notifications/tools/list_changed`.
- Rate limiting: LLM-spending tools (`ask_knowledge`, `agent_chat`, `search_knowledge`) and write tools (`write_knowledge`, `write_note`) are charged per partition; over-limit calls return `isError` + retry hint (no 429 in JSON-RPC).
- Execution failures → `CallToolResult { isError: true }` — never break the session stream.

## 3. Tool catalog

| Tool | Type | Availability |
|---|---|---|
| `search_knowledge` | hybrid/semantic/lexical search, metadata filters | always |
| `ask_knowledge` | retrieval + cited answer | always |
| `agent_chat` | tool-calling agent loop, HITL, threads | always |
| `write_knowledge` | persist + index content | always (write gate) |
| `query_{slug}` | per-source filtered search | per active source |
| `read_document` | read vault note (`SafePath`) | ≥1 active ObsidianVault |
| `write_note` | write vault `.md` + re-index | ≥1 active ObsidianVault (write gate) |
| `find_dependencies` · `find_dependents` · `find_path` · `analyze_impact` | knowledge graph traversal | `Graph:Enabled` (default **on**) |
| `set_api_key_settings` | per-key chat/integration config | always |
| `ask_question` · `read_wiki_structure` · `read_wiki_contents` | DeepWiki proxy | `DeepWiki:Enabled` |
| `firecrawl_*` (6 tools) | Firecrawl proxy | `Firecrawl:Enabled` + key |
| `tavily_*` (6 tools) | Tavily proxy | `Tavily:Enabled` + key |
| `resolve-library-id` · `query-docs` | Context7 proxy | `Context7:Enabled` + key |

Resources: `knowledge://sources` (catalog JSON) + `obsidian://{slug}/{path}` per indexed document.

## 4. Deployment

```mermaid
%%{init: {'theme':'neutral'}}%%
flowchart LR
    subgraph Host["Single host (container or bare binary)"]
        EXE["KnowledgeHub<br/>single-file binary or<br/>knowledgehub:latest image"]
        DB[("knowledgehub.db<br/>./data volume")]
        LOGV[("./logs volume<br/>Serilog file sink")]
        EXT["%TMP%/.net extraction<br/>native libs"]
        CFG["appsettings.json + env vars<br/>(__ nesting) + .env (env_file)"]
        EXE --> DB
        EXE --> LOGV
        EXE -.->|self-extract| EXT
        CFG -.->|config| EXE
    end

    subgraph HostSvc["Host / external services (opt-in)"]
        PG2[("PostgreSQL + pgvector<br/>POSTGRES_* in .env<br/>host.docker.internal")]
        RDS[("Redis<br/>REDIS_CONNECTIONSTRING")]
    end
    EXE -.->|"VectorStore__*"| PG2
    EXE -.->|"Cache__*"| RDS

    DEV["docker compose build<br/>or dotnet publish -r <RID>"] -->|produces| EXE

    subgraph Net["One port (default :8080 container / :5000 dev)"]
        R1["/            → SPA + deep links"]
        R2["/api/*       → REST (auth)"]
        R3["/mcp         → Streamable HTTP"]
        R4["/mcp/sse|msg → legacy SSE"]
        R5["/hubs/mcp    → SignalR"]
        R6["/metrics     → Prometheus (opt-in)"]
        R7["/healthz     → liveness"]
    end
    EXE --- Net

    classDef bin fill:#d5e8d4,stroke:#82b366,stroke-width:2px,color:darkgreen
    classDef data fill:#f5f5f5,stroke:#666666,stroke-width:2px,color:black
    classDef net fill:#dae8fc,stroke:#6c8ebf,stroke-width:2px,color:darkblue
    class EXE bin
    class DB,LOGV,EXT,CFG,PG2,RDS data
    class R1,R2,R3,R4,R5,R6,R7 net
```

- `docker compose up -d` builds `knowledgehub:latest`; `./data` persists SQLite + uploads, `./logs` persists the Serilog file sink (pre-create both as uid 1654); EF migrations run at startup (`DatabaseMigrator`).
- `docker-compose.yml` reads `.env` (`env_file`, `required: false`); `VectorStore__ConnectionString` is composed from `POSTGRES_HOST/PORT/DB/USER/PASSWORD` (`VECTORSTORE_CONNECTIONSTRING` wins when set). Host Postgres needs `pgvector` installed + a `pg_hba.conf` rule for the container subnet.
- `dotnet publish -r <RID>` produces a single-file self-contained binary; `install.sh --host --systemd` installs a hardened systemd unit.
- `backup.sh`/`restore.sh` cover SQLite + uploads.

## 5. Configuration surface

```jsonc
{
  "Database":    { "Path": "knowledgehub.db" },
  "Embeddings":  { "Provider": "deterministic|ollama|openai|onnx", "Endpoint": "...",
                   "ApiKey": "", "Model": "nomic-embed-text", "Dimensions": 384,
                   "ModelPath": "models/all-MiniLM-L6-v2" },
  "VectorStore": { "Provider": "sqlite|sqlite-vec|postgres", "ConnectionString": "",
                   "Postgres": { "HnswThreshold": 1000, "HnswM": 16,
                                 "HnswEfConstruction": 64, "HnswEfSearch": 40,
                                 "IterativeScan": false, "StorageType": "vector|halfvec",
                                 "AllowStorageMigration": false,
                                 "MinPoolSize": 5, "MaxPoolSize": 50 } },
  "Chat":        { "Provider": "none|ollama|openai", "Endpoint": "...",
                   "Model": "llama3.2", "ApiKey": "", "TimeoutSeconds": 120 },
  "Search":      { "Lexical": { "Enabled": true },
                   "QueryRewrite": { "Enabled": false, "LexicalToo": false },
                   "Rerank": { "Enabled": false, "MaxCandidates": 50 } },
  "Cache":       { "Provider": "memory|redis", "Redis": { "ConnectionString": "" },
                   "ToolCacheEnabled": true, "ToolCacheTtlMinutes": 60,
                   "L1Enabled": true, "L1MaxTtlMinutes": 5, "DefaultTtlMinutes": 10,
                   "RegionTtlMinutes": { "emb": 1440, "search": 5, "ans": 10,
                                         "mcp:tool": 60, "rewrite": 1440,
                                         "expand": 60, "index": 10080, "secret": 60 },
                   "AnswerCache": { "Enabled": false, "TtlSeconds": 600 } },
  "RateLimiting":{ "Enabled": true, "LlmPermitLimit": 20, "LlmWindowSeconds": 60,
                   "AnonymousLlmPermitLimit": 5, "SyncPermitLimit": 10,
                   "SyncWindowSeconds": 3600, "GeneralPermitLimit": 300,
                   "GeneralWindowSeconds": 60, "TrustForwardedHeaders": false },
  "Security":    { "Injection": { "ExcludeFlagged": true } },
  "Graph":       { "Enabled": true, "MaxChunksPerSync": 200,
                   "MaxChunkChars": 2000, "MaxResults": 200 },
  "Ingestion":   { "QueueSize": 100, "MaxTokens": 500, "OverlapTokens": 50,
                   "Semantic": { "BreakpointPercentile": 95 } },
  "Telemetry":   { "Otlp": { "Endpoint": "" }, "Metrics": { "Prometheus": false } },
  "Mcp":         { "SessionMode": "StatefulForInitializeClients",
                   "MaxConcurrentCallsPerSession": 8, "ActivityFeedCapacity": 500 },
  "Auth":        { "AdminInitialPassword": "123qwe", "SessionHours": 12 },
  "Host":        { "ShutdownTimeoutSeconds": 30 },
  "Serilog":     { "MinimumLevel": { "Default": "Information",
                                     "Override": { "Microsoft": "Warning",
                                                   "System": "Warning" } },
                   "WriteTo": [ "Console", "File (logs/, daily rolling, 14d)" ] }
}
```

Runtime-editable settings (stored in SQLite, override env, no restart): **Chat** (`/api/settings/chat`), **Embeddings** (`/api/settings/embeddings` — provider swap with drain + model stamping), **Graph** (`/api/settings/graph`), **integration secrets** (`/api/settings/integrations/*`), **runtime log level** (`/api/settings/log-level`, `minutes` 0–120), and **per-API-key** chat/integration/rate-limit/scopes overrides (`/api/api-keys/{id}/*`).

Security boundaries: source configs are **redacted** in API responses; vault paths reject `..`; `AllowedHosts` restricted to localhost; secrets live in env vars or the encrypted `IntegrationSecrets` store; embedding model changes are detected via `EmbeddingModel` stamping so stale vectors are never mixed.

## Editable diagram

`knowledge-hub-architecture.drawio` mirrors section 1 — open in [draw.io / diagrams.net](https://app.diagrams.net) to edit. `runtime-architecture.html` is an interactive trace-enabled view (archify) for the same runtime.

## Decision records

See `AD-*.md` in this directory — indexed in `docs/architecture/README.md`.
