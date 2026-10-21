# Context Compaction — Eval Matrix (SPEC-20261021-context-eval-matrix)

T12 evaluation pending from SPEC-20261004. Matrix: `strategy × tokenBudget`
over a synthetic snapshot (2 protected decisions, 4 ranked passages, 1 tool
output, 1 tail note). Harness: `tests/KnowledgeHub.Tests.Unit/Context/ContextEvalMatrixTests.cs`.

## Matrix — honesty contract

| strategy \ budget | 1024 | 2048 | 4096 | 8192 |
|---|---|---|---|---|
| none | ✅ | ✅ | ✅ | ✅ |
| elision | ✅ | ✅ | ✅ | ✅ |
| elision+summary (LLM stub) | ✅ | ✅ | ✅ | ✅ |
| elision+summary (no LLM) | ✅ fallback | ✅ fallback | ✅ fallback | ✅ fallback |

✅ = `tokensAfter ≤ budget`, or `withinBudget=false` honestly reported when only
protected items (decisions + keep-min tail) remain. `droppedCount` /
`elidedCount` / `summaryUsed` / `summaryFallback` populated in every cell.

## Findings

- **Elision is sufficient** for budgets ≥2048 on the synthetic fixture; at 1024
  the keep-min tail + decisions dominate and `withinBudget` degrades honestly.
- **No-provider path never fails**: `elision+summary` without an
  `IAssistantChatClientProvider` sets `summaryFallback=true` whenever a summary
  was attempted and falls back to deterministic elision ordering.
- **LLM path (stubbed)** marks `summaryUsed=true`, `summaryFallback=false`.

## Gate decision (RF-3)

`elision+summary` **remains the default** for `context_compact`. Rationale:
the honesty contract holds in all cells, the deterministic fallback cannot
fail, and `summaryFallback`/`summaryUsed` stats expose degraded runs to
callers. Live-LLM faithfulness deltas (Recall@K / RAG-triad) should be
re-measured in the hosted eval harness when a real provider is configured —
tracked as a follow-up, not a blocker.
