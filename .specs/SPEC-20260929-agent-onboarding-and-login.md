# SPEC-20260929-agent-onboarding-and-login-ux

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-onboarding-and-login-ux` |
| Type | `Docs/Fix` (UX + protocolo de agente) |
| Stack | `Markdown`, Blazor WASM (`Login.razor`, `Home.razor`) |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-agent-onboarding` |
| Status | `Draft` |
| Source | Direto do dono (2026-09-29) — prompt "Conectar Agente de IA ao MCP" + tela de login |

## 1. User Story

**As a** mantenedor
**I want** o prompt "Conectar Agente de IA ao MCP" ensinar o agente a se grounding primeiro (DeepWiki) e a registrar o trabalho feito, e a tela de login apresentar esse prompt como item principal
**So that** qualquer agente conectado siga o protocolo de grounded-first + registro de notas, e o onboarding seja visível já na porta de entrada da plataforma.

## 2. Findings

1. 🔍 O prompt "Conectar Agente de IA ao MCP" (`Home.razor` → `LlmConfigPrompt`) não instrui o agente a consultar o repositório antes de trabalhar — usar `read_wiki_contents` (DeepWiki) e `ask_question` para perguntar sobre o repositório primeiro.
2. 🔍 `AGENTS.md`/`CLAUDE.md` não mandam registrar o que foi feito: toda sessão deve criar nota com `write_note`; conhecimento/memória durável com `write_knowledge`.
3. 🔍 Tela de login (`Login.razor`) não exibe o card "Conectar Agente de IA ao MCP" — o prompt completo só existe na Home (pós-login).

## 3. Requirements

- **RF-001 — Prompt "Conectar Agente de IA ao MCP"** (`Home.razor` → `LlmConfigPrompt`):
  - Nova seção "0. Grounding do repositório" no topo do prompt: antes de qualquer alteração, usar `read_wiki_contents` para ler a wiki do repositório e `ask_question` (DeepWiki) para perguntar sobre arquitetura/convenções — nunca assumir regras de negócio sem grounding.
  - Seção "Recomendações de Uso" ganha os itens `read_wiki_contents` / `ask_question` (DeepWiki) como passo de grounding de repositório.
  - Nova seção "4. Registro de Trabalho": toda sessão registra o que foi feito com `write_note(title, content, path)` no vault Obsidian conectado; conhecimento reutilizável/memória durável com `write_knowledge(title, content)`.
- **RF-002 — AGENTS.md / CLAUDE.md**: seção "Memory Protocol" (CLAUDE.md) e nova seção em `AGENTS.md` passam a mandar:
  - sempre criar nota do que foi feito com `write_note` (via MCP do KnowledgeHub);
  - conhecimento/memória reutilizável promovida com `write_knowledge`.
- **RF-003 — Tela de login** (`Login.razor`):
  - O card "Conectar Agente de IA ao MCP" (prompt completo, igual ao da Home) entra como **primeiro item** da página.
  - Desktop: card de login à **esquerda** e no topo (order-lg-1, col-lg-5); o restante (prompt MCP + instruções de client) à **direita** (order-lg-2).
  - Mobile: login primeiro, prompt depois (ordem natural do DOM).

## 4. Acceptance Criteria

- AC-1: `LlmConfigPrompt` menciona `read_wiki_contents` e `ask_question` como passo de grounding do repositório.
- AC-2: `AGENTS.md` e `CLAUDE.md` instruem `write_note` (registro do trabalho) e `write_knowledge` (conhecimento/memória).
- AC-3: `/login` mostra o card "Conectar Agente de IA ao MCP" como primeiro item; login à esquerda no desktop; mobile: login → prompt.
- AC-4: o prompt da tela de login é copiável (mesmo comportamento do botão copy da Home).

## 5. Out of Scope

- Mudanças no fluxo de autenticação ou na geração de API keys.
- Internacionalização do prompt (pt-BR apenas, paridade en fica para a wave de docs).
