# Chat

**Route:** `/chat`

![Chat](../../screenshots/chat.png)

## How it works

`Chat.razor` is the conversational interface over the agent loop
(`agent_chat` / `AgentService`):

- **Thread sidebar** — persisted conversation threads; create new ones or
  delete old ones (`Common_Delete` confirm).
- **Messages** — user/assistant bubbles including the tool-call steps the
  agent chained (log line per call).
- **Streaming** — answers stream over `/api/agent/stream` (SSE); when the
  stream yields no event the page falls back to the sync POST.
- **Summarization badge** — when the history compactor summarizes older
  turns, a `Summarized` marker shows where context was compressed.
- **HITL states** — "waiting" spinner while the model runs; if a tool needs
  approval, an `AwaitingApproval` state links you to
  [/approvals](approvals.md).
