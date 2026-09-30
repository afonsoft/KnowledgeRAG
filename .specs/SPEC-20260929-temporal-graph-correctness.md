# SPEC-20260929-temporal-graph-correctness

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `temporal-graph-correctness` |
| Type | `Fix` (corretude + escopo + viewer) |
| Stack | `.NET 10 / C#` — TemporalGraphRetriever, SqliteKnowledgeGraphStore, /graph |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-temporal-graph-correctness` |
| Status | `Done` |
| Source | Comentários `devin-ai-integration` em PRs #378, #389, #392 |

## 1. User Story

**As a** mantenedor do KnowledgeHub
**I want** o grafo temporal coerente (re-observação revalida, janelas respeitadas, migração Postgres desbloqueada) e o viewer sem falsos positivos
**So that** `search_graph_*` e `/graph` mostrem a verdade temporal sem vazar fatos fora do escopo.

## 2. Findings

1. 🔴 Migração PostgreSQL bloqueada por nós existentes — constraint/seed assume estado vazio; `KgEpisode`/`KgNode` preexistentes conflitam.
2. 🔴 Episódios bloqueiam a transferência para PostgreSQL — FK/PK ou ordinal mismatch na migration gerada.
3. 🔴 Fato re-observado permanece invalidado — nova `ObservedAt` não limpa `ValidTo` do row anterior nem cria superseding coerente.
4. 🔴 `search_graph_recent` inclui nós observados antes da janela — cutoff usa `ValidFrom` em vez de `ObservedAt` (ou janela 24h inclui observações antigas, conforme #389).
5. 🔴 Arestas existentes recebem peso incorreto — upsert não preserva/atualiza `Weight` conforme SPEC.
6. 🔴 Evidências independentes (nodes sem edges) desaparecem do "grafo atual" — `current-only` filtra por edge em vez de node.
7. 🟥 **SECURITY** — busca temporal não filtra por `CallerScope`/`sourceIds` — `KgNode` de fonte restrita vaza para caller sem acesso (verificado: `TemporalGraphRetriever` tem 0 referências a CallerScope/SourceId).
8. 🟡 Episódio omite entidades descobertas em relações — `SearchEpisodeContextAsync` lista nodes por `EpisodeId` mas não as dos edges do episódio.
9. 🟡 Viewer `/graph`: expansão de um nó pode mostrar arestas de outro (id mismatch no mapa); filtro temporal não limita as arestas exibidas; grafos grandes perdem nós após limite inicial (sem paginação); datas locais do browser deslocam a janela UTC.

## 3. Requirements

- RF-001: Migração Postgres idempotente/compatível com dados existentes — `Up` tolera rows pré-existentes ou documenta ordem de aplicação; teste de migração contra banco com dados.
- RF-002: Re-observação: novo `ObservedAt` cria nova versão temporal do fato (ou reabre `ValidTo`) — semântica decidida e testada.
- RF-003: `search_graph_recent` usa `ObservedAt >= cutoff` consistente; janela `24h` só traz observações do período.
- RF-004: `Weight` preservado/incrementado conforme regra da SPEC original — teste de upsert.
- RF-005: `current-only` considera nodes órfãos de edges — evidência standalone permanece visível.
- RF-006: `CallerScope` aplicado nas queries do retriever — join `KgNode → DocumentChunk → KnowledgeSource` (ou `Episode → IngestionJob.SourceId`) filtra por `sourceIds` do caller; sem scope → erro 403/empty, não "tudo".
- RF-007: Episódio agrega nodes diretos + nodes referenciados pelos edges do episódio.
- RF-008: Viewer: mapa de edges chaveado por node-id correto; filtro temporal aplicado às arestas também; `take` configurável/paginação; datas convertidas para UTC antes de enviar.

## 4. Acceptance Criteria

- AC-1: `dotnet ef database update` num SQLite→Postgres com dados → sucesso — teste de migração.
- AC-2: `search_graph_recent` com `window=1h` não retorna fatos observados há 2h — teste.
- AC-3: caller com `sourceIds={A}` não recebe nodes de `B` via `search_graph_*` — teste de segurança.
- AC-4: `/graph` exibe arestas só do nó expandido e respeita a janela temporal — teste bUnit ou integração.
- AC-5: suite verde.
