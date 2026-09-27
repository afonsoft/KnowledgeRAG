# SPEC-20260926-home-mcp-prompt-and-agents-recommendations

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `home-mcp-prompt-and-agents-recommendations` |
| Type | `Frontend + Docs` |
| Stack | `Blazor WebAssembly + BootstrapBlazor / Markdown (.NET 10)` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/opencode-20260926-home-mcp-prompt-and-agents-recommendations` |
| Ticket | `#home-mcp-prompt` |
| Status | `Done` |

## 1. User Story

**As a** desenvolvedor ou operador acessando o Knowledge MCP Hub  
**I want** encontrar na página inicial (`Home.razor`) um painel central contendo um prompt pronto para colar no meu LLM / agente de IA (Claude Code, OpenCode, Cursor, Devin, etc.) configurando o servidor MCP, além de ter as recomendações de uso do MCP documentadas no `AGENTS.md`  
**So that** qualquer agente de IA ou desenvolvedor consiga conectar rapidamente o KnowledgeHub como seu servidor MCP de conhecimento e seguir as melhores práticas recomendadas de RAG, GraphRAG e gerenciamento de chaves.

**Problem context:**  
Atualmente, para descobrir como conectar um agente ao KnowledgeHub, o usuário precisa navegar até a página `/mcp-monitor` e rolar até o final da tela para ver snippets técnicos individuais. Não existe na Home (`/`) um local unificado e de fácil acesso com um prompt textual abrangente que o usuário possa copiar com 1 clique e enviar diretamente ao assistente (ex.: ChatGPT, Claude, Cursor, OpenCode) para instruí-lo a configurar e usar a ferramenta. Além disso, o arquivo `AGENTS.md` do repositório é bastante conciso e carece de uma seção formal detalhando as recomendações de uso das ferramentas do MCP (quando usar `search_knowledge`, como usar GraphRAG, como configurar settings per-key).

## 2. Scope

**In scope:**
- **Painel Central na Home (`Home.razor`):**
  - Card central de destaque ("Conectar Agente de IA / MCP") posicionado logo após o texto de boas-vindas e antes dos cards de navegação.
  - Prompt universal completo e formatado para o usuário fornecer ao LLM, contendo:
    - URL do endpoint MCP (`{BaseUri}/mcp`).
    - Instruções de autenticação com API Key (`Authorization: Bearer aft_...`) com link direto para criar em `/api-keys`.
    - Snippets rápidos para os principais clientes (Claude Code, OpenCode, Cursor/Devin/Genérico).
    - Guia de ferramentas essenciais (`search_knowledge`, `ask_knowledge`, `find_dependencies`, `write_knowledge`, `set_chat_settings`).
  - Botão de cópia icon-only (`fa-solid fa-copy` com `TooltipText="Copiar prompt para o LLM"`), seguindo o padrão responsivo sem texto.
  - Alerta sutil lembrando de criar a API key em `/api-keys` caso ainda não possua.
- **Recomendações no `CLAUDE.md` e `AGENTS.md`:**
  - Adição de seção dedicada `## MCP Server & Como Usar` no `CLAUDE.md` (fonte da verdade) e `## MCP Server & Recomendações` no `AGENTS.md`:
    - Descrição do endpoint Streamable HTTP `/mcp` e SSE `/mcp/sse`.
    - Como conectar cada assistente (Claude Code, OpenCode, Cursor, Devin).
    - Catálogo das principais tools e quando o agente deve invocá-las (`search_knowledge`, `ask_knowledge`, `agent_chat`, `find_dependencies`, `analyze_impact`, `write_knowledge`, `write_note`, `set_chat_settings`, `set_api_key_settings`).
    - Diretrizes de Grounding & Retrieval: priorizar consulta à base de conhecimento antes de assumir regras de negócio.
    - Guardrails de segurança: nunca expor ou commitar chaves `aft_*`.

**Out of scope:**
- Alterações nos contratos de tools MCP ou endpoints de API REST.
- Modificação no comportamento dos cards existentes da Home (Fontes, Monitor, Playground).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Client/Pages/Home.razor`: Inclusão do painel central com o prompt para o LLM, URL dinâmica injetada via `NavigationManager`, botão de cópia com clipboard JS.
- `CLAUDE.md`: Seção completa `## MCP Server & Como Usar`.
- `AGENTS.md`: Seção de referência `## MCP Server & Recomendações`.

**Files to read before implementing:**
- `CLAUDE.md`
- `AGENTS.md`
- `src/KnowledgeHub.Client/Pages/Home.razor`
- `src/KnowledgeHub.Client/Pages/McpMonitor.razor`

**Files to create or modify:**
```text
src/KnowledgeHub.Client/Pages/Home.razor
CLAUDE.md
AGENTS.md
```

## 4. Requirements

### RF-001: Painel Central de Conexão do MCP na Home
- **Description:** A página `Home.razor` deve renderizar um card central proeminente ("Conectar Agente de IA ao MCP") posicionado entre o parágrafo de introdução e a grade de cards rápidos de navegação.
- **Rules:**
  - O painel deve conter uma caixa de código formatada com o prompt universal pronto para o LLM.
  - A URL do servidor MCP deve ser dinâmica, baseada em `NavigationManager.BaseUri` (ex.: `http://localhost:5000/mcp`).
  - O cabeçalho do card deve conter o título "Conectar Agente de IA ao MCP" e um botão icon-only de cópia (`fa-solid fa-copy` com `TooltipText="Copiar prompt para o LLM"`), sem texto, alinhado à direita.
  - Ao clicar no botão de copiar, o texto do prompt deve ser transferido para o clipboard via JS interop (`navigator.clipboard.writeText`) e uma notificação Toast de sucesso deve ser exibida.
  - O prompt deve conter:
    1. Instruções para o LLM de como se conectar ao endpoint `{BaseUri}mcp`.
    2. Header de autenticação: `Authorization: Bearer aft_SUA_CHAVE`.
    3. Snippets de configuração rápida para Claude Code, OpenCode e Cursor/Devin.
    4. Guia prático de ferramentas essenciais (`search_knowledge`, `ask_knowledge`, `find_dependencies`, `write_knowledge`, `set_chat_settings`).
  - Um aviso contextual com link direto para `/api-keys` deve alertar o usuário para gerar sua chave caso ainda não possua.

