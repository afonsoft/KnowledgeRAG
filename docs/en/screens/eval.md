# Eval

**Route:** `/eval`

![Eval](../../screenshots/eval.png)

## How it works

`Eval.razor` runs retrieval/answer evaluations over the indexed knowledge and
tracks quality over time:

- **New run** — pick a dataset (built-in cases or a named one) and Run; each
  run scores recall and other metrics plus p50/p95/p99 latencies.
- **Baselines** — save the current run as a named baseline or promote an
  existing one; later runs compare against it.
- **Regressions** — queries that dropped vs the baseline are listed with the
  delta so you can investigate the cause.
- **Gate rules** — the CI-grade checks (min recall, max latency…) and the
  violations a run produced; used by the eval gate in pipelines.
- **Recent runs** — history with date, dataset and outcome; "view" expands a
  run's per-case detail.
