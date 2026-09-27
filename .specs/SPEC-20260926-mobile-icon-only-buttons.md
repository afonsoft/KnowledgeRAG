# SPEC-20260926-mobile-icon-only-buttons

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `mobile-icon-only-buttons` |
| Type | `Frontend` |
| Stack | `Blazor WebAssembly + BootstrapBlazor / CSS (.NET 10)` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/opencode-20260926-mobile-icon-only-buttons` |
| Ticket | `#mobile-buttons-ux` |
| Status | `Approved` |

## 1. User Story

**As a** usuário acessando o Knowledge MCP Hub (especialmente via dispositivo móvel ou telas estreitas)  
**I want** que os botões de copiar exibam apenas o ícone de cópia (sem o texto "Copiar"), e que os demais botões com ícone em mobile (< 576px) ocultem seus rótulos de texto mantendo apenas o ícone centralizado  
**So that** os cabeçalhos, toolbars, cards, blocos de código e formulários fiquem visivelmente compactos, organizados e sem quebras de linha indesejadas em qualquer dispositivo.

**Problem context:**  
1. Vários botões de cópia (ex.: blocos de configuração de clientes no Monitor MCP e modais de criação/segredo de API Keys) possuem o texto redundante "Copiar" / "Copiar chave", ocupando espaço valioso ao lado de campos de texto e blocos `<pre>`, enquanto em `Login.razor` o botão de cópia já é elegantemente icon-only (`Icon="fa-solid fa-copy" TooltipText="Copiar"`).
2. Em viewports móveis (< 576px), botões com ícone e texto (ex.: toolbars de tabelas "Nova fonte", "Nova chave"; ações de cards "Nova", "Excluir", "Atualizar", "Executar", "Salvar") provocam quebra de linha em cabeçalhos compactos.
3. Botões de ação em algumas páginas (como "Aprovar" / "Negar" em Aprovações, "Reconectar" em Monitor, "Ver" / "Promover" em Eval, "Confirmar escrita" em Playground) contêm apenas texto, não conseguindo colapsar para ícone em mobile sem sumir por completo.

## 2. Scope

**In scope:**
- **Botões de Copiar (Global - Desktop e Mobile):** Remoção do texto nos botões de cópia em toda a aplicação, deixando apenas o ícone `fa-solid fa-copy` com `TooltipText="Copiar"` e `Title="Copiar"`:
  - `McpMonitor.razor`: 5 botões de cópia de configuração de clientes (Claude Code, Devin, opencode, agy, Generic).
  - `ApiKeys.razor`: botão de cópia no modal de revelação de chave criada (`CopyKey`).
  - `ApiKeys.razor`: botão de cópia no modal de detalhes/segredo (`CopySecretAsync`).
- **Regra CSS Responsiva Mobile (`@media (max-width: 575.98px)`):**
  - Ocultar o texto (`font-size: 0;`) em qualquer botão que possua ícone (`.btn:has(i)`, `.btn:has(svg)`), mantendo o ícone legível (`font-size: 0.875rem` a `1rem`) e centralizado (`margin: 0 !important;`).
  - Preservar a área mínima de toque de 44×44px (`min-width: 44px; min-height: 44px;`).
- **Equiparação de Ícones em Botões de Ação:** Adicionar ícones FontAwesome semânticos aos botões de ação que possuem apenas texto:
  - `/approvals`: "Aprovar" (`fa-solid fa-check`), "Negar" (`fa-solid fa-xmark`).
  - `/mcp-monitor`: "Reconectar" (`fa-solid fa-rotate`).
  - `/eval`: "Ver" (`fa-solid fa-eye`), "Promover a baseline" (`fa-solid fa-star`).
  - `/playground`: "Confirmar escrita" (`fa-solid fa-triangle-exclamation`), "Cancelar" (`fa-solid fa-xmark`).
  - `/settings`: "Aplicar" de nível de log (`fa-solid fa-check`).
  - Modais (`ModalDialog`): Configurar `SaveButtonIcon="fa-solid fa-floppy-disk"` (ou `fa-check`) e `CloseButtonIcon="fa-solid fa-xmark"`.
- **Salvaguardas de Opt-out:**
  - Botões de bloco de formulário (ex.: "Entrar" no Login e "Salvar nova senha") e botões/chips de filtro sem ícone (ex.: "24h", "7d", "30d") preservam o texto normalmente via `:not(.w-100):not(.btn-block):not(.kh-keep-text)`.
- **Acessibilidade:**
  - Manter acessibilidade com `title`, `aria-label` e `TooltipText`.

