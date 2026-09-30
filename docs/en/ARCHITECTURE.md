# KnowledgeHub Architecture

## Overview

KnowledgeHub is an all-in-one standalone knowledge platform built on .NET 10. It combines a Blazor WebAssembly admin SPA, a REST management API, a native Model Context Protocol (MCP) server, an A2A v1.0 (Agent-to-Agent) endpoint, and a SignalR activity hub into a single Kestrel-hosted process.

> Full diagrams, ADRs, and the interactive runtime view live in [`docs/architecture/`](../architecture/) — see `system-architecture.md` and `README.md` there.

## System Layers

```
     +---------------------------+   +---------------------------+
     |    External MCP Clients   |   |      A2A Agents (v1.0)    |
     | (Cursor, Claude Desktop)  |   | (JSON-RPC / HTTP+JSON)    |
     +--------------+------------+   +--------------+------------+
                    | Streamable HTTP / SSE      | /.well-known/agent-card.json
                    |                            | /a2a  (same aft_* scope)
                    v                            v
+------------------+             +---------------+-----------+
|    Blazor SPA    |             |  KnowledgeHub.Server      |
| (Admin PWA / UI) |             |  (Kestrel, Minimal APIs)  |
+--------+---------+             +---------------+-----------+
         | REST / SignalR                        |
         v                                       v
+------------------+             +---------------+-----------+
| KnowledgeHub.    |             | KnowledgeHub.McpEngine    |
|   Client         |             | (JSON-RPC 2.0 Dispatcher) |
+------------------+             +---------------+-----------+
         |                                       |
         +-------------------+-------------------+
                             |
                             v
                 +-----------+-----------+
                 | KnowledgeHub.Shared   |
                 | (Contracts, DTOs)     |
                 +-----------+-----------+
                             |
                             v
                 +-----------+-----------+
                 |    EF Core SQLite     |
                 | (sqlite-vec / pgvector) |
                 +-----------------------+
```

## Core Projects

1. **KnowledgeHub.Shared**: shared DTOs, JSON-RPC 2.0 / MCP contracts, enums.
2. **KnowledgeHub.Client**: Blazor WebAssembly SPA (BootstrapBlazor) — administration, MCP monitor, playground, chat, approvals, eval, settings.
3. **KnowledgeHub.Server**: Kestrel host — Minimal APIs, EF Core SQLite, connectors/sync engine, hybrid search, answer/agent loops, GraphRAG, auth (cookie + API keys), rate limiting, upstream MCP proxies, OpenTelemetry.
4. **KnowledgeHub.McpEngine**: native MCP server — SSE sessions, Streamable HTTP transport, JSON-RPC dispatch.

## Data Flow & Retrieval

- **Ingestion**: connectors ingest Obsidian vaults (local/WebDAV), web pages, document files, Notion, REST APIs, SQL databases, and cloud storage (AWS S3, Azure Files, OCI Object Storage via S3-compatible endpoint, Google Drive shared links); sync runs on a persisted async job queue (202+jobId, per-doc counters, cancel, selective reindex by chunker version); structure-aware chunking for markdown/code/config plus per-source opt-in semantic chunking (embedding-breakpoint boundaries); every chunk passes the prompt-injection sanitizer (`SuspicionFlags` + `security_events` audit) before embedding.
- **GraphRAG**: when `Graph:Enabled` (default **on**) and the source opts in (`"graph": true`), an LLM extracts entities/relations into `KgNodes`/`KgEdges`/`KgAliases` with full provenance — traversed by `find_dependencies`, `find_dependents`, `find_path`, `analyze_impact` and joinable into retrieval via `useGraph`.
- **Hybrid retrieval**: SQLite FTS5 lexical + vector KNN (sqlite-vec or pgvector HNSW) fused via RRF (k=60); MMR diversity + per-document quota + score floor; optional LLM query rewriting (history-aware) and reranking; multi-query expansion + HyDE; corrective-RAG loop (grading → retry → abstention); contextual chunk enrichment (`SectionPath`) and hierarchical context expansion (`contextExpand`); asymmetric query/document embeddings; metadata filters; per-API-key source authorization; flagged-chunk exclusion.
- **Synthesis & agent**: `IChatClient` (Ollama/OpenAI) answers with `[n]` citations; agent loop supports tool-calling, HITL approvals, bounded SSE streaming, and conversation threads with summarization.
- **Observability**: `Meter`/`ActivitySource` instrument search stages, LLM calls, agent iterations, sync runs, and cache regions — exported via opt-in OTLP or Prometheus (`/metrics`).

## Decision records

Key decisions are recorded as ADRs in [`docs/architecture/`](../architecture/README.md) — single-process monolith, SQLite-first storage, hybrid MCP sessions, dual auth, hybrid retrieval, GraphRAG on adjacency tables, partitioned rate limiting, injection guard, OTel observability, upstream proxies.
