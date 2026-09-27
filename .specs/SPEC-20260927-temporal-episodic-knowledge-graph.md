# SPEC-20260927-temporal-episodic-knowledge-graph

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `temporal-episodic-knowledge-graph` |
| Type | `Feature` |
| Stack | `.NET 10 / EF Core / SQLite / Npgsql / GraphRAG / System.Text.Json` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `#274` |
| Status | `Approved` |

## 1. User Story

**As a** engenheiro de software ou agente de IA consultando o grafo de conhecimento do Knowledge MCP Hub
**I want** consultar nós, arestas e fatos com dimensões temporais e agrupamento episódico
**So that** eu possa rastrear como relacionamentos evoluíram no tempo, consultar fatos recentes dentro de janelas de tempo deslizantes (`1h`, `24h`, `7d`) e agrupar descobertas por episódios de sessão sem estourar o contexto do LLM.

**Problem context:**
O GraphRAG existente no Knowledge MCP Hub suporta consultas estáticas de dependência (`find_dependencies`, `find_dependents` e `analyze_impact`). No entanto, ele trata todas as relações como atemporais e não preserva o contexto do momento em que foram aprendidas ou alteradas. Em bases dinâmicas (código em evolução, atas de reuniões, notas temporais do Obsidian ou sessões de análise), fatos antigos podem contradizer fatos novos.
Inspirado na integração do **PentAGI** com o Graphiti (`pkg/tools/graphiti_search.go` e `pkg/graphiti`), esta SPEC expande o GraphRAG para incorporar propriedades temporais (`ValidFrom`, `ValidTo`, `ObservedAt`), nós do tipo `Episode` (vinculando fatos a sessões ou jobs de ingestão), e 5 novos modos de busca:
1. `TemporalWindowSearch`: busca em intervalo de tempo explícito com parsing tolerante de datas.
2. `RecentContextSearch`: busca em janelas deslizantes (`1h`, `6h`, `24h`, `7d`).
3. `EntityRelationshipsSearch`: exploração de grafos multi-hop com profundidade controlada (`max_depth = 2`).
4. `DiverseResultsSearch`: diversificação semântica baseada em comunidades de nós para evitar repetições.
5. `EpisodeContextSearch`: recuperação de fatos extraídos de um episódio ou sessão específica.
A SPEC também introduz salvaguardas de truncamento de resposta para impedir que respostas volumosas de grafo saturem o contexto do modelo.

## 2. Scope

**In scope:**
- **Esquema de Dados e Entidades do Grafo:**
  - `GraphEntity`: campos `ObservedAt` (DateTime UTC), `ValidFrom`, `ValidTo` (opcionais), `EpisodeId` (opcional), `Labels` (string[]).
  - `GraphRelation`: campos `ObservedAt`, `Weight`, `PropertiesJson` (metadados temporais).
  - `GraphEpisode`: registro de episódios de ingestão ou sessões de agente com resumo textual.
- **Motor de Busca Temporal e Episódica (`TemporalGraphRetriever`):**
  - Métodos de busca:
    - `SearchTemporalWindowAsync(string query, DateTime? start, DateTime? end, int maxResults = 15)`
    - `SearchRecentContextAsync(string query, TimeSpan window, int maxResults = 10)`
    - `SearchDiverseResultsAsync(string entity, string diversityLevel = "medium", int maxResults = 10)`
    - `SearchEpisodeContextAsync(string episodeId, int maxResults = 10)`
  - Parser tolerante de timestamps (`ISO-8601`, `yyyy-MM-ddTHH:mm:ss`, sem timezone assumindo UTC).
  - Proteção de contexto: truncamento defensivo com preview seguro de saída (`MaxGraphResponsePreviewBytes = 8192`).
- **Novas Ferramentas MCP:**
  - `search_graph_temporal`: busca no grafo por janela de tempo.
  - `search_graph_recent`: busca no grafo por janela recente (`1h`, `6h`, `24h`, `7d`).
  - `search_graph_diverse`: busca de conceitos e entidades diversificadas em múltiplos clusters.
- **Visualização na UI:**
  - Visualizador de grafo atualizado com filtro de linha do tempo e badges de episódios.

