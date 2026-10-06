# Docker deployment

Complete guide to running Knowledge MCP Hub (`afonsoft/knowledgerag`) with Docker — compose layout, `.env` reference, volumes, upgrades and troubleshooting. For a quick install see [INSTALL.md](INSTALL.md).

## Prerequisites

- Docker Engine ≥ 24 (or Docker Desktop) with the Compose plugin (`docker compose version`)
- Ports/volumes below are free on the host

## Quick start

**Prebuilt image, no checkout:**

```bash
docker pull afonsoft/knowledgerag
docker run -d --name knowledgerag -p 5000:8080 \
  -v knowledgerag-data:/data \
  -v vault:/vaults/default -e Vault__Path=/vaults/default \
  afonsoft/knowledgerag
```

**Compose (recommended):**

```bash
git clone https://github.com/afonsoft/KnowledgeRAG.git
cd KnowledgeRAG
cp .env.example .env                 # edit values — see reference below
mkdir -p data logs && chown -R 1654:1654 data logs
docker compose up -d
```

Open `http://localhost:5000`, sign in `admin` / `123qwe` — password change is forced on first login.

> The compose file also works **without** the repo checkout beyond `docker-compose.yml` + `.env`: remove the `build:` line (or run `docker compose pull && docker compose up -d`) to use the published image directly.

## Containers & volumes

| Mount | Purpose | Notes |
|---|---|---|
| `./data` → `/data` | SQLite catalog/vector DB + uploads | bind mount — `chown 1654:1654` required |
| `./logs` → `/app/logs` | Serilog file sink (daily rolling, 14-day retention) | bind mount — `chown 1654:1654` required |
| `vault` → `/vaults/default` | **Default Obsidian vault** | named volume — created automatically, inherits uid 1654 from the image |

The container runs as non-root `app` (uid **1654**) and listens on **8080** internally (`${KNOWLEDGEHUB_PORT:-5000}:8080` published). Health probe: `GET /health/ready` (also the image `HEALTHCHECK`); liveness: `GET /healthz`.

### The `vault` volume

- Declared as a host-global named volume (`volumes.vault.name: vault`) so its name stays `vault` regardless of the checkout directory. Drop the `name:` line if you prefer a per-project `<project>_vault` volume — but pick another name if a second KnowledgeHub stack shares this Docker host.
- On first boot — while **zero** `ObsidianVault` sources exist — the `VaultSeeder` registers the mount as a source named `VAULT_NAME`, so `write_note` / `write_knowledge` / `read_document` work immediately and notes written by agents persist in the volume.
- **Opt out:** set `VAULT_PATH=` (empty) in `.env` — no source is seeded. If you delete the seeded source in the UI, it is only re-created when no other ObsidianVault source exists.
- **Use your own vault instead:** bind-mount a host dir in `docker-compose.override.yml` (e.g. `- /srv/my-vault:/vaults/default`) — `chown -R 1654:1654` the host dir — or mount it under a different path (`/vaults/mine`) and register a second source in the UI.
- **Inspect/backup:** `docker volume inspect vault`, or `docker run --rm -v vault:/v -v $PWD:/b alpine tar -C /v -czf /b/vault-backup.tgz .`

## `.env` reference

`docker-compose.yml` reads `.env` twice: (1) `env_file` injects every `Section__Key` var into the container (ASP.NET Core reads them case-insensitively), and (2) `${VAR}` interpolations in the file itself resolve from it. `required: false` means a missing `.env` silently falls back to the defaults shown — verify with `docker compose config` before `up -d`.

### Deployment

| Var | Default | Notes |
|---|---|---|
| `KNOWLEDGEHUB_PORT` | `5000` | host port published → container `8080` |
| `ALLOWED_HOSTS` | `*` | pin your public hostname behind a reverse proxy |
| `AUTH_ADMIN_INITIAL_PASSWORD` | `123qwe` | seed for first-boot `admin` (forced change regardless) |

### Default vault

| Var | Default | Notes |
|---|---|---|
| `VAULT_PATH` | `/vaults/default` | empty disables the first-boot vault seed |
| `VAULT_NAME` | `Default Vault` | display name of the seeded source |

### Embeddings

| Var | Default | Notes |
|---|---|---|
| `EMBEDDINGS_PROVIDER` | `deterministic` | `deterministic` (zero-infra), `ollama`, `openai`, `onnx`, `voyage`, `cohere` |
| `EMBEDDINGS_ENDPOINT` | `http://host.docker.internal:11434` | host Ollama is reachable via the `extra_hosts` mapping |
| `EMBEDDINGS_MODEL` | `nomic-embed-text` | e.g. `text-embedding-3-small` for OpenAI |
| `EMBEDDINGS_APIKEY` | — | required for hosted providers |

### Database & vector store

