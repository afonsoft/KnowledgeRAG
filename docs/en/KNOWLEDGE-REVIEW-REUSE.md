# Knowledge Review — reusing in other repositories

`Knowledge Review` ships as a **reusable GitHub Actions workflow**
(`workflow_call`) hosted in this repository. Any repo can adopt the
self-hosted AI code review by adding a thin caller workflow — no code to
copy, no project to vendor.

## TL;DR — caller workflow

Create `.github/workflows/knowledge-review.yml` in the target repository:

```yaml
name: 🔍 Knowledge Review

permissions: {}

concurrency:
  group: knowledge-review-${{ github.event.pull_request.number || inputs.pr }}
  cancel-in-progress: true

on:
  pull_request:
    types: [opened, synchronize, reopened, ready_for_review]
  workflow_dispatch:
    inputs:
      pr:     { description: "PR number to review", required: true, type: number }
      dry_run: { description: "Plan only — no comments/status/merge",
                 required: false, type: boolean, default: true }

jobs:
  review:
    uses: afonsoft/KnowledgeRAG/.github/workflows/knowledge-review-reusable.yml@main
    with:
      pr: ${{ github.event.pull_request.number || inputs.pr }}
      dry_run: ${{ inputs.dry_run || vars.REVIEW_DRY_RUN == 'true' }}
      wait_for_signals: true
      cli_ref: main        # KnowledgeRAG ref that provides the CLI
      enable_pr_agent: ${{ vars.REVIEW_ENABLE_PR_AGENT == 'true' }}
    secrets:
      REVIEW_LLM_API_KEY: ${{ secrets.REVIEW_LLM_API_KEY }}
      REVIEW_LLM_ENDPOINT: ${{ vars.REVIEW_LLM_ENDPOINT }}
      REVIEW_LLM_MODEL: ${{ vars.REVIEW_LLM_MODEL }}
      KNOWLEDGE_REVIEW_TOKEN: ${{ secrets.KNOWLEDGE_REVIEW_TOKEN }}
      KNOWLEDGE_HUB_URL: ${{ vars.KNOWLEDGE_HUB_URL }}
      KNOWLEDGE_HUB_API_KEY: ${{ secrets.KNOWLEDGE_HUB_API_KEY }}
```

That's it — every PR opened against the default branch gets the
collect → review → gate → publish pipeline.

## Configuration

Configuration lives in **the caller repository** (GitHub resolves
`secrets:`/`vars:` in the caller context, not in KnowledgeRAG).

### Secrets (Settings → Secrets and variables → Actions → Secrets)

| Secret | Required | Purpose |
|---|---|---|
| `REVIEW_LLM_API_KEY` | recommended | API key for the review model (OpenAI-compatible). Absent → gate `llm-missing` → comment-only mode, never approve. |
| `KNOWLEDGE_REVIEW_TOKEN` | optional | PAT (`repo` scope) that **overrides `GITHUB_TOKEN`**. Required to read branch protection (the default token gets HTTP 403) and to publish on behalf of a stable identity. Without it, the required-check set degrades to "every check counts" — conservative, correct, just stricter. |
| `KNOWLEDGE_HUB_API_KEY` | optional | `aft_*` key minted at `/api-keys` on the hub — enables `search_knowledge` conventions + `write_knowledge` persistence. |
| `KNOWLEDGE_HUB_URL` | optional | Hub base URL (secret or var — a var is fine, it's not secret). |

### Variables (… → Variables tab)

| Variable | Default | Effect |
|---|---|---|
| `REVIEW_LLM_ENDPOINT` | — | OpenAI-compatible base URL, e.g. `https://omniroute.example/v1` |
| `REVIEW_LLM_MODEL` | — | model id used for the review passes |
| `REVIEW_LANGUAGE` | `pt-BR` | summary comment language (`pt-BR`/`en-US`; `REVIEW.md` `language:` wins) |
| `REVIEW_SKIP_LABEL` | `no-review` | label that makes the PR skip the review |
| `REVIEW_MAX_DIFF_KB` | `256` | diff cap — above it the review runs in summary mode |
| `REVIEW_MIN_CONFIDENCE` | `0.6` | findings below it are dropped |
| `REVIEW_PASSES` | `1` | LLM passes (1–4) + ⌈passes/2⌉ consensus dedup |
| `REVIEW_SIGNAL_TIMEOUT_MIN` | `15` | how long collect waits for required checks/bots |
| `REVIEW_MERGE_METHOD` | `SQUASH` | `SQUASH`\|`MERGE`\|`REBASE` for auto-merge |
| `REVIEW_BOT_AUTHORS` | see defaults | CSV of extra bot logins whose comments are ingested |
| `REVIEW_WRITE_KNOWLEDGE` | `true` | persist the review in the hub as `review/{repo}/pr-{N}` |
| `REVIEW_DRY_RUN` | `false` | `true` → plan only, zero mutations |
| `REVIEW_ENABLE_PR_AGENT` | `false` | `true` → run pr-agent first; its comments enter as bot signals |

### Repo settings

- **Allow auto-merge** (Settings → General) — required for the
  `enablePullRequestAutoMerge` step; absent, approval still publishes.
- **Workflow permissions** (Settings → Actions → General): the reusable
  job already declares `pull-requests:write`, `issues:write`,
  `checks:read`, `statuses:write`, `contents:read` — the default
  `GITHUB_TOKEN` just needs to not be set to *read-only* repo-wide.

## What the caller sees on a PR

1. Commit status `knowledge-review/verdict` (pending → success/failure).
2. Idempotent summary comment `<!-- knowledge-review -->` (updated, never duplicated).
3. A PR review with inline comments (`APPROVE` or `COMMENT` — it never
   auto-submits `REQUEST_CHANGES`).
4. Auto-merge enabled when the verdict approves and the repo allows it.
5. The review persisted in the hub as `review/{owner}/{repo}/pr-{N}`
   when the hub secrets are configured.
6. `signal.json` + `run.json` artifacts (14d) on the workflow run.

## workflow_dispatch (manual run / dry run)

Actions → **Knowledge Review** → *Run workflow*: pass `pr` (required) and
`dry_run` (default **true**) to preview the full plan — status, summary,
review event, inline comments, auto-merge — with zero mutations.

## Pinning and updates

- `@main` tracks the latest CLI; use `@<tag>` or `@<sha>` on the `uses:`
  line (and/or `cli_ref`) to pin.
- The caller's own `REVIEW.md` → `AGENTS.md`/`CLAUDE.md` feed the prompt
  (the loader reads them from the *built CLI checkout* — place them in
  the KnowledgeRAG checkout or extend `InstructionLoader` to fetch from
  the reviewed repo when needed).
- Flow alternative: `docs/flows/knowledge-review.flow.json` imports in
  the hub `/flows` UI (JSON view) for a hub-native pipeline instead of
  GitHub Actions.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Review comments but never auto-merges | verdict found findings, or `Allow auto-merge` off | enable the repo setting; check reasons in the summary |
| Status stuck `pending`, verdict `Inconclusive` | required checks still running | wait or raise `REVIEW_SIGNAL_TIMEOUT_MIN` |
| Required checks treated as "all of them" | default `GITHUB_TOKEN` can't read branch protection (403) | create `KNOWLEDGE_REVIEW_TOKEN` PAT |
| `_hub unreachable_` in the comment | hub URL/key missing or down | set `KNOWLEDGE_HUB_URL` + `KNOWLEDGE_HUB_API_KEY`, or accept local mode |
| `_summary mode_` in the comment | diff above `REVIEW_MAX_DIFF_KB` | raise the cap or split the PR |
