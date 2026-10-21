# SPEC-20261021-context-test-coverage

| Campo | Valor |
|---|---|
| Status | Done |
| Ticket | [#589](https://github.com/afonsoft/KnowledgeRAG/issues/589) |
| Escopo | Cobertura faltante dos ACs de SPEC-20261004-context-snapshots |

## Requisitos

- **RF-1 — Concorrência de versões.** Teste de integração: 5 `context_save` paralelos no mesmo repo produzem versões 1..5 sem duplicatas (exercita o retry de `UX (RepoKey,Version)`). Usar `Task.WhenAll` sobre `TestMcp` ou `ContextSnapshotService` direto.
- **RF-2 — Postgres condicional.** Classe `ContextToolsPostgresTests` no padrão `PostgresVectorStoreLiveTests` (Testcontainers; skip sem Docker/`Postgres:ConnectionString`): save→recovery→retention funcionam identicamente ao SQLite, incluindo coluna `jsonb`.
- **RF-3 — Label selector.** `context_recovery` com `label` retorna o snapshot mais recente daquele rótulo (AC existe no provider, falta teste).
- **RF-4 — `context_compact` com `items` diretos** (sem `fromVersion`) — caminho coberto só parcialmente hoje.

## Critérios de aceite
Todos os novos testes verdes em SQLite; Postgres suite pulada com mensagem clara quando indisponível.
