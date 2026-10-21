# SPEC-20261021-context-eval-matrix

| Campo | Valor |
|---|---|
| Status | Done |
| Ticket | [#590](https://github.com/afonsoft/KnowledgeRAG/issues/590) |
| Escopo | T12 pendente da SPEC-20261004 — avaliação `strategy × tokenBudget` |

## Requisitos

- **RF-1 — Harness de avaliação.** Casos de teste no harness de eval existente (`EvalRunner`) cobrindo `strategy ∈ {none, elision, elision+summary}` × `tokenBudget ∈ {1024, 2048, 4096, 8192}` sobre snapshots sintéticos e reais.
- **RF-2 — Métricas.** `tokensAfter ≤ budget` em 100% dos casos (ou `withinBudget=false` honesto quando só restam protegidos); faithfulness e Recall@K não caem >2 pontos vs `none`; medir `droppedCount`/`elidedCount`/`summaryFallback`.
- **RF-3 — Gate de default.** Se o gate passar, avaliar mudar o default de `strategy` de `none` para `elision+summary` em `context_compact` (decisão explícita no PR).
- **RF-4 — Publicação.** Resultados em `docs/` ou artefato de eval conforme convenção do repo; regressão registrada na SPEC-20261004.

## Fora de escopo
Novas estratégias de compactação, tuning de `SummaryMaxTokens` além do configurável.
