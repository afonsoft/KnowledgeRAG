---
id: SPEC-20260930-sonarcloud-gate-blockers
title: SonarCloud Quality Gate blockers — ReDoS timeouts, cancellation tokens, dead condition
status: Done
type: Bugfix
ticket: gap-analysis-20260930 / sonarqube-autofix run
source: .sonar_devin_auto_fix/sonarqube_issues.json (sinceLeakPeriod, branch=main)
---

# SonarCloud Quality Gate blockers

## Context

SonarCloud Quality Gate on `main` fails with **C Reliability Rating** and **B Security Rating** on new code. The gate is driven by 8 `BUG` + 3 `VULNERABILITY` issues introduced in the recent connector/A2A waves. Fixing them restores the gate; the remaining ~121 `CODE_SMELL` findings are tracked separately as tech debt (they do not fail the configured gate).

## Issues covered

| Issue type | Rule | File:line | Summary |
| --- | --- | --- | --- |
| VULNERABILITY | csharpsquid:S6444 | `Ingestion/Connectors/SqlDatabaseConnector.cs`:217 | `Regex.IsMatch` on user connection string without `matchTimeout` (ReDoS) |
| VULNERABILITY | csharpsquid:S6444 | `Ingestion/Connectors/SqlQueryGuard.cs`:37 | `Regex.Match` on user SQL without `matchTimeout` (ReDoS) |
| VULNERABILITY | csharpsquid:S6444 | `Services/KnowledgeSourceService.cs`:518 | `Regex.IsMatch` on user `language` config without `matchTimeout` (ReDoS) |
| BUG | csharpsquid:S2583 | `Ingestion/Connectors/RestApiConnector.cs`:56 | `pageParam is null` checked twice in nested ternary — dead branch |
| BUG | csharpsquid:S8949 | `Ingestion/Connectors/YouTubeClientAdapter.cs`:21,32,43,54,63,75 | `ct` not forwarded to YoutubeExplode calls (6 sites) |
| BUG | csharpsquid:S3887 | `Ingestion/Connectors/SqlQueryGuard.cs`:23 | `public static readonly string[] Keywords` mutable — use immutable surface |

## Requirements

1. **RF-001 (S6444 ×3)** — Pass an explicit `matchTimeout` to the three regex calls operating on user-supplied input. Use a bounded timeout (≤1s), consistent across sites (`private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1)` or inline `TimeSpan.FromMilliseconds(500)` per file convention). `RegexTimeoutException` must not escape unhandled where it would turn a validation path into a 500 — wrap the SqlDatabaseConnector/KnowledgeSourceService sites so a timeout returns the safe negative answer (connection string without explicit Mode → apply ReadOnly default; invalid language → validation error).
2. **RF-002 (S2583)** — Simplify the `requestUrl` selection in `RestApiConnector` to `pageParam is null ? endpoint : PageUrl(endpoint, pageParam, page)`. The original `pageParam is null && page == 1` branch was dead logic: `PageUrl` must be used for every page (including page 1) when `pageParam` is set — verified by `RestApiConnectorTests.Pagination_*`.
3. **RF-003 (S8949 ×6)** — Forward `ct` to `_client.Videos.GetAsync`, `_client.Videos.ClosedCaptions.GetManifestAsync` (both call sites) and `_client.Videos.ClosedCaptions.GetAsync`. YoutubeExplode methods accept `CancellationToken`.
4. **RF-004 (S3887)** — Change `SqlQueryGuard.Keywords` to `public static IReadOnlyList<string>` (array backing preserved). No external callers exist — verified by grep.
5. **RF-005** — Behavior parity: `SqlQueryGuard.Validate` must return identical verdicts for the existing unit-test matrix (`SqlQueryGuardTests`).

## Acceptance criteria

- `dotnet build` + `dotnet test` green.
- `dotnet format --verify-no-changes` clean on touched files.
- SonarCloud no longer reports the 11 issues on new code after merge (Quality Gate: Reliability ≥A, Security ≥A).

## Task plan

1. Apply RF-001..RF-004 edits.
2. `dotnet format` touched files; `dotnet build`; run `SqlQueryGuard`, `RestApiConnector`, `YouTubeConnector`, `KnowledgeSourceService` test classes.
3. Push branch `feature/Devin-20260930-sonarcloud-gate-fixes` → PR → merge.
4. Confirm Quality Gate green on `main`.
