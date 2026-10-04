# API keys

**Route:** `/api-keys`

![API keys](../../screenshots/api-keys.png)

## How it works

`ApiKeys.razor` manages the `aft_*` bearer keys MCP/A2A clients authenticate
with:

- **Keys table** — name, created-at, last used, average latency, error count
  and status; keys can be revoked without deleting usage history.
- **Create** — the full secret is shown **once** in a dialog with a copy
  button (a warning appears when clipboard access is unavailable); after that
  only the masked key remains.
- **Per-key config** — each key can override the global settings for its own
  session: chat endpoint/model/key (what `set_chat_settings` manipulates) and
  integration keys (`set_api_key_settings` — firecrawl, deepwiki, tavily,
  context7). "Back to global" clears an override.
- **Usage audit** — per-key call counts and last-used timestamps feed the
  security audit trail.
