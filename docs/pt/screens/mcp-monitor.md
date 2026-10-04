# Monitor MCP

**Rota:** `/mcp-monitor`

![Monitor MCP](../../screenshots/monitor.png)

## Como funciona

`McpMonitor.razor` é um console em tempo real do servidor MCP nativo — os
eventos chegam via SignalR, sem refresh:

- **Card de sessões** — sessões ativas de clients MCP/A2A; o estado vazio
  mostra a dica de conexão com o endpoint `/mcp`.
- **Stream de atividade** — cada mensagem JSON-RPC (chamadas de tool,
  resultados, erros) com timestamp, tipo, sessão e badge de resultado.
- **Filtros** — tipo (tools/list, tools/call, …), resultado (todos/ok/erros),
  busca livre e janela "desde"; eventos que não batem são ocultados no client.
- **Estatísticas** — cards de latência média e top tools; badge de saúde de
  erros.
- **Ações** — reconectar quando o SignalR cai, e **Export CSV** para baixar a
  atividade filtrada.
