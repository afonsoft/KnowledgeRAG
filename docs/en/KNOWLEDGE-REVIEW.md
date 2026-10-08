# Knowledge Review

Self-hosted AI code review for GitHub PRs — a .NET 10 console CLI (`src/KnowledgeHub.Review.Cli`) that replicates the Devin Review workflow inside your own GitHub Actions: it collects every review signal already on the PR, analyzes the diff with an LLM against repository conventions, publishes a Devin-Review-style verdict and persists each review as knowledge in the Knowledge MCP Hub.

## Pipeline

```
collect → review → gate → publish          (`run` chains all stages)
```

| Stage | What it does |
|---|---|
| `collect` | Builds `PullRequestSignal` (schema v1): PR metadata, per-file diffs, check-runs + commit statuses, bot comments (`*[bot]` + allowlist), check-run annotations, human reviews, stacked-PR chain. |
| `review` | Runs the diff through `IChatClient` in chunks bounded by `REVIEW_MAX_DIFF_KB`, guided by `REVIEW.md`/`AGENTS.md`/`CLAUDE.md` + `search_knowledge` conventions from the hub. Findings: `{kind: bug\|style\|security\|flag, severity, file, line, cwe?, confidence, rationale, suggestion}`. |
| `gate` | Deterministic vetoes — draft, `knowledge-review:skip` label, human `CHANGES_REQUESTED`, failing required checks → `block`; pending required checks → `inconclusive`; fork PRs and failing stack layers → `block` (comment-only). |
| `publish` | Commit status `knowledge-review/verdict` + idempotent summary comment (`<!-- knowledge-review -->` marker) + PR review with inline comments (`APPROVE` when clean, `COMMENT` otherwise — never auto `REQUEST_CHANGES`) + `enablePullRequestAutoMerge` (SQUASH by default) + `write_knowledge` to the hub. |

## Usage

```bash
dotnet run --project src/KnowledgeHub.Review.Cli -- <command> [options]

knowledge-review collect --repo owner/repo --pr 123 [--wait-for-signals]   # → signal.json on stdout
knowledge-review review  --repo owner/repo --pr 123 [--input signal.json]  # → findings + verdict
knowledge-review gate    --repo owner/repo --pr 123                        # → gate outcome (exit 0/2)
knowledge-review run     --repo owner/repo --pr 123 --wait-for-signals     # full pipeline
```

Options: `--repo`, `--pr`, `--input <signal.json>` (replay a collected signal), `--wait-for-signals` (poll required checks until `REVIEW_SIGNAL_TIMEOUT_MIN`), `--reasoning local|hub` (`hub` delegates analysis to `agent_chat`), `--dry-run` (print planned mutations, change nothing).

## Configuration (env vars)

| Variable | Default | Purpose |
|---|---|---|
| `GITHUB_TOKEN` | — | GitHub auth (Actions injects it) |
| `REVIEW_LLM_ENDPOINT` / `REVIEW_LLM_MODEL` / `REVIEW_LLM_API_KEY` | — | OpenAI-compatible chat endpoint; absent → comment-only mode |
| `KNOWLEDGE_HUB_URL` / `KNOWLEDGE_HUB_API_KEY` | — | hub MCP endpoint + `aft_*` key; absent → local mode (warning in summary) |
| `REVIEW_REASONING` | `local` | `local` = chunked `IChatClient`; `hub` = `agent_chat` |
| `REVIEW_LANGUAGE` | `pt-BR` | Findings + summary language (`REVIEW.md` `language:` line wins) |
| `REVIEW_PASSES` | `1` | LLM passes per chunk (1–4); a finding survives when seen in ≥⌈passes/2⌉ passes |
| `REVIEW_MIN_CONFIDENCE` | `0.6` | Minimum confidence to keep a finding |
| `REVIEW_MAX_DIFF_KB` | `256` | Chunk budget; >4× the budget → summary-only mode |
| `REVIEW_SIGNAL_TIMEOUT_MIN` | `15` | `--wait-for-signals` timeout; expiry → `partial=true` |
| `REVIEW_MERGE_METHOD` | `SQUASH` | `enablePullRequestAutoMerge` method |
| `REVIEW_SKIP_LABEL` | `knowledge-review:skip` | Label that skips the review |
| `REVIEW_STATUS_CONTEXT` | `knowledge-review/verdict` | Commit status context |
| `REVIEW_BOT_AUTHORS` | built-in list | Extra bot logins ingested (comma-separated) |
| `REVIEW_DRY_RUN` | `false` | No mutations — plan printed to stdout |
| `REVIEW_NO_KNOWLEDGE` | `false` | Skip `write_knowledge` |
| `REVIEW_ENABLE_PR_AGENT` | `false` | Workflow var: run `the-pr-agent/pr-agent` first (its comments enter bot ingestion) |

## GitHub Actions

`.github/workflows/knowledge-review.yml` runs on `pull_request` (opened/synchronize/reopened/ready_for_review) and `workflow_dispatch` (with a `pr` input). Token permissions: `contents:read`, `pull-requests:write`, `issues:write`, `checks:read`, `statuses:write` — auto-merge needs repo setting **Allow auto-merge** enabled.

Required secrets: `REVIEW_LLM_API_KEY`. Optional: `KNOWLEDGE_HUB_URL`/`KNOWLEDGE_HUB_API_KEY` (vars/secret), `REVIEW_*` vars for tuning.

## Prompt-injection hard rule

The system prompt treats PR title, description, commit messages and every diff line as **untrusted data** — instructions embedded in them are never followed (the "skeptical by default" stance plus this rule is what keeps a malicious PR from talking the reviewer into approving itself).

## Findings taxonomy

| Kind | Severities | Merge impact |
|---|---|---|
| `bug` | `severe`, `non-severe` | `severe` blocks approval + auto-merge |
| `security` | `critical`, `warning` | `critical` blocks; CWE tagged when applicable |
| `style` | `info`, `warning` | never blocks |
| `flag` | `investigate`, `info` | never blocks — needs a human look |

## Knowledge loop

Before analysis, `search_knowledge` pulls repo conventions into the prompt. After publish, `write_knowledge` stores `review/{owner}/{repo}/pr-{N}` with verdict, findings and rationale — recurring findings become searchable knowledge for the next review.

## Flow import

`docs/flows/knowledge-review.flow.json` is a ready-made Knowledge Hub flow (paste into `/flows` → JSON view): `search_knowledge` → GitHub REST PR fetch (`secretRef:"github"`) → skeptical LLM review → human approval gate on severe/critical findings → optional summary comment → `write_knowledge` → output.
