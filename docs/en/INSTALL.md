# Installation

KnowledgeHub runs three ways: from source (.NET SDK), as a Docker container, or as a self-contained single-file binary.

## Docker (recommended)

### Prebuilt image

Published to [Docker Hub](https://hub.docker.com/r/afonsoft/knowledgerag) (tags `latest` / `X.Y.Z`) and GHCR on every release — no checkout needed:

```bash
docker pull afonsoft/knowledgerag
docker run -d --name knowledgerag -p 5000:8080 -v knowledgerag-data:/data afonsoft/knowledgerag
```

`ghcr.io/afonsoft/knowledgerag` serves the same tags. Open `http://localhost:5000` and sign in `admin` / `123qwe` (forced password change).

### Compose (from a checkout)

```bash
cp .env.example .env   # edit values
mkdir -p data logs && chown -R 1654:1654 data logs   # container runs as uid 1654 (app)
docker compose up -d
```

- Builds `knowledgehub:latest` and serves on the mapped port (`docker-compose.yml` — `5550:8080` by default).
- `./data` is bind-mounted and persists `knowledgehub.db` + uploads.
- `./logs` is bind-mounted and persists the Serilog file sink — daily rolling files (`knowledgehub-YYYYMMDD.log`), 14-day retention, secrets redacted as `***REDACTED***`. **If `./logs` or `./data` don't exist Docker creates them as `root` and neither the file sink nor the SQLite migration can write** (the container runs as `app`, uid 1654) — pre-create it with the `chown` above or fix it once after the first `up`.
- EF Core migrations apply automatically at startup.
- Health probe: `GET /healthz`.

## From source

```bash
dotnet build KnowledgeHub.slnx
dotnet ef database update -p src/KnowledgeHub.Server   # optional — runs at startup too
dotnet run --project src/KnowledgeHub.Server           # http://localhost:5000
```

Requires .NET SDK 10.0.x (`global.json`). First login: `admin` / `123qwe` (forced password change; override the seed via `Auth__AdminInitialPassword`).

## Standalone binary + systemd

```bash
dotnet publish src/KnowledgeHub.Server -c Release -r linux-x64   # or win-x64 / osx-arm64
sudo ./install.sh --host --systemd
```

Produces a ~120 MB self-contained binary — no .NET runtime required. The installer creates a hardened systemd unit; `knowledgehub.db` is created beside the executable. `backup.sh`/`restore.sh` cover the database and uploads.

## Configuration

`appsettings.json` + environment variables (`__` nesting) + `.env`. Runtime-editable settings (Chat, Graph, integration keys, per-API-key overrides) are stored in SQLite via `/settings` — no restart needed. See `docs/architecture/system-architecture.md` §5 for the full surface.

## Optional backends

| Option | Setting | Purpose |
|---|---|---|
| PostgreSQL + pgvector | `VectorStore:Provider=postgres` + `VectorStore:ConnectionString` | HNSW vector search at scale |
| Redis | `Cache:Provider=redis` + `Cache:Redis:ConnectionString` | Distributed cache for search/embedding/answer regions |
| Ollama / OpenAI-compat | `Embeddings:*` / `Chat:*` | Embedding and chat models |
| OTLP / Prometheus | `Telemetry:Otlp:Endpoint` / `Telemetry:Metrics:Prometheus` | Traces + `/metrics` (+ OTLP log sink) |
| pgvector tuning | `VectorStore:Postgres:*` | `StorageType` vector\|halfvec (pgvector ≥0.7 + `AllowStorageMigration`), `IterativeScan` (≥0.8), `Hnsw*`, pool sizes — see README §Configuration |
| Cache tuning | `Cache:RegionTtlMinutes`, `Cache:L1*` | Per-region TTLs + in-process L1 in front of Redis (invalidation via `kh:invalidate` pub/sub) — see README §Configuration |
| Runtime log level | `GET/PUT /api/settings/log-level` | `LoggingLevelSwitch` with optional `minutes` (0–120) |
| Voyage AI / Cohere | `Embeddings:Provider=voyage\|cohere` + `Embeddings:Voyage|Cohere:{ApiKey,Model}` | Hosted embedding providers (sub-section wins; top-level `ApiKey`/`Model`/`Dimensions` are the fallback, e.g. settings UI) |
| Resilience fallback | `Resilience:Fallback` | `Mode` `disabled`\|`observe`\|`enforce`, `MaxFallbackAttempts`, `ChatFallbacks[]` (ordered `{endpoint,model,apiKey}` alternates), `ToolCapabilities` map (`WebSearch`/`DeepDocLookup`) |
| Agent loop | `Agent:*` | `EnableDynamicActionBridge` (live MCP tools inside `ask_knowledge`), `MaxChainedDynamicCalls` (default 3), `ContextManagement:*` (ChainAst compaction + orphan tool-call repair) |
| Retrieval tuning | `Search:*` | `LimitMode` `fixed`\|`autocut`, `Autocut:Sensitivity`/`MaxClamp`, `Expansion:WindowThresholdPercent`, `Relaxation:Enabled`/`MinResults` |
| Connector secrets | `restapi:{id}` · `sql:{id}` · `git:{id}` · `unstructured:{id}` · `audio:{id}` | Per-source secrets in the integration-secret store (headers, connstring, PAT, API keys) — never in `configuration` |

## Unified database provider (catalog + vector store)

`Database:Provider` selects a single backend for the EF Core catalog and the
vector store: `auto` (default — probes `POSTGRES_*`/`Database:ConnectionString`
and falls back to SQLite when Postgres is unreachable, logging an error),
`postgres` (requires a connection string — startup error otherwise), or
`sqlite`. On the first Postgres boot, an empty catalog is backfilled from the
existing SQLite file (one-shot, IDs preserved, `.migrated` marker). Lexical
search uses a `tsvector` generated column + GIN index on Postgres and FTS5 on
SQLite. `VectorStore:Provider` remains an explicit override for mixed mode.

## PostgreSQL + pgvector (host or external)

`docker-compose.yml` reads `.env` (`env_file`, `required: false`) and composes
`VectorStore__ConnectionString` from discrete vars — set `VECTORSTORE_PROVIDER=postgres` plus:

```dotenv
POSTGRES_HOST=host.docker.internal   # host Postgres via compose extra_hosts
POSTGRES_PORT=5432
POSTGRES_DB=rag_db
POSTGRES_USER=rag_user
POSTGRES_PASSWORD=...
# Escape hatch — wins over the POSTGRES_* composition when set:
# VECTORSTORE_CONNECTIONSTRING=Host=...;Database=...;Username=...;Password=...;SSL Mode=Require
```

Provisioning checklist on the Postgres side:

1. **Extension** — the `vector` extension must exist in the target database. `PostgresVectorStore` runs `CREATE EXTENSION IF NOT EXISTS vector` at init, which works when the app user has `CREATE` privilege on the database; otherwise pre-create it as a superuser (`CREATE EXTENSION vector`). The pgvector package must match the server major (e.g. `postgresql-18-pgvector`, or build from source with `PG_CONFIG=<path>/pg_config`).
2. **pg_hba.conf** — when Postgres runs on the Docker host, `host.docker.internal` resolves to the host gateway but the **client IP is the container's** — add a host rule covering the compose network (e.g. `host rag_db rag_user 172.22.0.0/16 md5`) and reload.
3. **Verify** — `docker compose config` should render the interpolated connection string; after `up`, `GET /api/diagnostics/vectorstore` reports `postgres`, the dimension, chunk count and index state (HNSW auto-created past `HnswThreshold`).

> **Redis note.** The same `host.docker.internal` pattern applies to `REDIS_CONNECTIONSTRING` (`Cache:Provider=redis`). An unauthenticated Redis triggers a startup warning — see "Redis security" in the README for hardening.

### Production deploy checklist (post-incident 2026-09-30)

1. **`.env` presence** — `env_file` is `required: false` so clean checkouts deploy, which means a deleted `.env` **silently** falls back to defaults (port `5000`, in-memory cache). Before `docker compose up -d` on a host deploy, verify `docker compose config` renders the expected `KNOWLEDGEHUB_PORT` and connection strings — a missing `.env` reproduces silently, and a port drift shows as 502 at the reverse proxy.
2. **Reverse proxy must pass `/.well-known/*`** — A2A discovery requires `/.well-known/agent-card.json`. Some edge configs (e.g. Cloudflare rules/WAF blocking dotted paths) return 404 before the request reaches the container; the app itself serves 200. Allowlist `/.well-known/agent-card.json` in the proxy/zone config.
3. **PWA overlap** — the service worker retains the previous precache generation; after a deploy, tabs on the old version keep working until reload. Force-refresh (`Ctrl+Shift+R`) still resolves any stale-shell edge cases immediately.
