# SPEC-20261021-context-rest-contract-fixes

| Campo | Valor |
|---|---|
| Status | Done |
| Ticket | [#588](https://github.com/afonsoft/KnowledgeRAG/issues/588) |
| Escopo | Corrigir drift de contrato REST/MCP das tools `context_*` (pós-merge SPEC-20261004) |

## Requisitos

- **RF-1 — Source correto por superfície.** `ContextSnapshot.Source` deve ser `rest` quando a chamada vem de `/api/context/*`. Implementação: `ContextEndpoints` passa `source:"rest"` via `ToolCallContext` (campo ou convenção), e `ContextToolsProvider` usa-o em vez do `SourceTag` fixo `mcp`. Critério: save via REST persiste `Source="rest"`, via MCP persiste `"mcp"` (teste de integração).
- **RF-2 — Status 201.** `POST /api/context/snapshots` retorna `201 Created` (com `Location` opcional) em vez de 200, conforme contrato da SPEC-20261004. Demais rotas inalteradas.
- **RF-3 — OutputSchema fiel.** `context_compact` declara schema próprio (`compacted`, `droppedCount`, `elidedCount`, `stats`, `savedVersion`, `withinBudget`) — atualizar `ContextToolsProvider` e o pin em `McpContractTests` no mesmo commit.
- **RF-4 — Rate limit REST espelha `context`.** `/api/context/compact` deve usar a partição `context` (60/min) e não `GeneralPolicy`, pois pode acionar summarization LLM — avaliar `RequireRateLimiting` por-endpoint dentro de `MapContextApi`.

## Fora de escopo
Sem mudança de payload, sem novos endpoints.
