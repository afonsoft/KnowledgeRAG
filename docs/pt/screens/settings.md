# Settings

**Rota:** `/settings`

![Settings](../../screenshots/settings.png)

## Como funciona

`Settings.razor` é um painel em abas sobre opções de nível `appsettings` que
podem ser alteradas em runtime:

- **Chat (LLM)** — provider, endpoint, API key e modelo do `IChatClient`
  principal, mais providers de fallback e ação Testar conexão;
  "Restaurar env" volta aos valores configurados por ambiente.
- **Assistant** — o client do assistente do chat (endpoint/key/modelo) com
  teste de conexão.
- **GraphRAG** — opções de extração de entidades/relacionamentos usadas na
  ingestão.
- **Resilience** — modo `Resilience:Fallback` (disabled/observe/enforce),
  **capacidades** de tools e regras de roteamento do `ResilientChatClient`.
- **Integrações** — API keys dos proxies MCP upstream e conectores
  (firecrawl, deepwiki, tavily, context7) com toggle ativar/desativar.
- **Database** — estatísticas e limpeza de cache, diagnóstico do vector store
  (backend, dimensões) e info do provider.

Edições salvam por seção e podem ser revertidas aos valores do ambiente.
