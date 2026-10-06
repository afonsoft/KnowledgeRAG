# Docker Hub publishing

Canonical page: <https://hub.docker.com/r/afonsoft/knowledgerag>

The image is published by `.github/workflows/release.yml` on every `vX.Y.Z` tag
(`afonsoft/knowledgerag:X.Y.Z` + `latest`, and `ghcr.io/afonsoft/knowledgerag`).
This folder covers what CI does **not** manage: the repository **overview**
(readme on the Hub page) and the repository **logo**.

## Overview (`overview.md`)

`overview.md` in this directory is the source of truth for the Hub page.
Self-contained, basic Markdown only — Docker Hub sanitizes HTML, and images
must use absolute URLs (they render after merge to `main` via
`raw.githubusercontent.com`).

Update it on Docker Hub in one of three ways:

1. **Web UI** — My Hub → Repositories → `knowledgerag` → *Repository overview* →
   **Edit** → paste `overview.md` → Update. Also set the ≤100-char *description*
   (pencil icon under the description field):
   `All-in-one .NET RAG platform — native MCP server, hybrid retrieval, Blazor admin UI.`
2. **Script** — `./scripts/update-dockerhub-overview.sh` with
   `DOCKERHUB_TOKEN=<PAT>` (PAT needs Read/Write/Delete scope — Hub treats
   repo-metadata edits as admin-level). It PATCHes
   `/v2/repositories/{ns}/{repo}` (`description` + `full_description`).
3. **CI (optional)** — `peter-evans/dockerhub-description` in the
   `publish-docker` job keeps it in sync per release. `.github/workflows/` is
   branch-protected, so adding that step is an admin operation.

## Repository logo (`docs/assets/icon.svg` + `icon-512.png`)

Icon source: `docs/assets/icon.svg` (PNG renders: `icon-512.png`, `icon-120.png`).
Design: brain silhouette with a knowledge-graph network — the KnowledgeRAG brand.

Docker Hub rules (docs.docker.com/docker-hub):

- **Per-repository logo** is a **Docker Verified Publisher** feature only
  (PNG/JPEG, 120–1000 px, ≤5 MB, uploaded on the repo page by an org owner/editor).
- For a regular user/org repo — `afonsoft/knowledgerag` included — the icon
  shown is the **namespace avatar**: the account profile picture for user
  namespaces (Account settings → General → upload `icon-512.png`), or the org
  logo for organization namespaces (Organizations → `afonsoft` → Settings).
  Uploading the icon once there brands every repo under the namespace.

## Verify

```bash
curl -fsS https://hub.docker.com/v2/repositories/afonsoft/knowledgerag \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["description"]); print(len(d.get("full_description") or ""), "chars")'
```
