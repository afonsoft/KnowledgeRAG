# SPEC-20261001-mcp-recall-ergonomics

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `mcp-recall-ergonomics` |
| Type | `Feature` |
| Stack | `.NET 10 / ASP.NET Core`, MCP (Streamable HTTP + SSE) |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20261001-mcp-recall-ergonomics` |
| Status | `Done` |
| Ticket | #441 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (recall tool surface) |

## 1. User Story

**As a** agente de IA consumindo o KnowledgeHub via MCP (Claude Code, Devin, OpenCode)
**I want** controlar o custo/qualidade do recall por chamada (`budget`, `maxTokens`, `minScores`, `temporalWindow`)
**So that** sub-tarefas baratas usem busca leve, respostas longas caibam na janela de contexto e a recuperação temporal seja determinística sem depender de parsing da query.

## 2. Context

- Análise do `hindsight` (2026-10-01, clone `/tmp/hindsight-analysis`): o tool `recall` expõe `budget` (low/mid/high — controla profundidade da busca), `max_tokens` (orçamento de resposta), `min_scores` (pisos por estágio: semantic/keyword/reranker/final — o piso `final` faz o recall **abster**), `temporal_window` (janela explícita em vez de extrair datas do texto) e `prefer_observations` (dedup de fatos brutos sob observação consolidada).
- O `search_knowledge`/`ask_knowledge` do KnowledgeHub hoje expõe apenas `query`, `topK`, `mode`, `source`, `filter`, `limitMode` (`src/KnowledgeHub.Server/Mcp/ToolProviders/KnowledgeToolsProvider.cs`).
- A pipeline já tem todos os mecanismos internos: RRF híbrido, MMR, autocut (`Search:LimitMode`), corrective-RAG com abstenção honesta (`CorrectiveRetrievalService`), janela temporal no `TemporalGraphRetriever` (parsing via `TemporalDateParser`).
- Args desconhecidos já são tolerados (leitura por chave via `ToolArgs.*`) — sem RF necessário.
- O catálogo hoje **não declara `annotations` MCP** (`readOnlyHint`/`destructiveHint`/`idempotentHint`) — clientes não conseguem pré-classificar tools sem ler descrições.

## 3. Requirements

- **RF-001 — `budget` (low/mid/high)**: parâmetro opcional em `search_knowledge` e `ask_knowledge`. Mapeamento: `low` → half candidate pool por braço + sem query expansion + sem retries de grading; `mid` → default atual sem expansion; `high` → comportamento atual (default). Não muda resultados para `high` — só encurta caminho para chamadas baratas.
- **RF-002 — `maxTokens` (orçamento de resposta)**: opcional (default 4096, clamp 256–32768). Trunca a lista de resultados por orçamento aproximado de tokens (chars/4) **depois** do MMR/autocut — complementa `topK` (que limita por contagem). Resposta reporta `truncatedByTokens: true` quando aplicado.
- **RF-003 — `minScores` (pisos por estágio)**: objeto opcional `{semantic, lexical, final}`. `semantic`/`lexical` podam o braço correspondente antes do RRF; `final` filtra pós-fusão e, quando zera resultados, o `ask_knowledge` abste (reusa o caminho de abstenção do corrective-RAG — nunca sintetiza sem evidência).
- **RF-004 — `temporalWindow` explícito**: objeto opcional `{start, end}` (ISO-8601) em `search_knowledge`. Validado por `TemporalDateParser`; quando presente, prioriza chunks/edges com `ObservedAt` na janela (boost, não filtro — paridade com hindsight) sem depender de parsing da query.
- **RF-005 — `annotations` MCP no catálogo**: ✅ **já entregue** pela SPEC-20260926-mcp-sdk-alignment RF-001 — `tools/list` projeta `ToolAnnotations` (title/readOnlyHint/destructiveHint/idempotentHint/openWorldHint) a partir do catálogo (`KnowledgeHubServiceCollectionExtensions.ListToolsHandler`); todos os providers declaram os hints. Sem trabalho restante — mantido na SPEC como verificação de cobertura.

## 4. Acceptance Criteria

- AC-1: `search_knowledge {query, budget:"low"}` executa sem query expansion e com pool reduzido; `budget:"high"` (default) mantém resultados idênticos ao comportamento atual (teste de paridade).
- AC-2: `maxTokens: 512` retorna menos resultados que `topK: 50` produziria, com `truncatedByTokens: true` no output.
- AC-3: `minScores: {final: 0.9}` sobre corpus sem matches fortes → `ask_knowledge` abste (resposta de abstenção, zero chamada de síntese).
- AC-4: `temporalWindow: {start:"2026-09-01", end:"2026-09-30"}` prioriza resultados de setembro; janela inválida → erro de validação claro (não silencioso).
- AC-5: `tools/list` inclui `annotations` para todas as tools; `tools/call` rejeita apenas args de tipo errado (args desconhecidos continuam tolerados).
- AC-6: schemas do playground/UI refletem os novos parâmetros (schema dinâmico existente).

## 5. Risks

- `budget:"low"` pode degradar recall em queries difíceis — mitigado por default `high` e por documentação no schema.
- Pisos `minScores` mal calibrados podem over-abster — o piso `final` é opt-in por chamada; default continua sem piso.
- Aproximação de tokens (chars/4) é imprecisa para CJK — aceitável para orçamento de contexto; documentado.
