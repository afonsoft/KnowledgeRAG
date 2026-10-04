# Eval

**Rota:** `/eval`

![Eval](../../screenshots/eval.png)

## Como funciona

`Eval.razor` roda avaliações de retrieval/resposta sobre o conhecimento
indexado e acompanha a qualidade ao longo do tempo:

- **Nova run** — escolhe um dataset (cases embutidos ou nomeado) e Run; cada
  run mede recall e outras métricas, além de latências p50/p95/p99.
- **Baselines** — salva a run atual como baseline nomeado ou promove um
  existente; runs posteriores comparam contra ele.
- **Regressões** — queries que caíram vs o baseline são listadas com o delta
  pra você investigar a causa.
- **Gate rules** — os checks de nível CI (recall mínimo, latência máxima…) e
  as violações que a run produziu; usados pelo gate de eval em pipelines.
- **Runs recentes** — histórico com data, dataset e resultado; "ver" expande
  o detalhe por case.