**Out of scope:**
- Ocultar texto de botões comuns em telas desktop (exceto os botões de Copiar, que se tornam icon-only padrão).
- Modificações de backend, endpoints ou regras de autorização.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Client/wwwroot/css/app.css`: Regras CSS de media query para colapso de texto e centralização de ícones em botões mobile.
- `src/KnowledgeHub.Client/Pages/McpMonitor.razor`: Remoção de texto dos botões de cópia de configuração e adição de ícone no botão Reconectar.
- `src/KnowledgeHub.Client/Pages/ApiKeys.razor`: Remoção de texto dos botões de cópia (reveal modal e usage modal).
- `src/KnowledgeHub.Client/Pages/Approvals.razor`: Adição de ícones nos botões Aprovar / Negar.
- `src/KnowledgeHub.Client/Pages/Playground.razor`: Adição de ícones nos botões de escrita.
- `src/KnowledgeHub.Client/Pages/Eval.razor`: Adição de ícones nos botões Ver e Promover.
- `src/KnowledgeHub.Client/Pages/Settings.razor`: Adição de ícone no botão Aplicar log level.
- `src/KnowledgeHub.Client/Pages/Sources.razor`: Ícones nos modais e conferência de botões.

**Files to read before implementing:**
- `CLAUDE.md`
- `src/KnowledgeHub.Client/wwwroot/css/app.css`
- `src/KnowledgeHub.Client/Pages/McpMonitor.razor`
- `src/KnowledgeHub.Client/Pages/ApiKeys.razor`
- `src/KnowledgeHub.Client/Pages/Approvals.razor`

**Files to create or modify:**
```text
src/KnowledgeHub.Client/wwwroot/css/app.css
src/KnowledgeHub.Client/Pages/McpMonitor.razor
src/KnowledgeHub.Client/Pages/ApiKeys.razor
src/KnowledgeHub.Client/Pages/Approvals.razor
src/KnowledgeHub.Client/Pages/Chat.razor
src/KnowledgeHub.Client/Pages/Playground.razor
src/KnowledgeHub.Client/Pages/Eval.razor
src/KnowledgeHub.Client/Pages/Settings.razor
src/KnowledgeHub.Client/Pages/Sources.razor
```

## 4. Requirements

### RF-001: Botões de Copiar Icon-Only (Desktop e Mobile)
- **Description:** Todos os botões cuja ação seja copiar texto/código para a área de transferência devem ser renderizados apenas com o ícone FontAwesome `fa-solid fa-copy`, removendo a propriedade `Text="Copiar"`.
- **Rules:**
  - Devem conter `TooltipText="Copiar"` (ou respectivo detalhe, como "Copiar chave") e atributo `Title` ou `aria-label`.
  - Aplicável em:
    - `/mcp-monitor`: Blocos de conexão de Claude Code, Devin, opencode, agy e genérico (5 botões).
    - `/api-keys`: Modal de chave criada (`CopyKey`) e modal de uso (`CopySecretAsync`).
- **Input → Output:** `<Button Icon="fa-solid fa-copy" Text="Copiar" />` → `<Button Icon="fa-solid fa-copy" TooltipText="Copiar" TooltipPlacement="Placement.Top" />`.

### RF-002: Regra CSS Global Responsiva para Botões em Mobile (< 576px)
- **Description:** Em viewport móvel com largura até 575.98px, qualquer botão com ícone (seletor `.btn:has(i)`, `.btn:has(svg)`), incluindo botões de toolbar de tabelas (`.table-toolbar .btn`), deve colapsar o texto do rótulo e exibir unicamente o ícone centralizado.
- **Rules:**
  - Tamanho de fonte do botão: `font-size: 0;`.
  - Tamanho de fonte do ícone interno: restaurado para `0.875rem` / `1rem`.
  - Margem do ícone: `margin: 0 !important;` (anulando classes como `.me-1` ou margens de framework para garantir centralização exata).
  - Dimensões mínimas de toque: `min-width: 44px; min-height: 44px; justify-content: center; align-items: center;`.
  - Exceção: Botões com classe `.kh-keep-text` ou botões full-width `.w-100` / `.btn-block` permanecem com texto visível.

### RF-003: Equiparação de Ícones em Botões de Ação
- **Description:** Botões de ação que atualmente possuem apenas texto devem receber ícones FontAwesome semânticos:
  1. `/approvals`: Botão "Aprovar" recebe `Icon="fa-solid fa-check"`, botão "Negar" recebe `Icon="fa-solid fa-xmark"`.
  2. `/mcp-monitor`: Botão "Reconectar" recebe `Icon="fa-solid fa-rotate"`.
  3. `/eval`: Botão "Ver" recebe `Icon="fa-solid fa-eye"`, botão "Promover a baseline" recebe `Icon="fa-solid fa-star"`.
  4. `/playground`: Botões no alerta de escrita: "Confirmar escrita" recebe `Icon="fa-solid fa-triangle-exclamation"`, "Cancelar" recebe `Icon="fa-solid fa-xmark"`.
  5. `/settings`: Botão "Aplicar" de nível de log recebe `Icon="fa-solid fa-check"`.
  6. Modais (`ModalDialog`): Configurar `SaveButtonIcon` e `CloseButtonIcon` onde aplicável.

### RF-004: Acessibilidade e Tooltips
- **Description:** Para todo botão icon-only ou que colapsa em mobile, leitores de tela e toques prolongados devem identificar a ação com precisão via `TooltipText`, `title` ou `aria-label`.

### RF-005: Preservação de Botões Críticos de Formulário
- **Description:** O botão "Entrar" em `/login` e "Salvar nova senha" em `/change-password` não devem ter seu texto ocultado, mantendo a identidade clara do CTA principal da página.

## 5. API Contract (if applicable)

*N/A — Mudança restrita a componentes de apresentação Razor e CSS.*

## 6. Acceptance Criteria

- [ ] **Given** a página `/mcp-monitor` em desktop ou mobile **when** o usuário visualiza os blocos de instruções de clientes (Claude Code, Devin, opencode, agy, genérico) **then** os botões de cópia exibem somente o ícone `fa-copy` com tooltip "Copiar", sem o texto "Copiar".
- [ ] **Given** a página `/api-keys` ao gerar uma chave **when** o modal de revelação é exibido **then** o botão de cópia adjacente ao input exibe apenas o ícone `fa-copy` com tooltip.
- [ ] **Given** um smartphone com largura ≤ 575px **when** o usuário acessa as telas da aplicação (Fontes, API Keys, Chat, Settings, Eval, Playground, Aprovações) **then** botões que combinavam ícone e texto exibem apenas o ícone centralizado, sem quebra de linha no cabeçalho ou toolbar.
- [ ] **Given** um desktop com largura ≥ 576px **when** o usuário visualiza botões como "Nova fonte", "Nova chave", "Salvar", "Executar" **then** ícone e texto continuam aparecendo normalmente lado a lado.
- [ ] **Given** a página `/approvals` em tela mobile **when** renderizada a lista de aprovações pendentes **then** os botões de aprovação e negação aparecem com os ícones de check (`fa-check`) e xmark (`fa-xmark`) centralizados, com área mínima de 44×44px.
- [ ] **Given** a página `/login` **when** acessada em mobile **then** o botão "Entrar" mantém seu texto legível em tela cheia.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Ícone com classe de margem (`me-1`) em mobile | `<i class="fa-solid fa-play me-1">` | Margem é sobrescrita para `0 !important`, ícone fica no centro do botão quadrado. |
| Botão sem ícone (ex.: filtros de data "24h", "7d") | `<button class="btn">24h</button>` | Mantém texto inalterado, não colapsa. |
| Botão de cópia em input-group | Input group com input + botão de cópia | Botão fica integrado sem quebrar o alinhamento do grupo. |

## 7. Task Plan (agent execution)

- [ ] **T1 — Botões de Copiar:** Remover a propriedade `Text="Copiar"` e adicionar `TooltipText="Copiar"` em `McpMonitor.razor` e `ApiKeys.razor`.
- [ ] **T2 — Inclusão de Ícones Faltantes:** Adicionar ícones semânticos em `Approvals.razor`, `Playground.razor`, `Eval.razor`, `McpMonitor.razor` e `Settings.razor`.
- [ ] **T3 — Regras CSS Responsivas Globais:** Expandir `app.css` com a media query `< 575.98px` para `.btn:has(i), .btn:has(svg)` com reset de margin e touch targets.
- [ ] **T4 — Toolbars e Modais:** Garantir que `.table-toolbar .btn` e botões de `ModalDialog` colapsem adequadamente e possuam ícones.
- [ ] **T5 — Verificação:** Compilar a solução (`dotnet build`), compilar client Blazor WASM e executar a suíte de testes (`dotnet test`).

**7.1 Validation strategy by type/stack**

| Type / Stack | Required evidence |
|---|---|
| **Frontend / Blazor WASM** | `dotnet build KnowledgeHub.slnx` 0 warnings novos/0 erros; compilação do client WASM; `dotnet test` passando 100%. |

## 8. Organization Guardrails

- Nunca commitar diretamente em `main`, `master` ou `develop`. Utilizar branch `feature/opencode-20260926-mobile-icon-only-buttons`.
- Não alterar `.github/workflows/`.
- Manter touch target mínimo de 44×44px em telas touch.
- Preservar acessibilidade (leitores de tela continuam tendo acesso ao texto semântico).

## 9. Definition of Done

- [ ] Todos os botões de copiar tornados icon-only com tooltip em toda a aplicação.
- [ ] Requisitos RF-001 a RF-005 implementados.
- [ ] Critérios de aceite validados.
- [ ] `dotnet build` e `dotnet test` executados com sucesso.
- [ ] Guardrails respeitados.

## Open Questions / Pending Ambiguity

*Nenhuma pendência em aberto. Requisitos alinhados com o usuário.*
