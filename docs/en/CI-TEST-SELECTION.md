# CI Test Selection — `scripts/select-tests.py`

Diff-scoped test selection (SPEC-20261021, #596) — the .NET analogue of
LangChain's `check_diff.py`. Maps the files changed in a PR to the test
projects that must run, following the `ProjectReference` graph transitively.

## Usage

```bash
python3 scripts/select-tests.py --base origin/main                 # paths for dotnet test
python3 scripts/select-tests.py --base origin/main --format=json   # machine-readable
python3 scripts/select-tests.py --files file1.cs file2.cs          # explicit list
```

Output (`--format=dotnet`): space-separated project directories, ready for
`dotnet test <dir>`. JSON adds `fallback` (true = run everything) and
`changed` (mapped source projects).

## Selection rules

| Change | Selected tests |
|---|---|
| `src/KnowledgeHub.<X>/**` | every test project transitively referencing `<X>` |
| `tests/KnowledgeHub.Tests.<X>/**` | `<X>` itself + dependents |
| `KnowledgeHub.slnx`, `global.json`, `Directory.*.props` | all test projects (`fallback=true`) |
| `docs/`, `.specs/`, `*.md`, `sdks/{python,java,go}/`, `tests/eval/` | none |
| unknown path | all test projects (conservative) |

Special case: `KnowledgeHub.Tests.Ui` has no `ProjectReference` (it drives
the built app), so the script adds explicit edges to `Server` + `Client`.

Rationale: any ambiguity resolves to running more tests, never fewer.

## Workflow wiring

`ci-build-test.yml` has a `select-tests` job that runs only on
`pull_request` events: it executes the script against `origin/${{ github.base_ref }}`
and publishes per-suite outputs (`unit`, `integration`, `performance`, `ui`).
Each test job carries `needs: [build-knowledgerag, select-tests]` and
`if: github.event_name != 'pull_request' || needs.select-tests.outputs.<suite> == 'true'`
— so pushes always run the full suite (post-merge signal stays complete),
while PRs run only the affected projects. Skipped jobs report success to
required-check gating. A selector failure falls back to the full suite
rather than blocking the merge.
