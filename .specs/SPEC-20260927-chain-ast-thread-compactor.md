# SPEC-20260927-chain-ast-thread-compactor

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chain-ast-thread-compactor` |
| Type | `Feature` |
| Stack | `.NET 10 / Microsoft.Extensions.AI / C# 12 / System.Text.Json` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `#269` |
| Status | `Done` |

## 1. User Story

**As a** desenvolvedor ou agente autônomo interagindo com o Knowledge MCP Hub via `agent_chat` ou endpoints de conversação contínua
**I want** que o histórico da conversa seja estruturado como uma Árvore Sintática Abstrata (Chain AST) com compactação semântica e reparo automático de tool calls
**So that** threads longas não estourem a janela de contexto de tokens de LLM e nunca falhem por dessincronização de pares `ToolCall`/`ToolResponse` ou assinaturas de raciocínio corrompidas.

**Problem context:**
Em execuções de agentes com loops reativos de tool-calling (ex.: `agent_chat`, fluxos LangGraph e chamadas MCP encadeadas), o histórico cresce exponencialmente. Métodos ingênuos de truncamento (sliding window por contagem de mensagens ou corte de texto arbitrário) frequentemente rompem a invariante dos provedores modernos de LLM (OpenAI, Anthropic Claude, Google Gemini): cada `tool_call` gerado pela IA DEVE ter um `tool_result`/`ToolResponse` correspondente com o mesmo `call_id`. Se uma mensagem intermediária for descartada ou se uma chamada foi interrompida, o LLM retorna erro 400 (`unmatched tool_call_id`).
Inspirado na arquitetura do **PentAGI** (`pkg/cast` e `pkg/csum`), esta SPEC introduz um modelo de **Chain AST** em C# que divide a cadeia em seções (Headers: System/Human; Body: AI Request + Tool Responses), detecta chamadas pendentes ou orfãs sintetizando fallbacks de reparo, preserva blocos de raciocínio (`reasoning/thought`) e resume seções passadas via compactador assíncrono mantendo a integridade semântica da sessão.

## 2. Scope

**In scope:**
- `ChainAST` e modelo estruturado de mensagens em `KnowledgeHub.McpEngine` / `KnowledgeHub.Server/Agents/`:
  - `ChainSection`: Header (System + Human message inicial) e lista de `BodyPair`.
  - `BodyPair`: Par coeso formado por `ChatMessage` da IA (contendo `ToolCall`s ou completion) e a coleção associada de `ChatMessage` (tipo Tool com respectivos `CallId`s).
  - Tipos de `BodyPair`: `RequestResponse` (IA com tool calls e respostas), `Completion` (IA sem tool calls), `SummarizedSection` (resumo condensado de rodadas anteriores).
- `ChainAstParser`: construtor do AST a partir de `IReadOnlyList<ChatMessage>`.
- `ChainAstRepair`: analisador de integridade que identifica tool calls sem resposta (`PendingToolCalls`), gera respostas sintéticas padronizadas (`"The tool call was interrupted or unhandled; continuing session."`), e normaliza tool calls duplicadas ou malformadas.
- `ChainCompactor` / `IChainCompactor`: motor de redução de contexto configurável:
  - Preservação obrigatória: última seção ativa (Human prompt atual + execuções imediatas de tools).
  - Limite de bytes e tokens configuráveis (`MaxBodyPairBytes = 16KB`, `MaxTotalHistoryBytes = 64KB`, `KeepMinLastSections = 2`).
  - Compactador semântico: rodadas antigas são agregadas em um bloco de resumo markdown com prefixo padronizado (`**summarized content:**\n...`).
  - Suporte a metadados de raciocínio (`ThinkingContent` / `ReasoningSignature`) para provedores que exigem retenção de tokens de pensamento (Gemini, Claude 3.7/Opus, DeepSeek-R1).
- Integração no `AgentChatService` e no tool handler de `agent_chat`.
- Configuração em `appsettings.json` sob a chave `Agent:ContextManagement`.

