# SPEC-20260929-a2a-agent-interop

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `a2a-agent-interop` |
| Type | `Feature` |
| Stack | `.NET 10 / ASP.NET Core`, `A2A` + `A2A.AspNetCore` (v1.0 spec), Blazor Settings |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-a2a-interop` |
| Status | `Draft` |
| Source | Direto do dono — A2A Protocol v1.0 ([spec](https://a2a-protocol.org/latest/specification/), [SDK .NET](https://github.com/a2aproject/a2a-dotnet)) |

## 1. User Story

**As a** operador do KnowledgeHub
**I want** o KnowledgeHub participando do ecossistema A2A — expondo suas skills como um agente A2A completo e delegando sub-tarefas a um modelo assistente de baixo custo
**So that** agentes externos descubram e deleguem tarefas via protocolo aberto, e o modelo principal (caro) seja poupado nas sub-tarefas mecânicas.

## 2. Background

- **A2A v1.0**: protocolo aberto (Linux Foundation/Agentic AI Foundation) para comunicação agente↔agente — complementar ao MCP (agente→tool). Operações core: `SendMessage`, `SendStreamingMessage`, `GetTask`, `ListTasks`, `CancelTask`, `SubscribeToTask`, push-notification configs, `GetExtendedAgentCard`. Bindings normativos: JSON-RPC (§9), gRPC (§10), HTTP+JSON/REST (§11).
- **SDK**: `A2A` + `A2A.AspNetCore` (NuGet `1.0.0-preview2`, .NET 8+) — `IAgentHandler`, `TaskUpdater`, `AgentEventQueue`, `AddA2AAgent<T>()`, `MapA2A()`, `MapHttpA2A()`, `MapWellKnownAgentCard()`, `A2AClient`/`A2ACardResolver`.
- **Catálogo afonsoft/skills**: 24 skills instaladas via `skills-lock.json` (store `.agents/skills/`, symlink `.claude/skills/`) — arquitetura, specs (write/execute), issues, gap-analysis, orchestrator, QA, qualidade, observabilidade, conectores MCP (composio/notebooklm/wordpress/obsidian), diagramas (mermaid/drawio), diagnose, design, scaffolding, sonarqube-autofix, etc.

## 3. Requirements

### A. Servidor A2A (KnowledgeHub como Remote Agent)

- **RF-001 — Agent Card dinâmico**: `GET /.well-known/agent-card.json` (§8) gerado no startup:
  - `skills`: **todas as skills do catálogo afonsoft/skills** (24) viram `AgentSkill` — `id`/`name`/`description`/`tags` extraídos do frontmatter de cada `SKILL.md` (fonte: `skills-lock.json` + store `.agents/skills/`); skill nova instalada aparece no card sem deploy (gerado a cada request com cache curto, §8.6).
  - Além das skills de framework: skills de produto (`search_knowledge`, `ask_knowledge`, `agent_chat`, `read_document`, `write_note`, `write_knowledge`, `find_dependencies`, `analyze_impact`, `search_graph_*`) mapeadas 1:1 do catálogo MCP dinâmico.
  - `capabilities`: `streaming: true`, `pushNotifications: false` (follow-up); interfaces JSONRPC + HTTP+JSON (§5.2/§8.3); `protocolVersion: 1.0`.
  - `securitySchemes`: `bearer` (HTTPAuthSecurityScheme) — paridade com as API keys `aft_*`.
  - `AgentCardSignature` (§8.4): card assinado com a chave HMAC da cadeia de evidências (`evidence:master`) — JWS-like `AgentCardSignature`.
- **RF-002 — Endpoints**: `MapA2A("/a2a")` (JSON-RPC) + `MapHttpA2A("/a2a")` (REST §11) + `MapWellKnownAgentCard()`; ambos sob auth Bearer `aft_*` (401 sem key), CallerScope (allowedSourceIds) e rate limit por key aplicados a toda execução (§7.4/§7.5).
- **RF-003 — Task lifecycle**: `agent_chat` multi-turn e ingestões longas rodam como Tasks (`TaskUpdater`: submitted → working → input-required/completed/failed/canceled); `InMemoryTaskStore` com TTL 30min + cap 256 tasks (follow-up: persistência); `ListTasks` filtra por contextId/taskState (§6.5).
- **RF-004 — Streaming**: `SendStreamingMessage`/`SubscribeToTask` via SSE com eventos `TaskStatusUpdateEvent`/`TaskArtifactUpdateEvent` (§4.2), paridade com o MCP SSE existente.
- **RF-005 — Execução de skills**: uma task A2A nomeando uma skill do catálogo (ex. "run gap-analysis") roteia para o executor da skill (o mesmo handler do agente local); skills de escrita (`write_note`, `write_knowledge`, `create_issues`) exigem approval HITL quando o chamador não tem policy `auto-approve` (paridade HITL do agente).
- **RF-006 — Observabilidade**: spans `a2a.serve` (tags: skill, outcome, taskId) + contadores `knowledgehub.a2a.requests{skill,outcome}`; receipts de evidência (`ToolExecuted`, actor `A2aClient`) para tasks que geram resposta fundamentada; audit de uso por API key.

### B. Cliente A2A + assistente de baixo custo

- **RF-006 — Assistant provider (Settings)**: nova seção `Assistant` (tab em Settings, paridade com Chat): `mode: local|remote|off`, endpoint OpenAI-compatível, model, apiKey no `IIntegrationSecretStore` (`assistant:{id}`, JSON só `hasKey`), aplicação sem restart. Modelo de baixo custo padrão sugerido na UI (ex.: `gpt-4o-mini` / `llama3.2` local).
- **RF-007 — Delegação de sub-tarefas**: sub-tarefas baratas do loop agêntico (rewrite, grading, HyDE, sumarização de thread, classificação) roteadas ao assistant quando configurado (`Assistant:Route` allowlist); modelo principal reservado para síntese final/tool-calling. Fallback automático ao modelo principal em erro/timeout (contador `knowledgehub.assistant.fallbacks`) — nunca quebra a resposta.
- **RF-008 — A2A client para agentes remotos**: registro de agentes remotos (URL de Agent Card) em Settings; `A2ACardResolver` descobre capabilities; o agente principal delega tasks via `A2AClient` (JSON-RPC ou HTTP+JSON) e o resultado entra na pipeline como tool result/evidence; spans `a2a.delegate` + contador `knowledgehub.a2a.delegations{outcome}`.

## 4. Acceptance Criteria

- AC-1: `GET /.well-known/agent-card.json` retorna card válido (v1.0) com as 24 skills do catálogo + skills de produto, interfaces JSONRPC/HTTP-JSON, securityScheme bearer.
- AC-2: `A2AClient.SendMessageAsync` contra `/a2a` com `aft_*` executa `ask_knowledge` e devolve Message/Artifact; sem key → 401; caller com escopo restrito não vê fatos fora do escopo.
- AC-3: task multi-turn mantém estado entre chamadas (contextId) dentro do TTL do task store; `CancelTask` funciona.
- AC-4: streaming SSE entrega `TaskStatusUpdateEvent`/`TaskArtifactUpdateEvent` incrementais.
- AC-5: com assistant `local` configurado, grading/rewrite usam o modelo barato (span tag `model`), síntese usa o principal; assistant off → comportamento atual, sem erro.
- AC-6: `mode: remote` delega a sub-tarefa via A2A (Agent Card descoberto) e o resultado alimenta a resposta.
- AC-7: spans `a2a.serve`/`assistant.delegate` + contadores presentes; sem PII em tags.

## 5. Improvements sobre a primeira versão da SPEC (revisão)

1. **Card gerado do catálogo real** (skills-lock.json + frontmatter dos SKILL.md) em vez de lista manual — novas skills aparecem automaticamente; hash do card auditável.
2. **Operações completas da v1.0** (§3.1): `ListTasks`, `SubscribeToTask`, `GetExtendedAgentCard` incluídos no escopo (não só SendMessage) — paridade com o §9.4.
3. **Card assinado** (§8.4) reaproveitando a chave de evidência — clients podem verificar a identidade do servidor (§8.4.3).
4. **Version negotiation** (§3.6) explícita no card (`protocolVersion: 1.0`) + erro `-326`-equivalente para clients incompatíveis.
5. **In-task authorization** (§7.6): sub-tarefas herdando o escopo do chamador original — sem privilege escalation entre skills.
6. **Multi-tenancy** (§ Multi-Tenancy): partição de tasks por ApiKeyId — um key nunca lista tasks de outra.
7. **Riscos do SDK preview** isolados atrás de interface própria (`IA2AHost`), com pin exato de versão.
8. **Rate limiting + audit** por key desde o dia 1 (superfície nova pública).

## 6. Out of Scope

- gRPC binding (§10) — JSON-RPC + HTTP+JSON bastam na primeira entrega.
- Push notifications (webhook) — follow-up.
- Persistência de tasks A2A (InMemoryTaskStore + TTL na primeira entrega).
