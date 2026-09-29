# SPEC-20260929-a2a-assistant-delegation

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `a2a-assistant-delegation` |
| Type | `Feature` (cost optimization + interop) |
| Stack | `.NET 10`, `A2A` (client), `Microsoft.Extensions.AI`, Settings UI Blazor |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-a2a-assistant` |
| Status | `Draft` |
| Depends on | `SPEC-20260929-a2a-server-interop` (cliente A2A reutiliza o mesmo transporte) |

## 1. User Story

**As a** operador consciente de custo
**I want** um modelo assistente de baixo custo (endpoint OpenAI-compatível configurado em Settings) executando as sub-tarefas baratas do loop de agente
**So that** o modelo principal (caro) seja reservado para síntese/tool-calling complexo, reduzindo o custo por interação sem perder qualidade final.

## 2. Findings

1. 🔍 Sub-tarefas do loop agêntico (rewrite de query, grading de chunks, HyDE, resumo de thread, classificação de intenção) hoje usam o **mesmo modelo principal** — desperdício de custo/latência.
2. 🔍 Não existe segundo provedor "assistente" nas settings — só Chat principal + fallbacks de resiliência (semântica diferente: fallback é contingência, assistente é roteamento por custo).
3. 🔍 Não há caminho de delegação agent→agent: o loop não sabe falar A2A com agentes remotos.

## 3. Requirements

- **RF-001 — Assistant provider nos Settings**: nova seção `Assistant` (Settings UI + `/api/settings/assistant`), com `provider` (openai-compatível: `endpoint` base URL estilo OpenAI, `model`, `apiKey` no `IIntegrationSecretStore` sob `assistant:key`, `enabled`). Padrão: desabilitado. Paridade de UX com a tab Chat (masked key `***`, test connection, invalidação de snapshot sem restart).
- **RF-002 — Roteamento de sub-tarefas**: os pontos de sub-tarefa do pipeline (`rewrite`, `grading`, `multi-query expansion`, `HyDE`, sumarização de thread, classificação do corrective-RAG) usam o **assistant client** quando configurado; o modelo principal fica para síntese final e tool-calling. Flag por sub-tarefa em `Assistant:Route` (default: rewrite+grade+summarize).
- **RF-003 — Delegação A2A**: além do modelo local, o assistant pode ser um **agente A2A remoto** — settings aceita `mode: local | remote`; em `remote`, o endpoint é a URL de um Agent Card (descoberta via `A2ACardResolver`), a sub-tarefa vira um A2A Task (`SendMessageAsync`/streaming) e o resultado volta como contexto do sub-task. Auth do remote em `assistant:{id}` no secret store.
- **RF-004 — Fallback seguro**: assistant não configurado, com erro ou timeout (> N s configurável) → sub-tarefa cai para o modelo principal (comportamento atual); nunca quebra a resposta. Contador `knowledgehub.assistant.fallbacks`.
- **RF-005 — Observabilidade & custo**: spans `assistant.delegate` (tags: task, outcome) + contadores `knowledgehub.assistant.calls{outcome}` e tokens estimados por chamada; sem PII em tags (paridade com o allowlist de telemetria).

## 4. Acceptance Criteria

- AC-1: `/api/settings/assistant` persiste endpoint/model (key no secret store, JSON só `hasKey`) e aplica sem restart.
- AC-2: com assistant configurado, uma pergunta com grading habilitado usa o modelo barato no grading (verificável por span/tag `model`) e o modelo principal na síntese.
- AC-3: assistant off/indisponível → sub-tarefas usam o modelo principal e a resposta sai igual (sem erro ao usuário).
- AC-4: `mode: remote` com URL de Agent Card A2A delega a sub-tarefa via A2A e o resultado alimenta a pipeline como tool result.

## 5. Out of Scope

- Servidor A2A do KnowledgeHub (SPEC-20260929-a2a-server-interop).
- Roteamento por custo dinâmico/model routing complexo — primeira entrega é allowlist de sub-tarefas.