**Out of scope:**
- Alteração no banco relacional de mensagens brutas existentes (a compactação atua na projeção do histórico enviada para a API do LLM).
- Implementação de provedores LLM externos além dos já suportados via `IChatClient`.
- Alteração nos contratos MCP públicos de entrada.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.McpEngine/Agents/ChainAst/`:
  - `ChainAst.cs`: classes do AST (`ChainAST`, `ChainSection`, `BodyPair`, `BodyPairType`, `ToolCallPair`).
  - `ChainAstParser.cs`: parsing e reconstrução de lista plana de `ChatMessage`.
  - `ChainAstRepair.cs`: detecção de tool calls órfãs e injeção de stubs defensivos.
  - `ChainCompactor.cs`: estratégia de compressão semântica e poda estruturada.
- `src/KnowledgeHub.Server/Services/AgentChatService.cs`: integração do compactor antes do envio de histórico ao `IChatClient`.
- `src/KnowledgeHub.Server/Configuration/ContextManagementOptions.cs`: DTO de configuração.
- `tests/KnowledgeHub.Tests.Unit/Agents/`: testes unitários para parsing, reparo de órfãos e compactação.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Services/AgentChatService.cs`
- `src/KnowledgeHub.McpEngine/Tools/AgentChatTool.cs`
- `src/KnowledgeHub.Shared/Contracts/McpModels.cs`
- `.specs/SPEC-20260914-conversation-threads.md`

**Files to create or modify:**
```text
src/KnowledgeHub.McpEngine/Agents/ChainAst/ChainAst.cs               (create)
src/KnowledgeHub.McpEngine/Agents/ChainAst/ChainAstParser.cs         (create)
src/KnowledgeHub.McpEngine/Agents/ChainAst/ChainAstRepair.cs         (create)
src/KnowledgeHub.McpEngine/Agents/ChainAst/ChainCompactor.cs        (create)
src/KnowledgeHub.McpEngine/Agents/ChainAst/IChainCompactor.cs       (create)
src/KnowledgeHub.Server/Configuration/ContextManagementOptions.cs    (create)
src/KnowledgeHub.Server/Services/AgentChatService.cs                (modify)
src/KnowledgeHub.Server/appsettings.json                            (modify)
tests/KnowledgeHub.Tests.Unit/Agents/ChainAstParserTests.cs         (create)
tests/KnowledgeHub.Tests.Unit/Agents/ChainAstRepairTests.cs         (create)
tests/KnowledgeHub.Tests.Unit/Agents/ChainCompactorTests.cs         (create)
```

## 4. Requirements

### RF-001: Representação em Árvore Sintática da Conversa (Chain AST)
- **Description:** O sistema deve estruturar qualquer histórico arbitrário de `ChatMessage` em um `ChainAST` tipado composto por seções e pares de mensagens.
- **Rules:**
  - Uma seção inicia com mensagens de controle/cabeçalho (`SystemMessage` e/ou `HumanMessage`).
  - Cada `BodyPair` agrupa estritamente um `AIMessage` e todas as `ToolMessages` que respondem aos `ToolCallId`s emitidos por essa mensagem.
- **Input → Output:** `IReadOnlyList<ChatMessage>` → `ChainAST`.

### RF-002: Detecção e Reparo Automático de Tool Calls Órfãs
- **Description:** Ao validar o AST, o sistema deve garantir que nenhum `ToolCall` fique sem resposta e que nenhuma `ToolMessage` fique sem correspondente `ToolCall`.
- **Rules:**
  - Se um `ToolCall` não possuir resposta associada (ex.: abort de rede ou timeout), o `ChainAstRepair` deve sintetizar uma `ToolMessage` defensiva com o mesmo `CallId` informando que a execução foi cancelada.
  - Se existir uma `ToolMessage` cujo `CallId` não pertença à mensagem anterior da IA, o parser deve descartar ou realocar a mensagem sem lançar exceção ao runtime.
- **Input → Output:** Histórico inconsistente com chamada pendente → `ChainAST` íntegro e seguro para envio ao LLM.

### RF-003: Compactação Semântica com Preservação de Janela Ativa
- **Description:** Quando o tamanho total do histórico exceder `MaxTotalHistoryBytes` ou contagem de seções, o `ChainCompactor` deve comprimir as seções históricas preservando as seções mais recentes.
- **Rules:**
  - A última seção ativa NUNCA deve ser resumida ou descartada.
  - As seções anteriores são processadas sequencialmente: corpos de ferramentas com grandes saídas de texto (> `MaxBodyPairBytes`) sofrem truncamento semântico ou sumarização de fatos.
  - O cabeçalho do resumo gerado deve incluir o marcador `**summarized content:**\n` para que a IA reconheça a compressão.
