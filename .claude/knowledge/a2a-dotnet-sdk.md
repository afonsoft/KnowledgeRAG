# A2A .NET SDK (1.0.0-preview2) — wire format & pitfalls

Package: `A2A` + `A2A.AspNetCore` (preview2). KnowledgeHub usage:
`src/KnowledgeHub.Server/A2A/` (agent handler + endpoint extensions),
`src/KnowledgeHub.Server/Assistant/A2AChatClient.cs` (IChatClient adapter).

## Wire format (what the server accepts)

- `Message.messageId` is **required** — omitting it fails JSON-RPC params
  deserialization (`-32602 request body could not be deserialized`).
- `role` serializes as `"ROLE_USER"` / `"ROLE_AGENT"` (enum names).
- Text parts serialize as `{"text":"..."}` — the `kind` discriminator is optional.
- `SendMessageRequest` params: `{"message": {...}, "metadata"?...}`.
- `SendMessageResponse` is a union: `{"message": {...}}` | `{"task": {...}}`.
- `AgentCard` requires `description`, `version`, `capabilities`, `skills`,
  `defaultInputModes`, `defaultOutputModes` — a minimal `{name, supportedInterfaces}`
  card does NOT round-trip. Build test cards with the SDK types +
  `A2AJsonUtilities.DefaultOptions`.
- `AgentInterface` requires `url`, `protocolBinding` (`"JSONRPC"` / `"HTTP+JSON"`
  via `ProtocolBindingNames`), `protocolVersion` (`"1.0"`).
- JSON-RPC method names are PascalCase strings: `SendMessage`,
  `SendStreamingMessage`, `GetTask`, `ListTasks`, `CancelTask`,
  `SubscribeToTask`, `GetExtendedAgentCard`, push-config CRUD.
  (See `A2AMethods` constants.)

## Task lifecycle gotcha

`TaskUpdater.FailAsync` on a task that was never `SubmitAsync`'d produces **no
events** → server returns `-32006 "Agent handler did not produce any response
events"`. Always `SubmitAsync` first (skip when `context.IsContinuation`).

## ASP.NET wiring

- `services.AddA2AAgent<THandler>(AgentCard)` registers handler + `IA2ARequestHandler`.
- `app.MapA2A(handler, "/a2a")` → JSON-RPC; `app.MapHttpA2A(handler, card, "/a2a")`
  → HTTP+JSON REST (`/a2a/message:send`, `/a2a/tasks/...`). Same base path, no
  route conflict.
- Well-known card must be `/.well-known/agent-card.json`; build absolute URLs
  per-request for proxy safety.
- Client side: `new A2ACardResolver(baseUri, http, "/.well-known/agent-card.json", logger)`
  → `card.SupportedInterfaces` picks the JSONRPC URL → `new A2AClient(uri, http)`.
- `A2AClient` takes the *interface* URL (e.g. `http://host/a2a`), not the host root.

## Integration with KnowledgeHub auth

- A2A endpoints get `RequireAuthorization(AuthPolicies.Operational)` +
  `RequireRateLimiting("llm")` — same `aft_*` keys as MCP; 401 without them.
- The handler resolves `IHttpContextAccessor.HttpContext.RequestServices` so the
  scoped `IDynamicToolCatalog`/`ICallerScopeProvider` apply per-caller.
