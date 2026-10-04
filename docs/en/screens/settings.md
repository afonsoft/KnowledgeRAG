# Settings

**Route:** `/settings`

![Settings](../../screenshots/settings.png)

## How it works

`Settings.razor` is a tabbed control panel over `appsettings`-level options
that can be changed at runtime:

- **Chat (LLM)** — provider, endpoint, API key and model for the main
  `IChatClient`, plus fallback providers and a Test connection action;
  "Restore env" reverts to env-configured values.
- **Assistant** — the chat-assistant client (endpoint/key/model) with
  connection test.
- **GraphRAG** — entity/relationship extraction options used during
  ingestion.
- **Resilience** — `Resilience:Fallback` mode (disabled/observe/enforce),
  tool **capabilities** and routed task rules for `ResilientChatClient`.
- **Integrations** — API keys for upstream MCP proxies and connectors
  (firecrawl, deepwiki, tavily, context7) with activate/deactivate toggles.
- **Database** — cache statistics and Clear cache, vector store diagnostics
  (backend, dimensions) and provider info.

Edits save per section and can be reverted to the environment/launch values.
