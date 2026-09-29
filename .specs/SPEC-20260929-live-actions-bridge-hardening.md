# SPEC-20260929-live-actions-bridge-hardening

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `live-actions-bridge-hardening` |
| Type | `Fix` (segurança + correção de cache/contexto) |
| Stack | `.NET 10 / C#` — McpDynamicRagActionBridge, AnswerService, cache |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-live-actions-bridge-hardening` |
| Status | `Approved` |
| Source | Comentários `devin-ai-integration` no PR #379 |

## 1. User Story

**As a** operador do KnowledgeHub
**I want** o bridge de ações ao vivo incapaz de disparar tools por texto injetado em documentos e a resposta refletindo dados ao vivo
**So that** um chunk com `<!-- mcp-tool: ... -->` plantado não execute tools arbitrárias e o cache não entregue respostas velhas após execução.

## 2. Findings

1. 🟥 **SECURITY** — Documentos/chunks podem conter `<!-- mcp-tool: name -->` injetado → `ToolActionAnnotationDetector` trata como nomination → execução de tool externo não solicitada pelo usuário. Necessário trust boundary: marcadores só são honrados vindos da *pergunta do usuário* ou com `readOnlyHint` + allowlist, nunca de chunks indexados.
2. 🔴 Respostas de ações ao vivo ficam desatualizadas no cache — cache key não incorpora que `enableLiveActions` disparou execução; segunda pergunta idêntica reaproveita resposta pré-execução.
3. 🔴 Ações sugeridas (`suggestedActions`) não chegam ao agente — o campo é computado no provider mas não propagado para `AgentService`/resposta final.
4. 🔴 Resposta ignora dados ao vivo quando faltam documentos — caminho de "0 chunks" não chama o bridge mesmo com nominations explícitas do usuário.
5. 🟡 Dados estruturados de tools (`Content` não-TextContentBlock — imagens/recursos) não chegam à resposta — só `TextContentBlock` é serializado.
6. 🔍 Marcadores parciais deixam a query obrigatória vazia — `query=""` passa sem validação.
7. 🔍 Fallback sem provedor omite citações vivas — quando a resposta usa só live-data, o `Citations` fica vazio sem flag explicando.

## 3. Requirements

- RF-001: `ToolActionAnnotationDetector` só considera markers da `question`/`message` do usuário e de `SystemPrompt`-trusted chunks; markers em `SearchResultItem.ChunkText` são ignorados (ou exigem flag `AllowDocMarkers` default false).
- RF-002: Tool executada via bridge é sempre `ReadOnly` + presente no `CallerScope` — já parcial; documentar e testar o bypass.
- RF-003: Cache de resposta inclui hash dos `LiveToolExecution` (ou bypass quando `liveExecutions.Count > 0`).
- RF-004: `suggestedActions` propagado para `AgentService` e para o DTO de resposta — UI pode renderizar chips.
- RF-005: Bridge roda mesmo com 0 chunks quando há nomination explícita do usuário.
- RF-006: `LiveToolExecution.Output` aceita `EmbeddedResource`/imagens com preview textual; args vazios rejeitam a nomination.
- RF-007: Resposta traz `liveExecutions` como pseudo-citations quando não há chunks (origem marcada `tool`).

## 4. Acceptance Criteria

- AC-1: chunk com marker injetado não executa tool — teste.
- AC-2: mesma pergunta executada duas vezes → segunda resposta reflete dados novos (cache bypass) — teste.
- AC-3: `suggestedActions` presente no response DTO — teste.
- AC-4: 0 chunks + nomination do usuário → bridge executa — teste.
- AC-5: suite verde.
