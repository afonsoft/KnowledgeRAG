# SPEC-20260929-a2a-server-interop

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `a2a-server-interop` |
| Type | `Feature` |
| Stack | `.NET 10 / ASP.NET Core`, `A2A` + `A2A.AspNetCore` (NuGet, v1.0 spec) |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-a2a-server` |
| Status | `Draft` |
| Source | Direto do dono — interoperabilidade agent-to-agent (A2A Protocol) |

## 1. User Story

**As a** operador do KnowledgeHub
**I want** expor o KnowledgeHub como um agente A2A (Agent Card + endpoints JSON-RPC/HTTP+JSON)
**So that** outros agentes (Claude Code, Devin, agentes corporativos) descubram e deleguem tarefas ao KnowledgeHub pelo padrão aberto A2A, com a mesma auth/scope do MCP.

## 2. Context

- A2A v1.0 (Linux Foundation / a2aproject): descoberta via **Agent Card** (`/.well-known/agent-card.json`), comunicação **JSON-RPC** e **HTTP+JSON REST**, streaming **SSE**, tarefas com ciclo de vida (`submitted → working → completed/failed/canceled/input-required`) e artifacts.
- SDK oficial .NET: `A2A` (core: `IAgentHandler`, `TaskUpdater`, `InMemoryTaskStore`, `A2AClient`, `A2ACardResolver`) + `A2A.AspNetCore` (`AddA2AAgent<THandler>()`, `MapA2A()`, `MapHttpA2A()`, `MapWellKnownAgentCard()`). Versão atual no NuGet: `1.0.0-preview2` — pinar versão exata; re-avaliar quando a stable sair.
- O KnowledgeHub já é servidor MCP nativo (`/mcp`, `/mcp/sse`) — A2A complementa (MCP = agent→tool; A2A = agent→agent).

## 3. Requirements

- **RF-001 — A2A server**: endpoint A2A no Kestrel (`/a2a` JSON-RPC + `/a2a` HTTP+JSON REST via `MapA2A()`/`MapHttpA2A()`) e Agent Card em `/.well-known/agent-card.json`. Skills publicadas: `search_knowledge`, `ask_knowledge`, `agent_chat`, `read_document` (reuso direto dos handlers existentes do catálogo — nenhuma lógica duplicada).
- **RF-002 — Auth & escopo**: endpoints A2A exigem `Authorization: Bearer aft_*` (mesmas API keys do MCP); o Agent Card declara o securityScheme (`bearer`); o `CallerScope` do chamador (allowedSourceIds, rate limit por key) aplica-se a toda execução delegada; chamadas sem key → 401.
- **RF-003 — Task lifecycle**: delegações longas (ex.: `agent_chat` multi-turn) rodam como A2A Tasks com `TaskUpdater` (Submit → Working → artifact → Completed/Fail/InputRequired); `InMemoryTaskStore` na primeira entrega, com TTL/cap e nota de follow-up para persistência.
- **RF-004 — Streaming**: SSE para progresso incremental (paridade com o MCP SSE existente), opt-in por capability no card.
- **RF-005 — Observabilidade**: spans `a2a.serve` (tags: skill, outcome) + contador `knowledgehub.a2a.requests`; receipts de evidência (`ToolExecuted`, actor `A2aClient`) quando a task gera resposta fundamentada.

## 4. Acceptance Criteria

- AC-1: `GET /.well-known/agent-card.json` retorna o Agent Card válido (nome, skills, interfaces JSONRPC+HTTP+JSON, securityScheme bearer).
- AC-2: `SendMessageAsync` via `A2AClient` contra `/a2a` com `aft_*` válido executa `ask_knowledge` e devolve a resposta como Message/Artifact; sem key → 401.
- AC-3: caller com `AllowedSourceIds` restrito não vê fatos fora do escopo em respostas A2A (mesmo contrato do MCP).
- AC-4: task long-running (multi-turn) mantém estado entre chamadas dentro do TTL do task store.

## 5. Risks

- SDK em preview (`1.0.0-preview2`) — API pode mudar até a stable; isolar atrás de `IA2AHost` próprio.
- Superfície nova exposta publicamente — rate limit `sync`-equivalente + audit de uso por key obrigatórios desde o dia 1.
