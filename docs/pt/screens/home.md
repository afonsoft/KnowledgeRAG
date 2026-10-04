# Home

**Rota:** `/`

![Home](../../screenshots/home.png)

## Como funciona

Página inicial (`Home.razor`) — ponto de partida para conectar um agente de IA
ao hub:

- **Card de prompt de setup MCP** — um prompt pronto descrevendo o servidor MCP
  do hub para um agente LLM; o botão de copiar manda pro clipboard pra colar no
  Claude/OpenCode/Cursor. O texto de ajuda aponta para
  [`/api-keys`](api-keys.md) para gerar uma chave `aft_*` usada no header
  `Authorization: Bearer`.
- **Cards de atalho** — links rápidos para [fontes](sources.md),
  [monitor MCP](mcp-monitor.md) e [playground](playground.md).

Endpoints úteis para o prompt: `/mcp` (Streamable HTTP) e `/mcp/sse`
(SSE legado).
