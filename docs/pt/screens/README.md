# Telas

Uma página por tela da SPA administrativa Blazor WebAssembly. Cada doc tem a
rota, um print (`docs/screenshots/`) e como a tela funciona.

| Tela | Rota | Para que serve |
|---|---|---|
| [Home](home.md) | `/` | Inicial: prompt de setup MCP + atalhos |
| [Fontes de conhecimento](sources.md) | `/sources` | Registrar e sincronizar fontes |
| [Monitor MCP](mcp-monitor.md) | `/mcp-monitor` | Tráfego JSON-RPC e saúde em tempo real |
| [Aprovações](approvals.md) | `/approvals` | Aprovação humana de chamadas de tool |
| [Chat](chat.md) | `/chat` | Threads do agente com tool calling |
| [Playground](playground.md) | `/playground` | Chamar tools do catálogo ad-hoc |
| [Grafo de conhecimento](graph.md) | `/graph` | Navegar entidades e arestas do GraphRAG |
| [Eval](eval.md) | `/eval` | Runs de avaliação e baselines |
| [Qualidade RAG](rag-quality.md) | `/rag-quality` | Dashboard da tríade de qualidade |
| [Flows](flows.md) | `/flows` | Editor canvas de flows, runs e triggers |
| [API keys](api-keys.md) | `/api-keys` | Chaves `aft_*` e overrides por chave |
| [Settings](settings.md) | `/settings` | Providers de LLM, resiliência, integrações |

Auth: tudo exceto `/login` exige a sessão por cookie (usuário `admin` semeado) —
`/login` → primeiro login força troca de senha.
