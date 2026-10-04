# Grafo de conhecimento

**Rota:** `/graph`

![Grafo de conhecimento](../../screenshots/graph.png)

## Como funciona

`Graph.razor` visualiza o store do GraphRAG — entidades extraídas na ingestão
e seus relacionamentos tipados:

- **Canvas do grafo** — nós (entidades) e arestas (relacionamentos)
  renderizados interativamente; clique num nó para ver detalhes.
- **Filtros de tempo** — o grafo é temporal (`ObservedAt`, `ValidFrom`,
  `ValidTo`, `EpisodeId`): última hora / 6h / 24h / 7d / tudo, janela
  customizada de/até, ou arestas "current" vs "historical".
- **Estados de carga** — entidades e arestas carregam separadamente com
  tratamento de erro próprio; estados vazios explicam quando ainda não há nós.
