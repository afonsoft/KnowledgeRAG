# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed
- `DocumentFileConnector.GlobMatcher`: `{a,b}` brace groups were never matched — groups are now split before `Regex.Escape` (braces are not escaped chars), with alternation terms individually escaped.
- Settings → Assistente (A2A): Endpoint/Model do assistente de baixo custo agora têm default igual ao provider do chat quando não há configuração salva; toggle "Habilitado" usa o mesmo componente `Switch` da aba GraphRAG.

### Added
- Unit tests (36) covering paths touched by the sonarqube-autofix pass: `GlobMatcher.Compile`, `McpProxyToolsProvider.ParseConfig`, `ResolvedSearchFilter.TryResolve`, `A2AEndpointExtensions.BuildAgentCard`, `ChunkerSelector.ChunkAsync`. Suite: 1245 unit tests, 100% pass; line coverage 20.17% (baseline gate 20%).

## [0.0.3] - 2026-09-26

### Added
- MCP v2 hybrid transport — `initialize`-handshake clients keep full stateful sessions, `2026-07-28` clients served statelessly (`Mcp:SessionMode`)
- Notion connector for RAG ingestion (REST read-only, incremental via `last_edited_time`)
- Context7 upstream MCP proxy (`resolve-library-id`, `query-docs` via `ctx7sk-*` keys)
- pgvector backend: HNSW index + batched upserts, provenance metadata on upserts, pooling, ANALYZE post-bulk, cascade delete per source
- pgvector advanced: iterative filtered scan (`pgvector ≥0.8`), `halfvec` storage (`pgvector ≥0.7` + `AllowStorageMigration` gate), `POSTGRES_*` env composition + compose `env_file`
- Partitioned rate limiting for HTTP and MCP surfaces + per-API-key overrides
- Prompt-injection guard for retrieved context (flag → exclude → audit)
- Structure-aware chunking for code and config sources + per-source opt-in semantic chunking
- Evaluation harness for retrieval quality (Recall@K/P@K/MRR/faithfulness, p50/p95/p99, named baselines, regression gates)
- Retrieval quality: filters, reranker, query rewriting, MMR + per-doc quota + score floor, corrective-RAG, multi-query + HyDE, contextual enrichment (`SectionPath`), hierarchical expansion (`contextExpand`), graph arm (`useGraph`), asymmetric embeddings
- Async ingestion queue with persisted jobs (`202+jobId`, cancel, selective reindex, auto-sync routing)
- GraphRAG — entity/relation extraction, adjacency graph store, traversal tools, graph-component discovery via search
- Observability metrics + OTel traces across the retrieval pipeline; Serilog request logging + daily rolling file sink + secret redaction + runtime log level
- Distributed cache — Redis L2 + in-process L1, per-region TTLs, tool-result cache (`mcp:tool`), pub/sub invalidation (`kh:invalidate`)
- Cloud storage connectors — AWS S3, Azure Files, OCI Object Storage; Google Drive shared-link connector
- Per-API-key source/tool scoping, rate limits, chat + integration settings; key usage audit + secret reveal/copy
- Runtime Graph settings, embeddings provider editor, cache/redis stats and database metrics under `/settings`
- Agent runtime hardening — bounded SSE, answer cache, HTTP resilience
- MCP monitor with audit-grade activity feed; icon-only action grids; split login layout
- Single-file release publish + `install.sh --host --systemd`; backup/restore scripts
- `ToolCacheService` with canonical argument hashing and index-version invalidation + 20 BDD-style unit tests
- `GET /api/mcp/capabilities`, `GET /api/diagnostics/vectorstore`, `GET /api/settings/database`, `POST /api/agent/resume`

### Changed
- `dotnet format --verify-no-changes`: **0 of 371 files** needed changes — code style 100% clean
- GraphRAG enabled by default with clean `/settings` toggle
- Tool descriptions emitted in en-US; `read_document` returns citation paths

### Fixed
- `PostgresVectorStore.SearchAsync` committing the transaction with the reader still open — `NpgsqlOperationInProgressException` on Npgsql 10 broke every vector search
- `CS0117` build error — `ConfigurationOptions.TryParse` does not exist; replaced with `Parse` + try/catch
- Ingestion integrity: reindex no longer wipes documents, transient errors ≠ deletes, queue deadlock resolved, cache bust on delete order
- KgAliases unique-violation poisoning whole syncs + per-job error details
- Embeddings coherence: dims vs actual model output, `Auto` signature, swap-with-drain, settings-changed propagation
- Search correctness: dedicated connection for expansion, corrective stream pairing, per-dataset baselines
- Cache: `CacheTtlPolicy` now effective, striped locks, resilient subscribe, remote L2 clear, degrade outside cache
- `hnsw.iterative_scan` ordering vs halfvec migration + extension creation on fresh databases
- Ops: log-level restore/generation, audit caller, honest monitor/diagnostics, backup vault-slug collision
- CI: release archives upload, Docker build cache, single-file publish props (NETSDK1098), icon font loading
- Settings: per-key integration secrets applied to upstream calls

## [0.0.2] - 2025-09-14

### Added
- Initial standalone release with Blazor WebAssembly admin UI, REST API, native MCP server, SQLite persistence, and Obsidian ingestion.

[Unreleased]: https://github.com/afonsoft/LangGraph-UI/compare/v0.0.3...HEAD
[0.0.3]: https://github.com/afonsoft/LangGraph-UI/compare/v0.0.2...v0.0.3
[0.0.2]: https://github.com/afonsoft/LangGraph-UI/releases/tag/v0.0.2