### RF-002: Inclusão das Recomendações do MCP no `CLAUDE.md` e `AGENTS.md`
- **Description:** `CLAUDE.md` e `AGENTS.md` devem ser enriquecidos com uma seção clara e estruturada orientando os agentes de IA sobre como se conectar e tirar o melhor proveito do KnowledgeHub.
- **Rules:**
  - `CLAUDE.md` (fonte da verdade do repositório) ganha a seção completa `## MCP Server & Como Usar`:
    - Endpoint Streamable HTTP `/mcp` e SSE `/mcp/sse`.
    - Autenticação com Bearer `aft_*`.
    - Catálogo detalhado das principais tools:
      - `search_knowledge`: Busca híbrida FTS5 + vetorial sobre os vaults e documentos.
      - `ask_knowledge`: Pergunta direta com resposta sintetizada e fundamentada.
      - `find_dependencies` / `analyze_impact`: Navegação no grafo de entidades e dependências (GraphRAG).
      - `read_document` / `write_note`: Leitura e escrita no Obsidian vault.
      - `set_chat_settings`: Configuração isolada de endpoint/modelo de chat para a chave atual.
      - `set_api_key_settings`: Configuração per-key de chaves de upstream (DeepWiki, Firecrawl, Tavily, Context7).
    - Diretrizes de Grounding: Sempre realizar busca de contexto na base de conhecimento antes de propor alterações arquiteturais ou de negócio.
  - `AGENTS.md` ganha a seção executiva `## MCP Server & Recomendações`:
    - Referência a `CLAUDE.md`.
    - Bloco executivo de instruções de ferramentas para agentes.
    - Regras de segurança (nunca commitar segredos `aft_*`).

## 5. API Contract (if applicable)

*N/A — Mudança de UI (Blazor) e documentação de agente (Markdown).*

## 6. Acceptance Criteria

- [ ] **Given** a página inicial `/` **when** visualizada por qualquer usuário autenticado **then** é exibido um card central com o título "Conectar Agente de IA ao MCP" contendo o prompt pronto para o LLM.
- [ ] **Given** o card central na Home **when** o usuário clica no botão de cópia **then** o conteúdo completo do prompt é copiado para a área de transferência e uma mensagem de confirmação (Toast) é exibida.
- [ ] **Given** o card central em mobile (< 576px) **when** renderizado na tela **then** o botão de cópia é exibido no formato icon-only sem quebrar o alinhamento do cabeçalho do card.
- [ ] **Given** o arquivo `CLAUDE.md` e `AGENTS.md` **when** lidos por um agente de IA **then** constam as instruções completas de configuração do MCP, ferramentas disponíveis e recomendações de boas práticas de RAG e GraphRAG.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Acesso via hostname ou porta customizada | `http://vps:5550/` | O prompt exibe a URL correspondente `http://vps:5550/mcp` dinamicamente. |
| Usuário ainda não possui API Key criada | Clique no link de aviso | Redireciona para `/api-keys` para geração de chave. |

## 7. Task Plan (agent execution)

- [x] **T1 — Painel Central em `Home.razor`:**
  - Injetar `NavigationManager`, `IJSRuntime` e `ToastService`.
  - Adicionar o card central de destaque com o prompt gerado dinamicamente.
  - Implementar o botão de cópia com ícone `fa-solid fa-copy` e notificação de sucesso.
- [x] **T2 — Recomendações no `CLAUDE.md` e `AGENTS.md`:**
  - Adicionar a seção detalhada `## MCP Server & Como Usar` no `CLAUDE.md` e a seção de recomendações no `AGENTS.md`.
- [x] **T3 — Validação e Testes:**
  - Compilar a solução (`dotnet build`).
  - Executar a suíte de testes (`dotnet test`) garantindo que a documentação e os componentes atendem a todos os critérios.
  - Validar build e formatação.

**7.1 Validation strategy by type/stack**

| Type / Stack | Required evidence |
|---|---|
| **Frontend + Docs** | `dotnet build KnowledgeHub.slnx` 0 erros; `dotnet test` 100% verde; integridade visual e funcional de `Home.razor`, `CLAUDE.md` e `AGENTS.md`. |

## 8. Organization Guardrails

- Nunca commitar diretamente em `main`, `master` ou `develop`. Trabalhar na branch `feature/opencode-20260926-home-mcp-prompt-and-agents-recommendations`.
- Não alterar `.github/workflows/`.
- Preservar acessibilidade e padrão icon-only em mobile.

## 9. Definition of Done

- [x] Painel central incluído em `Home.razor` com prompt para o LLM e botão de cópia funcional.
- [x] `CLAUDE.md` e `AGENTS.md` atualizados com as recomendações completas do MCP.
- [x] `dotnet build` e `dotnet test` 100% verdes.
- [x] Todos os critérios de aceite validados.

## Open Questions / Pending Ambiguity

*Nenhuma ambiguidade impeditiva. Requisitos bem delineados.*
