# SPEC-20260929-search-scope-pipeline-hardening

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `search-scope-pipeline-hardening` |
| Type | `Fix` (bugs + segurança de escopo) |
| Stack | `.NET 10 / C#` — SearchService, AutocutFilter, expanders |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-search-scope-pipeline-hardening` |
| Status | `Draft` |
| Source | Comentários `devin-ai-integration` em PRs #369, #370, #380, #381 |

## 1. User Story

**As a** mantenedor do KnowledgeHub
**I want** a pipeline de busca corrigida nos pontos apontados pela revisão (autocut, relaxamento, expansão, cache, escopo)
**So that** resultados estritos não sejam adulterados por relaxamento, conteúdo sinalizado não reentre pelo contexto expandido e o cache não entregue respostas de outra configuração.

## 2. Findings (verbatim da revisão — todos confirmáveis em código)

1. 🔴 Buscas sem filtros ignoram `Search:LimitMode=autocut` (`SearchService.cs` — filtro nulo → `fixed`).
2. 🔴 Autocut compara `Fused` mas hits chegam ordenados por `Rerank` quando reranker ativo (`AutocutFilter.cs`) — corte artificial.
3. 🔴 Cache de resposta ignora `windowSize`/expansão (`CacheKeys.Answer` não cobre os novos knobs) — síntese de outra janela reutilizada; ações ao vivo também ficam stale no cache (PR #379).
4. 🟥 **SECURITY** — expansão de janela/seção consulta vizinhos sem filtrar `SuspicionFlags`: chunk sinalizado reentra no prompt mesmo com `ExcludeFlagged=true` (linhas ~590 de `SearchService.cs`).
5. 🟥 **SECURITY** — fonte explicitamente negada (`excludeSources`/`sourceIds` restritos) dispara relaxamento que busca em outras fontes (PR #380).
6. 🔴 Relaxamento não aplica quando a busca híbrida retorna vazia; resultados relaxados podem ranquear acima dos estritos.
7. 🟡 Falha em uma sub-consulta (`subQueries`) descarta/interrompe as demais — precisa de isolamento por sub-query.
8. 🟡 Janelas adjacentes duplicam passagens no contexto (união sem dedup exigida pela SPEC de expansão); `ExpandedChunkIndices` inclui passagens cortadas por `MaxParentTokens`; expansão ultrapassa o teto por-documento (2000 tokens).
9. 🟡 `ask_knowledge generate=false` ignora a janela solicitada (`FormatAnswerContext` não usa expansão).
10. 🔍 `SearchOutputSchema` do MCP não documenta `totalMatches`/`limitModeApplied`; resposta estruturada omite aviso de "evidências fora do filtro".

## 3. Requirements

- RF-001: `LimitMode` default aplicado mesmo com `filter == null`.
- RF-002: Autocut opera sobre a chave de ordenação efetiva (rerank → `Rerank`, senão `Fused`).
- RF-003: Cache keys incorporam `windowSize`, `contextExpand`, e sinal de live-actions — ou bypass de cache quando o contexto contém dados vivos.
- RF-004: Expansão (`window`/`section`) aplica `SuspicionFlags` quando `ExcludeFlagged` — nunca devolve chunk sinalizado como contexto.
- RF-005: Relaxamento hierárquico nunca expande além do escopo do caller (`CallerScope`/sourceIds negados); resultados relaxados são marcados e nunca ultrapassam estritos no ranking.
- RF-006: Falha de uma sub-consulta → aquela retorna vazia com warning; demais seguem.
- RF-007: União de janelas deduplica passagens; `ExpandedChunkIndices` reflete apenas conteúdo efetivamente entregue; teto por-documento respeitado.
- RF-008: `generate=false` respeita `windowSize`; `SearchOutputSchema` documenta `totalMatches`/`limitModeApplied`/`warnings`.
- RF-009: Resposta estruturada carrega `warnings` quando relaxamento/expansão de escopo ocorreu.

## 4. Acceptance Criteria

- AC-1: teste unitário reproduz cada item 1–9 e passa após correção.
- AC-2: chunk com `SuspicionFlags` nunca aparece em `Context`/`ExpandedChunkIndices` — teste dedicado.
- AC-3: `sourceIds`-denied + relaxamento → resposta vazia com warning, sem hits de outras fontes — teste.
- AC-4: 0 warnings; suite verde.
