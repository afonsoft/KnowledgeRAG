# Agent Skill

The hub ships three [Agent Skills](https://agentskills.io) under `skills/` —
installable instructions that teach any coding agent how to use your hub
correctly without reading this repository.

```bash
npx skills add afonsoft/KnowledgeRAG -s knowledge-hub
```

| Skill | What it teaches |
|---|---|
| `knowledge-hub` | Session lifecycle: `memory_session_begin` + `memory_context` + `context_recovery` on start, `memory_session_end` + `context_save` on end; grounding-first (`search_knowledge`/`memory_recall` before assuming); proactive `memory_retain`; `repo`-derived bank identity + `memory_bank_merge` for duplicates; `Correction: <topic>` supersede protocol; `📚 From KnowledgeHub` credit markers; per-harness MCP wiring. |
| `knowledge-hub-docs` | Operational reference: endpoints, `aft_*` auth, tool catalog, transports, jobs, troubleshooting. **Generated** from `CLAUDE.md` + `docs/en/API.md` by `scripts/generate-skills.sh` — never edited by hand; CI fails on divergence. |
| `knowledge-hub-architect` | Adoption planner: maps your knowledge locations to `SourceType`s, proposes memory banks, tag schema and a first eval baseline. |

## Wiring

Per-harness MCP config snippets live in `skills/knowledge-hub/wiring/`
(claude-code, cursor, devin, opencode, windsurf). All use Streamable HTTP
`/mcp` + `Authorization: Bearer AFT_KEY_HERE`; harnesses without header
support fall back to `/mcp/sse?access_token=...`.

## Freshness contract

`skills/knowledge-hub-docs/SKILL.md` is generated from canonical docs —
edit the `<!-- skill-docs:begin/end -->` sections in `CLAUDE.md` /
`docs/en/API.md` and run:

```bash
scripts/generate-skills.sh          # regenerate
scripts/generate-skills.sh --check  # CI check (non-zero when stale)
```

`tests/KnowledgeHub.Tests.Unit/Skills/SkillsFreshnessTests.cs` enforces:
generated file freshness, valid frontmatter per skill, parseable wiring
JSON with `AFT_KEY_HERE` placeholders, and zero real secrets in `skills/`.
