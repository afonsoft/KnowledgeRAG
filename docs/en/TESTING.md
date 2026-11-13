# Testing — Journey Bench

`tests/KnowledgeHub.Tests.Bench` measures the **agent journey**: how many MCP
calls, how much wall-time and how many payload bytes a typical workflow costs —
against committed baselines in `tests/eval/journeys/baseline.json`.

It drives the app through **public seams only** (in-process host + MCP
JSON-RPC, same harness pattern as `tests/KnowledgeHub.Tests.Blackbox`) and runs
in deterministic mode: zero LLM, zero network beyond loopback.

## Run

```bash
dotnet test tests/KnowledgeHub.Tests.Bench
```

## Journeys (v1)

| Journey | Flow | Hard gate |
|---|---|---|
| `memory-lifecycle` | bank_create → retain(verbatim) → recall → context → timeline → stats → list | ≤7 calls |
| `session-grounding` | context → recall → list (the E30 session-start pattern, on a populated bank) | ≤3 calls |
| `retain-dedupe-prompt` | retain → retain(same prompt+session) | ≤2 calls, proves `promptSeen:true` |
| `retain-async-poll` | retain(async) → operations_get poll until done (≤20 × 100ms) | ≤21 calls |

## Baselines

`tests/eval/journeys/baseline.json` per journey: `{ maxCalls, budgetMs, warnMs }`.

- **maxCalls** — hard gate. Structural friction: a regression here means an
  extra MCP round-trip for the same outcome.
- **budgetMs** — hard gate, generous (~10× observed). Catches hangs/catastrophic
  slowdowns without flaking on CI noise.
- **warnMs** — informational only, logged via test output.
- A journey **without** a baseline entry fails — forces baselining new journeys.

Regenerate after a deliberate change (local only, never in CI):

```bash
BENCH_UPDATE_BASELINE=1 dotnet test tests/KnowledgeHub.Tests.Bench
```

## Adding a journey

1. Add a named `Journey` in `Journeys/MemoryJourneys.cs` — ordered
   `JourneyStep`s `{tool, args, assert}`; use a unique `bankKey` per run and
   `strategy:"verbatim"` to stay deterministic (no LLM).
2. Run once with `BENCH_UPDATE_BASELINE=1` to write its baseline.
3. Commit the updated `baseline.json` with the journey code.

A flaky journey is a bug report — there is no retry machinery by design.
