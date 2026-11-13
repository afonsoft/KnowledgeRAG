---
name: knowledge-hub
license: MIT
description: Use Knowledge MCP Hub as your persistent memory and knowledge layer — ground answers before coding (search_knowledge/ask_knowledge/memory_recall), retain learnings proactively (memory_retain), and checkpoint sessions with context_save/context_recovery. Trigger on "knowledge hub", "search knowledge first", "remember this", "use o hub", "memória persistente", "context recovery".
metadata:
  version: "1.0.0"
  visibility: public
  author: afonsoft
  url: https://github.com/afonsoft/KnowledgeRAG
---

# Knowledge MCP Hub

You have access to a **Knowledge MCP Hub** — a persistent knowledge + memory
server the user runs for you. Treat it as your long-term memory: ground
yourself before assuming, record what you learn, and start each session by
recovering context instead of asking the user to repeat it.

## 1. Connection

Check whether an MCP server named `knowledge-hub` (or `knowledgehub`) is
already configured in your harness (`mcp` section of your config file). If it
is, you're set — its tools appear as `mcp__knowledge-hub__*`.

If it is **not** configured, ask the user for the hub URL and an `aft_*` API
key (created in the hub UI → API Keys), then point them to the wiring snippet
for your harness — see `wiring/` next to this file:

| Harness | Config file | Snippet |
|---|---|---|
| Claude Code | `~/.claude.json` | `wiring/claude-code.json` |
| Cursor | `~/.cursor/mcp.json` | `wiring/cursor.json` |
| Devin CLI | `~/.config/devin/mcp.json` | `wiring/devin.json` |
| OpenCode | `~/.config/opencode/opencode.json` | `wiring/opencode.json` |
| Windsurf | `~/.codeium/windsurf/mcp_config.json` | `wiring/windsurf.json` |

Transport is Streamable HTTP at `/mcp`. Harnesses that cannot send headers
fall back to legacy SSE: `/mcp/sse?access_token=aft_...`.

No hub running yet? Point the user at `docs/en/DEPLOY-DOCKER.md`
(`docker compose up`) — do not improvise another setup.

## 2. Grounding first — before you assume

Before writing code, contracts, or business rules, check the hub:

- `search_knowledge(query)` — hybrid retrieval over the user's sources
  (docs, runbooks, wikis, API specs). Use it to verify conventions and to
  find existing utilities before creating duplicates.
- `ask_knowledge(question)` — cited synthesis; prefer for "how does X work".
- `memory_recall(query)` — your own durable memory of past sessions:
  decisions, corrections, learned facts. Query it before asking the user.
- `find_dependencies` / `analyze_impact` — before refactoring shared types.

If the hub returns nothing relevant, say so and proceed — grounding is a
check, not a blocker.

## 3. Session lifecycle

**Session start** — before the first non-trivial task:

1. `memory_session_begin {sessionId, label}` — mark the session open on the
   bank (use your agent_chat threadId as sessionId). Idempotent — repeat
   begin returns `alreadyOpen:true`.
2. `memory_context` — deterministic bank digest: directives, recent facts,
   top observations, stale models/pages, recent failures, plus open
   sessions and where the last session stopped. Zero LLM cost;
   call it first whenever a bank exists.
3. `context_recovery {repo: "owner/name"}` — latest compacted snapshot of
   prior work on this repo (decisions, open items). `repo` is the GitHub
   `owner/name` (lowercase), or the URL — it is normalized.
4. `memory_recall` for durable facts beyond the working snapshot
   (project conventions, standing corrections).
5. `memory_pages_tree` / `memory_page` — standing pages (architecture,
   conventions) maintained by the hub; cheap reads, no LLM cost.

Supporting reads while planning: `memory_timeline` (what happened when,
bucketed by day/hour/week) and `memory_stats` (bank health — consolidation
backlog, embedding coverage, observation proof counts).

**Bank identity** — prefer passing `repo` (your repo slug `owner/name` or git
URL) over a free-form `bankKey`: repo-derived banks converge across agents.
When two banks ended up naming the same project, unify them with
`memory_bank_merge {sourceKey, targetKey}` — memories move to the target and
the old key keeps resolving, so nothing breaks.

**During work** — when you learn something worth keeping:

- `memory_retain(content, strategy: "extract")` — durable facts: decisions,
  conventions, gotchas, corrections. Retain proactively, not only when asked.
- `write_note(title, path, content)` — session log entries in the vault.
- `write_knowledge(title, content)` — durable knowledge docs.

**Session end / before compaction**:

- `memory_session_end {sessionId, summary}` — close the session with where
  you stopped; the next `memory_context` shows it as "last ended".
- `context_save {repo, items: [...], label}` — typed items (`decision`,
  `note`, `passage`, `tool_output`, `summary`). Save before long pauses,
  branch switches, or context resets. Never store secrets — writes are
  secret-scanned and blocked on `block` policies.

## 4. Correction protocol

When a recalled memory or doc is **wrong or stale**, never just work around
it — supersede it:

```
write_knowledge(
  title: "Correction: <topic>",
  content: "Memory said: <old claim>. Correct as of <date>: <verified truth>. Evidence: <what you checked>.")
```

Optionally `memory_retain` the correction too (`experience` fact). Future
retrieval then surfaces the correction next to the stale claim.

## 5. Credit protocol

When an answer or edit is grounded in hub content, mark it:

```
📚 From KnowledgeHub (<source>/<doc or memory>): <the fact used>
```

This keeps provenance visible to the user and teaches teammates the hub
exists.

## 6. Rules

- Never commit, print, or log `aft_*` keys or secrets — anywhere.
- `context_save`/`memory_retain` content is secret-scanned; a `422 blocked`
  response means a credential leaked into your payload — remove it, don't retry verbatim.
- Prefer `memory_recall`/`search_knowledge` over asking the user to re-explain.
- Retain facts, not transcripts — one meaningful fact per `memory_retain` chunk.
- Respect per-bank boundaries: if the user names a bank, pass `bankKey`;
  otherwise the server default applies.

## 7. Delegation triggers

Measured thresholds (see `tests/KnowledgeHub.Tests.Bench`) — when a subagent
pays off vs working inline:

| Situation | Threshold | Action |
|---|---|---|
| Codebase exploration | >3 search/read calls or >10k tokens of reading expected | Delegate to an explore subagent — it returns a digest, not raw output |
| Parallel writers | ≥2 disjoint file surfaces (~100+ lines apart, no shared state) | One subagent per surface, in parallel |
| Independent verification | Named risk only: security, data loss, public contract, migration | Dedicated verifier after implementation |
| Everything else | Below thresholds | Work inline — delegation costs a context round-trip |
