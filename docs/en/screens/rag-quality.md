# RAG quality

**Route:** `/rag-quality`

![RAG quality](../../screenshots/rag-quality.png)

## How it works

`RagQualityDashboard.razor` shows the post-answer RAG triad evaluations
(`RagEvaluations`):

- **Cards** — evaluations in the last 7 days and the hallucination rate across
  them.
- **Flagged answers** — answers the triad (context relevance, groundedness,
  answer relevance) flagged, with the flag timestamp; clicking through shows
  why it was flagged.
- **Retention** — rows live 90 days; the dashboard reads
  `GET /api/v1/evaluation/stats`, so it reflects what the API currently holds.
