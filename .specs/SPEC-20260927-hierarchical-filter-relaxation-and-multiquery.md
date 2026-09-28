# SPEC-20260927-hierarchical-filter-relaxation-and-multiquery

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `hierarchical-filter-relaxation-and-multiquery` |
| Type | `Feature` |
| Stack | `.NET 10 / Microsoft.Data.Sqlite / Npgsql / pgvector / FTS5 / System.Text.Json` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `#273` |
| Status | `Done` |

## 1. User Story

**As a** usuário ou agente de IA consultando a base de conhecimento através de `search_knowledge` ou `ask_knowledge`
**I want** que consultas complexas sejam decompostas automaticamente em sub-perguntas paralelas e que filtros restritivos relaxem hierarquicamente em caso de zero resultados
**So that** perguntas multifacetadas encontrem todas as evidências relevantes e filtros estreitos (por source, tag ou thread) não retornem respostas vazias quando o conhecimento existe na base ampla.

**Problem context:**
No pipeline de RAG atual do Knowledge MCP Hub, buscas via `search_knowledge` executam um único termo de consulta contra o índice híbrido (FTS5 + SQLite-Vec/PgVector). Consultas humanas ou de agentes frequentemente combinam múltiplos conceitos (ex.: *"qual o timeout do HTTP client e como configurar o banco PostgreSQL?"*). Além disso, quando filtros de metadados estritos são fornecidos (ex.: restringindo a uma `sourceId` específica ou tag de documento), qualquer erro de digitação ou ausência parcial do tópico naquela fonte específica resulta em 0 chunks retornados (`not_found`).
No **PentAGI** (`pkg/tools/memory.go`), a ferramenta de memória emprega duas técnicas fundamentais:
1. **Multi-Query Decomposition**: processa um conjunto de perguntas (`Questions`), busca chunks em paralelo com threshold de similaridade e deduplica os resultados via fusão por score.
2. **Hierarchical Filter Fallback**: quando uma busca com filtros específicos (`task_id`, `subtask_id`, `doc_type`) retorna zero documentos, o retriever automaticamente realiza um fallback para `globalFilters` (nível do fluxo/sistema), registra o evento na observabilidade (Langfuse/logs) e combina os achados.
Esta SPEC implementa essa inteligência de recuperação hierárquica e multi-query no Knowledge MCP Hub.

## 2. Scope

**In scope:**
- **Multi-Query Decomposition & Fusion:**
  - Extensão de `ISearchEngine` / `HybridRetriever` para suportar `MultiQuerySearchAsync(IReadOnlyList<string> queries, SearchOptions options)`.
  - Decomposição automática opcional de consultas complexas em 2 a 4 sub-queries usando heurística léxica leve ou `IChatClient` (quando habilitado).
  - Execução paralela de buscas híbridas para cada sub-query.
  - Fusão e desduplicação via Reciprocal Rank Fusion (RRF) balanceado com normalização de scores e deduplicação por hash de chunk (`ChunkHash`).
- **Hierarchical Filter Relaxation (Relaxamento Hierárquico de Filtros):**
  - Definição da hierarquia de escopo: `SpecificScope` (ex.: `SourceId` específico + `Tag` específico) → `GroupScope` (mesmo `SourceType` ou grupo de fontes) → `GlobalScope` (toda a base de conhecimento do tenant).
  - Execução em cascata: se a busca no escopo estrito retornar contagem de chunks < `MinResultsThreshold` (default = 1), o motor executa imediatamente a busca no nível imediatamente superior.
  - Sinalizador de relaxamento (`FilterRelaxed = true`, `OriginalScope`, `EffectiveScope`) retornado nos metadados de `SearchResult` e chunks.
  - Aplicação de fator de penalidade suave no ranking (`RelaxationPenaltyFactor = 0.85`) para garantir que resultados exatos do filtro original priorizem o ranking sobre os do fallback global.
- **Integração nas ferramentas MCP:**
  - Atualização de `search_knowledge` para aceitar parâmetro opcional `subQueries: string[]` e flag `allowRelaxation: bool = true`.
  - Integração em `ask_knowledge` para enriquecimento de contexto com chunks relaxados identificados.
