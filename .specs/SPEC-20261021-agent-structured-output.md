# SPEC-20261021-agent-structured-output

| Campo | Valor |
|---|---|
| Status | Done |
| Ticket | [#597](https://github.com/afonsoft/KnowledgeRAG/issues/597) |
| Escopo | `responseFormat`/JSON-schema opcional em `agent_chat`/`ask_knowledge` — resposta validada por schema, inspirado no `create_agent(response_format=…)` do LangChain |

## Contexto técnico

`ask_knowledge` e `agent_chat` retornam texto livre (+ citações). M.E.AI `IChatClient` suporta `ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(...)`; providers que não suportam ignoram ou falham. Hoje `AgentRequest`/`AnswerRequest` não têm campo de formato.

Arquivos-chave: `src/KnowledgeHub.Shared/Contracts/AgentDtos.cs` (`AgentRequest`/`AgentResponse`), DTOs de ask em `KnowledgeHub.Shared`, `src/KnowledgeHub.Server/Services/AgentService.cs`, `AnswerService.cs`, endpoints MCP em `KnowledgeHub.McpEngine`, `/api/agent`, `/api/ask`, testes de agente/ask.

## Requisitos

- **RF-1 — Contrato.** `AgentRequest.ResponseFormat` e campo equivalente no request de ask: `{ type: "json_schema", jsonSchema: { name, schema, strict? } }` (shape alinhado ao OpenAI/M.E.AI). `null` = comportamento atual (texto livre). `AgentResponse`/`AnswerResponse` ganham `StructuredOutput` (string JSON parseada) quando o formato foi honrado.
- **RF-2 — Implementação via M.E.AI.** Quando `ResponseFormat` setado: `chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, name, strict)`. Caminho único — sem prompt-hacking paralelo.
- **RF-3 — Fallback honesto.** Provider/modelo sem suporte → o response volta com `structuredOutput=null`, `structuredOutputFallbackReason` ("provider_no_support" | "invalid_json" | "schema_rejected") e o texto bruto em `Answer` — nunca falha a request inteira salvo `strict=true` explicitamente pedido.
- **RF-4 — MCP + REST.** `agent_chat`/`ask_knowledge` aceitam `responseFormat` no input schema; `/api/agent`, `/api/ask`, `/api/agent/stream`, `/api/ask/stream` aceitam o mesmo campo; SSE `done` carrega `structuredOutput` quando presente.
- **RF-5 — Validação do schema de entrada.** JSON-schema inválido no request → `400`/`ToolError` com mensagem clara antes de chamar o modelo. Schema com `name` inválido (regex `^[a-zA-Z0-9_-]{1,64}$`) → idem.
- **RF-6 — Testes.** Unit: serialização do contrato, validação de schema inválido, fallback reasons. Integration: FakeChatClient honrando `ResponseFormat` → `structuredOutput` populado; provider que ignora o formato → fallback reason correto.

## Fora de escopo

Validação server-side do JSON retornado contra o schema (M.E.AI já sinaliza suporte; revisitar se providers mentirem), tool `ask_knowledge` no A2A (pode vir depois), UI exibindo output estruturado.

## Validação

`dotnet test` verde; MCP `tools/list` mostra o campo novo no input schema.