- **Input → Output:** `ChainAST` de 120KB → `ChainAST` compactado ≤ 48KB sem perda de diretrizes de sistema nem da pergunta atual.

### RF-004: Suporte a Signatures de Raciocínio (Thinking Blocks)
- **Description:** Modelos com raciocínio expandido (Gemini 2.0 Flash Thinking, DeepSeek-R1, Claude 3.7 Sonnet Thinking) emitem blocos de raciocínio intermediários.
- **Rules:**
  - Durante o parsing do AST, metadados de pensamento devem ser preservados no `BodyPair`.
  - Caso o conteúdo de raciocínio precise ser compactado, uma assinatura sintética (`skip_thought_signature`) deve ser aplicada para modelos que rejeitam remoção total de blocos de pensamento.

## 5. API Contract (if applicable)

Configuração no `appsettings.json`:
```json
{
  "Agent": {
    "ContextManagement": {
      "EnableChainCompaction": true,
      "MaxTotalHistoryBytes": 65536,
      "MaxBodyPairBytes": 16384,
      "KeepMinLastSections": 2,
      "AutoRepairBrokenToolCalls": true
    }
  }
}
```

## 6. Acceptance Criteria

- [x] **Given** uma lista de mensagens onde a IA solicitou tool call `call_123` e a sessão foi reiniciada sem a resposta **when** `ChainAstParser.Parse(messages, forceRepair: true)` é chamado **then** o AST contém um `BodyPair` reparado com mensagem de ferramenta sintética para `call_123` e status OK.
- [x] **Given** um histórico de conversa longo com 10 seções de perguntas e respostas acumulando 150KB **when** `ChainCompactor.CompactAsync(ast)` é executado **then** as 8 primeiras seções são sumarizadas em blocos condensados, as 2 últimas seções permanecem íntegras e o tamanho total é menor que 64KB.
- [x] **Given** um `ChainAST` construído **when** `ast.ToChatMessages()` é invocado **then** a sequência resultante de mensagens respeita perfeitamente a alternância exigida pelos provedores (`Human` -> `AI [tool_calls]` -> `Tool [responses]` -> `AI`).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Histórico vazio | `[]` | Retorna `ChainAST` vazio sem erro |
| Apenas mensagem de System | `[SystemMessage]` | Seção única sem body pairs |
| Múltiplas tool calls em paralelo | 1 AIMessage com 4 calls e 4 respostas em ordem variada | AST correlaciona cada resposta ao ID exato |
| Tool response sem tool call | ToolMessage solta | Descartada ou encapsulada sem crash |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** Revisar `AgentChatService` e classes de mensagem em `KnowledgeHub.McpEngine`.
- [x] **T2 — AST Model:** Implementar `ChainAst.cs` definindo nós de seção, headers, pares de corpo e enums de tipo.
- [x] **T3 — Parser & Repair:** Implementar `ChainAstParser.cs` e `ChainAstRepair.cs` com testes de regressão para pares órfãos.
- [x] **T4 — Compactor:** Implementar `ChainCompactor.cs` com lógica de truncamento seguro e sumarização estruturada.
- [x] **T5 — Integration:** Conectar o compactor ao pipeline de envio do `AgentChatService`.
- [x] **T6 — Validation:** Executar suite completa de unit tests e aferir estabilidade de chamadas de agente.

## 8. Organization Guardrails

- **Branches:** Nunca commitar direto em `main`. Usar branch de feature no padrão exigido.
- **Segurança:** Nunca logar tokens ou chaves de API durante a depuração do AST.
- **Compatibilidade:** O AST deve converter bidirecionalmente entre `IReadOnlyList<ChatMessage>` padrão do `.NET` (`Microsoft.Extensions.AI`) sem perder dados semânticos.

## 9. Definition of Done

- [x] Todos os requisitos (RF-001 a RF-004) implementados.
- [x] Critérios de aceitação validados com testes unitários cobrindo cenários normais e edge cases de tool calls quebradas.
- [x] Nenhuma quebra de build ou regressão nas ferramentas existentes de chat.
- [x] Logs livres de dados sensíveis e código alinhado às diretrizes de C# 12 / .NET 10.
