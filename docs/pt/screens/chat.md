# Chat

**Rota:** `/chat`

![Chat](../../screenshots/chat.png)

## Como funciona

`Chat.razor` é a interface conversacional sobre o loop do agente
(`agent_chat` / `AgentService`):

- **Sidebar de threads** — threads de conversa persistidas; criar novas ou
  excluir antigas (confirm de `Common_Delete`).
- **Mensagens** — balões de usuário/assistente incluindo os passos de tool
  calling encadeados (uma linha de log por chamada).
- **Streaming** — respostas chegam por `/api/agent/stream` (SSE); quando o
  stream não emite evento, a página cai pro POST síncrono.
- **Badge de sumarização** — quando o compactor resume turnos antigos, um
  marcador `Summarized` indica onde o contexto foi comprimido.
- **Estados HITL** — spinner "aguardando" enquanto o modelo roda; se uma tool
  exigir aprovação, um estado `AwaitingApproval` leva a
  [/approvals](approvals.md).
