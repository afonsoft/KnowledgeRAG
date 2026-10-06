# Knowledge MCP Hub

![Knowledge MCP Hub](https://raw.githubusercontent.com/afonsoft/KnowledgeRAG/main/docs/assets/icon-512.png)

**All-in-one standalone agentic-RAG platform in a single container** (.NET 10 / Kestrel): Blazor WebAssembly admin UI + REST management API + **native MCP server** (Streamable HTTP + legacy SSE). Point your AI agents and apps at the knowledge you already have — Obsidian vaults, web pages, documents, APIs, SQL databases, cloud storage, RSS, YouTube and Git repositories.

## Quick start

```bash
docker pull afonsoft/knowledgerag

docker run -d --name knowledgerag \
  -p 5000:8080 \
  -v knowledgerag-data:/data \
  -v vault:/vaults/default -e Vault__Path=/vaults/default \
  afonsoft/knowledgerag
```

- Admin UI: `http://localhost:5000` — sign in `admin` / `123qwe` (forced password change on first login; override the seed via `Auth__AdminInitialPassword`).
- MCP endpoint for agents: `http://localhost:5000/mcp` (Streamable HTTP) or `/mcp/sse` (legacy SSE) — `Authorization: Bearer aft_*` (generate keys at `/api-keys` in the UI).
- Health probe: `GET /health/ready` (already wired as the image `HEALTHCHECK`).

The same image is also on GHCR: `docker pull ghcr.io/afonsoft/knowledgerag`.

## What you get

- **Dynamic MCP tool catalog** — `search_knowledge`, `ask_knowledge`, `agent_chat`, `read_document`, `write_knowledge`/`write_note`, GraphRAG tools (`find_dependencies`, `find_dependents`, `analyze_impact`, temporal `search_graph_*`), plus upstream proxies (DeepWiki, Firecrawl, Tavily, Context7) and arbitrary `McpProxy` sources.
- **Hybrid retrieval** — FTS5 + sqlite-vec/pgvector fused with RRF, MMR diversification, corrective-RAG grading/retry/abstain, history-aware rewrite, multi-query/HyDE expansion, window retrieval with autocut.
- **Connectors** — Obsidian (local/WebDAV), WebPage, DocumentFile, Notion, RestApi, SqlDatabase (SELECT-only, guarded), S3/Azure Files/OCI Object Storage, RSS/Atom, YouTube transcripts, GitRepository, UnstructuredDocument OCR, AudioTranscription.
- **Agentic loop** — tool-calling with chain AST, HITL approvals, SSE streaming, conversation threads, per-key chat/embeddings settings.
- **Pluggable embeddings & stores** — deterministic (zero-infra), Ollama, OpenAI-compatible, Voyage, Cohere, local ONNX (all-MiniLM-L6-v2); SQLite or Postgres/pgvector backend.
- **Ops built-in** — EF Core migrations at startup, Serilog structured logs with secret redaction, OpenTelemetry traces/metrics, health checks, backup/restore scripts, PWA admin with live MCP monitor (SignalR).

## Ports & volumes

| Item | Value |
|---|---|
| HTTP port | `8080` (container) — map with `-p <host>:8080` |
| Data volume | `/data` — SQLite catalog/vector DB + uploads |
| Vault | `/vaults/default` — default Obsidian vault; seeded as a source when `Vault__Path` points there |
| Logs | `/app/logs` — optional mount; Serilog file sink, daily rolling, 14-day retention |
| User | non-root `app` (uid **1654**) — `chown` bind-mounted host dirs accordingly |

## Common environment variables

All ASP.NET Core settings accept `Section__Key` env vars. Frequently used:

| Variable | Default | Purpose |
|---|---|---|
| `Embeddings__Provider` | `deterministic` | `deterministic` / `ollama` / `openai` / `onnx` / `voyage` / `cohere` |
| `Embeddings__Endpoint` | — | e.g. `http://host.docker.internal:11434` for host Ollama |
| `Embeddings__Model` | — | e.g. `nomic-embed-text` |
| `Chat__Provider` | `none` | `ollama` / `openai` — enables server-side answer synthesis & agent loop |
| `Chat__Endpoint` / `Chat__Model` / `Chat__ApiKey` | — | LLM used by `ask_knowledge` / `agent_chat` |
| `Database__Provider` | `auto` | `auto` / `sqlite` / `postgres` — `auto` falls back to SQLite |
| `VectorStore__ConnectionString` | — | pgvector target, e.g. `Host=host.docker.internal;Database=rag_db;...` |
| `Cache__Provider` | `memory` | `memory` / `redis` (distributed L2 cache) |
| `Mcp__SessionMode` | `StatefulForInitializeClients` | hybrid MCP sessions; `Stateless` for spec `2026-07-28`-only setups |
| `Auth__AdminInitialPassword` | `123qwe` | seed for first-boot `admin` (forced change on login) |
| `Vault__Path` | — | when set and no ObsidianVault source exists, seeds one at this path — pair with `-v vault:/vaults/default` |
| `Vault__Name` | `Default Vault` | display name of the seeded vault source |

Reaching services on the Docker host (Ollama, Postgres, Redis): add `--add-host=host.docker.internal:host-gateway` on `docker run`.

## Docker Compose

```yaml
services:
  knowledgerag:
    image: afonsoft/knowledgerag:latest
    ports: ["5000:8080"]
    volumes: ["./data:/data", "./logs:/app/logs", "vault:/vaults/default"]
    environment:
      Vault__Path: /vaults/default
      Chat__Provider: ollama
      Chat__Endpoint: http://host.docker.internal:11434
      Chat__Model: llama3.1
      Embeddings__Provider: ollama
      Embeddings__Endpoint: http://host.docker.internal:11434
      Embeddings__Model: nomic-embed-text
    extra_hosts: ["host.docker.internal:host-gateway"]
    restart: unless-stopped
volumes:
  vault:
    name: vault
```

> `./data` and `./logs` must be writable by uid **1654**: `mkdir -p data logs && chown -R 1654:1654 data logs`.

## Tags

- `latest` — most recent stable release
- `X.Y.Z` — immutable release tags (e.g. `0.1.1`), published on every `vX.Y.Z` Git tag

## Links

- Source & docs: [github.com/afonsoft/KnowledgeRAG](https://github.com/afonsoft/KnowledgeRAG)
- Install guide: [docs/en/INSTALL.md](https://github.com/afonsoft/KnowledgeRAG/blob/main/docs/en/INSTALL.md) · [pt-BR](https://github.com/afonsoft/KnowledgeRAG/blob/main/docs/pt/INSTALL.md)
- License: MIT — © Afonso Dutra Nogueira Filho, 2026
