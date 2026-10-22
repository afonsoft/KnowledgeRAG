# SPEC-20261021-agent-middleware-pipeline

| Campo | Valor |
|---|---|
| Status | Done |
| Ticket | [#592](https://github.com/afonsoft/KnowledgeRAG/issues/592) |
| Escopo | Refatorar o loop do `AgentService` (951 linhas, ordenação hardcoded) para um pipeline de middlewares componíveis, inspirado no `AgentMiddleware` do LangChain v1 (`before_agent`/`before_model`/`wrap_model_call`/`wrap_tool_call`/`after_model`/`after_agent`) |

## Contexto técnico

Hoje `AgentService.RunAsync`/`StreamAsync` executa o loop num while único em `ExecuteLoopAsync`: compactor de ChainAst → `GetModelResponseAsync` → coleta `FunctionCallContent` → `DispatchCallsAsync` (gate HITL ou `ExecuteToolCallAsync`). Concerns transversais — compactor, aprovação HITL, limite de iterações, spans de telemetria, métricas de duração — estão inline no serviço.

Arquivos-chave: `src/KnowledgeHub.Server/Services/AgentService.cs`, `src/KnowledgeHub.Server/Services/IAgentService.cs`, `src/KnowledgeHub.Server/Agent/AgentOptions.cs`, `src/KnowledgeHub.McpEngine/Agents/ChainAst/*`, `tests/KnowledgeHub.Tests.Integration/AgentApiTests.cs`, `tests/KnowledgeHub.Tests.Unit/Server/AgentRuntimeHardeningTests.cs`.

## Requisitos

- **RF-1 — Interface `IAgentMiddleware`.** Definir em `KnowledgeHub.Server/Agent/` (ou `KnowledgeHub.McpEngine/Agents/` se precisar ficar fora do Server) uma interface com hooks opcionais: `BeforeAgentAsync`, `WrapModelCallAsync` (recebe `Func<Task<ChatResponse>>` handler), `WrapToolCallAsync` (recebe `FunctionCallContent` + `Func<Task<object?>>` handler → resultado), `AfterAgentAsync`. Assinatura mínima: middlewares registrados em ordem, primeiro = mais externo.
- **RF-2 — Middlewares built-in preservando comportamento.** Extrair as lógicas existentes para middlewares internos: `ChainAstCompactionMiddleware` (repair+compact antes de cada model call — RF do SPEC-20260927), `HitlApprovalMiddleware` (suspende em tools write-gated), `IterationLimitMiddleware` (MaxIterations + mensagem "iteration limit reached"), `TelemetryMiddleware` (spans `agent_iteration`/`tool` + `LlmDuration`/`ToolDuration`). O comportamento observável (SSE events, AgentResponse, approvals persistidos) deve ser idêntico ao atual.
- **RF-3 — Extensibilidade por config.** `Agent:Middleware` como lista ordenada opcional no `AgentOptions`; middlewares built-in sempre presentes em ordem fixa quando a config está vazia. Registro via DI `IEnumerable<IAgentMiddleware>`.
- **RF-4 — Sem mudança de contrato público.** `IAgentService`, `AgentRequest`, `AgentResponse`, `/api/agent`, `/api/agent/stream`, `agent_chat` MCP e `ResumeAsync` (HITL) permanecem com mesmas assinaturas e semântica.
- **RF-5 — Cobertura.** Testes unitários novos para: ordem de composição (middleware externo envolve interno), `wrap_model_call` chamado uma vez por iteração, `wrap_tool_call` uma vez por tool call, middleware customizado injetado via DI é executado. Suíte existente de agente deve passar sem alteração de asserts comportamentais.

## Fora de escopo

Novas capacidades de middleware (retry com backoff, PII redaction, rate-limit por chamada) — ficam para SPECs futuras usando a mesma interface. Não alterar `ChainAst`/`ChainCompactor` internamente. Não portar para LangGraph/state machines.

## Validação

`dotnet test` com suíte de agente verde; cobertura não pode cair (ratchet `.ci/coverage-baseline.txt`).
