# SPEC-20260927-mcp-dynamic-rag-action-bridge

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `mcp-dynamic-rag-action-bridge` |
| Type | `Feature` |
| Stack | `.NET 10 / C# 12 / KnowledgeHub.McpEngine / Minimal APIs / Agentic RAG` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `[A DEFINIR]` |
| Status | `Draft` |

## 1. User Story

**As a** arquiteto de sistemas ou agente autônomo de IA operando via Knowledge MCP Hub
**I want** que o motor de RAG e o catálogo de ferramentas MCP operem em um pipeline simbiótico e unificado (Action-Augmented RAG)
**So that** quando documentos de conhecimento estáticos referenciarem sistemas dinâmicos ou procedimentos operacionais (ex.: "para consultar o estoque atual, consulte a tabela SQL X" ou "para verificar a fatura, chame a API Y"), o agente execute automaticamente as ferramentas MCP correspondentes em tempo real, combinando dados estáticos e dinâmicos em uma resposta factual definitiva.

**Problem context:**
Tradicionalmente, sistemas RAG e plataformas de execução de ferramentas (MCP) funcionam como silos isolados:
- O **RAG** injeta conhecimento estático recuperado de documentos, notas e PDFs no prompt da IA, mas é incapaz de atuar sobre sistemas vivos ou recuperar informações que mudam a cada segundo (ex.: saldo em conta, status de deploy, métricas de monitoramento em tempo real).
- O **MCP** permite que agentes invoquem ferramentas dinâmicas em tempo real (consultas SQL, chamadas de API REST, leitura de repositórios Git, execução de testes), mas não possui a inteligência de recuperação semântica sobre vastas bibliotecas de manuais e documentações conceituais.
No **Knowledge MCP Hub**, essas duas tecnologias coabitam no mesmo processo Kestrel. Esta SPEC estabelece a ponte de orquestração **Action-Augmented RAG** (`McpDynamicRagActionBridge`):
1. **Ancoragem de Ações em Documentos:** Documentos indexados no RAG podem declarar blocos de ferramentas sugeridas ou esquemas de ação MCP (`ToolReferenceAnnotation`).
2. **Loop Unificado no `agent_chat`:** Durante a geração de respostas, se os chunks recuperados apontarem para uma ferramenta MCP registrada (ex.: conector SQL, conector Git, API REST, ou ferramenta de monitoramento), o motor de raciocínio reativo dispara a chamada da ferramenta em tempo real, anexa o retorno dinâmico ao contexto da resposta e cita tanto o documento estático quanto o dado em tempo real.

## 2. Scope

**In scope:**
- **Anotações de Ação Semântica (`ToolActionAnnotation`):**
  - Reconhecimento automático nos chunks de padrões que invocam tools: marcadores explícitos (`<!-- mcp-tool: sql_query target="db_prod" -->`) ou inferidos pelo modelo no loop do `AgentChatService`.
  - Mapeamento entre fontes de conhecimento cadastradas (ex.: `SourceType.SqlDatabase` ou `SourceType.RestApi`) e as respectivas ferramentas de consulta MCP sob o mesmo identificador de chave.
- **Serviço de Ponte `McpDynamicRagActionBridge`:**
  - Orquestrador que atua durante o `ask_knowledge` e `agent_chat`.
  - Passo 1: Busca híbrida estática (`search_knowledge`) → recupera chunks conceituais e regras de negócio.
  - Passo 2: Análise de intenção dinâmica → se a pergunta do usuário ou os chunks recuperados exigirem dados em tempo real, sugere/dispara a ferramenta MCP apropriada (`ToolCall`).
  - Passo 3: Fusão contextual → combina a citação dos documentos estáticos com a saída estruturada da tool dinâmica em um veredito consolidado.
- **Rastreabilidade e Citações Híbridas:**
  - A resposta final emite dois blocos de transparência:
    - `Document Citations`: `[Doc 1: Política de Descontos v2.md]`
    - `Live MCP Citations`: `[Live Tool: sql_query em db_vendas (executado às 14:32:05Z)]`
- **Configuração no `appsettings.json`:**
  - `Agent:EnableDynamicActionBridge`: bool (default `true`).
  - `Agent:MaxChainedDynamicCalls`: int (default `3`).

**Out of scope:**
- Execução arbitrária de scripts não seguros ou comandos de shell sem validação HITL (Human-in-the-Loop) quando configurada aprovação prévia.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.McpEngine/Bridge/`:
  - `McpDynamicRagActionBridge.cs`: orquestrador de fusão estático+dinâmico.
  - `ToolActionAnnotationDetector.cs`: detector de intenção de tools nos chunks recuperados.
  - `HybridCitationFormatter.cs`: formatador de citações de documentos e tools ao vivo.
- `src/KnowledgeHub.Server/Services/AgentChatService.cs`: integração da ponte no loop reativo.
- `src/KnowledgeHub.McpEngine/Tools/AskKnowledgeTool.cs`: suporte ao parâmetro `enableLiveActions: bool = true`.
- `tests/KnowledgeHub.Tests.Unit/Bridge/`: testes unitários de fusão de conhecimento estático com tools MCP.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Services/LlmAnswerSynthesisService.cs`
- `src/KnowledgeHub.McpEngine/Tools/AskKnowledgeTool.cs`
- `src/KnowledgeHub.Server/Services/AgentChatService.cs`

