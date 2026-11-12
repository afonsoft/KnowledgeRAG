#!/usr/bin/env bash
# SPEC-20261111-knowledge-hub-agent-skill RF-004: single-source docs skill.
# Regenerates skills/knowledge-hub-docs/SKILL.md from canonical sections
# marked <!-- skill-docs:begin --> / <!-- skill-docs:end --> in CLAUDE.md and
# docs/en/API.md. `--check` exits non-zero when the committed file diverges.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/skills/knowledge-hub-docs/SKILL.md"
MODE="${1:-write}"

extract() { # file -> all marked blocks
  local src_file="$1"
  awk '/<!-- skill-docs:begin -->/{f=1;next}/<!-- skill-docs:end -->/{f=0;next}f' "$src_file"
}

gen() {
  cat <<'HDR'
---
name: knowledge-hub-docs
license: MIT
description: Knowledge MCP Hub operational documentation for agents — MCP/REST endpoints, aft_* auth, tool catalog, transports, jobs and troubleshooting. Use when wiring the hub, calling tools, or debugging 401/409/session errors.
metadata:
  version: "1.0.0"
  visibility: public
  author: afonsoft
  url: https://github.com/afonsoft/KnowledgeRAG
generated-from:
  - CLAUDE.md
  - docs/en/API.md
---

# Knowledge MCP Hub — operational docs

> GENERATED FILE — edit the `skill-docs` sections in CLAUDE.md / docs/en/API.md
> and run `scripts/generate-skills.sh`. Direct edits are overwritten.

The hub is a single Kestrel process exposing the admin SPA, the REST API and
the native MCP server. Connect over Streamable HTTP at `/mcp` (preferred) or
legacy SSE at `/mcp/sse`; agents authenticate with `Authorization: Bearer aft_*`
(API keys are created in the hub UI under API Keys — never commit one).

HDR
  extract "$ROOT/CLAUDE.md"
  cat <<'MID'

## REST API surface (excerpt)

Full route tables live in `docs/en/API.md`. Key groups:

MID
  extract "$ROOT/docs/en/API.md"
  cat <<'FTR'

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `401` on `/mcp` | missing/invalid `aft_*` key | `Authorization: Bearer aft_...` header (or `?access_token=` on `/mcp/sse`) |
| `tools/list` empty | key scope restricts tools | check key scopes in the hub UI |
| `409` on job cancel | job already running/terminal | poll `operations_get`, wait for finish |
| `notifications/initialized` → 400 | stateless session mode | omit the notification or use `Mcp:SessionMode=StatefulForInitializeClients` |
| `422 blocked` on writes | secret scan block policy | remove the credential — findings list pattern names only |
| SSE refused | server in `Stateless` mode | use Streamable HTTP `/mcp` |

FTR
}

case "$MODE" in
  --check)
    tmp="$(mktemp)"
    gen > "$tmp"
    if ! diff -q "$OUT" "$tmp" >/dev/null 2>&1; then
      echo "STALE: $OUT diverges from sources — run scripts/generate-skills.sh" >&2
      diff -u "$OUT" "$tmp" | head -40 >&2 || true
      rm -f "$tmp"; exit 1
    fi
    rm -f "$tmp"; echo "OK: $OUT is fresh"
    ;;
  *)
    mkdir -p "$(dirname "$OUT")"
    gen > "$OUT"
    echo "wrote $OUT"
    ;;
esac
