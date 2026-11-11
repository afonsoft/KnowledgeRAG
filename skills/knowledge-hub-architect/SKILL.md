---
name: knowledge-hub-architect
license: MIT
description: Design a Knowledge MCP Hub adoption plan for the user's application — which source types to register, memory bank layout, tag schema, chunking options and a first eval baseline. Use when onboarding a new app/team to the hub or when the user asks "how should I organize my knowledge in the hub".
metadata:
  version: "1.0.0"
  visibility: public
  author: afonsoft
  url: https://github.com/afonsoft/KnowledgeRAG
---

# Knowledge Hub Architect

Help the user design how their application/team plugs into a Knowledge MCP
Hub. Your output is a **plan**, not code: source registrations, memory bank
layout, tag schema, ingestion options, and a starter eval baseline.

Ask before assuming — one round of questions is expected.

## 1. Discovery questions

1. What knowledge exists and where? (wiki/docs folder, Obsidian vault,
   Confluence/Notion, REST APIs, databases, Slack exports)
2. Who are the consumers? (which agents/teams, read-only vs read-write)
3. Is a Knowledge MCP Hub already running? (URL + `aft_*` key) — if not,
   deployment comes first (`docs/en/DEPLOY-DOCKER.md`), stop there.
4. Any compliance constraints? (PII in sources, per-team isolation needs)

## 2. Source mapping

Map each knowledge location to a `SourceType`:

| Knowledge lives in | SourceType | Notes |
|---|---|---|
| Obsidian vault / markdown folder | `ObsidianVault` | `path` config; `readonly` for reference vaults; enables `write_note`/`write_knowledge` targets |
| Public/internal web pages | `WebPage` | crawl depth + allowed hosts |
| Loose files (PDF/MD/TXT/code) | `DocumentFile` | upload or path glob |
| REST/JSON APIs | `RestApi` | `itemsPath` dot-path, `titleField`/`contentFields`, header secrets via secret store |
| SQL databases | `SqlDatabase` | SELECT-only guard; `sqlite`/`postgres`; column mapping |
| Notion | `Notion` | read-only; token encrypted; incremental by `last_edited_time` |
| Cloud storage (S3/Azure/OCI) | cloud connectors | staged locally by ETag |
| Another MCP server | `McpProxy` | tools passthrough into the catalog |

Prefer **fewer, well-scoped sources** over many tiny ones — retrieval
quality beats bookkeeping.

## 3. Memory bank layout

One `MemoryBank` per durable scope — recommend:

- `bank per repo/project` — coding-agent memory (decisions, conventions,
  corrections) scoped so repos never bleed into each other
- `bank per agent role` only when the same project needs isolated personas
- shared `default` bank for cross-project learnings

Agents scope via `bankKey` arg, `X-Memory-Bank` header, or a dedicated
`/mcp/banks/{key}` endpoint. Configure per-bank `defense` overrides when a
bank must hard-block secrets instead of redacting them.

## 4. Tag schema

Propose a small controlled vocabulary (tags power memory filtering,
directive scoping and page triggers):

- `kind:*` — `kind:decision`, `kind:runbook`, `kind:gotcha`
- `area:*` — bounded contexts (`area:billing`)
- `lang:*`, `env:*` when multi-stack/multi-env

Warn against free-form tag sprawl — 10–20 tags max at start.

## 5. Ingestion options worth deciding

- `chunking`: `semantic` for prose-heavy sources; default elsewhere
- `contextual enrichment` is on — no action needed
- `graph: true` per source when entity/relation questions matter
- async jobs: large sources should expect `202` + `operations_get` polling

## 6. Eval baseline

Recommend an initial `POST /api/eval/runs` baseline on the top user
questions (10–20 real queries) *before* tuning — then re-run after each
source/chunking change.

## 7. Output template

```
## Hub adoption plan — <app>
### Sources (to register)
- <name> · <SourceType> · <config notes> · readonly?
### Memory banks
- <bankKey> — purpose, defense overrides?
### Tags
- kind:*, area:*, ...
### Ingestion
- per-source: chunking/graph notes
### First actions
1. deploy/verify hub + create aft_* key
2. register sources, run sync, spot-check search_knowledge
3. eval baseline on <N> real questions
4. install `knowledge-hub` agent skill on the team's agents
```
