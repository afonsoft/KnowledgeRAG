# Playground

**Rota:** `/playground`

![Playground](../../screenshots/playground.png)

## Como funciona

`Playground.razor` permite invocar o catálogo vivo de tools diretamente — as
mesmas tools que clients MCP veem:

- **Seletor de tool** — o catálogo dinâmico (built-ins + flows + proxies
  upstream); tools de escrita levam badge `write` com aviso, somente-leitura
  badge `read-only`.
- **Form de argumentos** — gerado a partir do `InputSchema` da tool; campos
  obrigatórios marcados, placeholders com exemplos e "preencher exemplo" para
  uma chamada pronta.
- **Run** — executa a chamada e renderiza o `structuredContent` mais o bloco
  `citations` (os chunks usados na resposta); falhas mostram o erro MCP
  literal.
- **Card de tarefa A2A** — envia uma task pra uma skill do hub
  (`ask_knowledge`, `search_knowledge`, `agent_chat`, `read_document`) pelo
  endpoint `/a2a` e mostra os passos/estado da task.