| Var | Default | Notes |
|---|---|---|
| `DATABASE_PROVIDER` | `auto` | `auto` probes Postgres then falls back to SQLite; `postgres`/`sqlite` force |
| `POSTGRES_HOST/PORT/DB/USER/PASSWORD` | `host.docker.internal:5432` / `rag_db` / `rag_user` | composed into `VectorStore__ConnectionString` |
| `DATABASE_CONNECTIONSTRING` | — | escape hatch — full Npgsql string wins over `POSTGRES_*` |
| `VECTORSTORE_PROVIDER` | — | unset follows `DATABASE_PROVIDER`; set for mixed mode (`sqlite-vec`/`postgres`) |
| `VECTORSTORE_CONNECTIONSTRING` | composed | full override for the vector store |

Postgres on the **Docker host**: `host.docker.internal` reaches it, but `pg_hba.conf` must allow the container subnet (e.g. `host rag_db rag_user 172.22.0.0/16 md5` — check your subnet via `docker network inspect`). The `vector` extension must exist in the target DB (`CREATE EXTENSION vector` — the app creates it if the user has permission).

### Chat (server-side LLM for `ask_knowledge` / `agent_chat`)

`Chat__*` is intentionally **not** mapped in the base compose — chat wiring lives in `docker-compose.override.yml` (copy from `docker-compose.override.yml.example`):

| Var | Notes |
|---|---|
| `CHAT__PROVIDER` | `ollama` or `openai` |
| `CHAT__ENDPOINT` | e.g. `http://host.docker.internal:11434` |
| `CHAT__MODEL` | e.g. `llama3.1` |
| `CHAT__APIKEY` | hosted providers only |

Per-key overrides (`set_chat_settings` MCP tool) and the persisted store (`ChatSettings`) take precedence over env.

### Upstream MCP proxies (optional)

`DEEPWIKI_*`, `FIRECRAWL_*`, `TAVILY_*`, `CONTEXT7_*` — each has `_ENABLED`, `_ENDPOINT`, `_APIKEY`, `_TIMEOUT_SECONDS`. All work keyless or disabled; see `.env.example` for defaults.

### Cache, MCP, misc

| Var | Default | Notes |
|---|---|---|
| `CACHE_PROVIDER` | `memory` | `redis` enables the distributed L2 — needs `REDIS_CONNECTIONSTRING` (e.g. `host.docker.internal:6379`; add `,password=...` if required) |
| `MCP__SESSIONMODE` | `StatefulForInitializeClients` | `Stateless` for pure `2026-07-28` clients |
| `MCP__MAXCONCURRENTCALLSPERSESSION` | `8` | tools/call concurrency per session |

## docker-compose.override.yml

Compose auto-merges `docker-compose.override.yml` (gitignored) over the base file — the right place for host-specific mounts and the `Chat__*` mapping:

```yaml
services:
  knowledgerag:
    volumes:
      - /srv/my-vault:/vaults/obsidian        # extra host vault (chown 1654:1654)
      # - /srv/webdav/vault:/vaults/webdav:ro # WebDAV mount — read-only
    environment:
      Chat__Provider: ${CHAT__PROVIDER:-none}
      Chat__Endpoint: ${CHAT__ENDPOINT:-}
      Chat__Model: ${CHAT__MODEL:-}
      Chat__ApiKey: ${CHAT__APIKEY:-}
```

See `docker-compose.override.yml.example` for the annotated template.

## Reverse proxy

- Set `ALLOWED_HOSTS` to the public hostname (or keep `*` when the tunnel is the trust boundary).
- Forward `X-Forwarded-Proto`/`X-Forwarded-For` — the A2A agent card and generated URLs honor the forwarded scheme/host.
- WebSocket/SSE must be allowed (MCP `/mcp`, `/mcp/sse`, SignalR `/hubs/*`, A2A streams) — disable response buffering on those paths.

## Upgrade & rollback

```bash
docker compose pull            # picks up :latest
docker compose up -d           # recreate; EF migrations run at startup
```

Pin a version in `.env`-less compose edits: `image: afonsoft/knowledgerag:0.1.1`. Roll back by pointing at the previous tag and `up -d` — data in `./data` and `vault` survive.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Container starts, logs empty / SQLite errors | `./data` and `./logs` created as `root` — `chown -R 1654:1654 data logs` and recreate |
| `write_note` fails / no vault source | check `Vault__Path` renders — `docker compose config`; empty `VAULT_PATH` disables the seed |
| Vault source reappears after deletion | it re-seeds while zero ObsidianVault sources exist — set `VAULT_PATH=` empty |
| Health check failing | `docker compose logs -f` → `GET /health/ready` reports which check is degraded |
| Postgres unreachable | `host.docker.internal` resolves but `pg_hba.conf` must allow the container subnet |
| Env drift / unexpected defaults | `docker compose config` renders the final env — a missing `.env` falls back silently |

Validate changes before deploying: `docker compose config -q`.
