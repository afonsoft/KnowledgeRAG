# SPEC-20261002-close-done-issues — Close GitHub Issues whose labels say done

| Campo | Valor |
|-------|-------|
| Status | Done |
| Ticket | Epic [#494 (E26)](https://github.com/afonsoft/LangGraph-UI/issues/494) — slice [#498](https://github.com/afonsoft/LangGraph-UI/issues/498) |
| Origem | /gap-analysis run — label/state convention drift |
| Tipo | Infra |

## 1. User Story

As a maintainer, I want Issues marked `done` to also be closed, so `gh issue list --state open` reflects real pending work.

## 2. Scope

In scope: 13 open Issues with `done` labels — epics #451, #452, #480 and slices #453, #458, #481, #482, #483, #484, #485, #486 (verified `gh issue list --state open` 2026-10-02; all SPECs `Done`, all PRs merged).
Out of scope: issues still `todo`/`in_progress`, new labels.

## 3. Technical Context

Label contract (create-issues): `done` = delivered; the repo convention keeps `Closes #` auto-close for slices referenced by PRs — these were closed-by-label only because PRs referenced the epic/issue via "part of" wording, leaving state `open`.

## 4. Requirements

- RF-01: `gh issue close` each of the listed Issues with a closing comment naming the delivering PRs.
- RF-02: After closing, `gh issue list --state open` shows no `done`-labeled Issues.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- Given `gh issue list --state open`, when filtered by label `done`, then the result is empty.

## 7. Task Plan

1. `gh issue close <n> --comment "Delivered via #<prs>"` per Issue.
2. Re-list open issues to verify.

## 8. Organization Guardrails

Comment-only closure, no state-destructive ops.

## 9. Definition of Done

RF-02 verified.