- **Telemetria e Auditoria:**
  - Emissão de tags de métrica OTel e logs estruturados `RetrievalFilterRelaxed` e `MultiQueryDispatched`.

**Out of scope:**
- Re-indexação de embeddings existentes (o esquema de chunks e vetores é preservado).
- Modificação na persistência física das tabelas SQLite/Postgres.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Retrieval/`:
  - `MultiQuerySearchService.cs`: orquestração paralela de queries e fusão RRF.
  - `HierarchicalFilterResolver.cs`: lógica de relaxamento e cascata de escopos.
  - `SearchOptions.cs`: inclusão de campos de sub-queries e relaxamento.
- `src/KnowledgeHub.Server/Services/KnowledgeRetrievalService.cs`: consumo das novas opções no fluxo principal.
- `src/KnowledgeHub.McpEngine/Tools/SearchKnowledgeTool.cs`: suporte aos novos parâmetros no tool call JSON-RPC.
- `tests/KnowledgeHub.Tests.Unit/Retrieval/`: testes unitários para decomposição, fusão RRF e relaxamento hierárquico.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Retrieval/HybridRetriever.cs`
- `src/KnowledgeHub.Server/Retrieval/ISearchEngine.cs`
- `src/KnowledgeHub.Server/Retrieval/RrfRanker.cs`
- `src/KnowledgeHub.McpEngine/Tools/SearchKnowledgeTool.cs`

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Retrieval/MultiQuerySearchService.cs            (create)
src/KnowledgeHub.Server/Retrieval/HierarchicalFilterResolver.cs        (create)
src/KnowledgeHub.Server/Retrieval/SearchOptions.cs                     (modify)
src/KnowledgeHub.Server/Retrieval/SearchResult.cs                      (modify)
src/KnowledgeHub.Server/Retrieval/HybridRetriever.cs                   (modify)
src/KnowledgeHub.McpEngine/Tools/SearchKnowledgeTool.cs                (modify)
src/KnowledgeHub.McpEngine/Tools/AskKnowledgeTool.cs                   (modify)
tests/KnowledgeHub.Tests.Unit/Retrieval/MultiQuerySearchTests.cs       (create)
tests/KnowledgeHub.Tests.Unit/Retrieval/HierarchicalFilterTests.cs     (create)
```

## 4. Requirements

### RF-001: Busca Paralela Multi-Query com Fusão de Ranks
- **Description:** O serviço de busca deve ser capaz de receber múltiplas formulações de consulta, despachar buscas híbridas simultâneas e fundir os resultados num ranking unificado.
- **Rules:**
  - Cada query é executada de forma concorrente respeitando um `SemaphoreSlim` para evitar saturação do pool de banco.
  - Documentos idênticos (mesmo `ChunkId` ou `Hash`) têm suas pontuações somadas via fórmula RRF: $RRF\_Score(d) = \sum_{q} \frac{1}{k + rank_q(d)}$ com $k=60$.
  - A lista final é ordenada pelo score acumulado e limitada ao `TopK` solicitado.
- **Input → Output:** `["como configurar timeout", "configuração de http client"]`, `TopK = 5` → Lista consolidada de 5 chunks deduplicados.

### RF-002: Relaxamento Hierárquico de Filtros em Fallback
- **Description:** Quando uma busca com filtro de fonte ou metadado estrito obtiver menos que `MinResultsThreshold` resultados, o sistema deve tentar o próximo nível hierárquico de filtro.
- **Rules:**
  - Níveis de relaxamento:
    1. `Level 0 (Strict)`: `SourceId == X && Metadata.Tag == Y`
    2. `Level 1 (Source Broad)`: `SourceId == X` (descarta tags restritivas)
    3. `Level 2 (Type Broad)`: `SourceType == X.Type` (todas as fontes do mesmo tipo)
    4. `Level 3 (Global)`: Sem restrição de fonte ou tag.
  - O fallback só ocorre se `allowRelaxation == true` (default).
  - Resultados vindos de níveis relaxados recebem marcador `RelaxationLevel` e multiplicador de score de 0.85 por nível de relaxamento para preservar a precedência de correspondências estritas.
- **Input → Output:** Busca por termo X com `SourceId=99` (sem chunks) → Retorna chunks de fontes afins com flag `RelaxedFromSourceId=99`.

### RF-003: Exposição de Metadados de Relaxamento no MCP Tool
- **Description:** A ferramenta MCP `search_knowledge` deve reportar se a resposta envolveu relaxamento de filtro para que agentes e usuários compreendam a proveniência dos dados.
- **Rules:**
  - O payload de retorno inclui o campo `filterRelaxed: true|false` e `appliedFilter: string`.
  - No corpo da resposta sintetizada de `ask_knowledge`, se os únicos chunks encontrados decorrerem de relaxamento, uma advertência amigável deve ser anexada: *(Evidências encontradas fora da fonte estrita solicitada)*.

## 5. API Contract (if applicable)

Chamada da Tool MCP `search_knowledge`:
```json
{
  "query": "qual o timeout do banco postgres",
  "subQueries": [
    "postgres connection timeout setting",
    "postgresql command timeout configuration"
  ],
  "sourceId": 12,
  "allowRelaxation": true,
  "topK": 5
}
```

Resposta MCP:
```json
{
  "totalMatches": 4,
  "filterRelaxed": true,
  "originalFilter": "sourceId=12",
  "appliedFilter": "sourceType=SqlDatabase",
  "items": [
    {
      "chunkId": "chk_987",
      "sourceId": 15,
      "title": "Postgres Configuration Guide",
      "content": "commandTimeoutSeconds defaults to 30s...",
      "score": 0.82,
      "isRelaxed": true
    }
  ]
}
```

## 6. Acceptance Criteria

- [x] **Given** uma busca executada com `subQueries` contendo 3 termos distintos **when** `MultiQuerySearchService` executa **then** 3 tarefas paralelas de busca são despachadas e os resultados são consolidados sem chunks duplicados.
- [x] **Given** uma busca com `sourceId = 42` que não possui chunks indexados sobre o tema **when** `allowRelaxation = true` **then** o motor busca na base global e retorna chunks de outras fontes marcados com `isRelaxed = true`.
- [x] **Given** uma busca com `sourceId = 42` que não possui chunks indexados **when** `allowRelaxation = false` **then** o motor retorna 0 resultados respeitando estritamente o filtro do usuário.
- [x] **Given** chunks retornados tanto no nível estrito quanto no nível relaxado **when** o ranking RRF é computado **then** os chunks do filtro estrito recebem pontuação maior devido à penalidade de relaxamento nos chunks fallback.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Lista de subQueries com strings vazias | `["term", "", "  "]` | Filtra strings em branco e executa apenas query válida |
| Base inteira não possui matches | Termo inexistente em qualquer fonte | Retorna lista vazia sem lançar exceção |
| Timeout em uma das sub-queries | Falha de rede em 1 de 3 queries | As demais queries continuam e completam o resultado |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** Analisar `HybridRetriever.cs`, `RrfRanker.cs` e estruturas de query atuais.
- [x] **T2 — Hierarchical Resolver:** Criar `HierarchicalFilterResolver.cs` implementando os 4 níveis de escopo e fator de penalização.
- [x] **T3 — Multi-Query Engine:** Implementar `MultiQuerySearchService.cs` com paralelismo seguro e fusão RRF.
- [x] **T4 — Tool Update:** Atualizar `SearchKnowledgeTool` e `AskKnowledgeTool` com os novos parâmetros e metadados de relaxamento.
- [x] **T5 — Tests & Verification:** Escrever testes unitários para cenários de fallback, deduplicação RRF e tolerância a falhas parciais.

## 8. Organization Guardrails

- **Desempenho:** Limitar o número máximo de sub-queries simultâneas a 4 por chamada para proteger conexões de banco e limites de rate limit de embeddings.
- **Transparência:** Nunca relaxar filtros silenciosamente sem sinalizar `filterRelaxed: true` no retorno.

## 9. Definition of Done

- [x] Todos os requisitos (RF-001 a RF-003) implementados.
- [x] Testes unitários com 100% dos cenários de aceitação passando.
- [x] Ferramentas MCP atualizadas sem quebrar retrocompatibilidade com chamadas simples de `query`.
