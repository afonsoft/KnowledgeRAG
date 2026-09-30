# SPEC-20261001-a2a-task-durability

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `a2a-task-durability` |
| Type | `Feature` |
| Stack | `.NET 10 / ASP.NET Core`, `A2A` + `A2A.AspNetCore` (1.0.0-preview2), EF Core |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20261001-mcp-a2a-improvements` |
| Status | `Draft` |
| Ticket | TBD |
| Source | Benchmark analysis: `vectorize-io/hindsight` (padrão async operations) + spec A2A v1.0 |

## 1. User Story

**As a** agente chamador A2A (Claude Code, Devin, pipeline corporativo)
**I want** consultar/cancelar tasks A2A depois de desconectar, receber progresso incremental em tasks longas e (opt-in) receber push webhook na conclusão
**So that** delegações sobrevivem a restart do servidor, não exigem polling cego e integram com automações externas.

## 2. Context

- Análise do `hindsight` (2026-10-01): toda operação assíncrona (`retain`) retorna um **operation** consultável (`list_operations`/`get_operation`/`cancel_operation`) — polling determinístico em vez de polling cego.
- O KnowledgeHub A2A (SPEC-20260929-a2a-server-interop, Done) usa o `InMemoryTaskStore` default do SDK: `tasks/get`/`tasks/cancel` funcionam **apenas em-processo** — restart perde o histórico e o chamador recebe `task not found`.
- O SDK expõe `ITaskStore` plugável (interface pública em `A2A.dll` preview2) — o padrão `EfMcpTaskStore` (SPEC-20260929) já provou o caminho EF para tasks MCP.
- O handler emite Submit → StartWork → AddArtifact → Complete/Fail, **sem updates intermediários** — `agent_chat` multi-turn pode rodar minutos sem nenhum evento de progresso.
- Card declara `PushNotifications = false`; o SDK tem CRUD de push-config (`CreateTaskPushNotificationConfigRequest` etc.) não mapeado.
- Proveniência: documentos criados via `write_knowledge` não registram **quem** criou (MCP key vs A2A caller) — o hindsight carimba `metadata.harness` por agente.

## 3. Requirements

- **RF-001 — Task store durável**: implementar `ITaskStore` sobre EF Core (tabela `A2aTasks`: task id, context id, status JSON, artifacts JSON, timestamps, key id do chamador), registrada no lugar do `InMemoryTaskStore`. TTL/config de limpeza (`A2a:TaskRetentionHours`, default 72h) com job de purge no maintenance loop existente. `tasks/get` sobrevive a restart.
- **RF-002 — Progresso incremental**: durante execução de `agent_chat` (e qualquer skill >2s), o handler emite `TaskUpdater.WorkingAsync` com mensagem de progresso por iteração do loop do agente (ex.: "iteração 2/10 — consultando search_knowledge"). Paridade com o streaming MCP existente.
- **RF-003 — Push notifications opt-in**: mapear o CRUD de push-config do SDK (persistido na mesma tabela, por task/context); quando configurado, POST webhook na transição terminal (`completed`/`failed`/`canceled`) assinado com HMAC-SHA256 (padrão da cadeia de evidências — header `X-KH-Signature`), retry exponencial 3×. Card passa a declarar `PushNotifications = true` quando a feature está habilitada (`A2a:PushNotifications:Enabled`).
- **RF-004 — Atribuição de origem (harness attribution)**: documentos criados via `write_knowledge`/`write_note` recebem frontmatter `origin: {channel: "mcp"|"a2a", keyId, agentName?, at}` — `agentName` extraído do Agent Card do chamador quando disponível (metadata da message A2A). Visível no documento e no admin UI.
- **RF-005 — Skills metadata**: Agent Card ganha `inputModes`/`outputModes` por skill (`read_document` aceita `application/json` com `{path}`; `agent_chat` aceita `text/plain`) e `preferredInput` de exemplo por skill — descoberta mais rica para chamadores.

## 4. Acceptance Criteria

- AC-1: task A2A criada, servidor reiniciado, `tasks/get` retorna a task com status/artifacts persistidos; task >72h é purgada.
- AC-2: `agent_chat` via `message/stream` emite ≥2 eventos `working` com progresso antes do artifact final.
- AC-3: push-config registrada via SDK → transição terminal dispara POST assinado ao webhook; 3 falhas → desiste com log de warning (sem retry infinito).
- AC-4: `write_knowledge` via A2A produz documento com frontmatter `origin` correto (channel/keyId/agentName); via MCP produz `channel: "mcp"`.
- AC-5: card atualizado com `PushNotifications` condicional e skills com input/output modes; testes de integração cobrem tasks/get pós-restart (recriar host no teste) e webhook assinado.

## 5. Risks

- Store durável aumenta escrita no banco em cada transição de task — mitigado por TTL e purge; volume esperado é baixo (delegações, não chat humano).
- Webhooks são superfície de SSRF — validar URL (https, sem IP privado — reusar o guard de egress dos conectores) antes de persistir a config.
- SDK preview pode mudar `ITaskStore` — implementação isolada atrás do adapter próprio, pin de versão mantido.
