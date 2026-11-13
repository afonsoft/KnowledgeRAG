# SPEC-20261113-agent-journey-bench

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-journey-bench` |
| Type | `Infra` |
| Stack | `.NET` |
| Repository | `afonsoft/KnowledgeRAG` (`/home/ubuntu/repos/KnowledgeRAG`) |
| Branch | `feature/devin-20261113-agent-journey-bench` |
| Ticket | #626 — Epic #624 |
| Status | `In implementation` |

## 1. User Story

**As a** KnowledgeHub maintainer improving the agent experience
**I want** named, deterministic MCP journeys that measure tool-call count, latency and payload size — compared against committed baselines
**So that** friction regressions (extra round-trips, slower memory calls) are caught in CI as test failures, exactly like gentle-ai's bench does for its journeys (`journeys_issueNNNN` per defect).

**Problem context:**
Current eval infrastructure measures *retrieval quality* (`tests/eval/` datasets + named baselines via eval gates) and *load* (`tests/KnowledgeHub.Tests.Performance`). Nothing measures the *agent journey* — how many calls and how much wall-time a typical workflow costs. E30 added `memory_context` specifically to shorten session-start grounding; without a bench we can't prove or protect that win.

## 2. Scope

**In scope:**
- Journey runner project `tests/KnowledgeHub.Tests.Bench` driving the app through **public seams only** (in-process host + MCP JSON-RPC and REST, same pattern as `tests/KnowledgeHub.Tests.Blackbox`).
- Named journey definitions (declarative, in-code): sequence of tool calls with assertions.
- Metrics per call and per journey: `calls`, `msPerCall`, `totalMs`, `bytesIn`, `bytesOut`; optional `tokensIn/tokensOut` when an LLM endpoint is configured (env-gated, off by default — deterministic mode has zero LLM).
- Committed baseline `tests/eval/journeys/baseline.json` per journey: `maxCalls`, `budgetMs` (hard cap), `warnMs` (informational). `--check` mode = xunit test that fails on `calls > maxCalls` or `totalMs > budgetMs`; wall-time below budget is recorded, not gated tightly (CI noise).
- First journeys: `memory-lifecycle` (bank create → retain → recall → context → timeline → stats), `session-grounding` (context → recall → list — the E30 session-start pattern), `retain-dedupe-prompt` (same prompt+session twice → `promptSeen` on 2nd, asserting the dedupe win), `retain-async-poll` (async retain → operations_get until done).
- Delegation trigger table as docs requirement: measured thresholds added to `CLAUDE.md` + `skills/knowledge-hub/SKILL.md` (exploration >3 calls or >10k tokens → delegate to subagent; parallel writers only on disjoint file surfaces; independent verifier only for named risk) — bundled here because the bench is what calibrates/validates such thresholds.

**Out of scope:**
- Real LLM cost measurement in CI (env-gated opt-in only; CI runs deterministic mode).
- Subprocess/CLI driving (journeys drive the MCP endpoint in-process, not `dotnet run` as a subprocess).
- Frontend/UI journeys.
- Flaky-tolerant retry machinery (journeys must be deterministic; a flaky journey is a bug report, not a retry).

## 3. Technical Context

**Where the change happens:**
New test project `tests/KnowledgeHub.Tests.Bench/` reusing the blackbox in-process-host harness; baseline JSON under `tests/eval/journeys/`; docs in `CLAUDE.md` + `skills/knowledge-hub/SKILL.md` (regenerate `skills/knowledge-hub-docs` via `scripts/generate-skills.sh`).

**Files to read before implementing:**
- `tests/KnowledgeHub.Tests.Blackbox/` (host harness, MCP JSON-RPC client usage)
- `tests/eval/dataset.sample.json` + `tests/KnowledgeHub.Tests.Integration/EvalBaselinesApiTests.cs` (baseline convention)
- `tests/KnowledgeHub.Tests.Performance/LoadHarness.cs` (timing/metric collection style)
- `CLAUDE.md`, `skills/knowledge-hub/SKILL.md`, `scripts/generate-skills.sh`
- `KnowledgeHub.slnx` (project registration)

**Files to create or modify:**
```text
tests/KnowledgeHub.Tests.Bench/KnowledgeHub.Tests.Bench.csproj   (new)
tests/KnowledgeHub.Tests.Bench/JourneyRunner.cs                  (new — step executor + metrics)
tests/KnowledgeHub.Tests.Bench/JourneyResult.cs                  (new — calls/ms/bytes records)
tests/KnowledgeHub.Tests.Bench/Journeys/MemoryJourneys.cs        (new — 4 named journeys)
tests/KnowledgeHub.Tests.Bench/BaselineGateTests.cs              (new — xunit gate vs baseline.json)
tests/eval/journeys/baseline.json                                (new — committed baselines)
KnowledgeHub.slnx                                                (modify — add project)
CLAUDE.md                                                        (modify — delegation table + bench docs)
skills/knowledge-hub/SKILL.md                                    (modify — delegation triggers)
docs/en/API.md or docs/en/TESTING.md                             (modify — how to run/extend bench)
```

## 4. Requirements

### RF-001: Journey runner
- **Description:** Executes a named journey — an ordered list of steps `{tool, args, assert?}` — against an in-process host via MCP JSON-RPC (and REST where the flow needs it), collecting metrics.
- **Rules:** Each step records `tool`, `ms`, `bytesIn`, `bytesOut`, `isError`; a step's `assert` is a predicate over the `CallToolResult` (e.g., `structuredContent.promptSeen == true`) — assertion failure fails the journey with the step name; the runner must not depend on wall-clock ordering of unrelated calls.
- **Input → Output:** `Journey` definition → `JourneyResult` (steps + aggregates).

### RF-002: Named journeys (v1)
- **Description:** Four deterministic journeys covering the E29/E30 memory surface.
- **Rules:**
  - `memory-lifecycle`: bank_create → retain(verbatim) → recall → context → timeline → stats → bank units check. Expected ≤7 calls; asserts `unitsCreated≥1`, `context` returns directives section, `stats` counts ≥1 unit.
  - `session-grounding`: context → recall → list. Expected ≤3 calls — the canonical session start; regression here directly hurts agent cold-start.
  - `retain-dedupe-prompt`: retain(srcPrompt+session) → retain(same prompt+session, different content). Asserts 2nd returns `promptSeen:true, unitsCreated:0` in ≤2 calls — proves provenance dedupe saves an extraction.
  - `retain-async-poll`: retain(mode:async) → operations_get poll until `succeeded` (max 20 polls, 100ms). Asserts completion; call count recorded, not hard-capped (poll count varies).
- **Input → Output:** journey name → deterministic result.

### RF-003: Baseline + gate
- **Description:** `tests/eval/journeys/baseline.json` — `{ "<journey>": { "maxCalls": n, "budgetMs": n, "warnMs": n } }`.
- **Rules:** `BaselineGateTests` runs every journey and asserts `calls ≤ maxCalls` (hard) and `totalMs ≤ budgetMs` (hard, generous — e.g. 10× observed median locally); exceeding `warnMs` logs a warning only; a missing baseline entry = test failure (forces baselining new journeys); `--update-baseline` (env `BENCH_UPDATE_BASELINE=1`) regenerates the file — never in CI.
- **Input → Output:** journey results → pass/fail vs baseline.

### RF-004: Delegation trigger table (docs)
- **Description:** `CLAUDE.md` + `skills/knowledge-hub/SKILL.md` gain a "Delegation triggers" table (measured thresholds).
- **Rules:** Content: exploration expected >3 search calls or >10k tokens of reads → delegate to `subagent_explore`; parallel writers only on disjoint file surfaces (~100+ line separation); independent verifier only for a named risk (security, data loss, public contract); otherwise work inline. Docs-skill regenerated; `SkillsFreshnessTests` stays green.
- **Input → Output:** markdown sections.

### RF-005: Registration + docs
- **Description:** Project added to `KnowledgeHub.slnx`, runs under `dotnet test tests/KnowledgeHub.Tests.Bench`; documented invocation + baseline-update flow.
- **Rules:** CI runs it in the standard test stage (deterministic mode); no new external dependency beyond existing test packages.

## 5. API Contract

N/A — test/infra feature; public seam used is the existing MCP `tools/call` and REST surface (no new endpoints).

## 6. Acceptance Criteria

- [ ] **Given** the host started in-process **when** `dotnet test tests/KnowledgeHub.Tests.Bench` **then** all 4 journeys execute and pass vs baseline.
- [ ] **Given** a journey edited to need +1 extra MCP call **when** the gate runs **then** `BaselineGateTests` fails naming the journey and expected vs actual calls.
- [ ] **Given** `retain-dedupe-prompt` **when** step 2 completes **then** assertion proves `promptSeen:true` and `unitsCreated:0`.
- [ ] **Given** a new journey without a baseline entry **when** the gate runs **then** it fails with "missing baseline".
- [ ] **Given** `BENCH_UPDATE_BASELINE=1` **when** running locally **then** `baseline.json` is regenerated with observed metrics.
- [ ] **Given** CLAUDE.md **when** an agent reads it **then** a delegation-trigger table with numeric thresholds exists; `skills/knowledge-hub-docs` regenerated and freshness tests green.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Poll journey times out | async op never succeeds in 20 polls | journey fails with op status |
| Host without LLM | deterministic mode | journeys skip token metrics; all pass |
| `retain-async-poll` poll count varies | timing | recorded, not hard-failed (budgetMs still applies) |
| Baseline file malformed | bad JSON | gate fails at parse with file path |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** read blackbox harness + eval baseline convention + perf harness.
- [ ] **T2 — Runner:** `Journey`/`JourneyStep`/`JourneyResult` + executor over the blackbox host (metrics per call).
- [ ] **T3 — Journeys:** the 4 named journeys with assertions.
- [ ] **T4 — Baseline gate:** `baseline.json` + `BaselineGateTests` + update-baseline escape hatch.
- [ ] **T5 — Docs:** delegation table in CLAUDE.md + SKILL.md; regenerate docs skill; slnx registration.
- [ ] **T6 — Gates:** run bench + full unit + blackbox + format.

## 8. Organization Guardrails

- Branches: `feature/devin-20261113-agent-journey-bench` off `main`; never commit to `main`.
- `.github/workflows/` protected — the bench runs in existing test stages; no workflow edits.
- Deterministic mode only in CI: zero LLM, zero network beyond loopback.
- Baselines are committed to the repo — `--update-baseline` is a deliberate local act, never automatic.
- TDD per `execute-specs`.

## 9. Definition of Done

- [ ] RF-001..RF-005 implemented.
- [ ] All 4 journeys green vs baseline; gate provably fails on regression (test the test).
- [ ] `dotnet test tests/KnowledgeHub.Tests.Bench` green locally and in CI stage.
- [ ] Delegation table present in CLAUDE.md + SKILL.md; docs skill regenerated.
- [ ] No flaky timing gates (only call counts + generous budgets are hard gates).

## Open Questions / Pending Ambiguity

- None — budgets are set generously on purpose: the bench's hard signal is **call count** (structural friction); wall-time is informational except for a wide safety cap.
