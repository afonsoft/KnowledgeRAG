# SPEC-20260929-agent-context-window-compaction

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-context-window-compaction` |
| Type | `Fix` |
| Stack | `.NET 10 / C#` — AgentService, ChainAst, compaction |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-agent-context-window-compaction` |
| Status | `Approved` |
| Source | Comentários `devin-ai-integration` no PR #376 |

## 1. User Story

**As a** usuário do agente
**I want** a compactação de contexto preservando as instruções de sistema e cobrindo resultados grandes de tools
**So that** o loop não perca o system prompt, não estoure a janela num único turno e não deixe tool calls sem resposta.

## 2. Findings

1. 🔴 Compactação remove as instruções de sistema — `system`/developer messages são varridas junto do histórico.
2. 🔴 Respostas grandes de tools não acionam compactação — o gate mede o turno, não o payload do tool result.
3. 🔴 Reparo de AST deixa chamadas sem resposta no modelo — `tool_calls` órfãos rejeitados pelo provider.
4. 🔴 Um único turno longo excede a janela do modelo — sem truncamento por token budget.
5. 🟨 Resumo pode promover texto não confiável a mensagem do assistente — tool output arbitrário vira "assistant said" no resumo.
6. 🔍 Resultado órfão sobrevive ao pareamento parcial; IDs duplicados permanecem; resumo limitado por chars, não bytes.

## 3. Requirements

- RF-001: Mensagens `system`/`developer` são pinadas — nunca removidas nem sumarizadas pela compactação.
- RF-002: Gate de compactação considera o tamanho total (system + histórico + tool results correntes), não só o acumulado de turnos.
- RF-003: Todo `tool_call` tem exatamente um `tool` result correspondente — repair fecha órfãos com placeholder error em vez de deixar a chamada pendente.
- RF-004: Turno único > budget → truncamento do maior tool result com marcador, não falha do loop.
- RF-005: Sumarização marca proveniência: conteúdo de tools fica citado como `tool output`, nunca como fala do assistente (prompt-injection hardening).
- RF-006: Limites por bytes estimados de tokens (`chars/4`), não chars.

## 4. Acceptance Criteria

- AC-1: system prompt idêntico antes/depois de compactar N turnos — teste.
- AC-2: tool result de 100KB dispara compactação — teste.
- AC-3: repair nunca produz `tool_call` sem `tool` — invariante testada com AST adversarial.
- AC-4: suite verde, 0 warnings.
