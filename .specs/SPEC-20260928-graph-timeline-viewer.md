# SPEC-20260928-graph-timeline-viewer

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `graph-timeline-viewer` |
| Type | `Feature` (UI) |
| Stack | `.NET 10 / Blazor WASM / BootstrapBlazor` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260928-graph-timeline-viewer` |
| Ticket | `#386` — https://github.com/afonsoft/LangGraph-UI/issues/386 |
| Status | `Done` |

## 1. User Story

**As a** usuário do Knowledge MCP Hub
**I want** uma página de visualização do grafo de conhecimento com filtro de linha do tempo e badges de episódio
**So that** eu possa explorar entidades/relações e ver quando cada fato foi observado — a superfície UI que faltou para o GraphRAG temporal/episódico.

**Problem context:**
SPEC-20260927-temporal-episodic-knowledge-graph (#274, Done) pedia "Visualizador de grafo atualizado com filtro de linha do tempo e badges de episódios" (§50-51) — **mas nenhum visualizador de grafo existe**: `src/KnowledgeHub.Client/Pages/` não tem página de grafo (lista: ApiKeys, Approvals, ChangePassword, Chat, Eval, Home, Login, McpMonitor, NotFound, Playground, RagQualityDashboard, Settings, SourceEditDialog, Sources). O backend temporal está completo (`KgNode.ValidFrom/ValidTo/ObservedAt/EpisodeId`, `KgEpisode`, `TemporalGraphRetriever`, 5 tools MCP) — o gap é inteiramente de UI. Débito registrado em `.claude/memory/memory.md`.

## 2. Scope

**In scope:**
- Nova página `Graph.razor` (`/graph`) no Client: lista/grid de `KgNode` + arestas; painel de detalhe por nó (labels, weight, evidence chunk links); renderização textual/tabular (BootstrapBlazor `Table`/`Card`) — **sem** biblioteca de grafos pesada na v1 (ver Technical Context).
- Filtro de linha do tempo: `timeStart`/`timeEnd` (RFC3339) + presets (`1h`, `6h`, `24h`, `7d` — mesma whitelist do `TemporalDateParser`) aplicados a `ValidFrom/ValidTo`; badge de `EpisodeId` + `ObservedAt` em cada item.
- Backend read endpoints REST: `GET /api/graph/nodes`, `GET /api/graph/nodes/{id}/edges`, `GET /api/graph/episodes`, `GET /api/graph/timeline?from=&to=` — projeções leves sobre `IKnowledgeGraphStore`/`GraphEpisodeService` (as tools MCP já expõem a lógica; REST é espelho para a UI, mesma policy de auth operacional).
- Entrada no menu lateral (icon rail) + rota protegida pela mesma auth das demais páginas.

**Out of scope:**
- Layout de grafo force-directed/canvas (d3/cytoscape) — v1 é tabular com detalhe; visualização espacial é follow-up.
- Edição do grafo pela UI (grafo é derivado de ingestão — read-only).
- Export/embed do grafo.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Client/Pages/Graph.razor` (novo) + menu/nav registration.
- `src/KnowledgeHub.Server/Api/GraphEndpoints.cs` (novo) — espelha `TemporalGraphRetriever`/`IKnowledgeGraphStore` reads.
- `src/KnowledgeHub.Shared/Contracts/` — DTOs `GraphNodeDto`, `GraphEdgeDto`, `GraphEpisodeDto`.
- `src/KnowledgeHub.Server/Graph/` — store/temporal retriever existentes (`ObservedAt`, `ValidFrom/To`, `EpisodeId` já persistidos como `DateTime` UTC — SPEC #274).

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Graph/{TemporalGraphRetriever,GraphEpisodeService,SqliteKnowledgeGraphStore}.cs`
- `src/KnowledgeHub.Server/Mcp/ToolProviders/TemporalGraphToolsProvider.cs` (shapes de resposta já usadas)
- `src/KnowledgeHub.Client/Pages/{Sources,Eval,RagQualityDashboard}.razor` (padrão de página + auth + bootstrap)
- `src/KnowledgeHub.Server/Api/*Endpoints.cs` (padrão Minimal API + policy)

## 4. Requirements

### RF-001: REST read endpoints
- **Description:** endpoints `GET /api/graph/{nodes,nodes/{id}/edges,episodes,timeline}` retornando DTOs com `ObservedAt`/`ValidFrom`/`ValidTo`/`EpisodeId`; paginação `skip/take` (take≤200); filtro temporal em `timeline`.
- **Rules:** mesma policy de auth dos endpoints operacionais existentes; `ValidTo==null` = vigente (default); `?includeHistorical=true` inclui historicizados.
- **Input → Output:** `GET /api/graph/timeline?from=2026-09-01&to=2026-09-27` → subconjunto delimitado.

### RF-002: Página `/graph`
- **Description:** grid de nós (nome, labels, weight, ObservedAt, badge de episódio) + expansão de arestas por nó + filtro de janela temporal (presets + range) + toggle "mostrar histórico".
- **Rules:** vazia → empty state com CTA para `/sources`; erro de API → toast + retry; mobile-first como as demais páginas.
- **Input → Output:** usuário filtra `24h` → grid mostra apenas observações da janela.

### RF-003: Navegação
- **Description:** item "Graph" no icon rail/sidebar, ícone BootstrapBlazor, rota autenticada.
- **Input → Output:** sidebar → `/graph` renderiza sem erro de role.

## 5. Acceptance Criteria

- AC-1: `/api/graph/timeline` respeita janela e `includeHistorical` (integration test).
- AC-2: página renderiza nós com badge de episódio e filtro temporal funcional (bunit ou integration test da page via API fake).
- AC-3: histórico (ValidTo!=null) só aparece com toggle; default = vigentes.
- AC-4: `dotnet build` 0 warnings; unit+integration verdes.

## 6. Task Plan

1. DTOs + `GraphEndpoints` (RED: integration tests de filtro/auth primeiro).
2. `Graph.razor` + nav item + service client no Client.
3. Badges de episódio + toggle histórico.
4. Verificação manual via `dotnet run` + suite verde.

## 7. Organization Guardrails

- **Branches:** `feature/Devin-20260928-graph-timeline-viewer` a partir de `main`.
- **UI:** BootstrapBlazor apenas (sem novo pacote de grafo na v1); seguir padrão das páginas existentes.
- **Read-only:** nenhum endpoint muta grafo.
