# SPEC-20261021-agent-streaming-events

| Campo | Valor |
|---|---|
| Status | In implementation |
| Ticket | [#593](https://github.com/afonsoft/KnowledgeRAG/issues/593) |
| Escopo | Enriquecer o stream SSE do agente/ask com eventos de granularidade fina inspirados no `astream_events` do LangChain: `reasoning`, `usage`, `tool_call_chunk`, `tool_output_delta` |

## Contexto técnico

Hoje `/api/agent/stream` e `/api/ask/stream` emitem `token`/`tool_start`/`tool_end`/`awaiting_approval`/`done`/`error`. Em `AgentService.GetModelResponseAsync` os `ChatResponseUpdate` já são iterados — conteúdos `TextReasoningContent`/`FunctionCallContent`/`UsageContent` do M.E.AI são descartados além de `TextContent`. `ExecuteToolCallAsync` só emite início/fim do tool.

Arquivos-chave: `src/KnowledgeHub.Server/Services/AgentService.cs` (`GetModelResponseAsync`, `ExecuteToolCallAsync`), `src/KnowledgeHub.Server/Services/AnswerService.cs`, `src/KnowledgeHub.Server/Services/SseEvent.cs`, `tests/KnowledgeHub.Tests.Integration/StreamingApiTests.cs`, SDKs em `sdks/` (stream parsers).

## Requisitos

- **RF-1 — `reasoning`.** Emitir `reasoning` com `{ delta }` para cada `TextReasoningContent` recebido no streaming (modelos thinking: o1, deepseek-r1 etc.). Não emitir quando o provider não envia reasoning.
- **RF-2 — `tool_call_chunk`.** Emitir `{ name?, argsDelta }` quando `FunctionCallContent` chegar parcialmente no stream (args incrementais). Se o provider entrega o call inteiro num update só, emitir um único chunk com os args completos.
- **RF-3 — `usage`.** Após cada model call (streaming ou buffered), emitir `usage` com `{ inputTokens, outputTokens, totalTokens, modelId }` a partir de `UsageContent`/`ChatResponse.Usage` quando disponível; omitir contagens null. O evento `done` passa a incluir `usage` agregado da run.
- **RF-4 — `tool_output_delta` (opt-in).** Tools podem opcionalmente emitir deltas parciais — definir callback no contexto de execução; quando usado, o SSE emite `{ tool, delta }`. Sem regressão para tools que não emitem.
- **RF-5 — Compatibilidade.** Eventos novos são aditivos — clients que desconhecem ignoram (`SseEvent` já é genérico). `done`/`error`/`awaiting_approval` mantêm o contrato atual. SDKs (`sdks/dotnet`, `sdks/python`, `sdks/go`, `sdks/java`) devem pelo menos tolerar os eventos novos sem quebrar o parse.
- **RF-6 — Testes.** Integration tests cobrindo: streaming com FakeChatClient que emite reasoning+usage+tool chunks → SSE contém os eventos na ordem; buffered path (sem streaming) emite `usage` pós-call; ausência de reasoning → nenhum evento `reasoning`.

## Fora de escopo

Persistência de reasoning na thread, UI mostrando reasoning traces (candidate a SPEC futura de frontend), billing/cost tracking por usage.

## Validação

`dotnet test` — StreamingApiTests + novos testes verdes; SDK stream parsers compilando.