**Out of scope:**
- Dependência de servidores externos proprietários (todo o grafo temporal é executado nativamente sobre SQLite/PostgreSQL com consultas recursivas CTE).
- Edição manual de arestas temporais via interface de arrastar e soltar (graph drag-and-drop).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Graph/`:
  - `TemporalGraphRetriever.cs`: motor de busca temporal e multi-hop.
  - `TemporalDateParser.cs`: parser tolerante de datas em formatos variados.
  - `DiversityRanker.cs`: agrupamento e diversificação de entidades por comunidade.
  - `GraphEpisodeService.cs`: vinculação de fatos a episódios.
- `src/KnowledgeHub.Server/Data/Entities/`:
  - `GraphEntityEntity.cs`, `GraphRelationEntity.cs`, `GraphEpisodeEntity.cs`.
- `src/KnowledgeHub.McpEngine/Tools/`:
  - `TemporalGraphSearchTools.cs`: ferramentas MCP para busca temporal, recente e diversa.
- `tests/KnowledgeHub.Tests.Unit/Graph/`: testes unitários de janelas temporais, parsing e diversificação.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Graph/KnowledgeGraphService.cs`
- `src/KnowledgeHub.Server/Data/KnowledgeHubDbContext.cs`
- `src/KnowledgeHub.McpEngine/Tools/GraphTools.cs`

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Graph/TemporalGraphRetriever.cs               (create)
src/KnowledgeHub.Server/Graph/TemporalDateParser.cs                  (create)
src/KnowledgeHub.Server/Graph/DiversityRanker.cs                     (create)
src/KnowledgeHub.Server/Data/Entities/GraphEpisodeEntity.cs         (create)
src/KnowledgeHub.McpEngine/Tools/TemporalGraphSearchTools.cs         (create)
src/KnowledgeHub.Server/Data/KnowledgeHubDbContext.cs                (modify)
src/KnowledgeHub.Server/Graph/KnowledgeGraphService.cs               (modify)
tests/KnowledgeHub.Tests.Unit/Graph/TemporalGraphRetrieverTests.cs   (create)
tests/KnowledgeHub.Tests.Unit/Graph/TemporalDateParserTests.cs       (create)
```

## 4. Requirements

### RF-001: Persistência Temporal e Episódica de Grafos
- **Description:** Entidades e relações do grafo devem registrar o carimbo temporal de observação e opcionalmente o identificador de episódio associado.
- **Rules:**
  - Toda aresta criada ou atualizada recebe `ObservedAt = DateTime.UtcNow`.
  - Se um relacionamento for invalidado ou substituído, `ValidTo` é preenchido com a data da substituição em vez de ser deletado fisicamente (soft historicization).
- **Input → Output:** Extração de fatos de uma nota → Nós e relações gravados com carimbo de tempo e ID de episódio.

### RF-002: Busca por Janela de Tempo com Parsing Tolerante
- **Description:** O método `SearchTemporalWindowAsync` deve filtrar arestas e nós válidos ou observados entre `timeStart` e `timeEnd`.
- **Rules:**
  - O parser `TemporalDateParser` aceita RFC3339, `yyyy-MM-ddTHH:mm:ss`, `yyyy-MM-dd HH:mm:ss` e datas puras `yyyy-MM-dd`.
  - Formatos sem timezone são interpretados como UTC.
  - A consulta recupera relacionamentos onde `ObservedAt >= timeStart` e `ObservedAt <= timeEnd`.
- **Input → Output:** `timeStart = "2026-09-01"`, `timeEnd = "2026-09-27"` → Retorna subgrafo temporalmente delimitado.

### RF-003: Busca de Contexto Recente em Janelas Deslizantes
- **Description:** Permitir consultas rápidas nas janelas relativas predefinidas: `1h`, `6h`, `24h` ou `7d`.
- **Rules:**
  - O parâmetro `window` é validado contra a whitelist: `1h`, `6h`, `24h`, `7d`.
  - O sistema calcula `cutoff = DateTime.UtcNow - window` e busca nós e arestas com `ObservedAt >= cutoff`.
- **Input → Output:** `query = "alterações de arquitetura"`, `window = "24h"` → Fatos descobertos nas últimas 24 horas.

### RF-004: Busca com Diversificação de Resultados
- **Description:** Evitar que a busca em grafo retorne 15 variantes da mesma entidade central, selecionando representantes de diferentes vizinhanças semânticas.
- **Rules:**
  - O algoritmo agrupa nós candidatos por `Label` ou componente conexo.
  - Seleciona no máximo `N` nós por cluster de acordo com `diversityLevel`:
    - `low`: até 5 nós por cluster.
    - `medium`: até 2 nós por cluster.
    - `high`: no máximo 1 nó por cluster.
- **Input → Output:** Busca com `diversityLevel = high` → Subgrafo diversificado cobrindo módulos distintos.

### RF-005: Ferramentas MCP e Proteção de Context Window
- **Description:** Expor as operações temporais como ferramentas MCP consumíveis por agentes com limite estrito de tamanho de resposta.
- **Rules:**
  - Se a serialização do subgrafo em Markdown ou JSON exceder 8KB, o conteúdo de nós secundários é condensado em sumários tabulares para não poluir o contexto da LLM.

## 5. API Contract (if applicable)

Chamada MCP `search_graph_recent`:
```json
{
  "query": "arquitetura de conectores",
  "window": "24h",
  "maxResults": 10
}
```

Resposta MCP:
```json
{
  "window": "24h",
  "cutoffUtc": "2026-09-26T15:00:00Z",
  "totalNodes": 6,
  "nodes": [
    {
      "name": "YouTubeConnector",
      "labels": ["Connector", "Ingestion"],
      "observedAt": "2026-09-27T10:15:00Z",
      "relationships": [
        { "type": "IMPLEMENTS", "target": "ISourceConnector" },
        { "type": "DEPENDS_ON", "target": "YoutubeExplode" }
      ]
    }
  ]
}
```

## 6. Acceptance Criteria

- [ ] **Given** uma requisição para `search_graph_recent` com `window = "24h"` **when** executada **then** apenas nós observados nas últimas 24 horas são retornados.
- [ ] **Given** strings de data variadas (`"2026-09-27T10:00:00"`, `"2026-09-27"`, `"2026-09-27T10:00:00Z"`) **when** processadas pelo `TemporalDateParser` **then** todas são convertidas corretamente para `DateTime` em UTC sem exceções.
- [ ] **Given** uma busca com `diversityLevel = high` contendo 10 nós do mesmo label `Database` e 2 nós de `Security` **when** `SearchDiverseResultsAsync` é executado com limite 5 **then** o resultado distribui os nós entre os diferentes labels em vez de monopolizar com `Database`.
- [ ] **Given** uma resposta de grafo de 25KB **when** formatada para retorno de tool MCP **then** o conteúdo é podado respeitando o limite seguro de bytes com aviso de truncamento.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Janela inválida | `window = "48h"` | Retorna erro 400 com valores permitidos (`1h`, `6h`, `24h`, `7d`) |
| Data final anterior à inicial | `start = "2026-09-27"`, `end = "2026-09-01"` | Retorna 400 informando que start deve preceder end |
| Nenhum nó no período | Consulta em período sem atividade | Retorna lista vazia com mensagem explicativa |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** Avaliar modelo de grafos atual em `KnowledgeHub.Server/Graph/`.
- [ ] **T2 — Entities:** Adicionar colunas `ObservedAt`, `ValidTo`, `EpisodeId` às entidades de grafo e gerar migração EF Core.
- [ ] **T3 — Date Parser:** Implementar `TemporalDateParser.cs` com testes para múltiplos formatos.
- [ ] **T4 — Temporal Engine:** Implementar `TemporalGraphRetriever.cs` e `DiversityRanker.cs`.
- [ ] **T5 — MCP Tools:** Implementar `TemporalGraphSearchTools.cs` integrando as novas operações ao catálogo MCP.
- [ ] **T6 — Tests:** Escrever testes de integração e unitários cobrindo consultas de tempo, diversificação e truncamento.

## 8. Organization Guardrails

- **Desempenho:** Consultas recursivas temporais em grafos devem conter limitador de profundidade máximo (`maxDepth = 3`) para evitar locks em bancos relacionais.
- **Privacidade:** Nós de episódios isolam dados por usuário/tenant e não cruzam fronteiras entre tenants.

## 9. Definition of Done

- [ ] Todos os requisitos (RF-001 a RF-005) implementados.
- [ ] Testes unitários com 100% dos cenários temporais passando.
- [ ] Novas ferramentas MCP registradas e documentadas no catálogo dinâmico.