**Files to create or modify:**
```text
src/KnowledgeHub.McpEngine/Bridge/McpDynamicRagActionBridge.cs        (create)
src/KnowledgeHub.McpEngine/Bridge/ToolActionAnnotationDetector.cs    (create)
src/KnowledgeHub.McpEngine/Bridge/HybridCitationFormatter.cs         (create)
src/KnowledgeHub.Server/Services/AgentChatService.cs                (modify)
src/KnowledgeHub.McpEngine/Tools/AskKnowledgeTool.cs                 (modify)
src/KnowledgeHub.Server/appsettings.json                            (modify)
tests/KnowledgeHub.Tests.Unit/Bridge/McpActionBridgeTests.cs         (create)
```

## 4. Requirements

### RF-001: Detecção e Orquestração de Ações em Tempo Real no RAG
- **Description:** Ao responder a uma pergunta via RAG que envolva estado mutável em tempo real, o sistema deve acionar a ferramenta MCP correspondente antes de fechar a resposta.
- **Rules:**
  - O RAG primeiro recupera o conhecimento semântico que explica o procedimento ou esquema do dado.
  - Em seguida, se houver uma ferramenta MCP disponível mapeada para a fonte (ex.: consulta SQL ou chamada de API REST), ela é executada no mesmo escopo de segurança da chave do usuário.
- **Input → Output:** Pergunta: *"Qual o saldo do cliente 123 e qual a regra de bloqueio?"* → O RAG lê a regra de bloqueio estática nos chunks e executa `sql_query` para buscar o saldo em tempo real, respondendo com ambos.

### RF-002: Citação Híbrida de Fontes Estáticas e Dados Dinâmicos
- **Description:** A resposta gerada deve distinguir claramente o que é proveniente de documentação estática e o que foi retornado por consulta dinâmica viva.
- **Rules:**
  - A formatação de citações deve incluir a tag `[Live Action: tool_name @ timestamp]`.
  - Nenhum dado dinâmico é alucinado pelo modelo: valores numéricos de ferramentas vivas devem corresponder exatamente ao retorno JSON da tool.
- **Input → Output:** Resposta sintetizada com seções distintas de embasamento documental e dados de telemetria ao vivo.

### RF-003: Limite de Chamadas Encadeadas e Proteção Contra Loops
- **Description:** O bridge dinâmico não deve permitir chamadas infinitas de ferramentas dentro do ciclo de uma única resposta.
- **Rules:**
  - Limite rígido `MaxChainedDynamicCalls` (máximo 3).
  - Se a 3ª ferramenta for executada, a resposta é finalizada com os dados acumulados até aquele momento.

## 5. API Contract (if applicable)

Chamada MCP `ask_knowledge`:
```json
{
  "question": "Como funciona o SLA de pedidos e qual o status atual do pedido 9876?",
  "enableLiveActions": true,
  "topK": 5
}
```

Resposta MCP:
```json
{
  "answer": "De acordo com o Manual de Logística (SLA de 48h úteis para entregas expressas), o pedido 9876 encontra-se atualmente com status 'Em Trânsito' após coleta confirmada hoje às 08:30.",
  "documentCitations": [
    { "sourceId": 2, "title": "Manual de Logística.md", "chunkId": "chk_45" }
  ],
  "liveToolExecutions": [
    {
      "toolName": "sql_query",
      "timestamp": "2026-09-27T15:10:02Z",
      "summary": "SELECT status, updated_at FROM orders WHERE id = 9876"
    }
  ]
}
```

## 6. Acceptance Criteria

- [ ] **Given** uma pergunta que solicita uma regra de negócio e um dado em tempo real **when** `ask_knowledge` executa com `enableLiveActions = true` **then** os chunks estáticos são recuperados e a tool MCP é invocada para preencher o dado dinâmico.
- [ ] **Given** `enableLiveActions = false` **when** a mesma pergunta é processada **then** o RAG responde apenas com base nos documentos estáticos sem executar nenhuma tool MCP externa.
- [ ] **Given** uma execução com encadeamento de tools **when** o contador atinge `MaxChainedDynamicCalls = 3` **then** o loop encerra imediatamente e o modelo compõe a resposta final.

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** Revisar `AskKnowledgeTool.cs` e `AgentChatService.cs`.
- [ ] **T2 — Bridge Core:** Implementar `McpDynamicRagActionBridge.cs`.
- [ ] **T3 — Annotation Detector:** Implementar `ToolActionAnnotationDetector.cs`.
- [ ] **T4 — Citations:** Implementar `HybridCitationFormatter.cs`.
- [ ] **T5 — Unit Tests:** Escrever testes de integração simulando consultas RAG com execução de tools mockadas.

## 8. Organization Guardrails

- **Segurança:** O bridge dinâmico herda e respeita estritamente o `CallerScope` da API Key chamadora; se a chave não tiver permissão para a tool invocada, a chamada é bloqueada com erro de autorização limpo.

## 9. Definition of Done

- [ ] Todos os requisitos (RF-001 a RF-003) implementados.
- [ ] Testes unitários com simulação de fluxo híbrido passando 100%.
- [ ] Citações híbridas comprovadas em respostas do `AskKnowledgeTool`.
