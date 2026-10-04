# Knowledge graph

**Route:** `/graph`

![Knowledge graph](../../screenshots/graph.png)

## How it works

`Graph.razor` visualizes the GraphRAG store — entities extracted during
ingestion plus their typed relationships:

- **Graph canvas** — nodes (entities) and edges (relationships) rendered
  interactively; click a node for its details.
- **Time filters** — the graph is temporal (`ObservedAt`, `ValidFrom`,
  `ValidTo`, `EpisodeId`): pick last hour / 6h / 24h / 7d / all, a custom
  from/to window, or "current" vs "historical" edges.
- **Loading states** — entities and edges load separately with their own
  error handling; empty states explain when the store has no nodes yet.
