# Aprovações

**Rota:** `/approvals`

![Aprovações](../../screenshots/approvals.png)

## Como funciona

`Approvals.razor` é a fila human-in-the-loop para chamadas de tool
privilegiadas:

- **Lista de pendentes** — chamadas que o agente quer executar mas que exigem
  aprovação (tools do catálogo sem `ReadOnly`, ou steps `approval` dentro de
  flows). Cada linha mostra a tool, os argumentos e quem pediu
  (`RequestedBy` — `agent`, `flow`, …).
- **Aprovar** — a chamada executa; se veio de um step `approval` de flow, a run
  suspensa retoma automaticamente (toast `ApprovedResumed`).
- **Negar** — com mensagem opcional explicando o motivo; o agente / flow
  recebe a negação e segue sem aquela chamada.
- **Refresh** — a lista é por pull; nada se perde se você fechar a página —
  aprovações ficam pendentes até serem resolvidas.
