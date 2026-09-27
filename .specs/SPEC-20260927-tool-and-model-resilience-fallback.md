# SPEC-20260927-tool-and-model-resilience-fallback

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `tool-and-model-resilience-fallback` |
| Type | `Feature` |
| Stack | `.NET 10 / Polly / Microsoft.Extensions.AI / C# 12 / Serilog` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `[A DEFINIR]` |
| Status | `Draft` |

## 1. User Story

**As a** operador ou agente utilizando o Knowledge MCP Hub para RAG e execução de ferramentas
**I want** uma política explícita e configurável de fallback de resiliência para falhas em ferramentas e provedores LLM/Embedding
**So that** indisponibilidades transitórias (erros 429, 503, timeouts ou incapacidade de gerar chamadas de ferramentas) acionem alternativas equivalentes de forma auditada sem interromper o fluxo de trabalho.

**Problem context:**
Atualmente, se o provedor primário configurado para síntese de chat ou embeddings (ex.: OpenAI) atinge rate limit (HTTP 429), sofre lentidão extrema ou indisponibilidade (HTTP 503), toda a operação no Knowledge MCP Hub falha imediatamente. Da mesma forma, se uma ferramenta externa de busca (como Tavily ou Firecrawl) expira sua quota ou falha, o agente não tem mecanismo para alternar para um provedor secundário de mesma categoria (ex.: DuckDuckGo ou busca local na base de conhecimento).
Inspirado na proposta de arquitetura do **PentAGI** (`examples/proposals/tool_model_fallback.md`), esta SPEC formaliza uma camada de resiliência baseada em políticas auditadas (`disabled`, `observe`, `enforce`), com taxonomia clara de capacidades, proteção contra loops infinitos de fallback, e registros estruturados de cada transição para total transparência operacional.

## 2. Scope

**In scope:**
- **Motor de Políticas de Fallback (`ResilienceFallbackPolicy`):**
  - Três modos de operação configuráveis:
    - `Disabled`: falhas retornam imediatamente o erro original sem tentar alternativas.
    - `Observe`: captura e classifica a falha, computa qual candidato seria acionado, registra logs estruturados e métricas de simulação, mas preserva a falha original.
    - `Enforce`: executa a transição para o provedor/ferramenta candidato respeitando orçamento de tentativas e regras de segurança.
- **Resiliência de Modelos LLM e Embeddings:**
  - Cascata configurada de provedores: Exemplo: `Chat: Primary = OpenAI (gpt-4o-mini) -> Secondary = Ollama (qwen2.5) -> Tertiary = Anthropic`.
  - Critérios de gatilho para fallback de LLM: HTTP 429 (Rate Limit), HTTP 503/504 (Gateway Timeout / Service Unavailable), Timeouts de socket, e falha repetida de geração de Tool Calls válidas.
  - Orçamento de tentativas (`MaxFallbackAttempts = 2` por requisição).
- **Resiliência de Ferramentas MCP e Motores de Busca:**
  - Mapeamento de capacidades equivalentes (`CapabilityTaxonomy`):
    - `WebSearch`: `Tavily` ↔ `Firecrawl` ↔ `DuckDuckGo`.
    - `DeepDocLookup`: `DeepWiki` ↔ `Context7` ↔ `InternalFts`.
  - Fallback restrito a ferramentas de mesma permissão de segurança e mesmo escopo de dados.
- **Auditoria e Rastreabilidade:**
  - Cada fallback executado anexa metadados na resposta: `fallbackTriggered: true`, `originalProvider`, `fallbackProvider`, `reason`, `attemptNumber`.
  - Emissão de logs estruturados Serilog e métricas OpenTelemetry (`rag.fallback.count`, `rag.fallback.latency`).
- **Configuração no `appsettings.json` e UI de Settings:**
  - Seção `Resilience:Fallback` com controle por categoria.

**Out of scope:**
- Instalação dinâmica ou download de novos binários/executáveis em tempo de execução.
- Fallback entre tenants diferentes ou bypass de autenticação de API keys.
- Filas de reprocessamento em background invisíveis (o fallback ocorre no ciclo da requisição síncrona).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Resilience/`:
  - `IFallbackPolicyEngine.cs`: interface do motor de decisões de fallback.
  - `FallbackPolicyEngine.cs`: implementação com avaliação de modo (`Disabled`, `Observe`, `Enforce`).
  - `ModelFallbackService.cs`: decorador sobre `IChatClient` e `IEmbeddingProvider`.
  - `ToolFallbackService.cs`: resolução de ferramentas alternativas baseada em taxonomia de capacidade.
  - `FallbackOptions.cs`: classes de configuração.
- `src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs`: registro dos decoradores de resiliência.
- `src/KnowledgeHub.McpEngine/Dispatcher/JsonRpcDispatcher.cs`: interceptação de erros de tool call para avaliação de fallback.
- `tests/KnowledgeHub.Tests.Unit/Resilience/`: testes unitários de fallback de modelo e ferramentas.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Embeddings/EmbeddingProviderResolver.cs`
- `src/KnowledgeHub.Server/Services/LlmAnswerSynthesisService.cs`
- `src/KnowledgeHub.McpEngine/Dispatcher/JsonRpcDispatcher.cs`
- `src/KnowledgeHub.Server/Configuration/`

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Resilience/FallbackMode.cs                     (create)
src/KnowledgeHub.Server/Resilience/FallbackOptions.cs                  (create)
src/KnowledgeHub.Server/Resilience/IFallbackPolicyEngine.cs           (create)
src/KnowledgeHub.Server/Resilience/FallbackPolicyEngine.cs           (create)
src/KnowledgeHub.Server/Resilience/ResilientChatClientDecorator.cs    (create)
src/KnowledgeHub.Server/Resilience/ToolCapabilityRegistry.cs          (create)
src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs    (modify)
src/KnowledgeHub.Server/appsettings.json                              (modify)
src/KnowledgeHub.Client/Pages/Settings.razor                          (modify — aba de Resiliência)
tests/KnowledgeHub.Tests.Unit/Resilience/FallbackPolicyEngineTests.cs (create)
tests/KnowledgeHub.Tests.Unit/Resilience/ResilientChatClientTests.cs  (create)
```

## 4. Requirements

### RF-001: Modos de Política de Fallback
- **Description:** O sistema deve implementar os modos `Disabled`, `Observe` e `Enforce` configuráveis globalmente ou por subsistema (Chat, Embeddings, Tools).
- **Rules:**
  - Em `Disabled`: exceções são repassadas diretamente ao chamador.
  - Em `Observe`: a exceção é interceptada, uma entrada de log com nível `Warning` detalha o candidato que teria sido acionado, e a exceção é relançada.
  - Em `Enforce`: a falha elegível aciona o próximo provedor/ferramenta da lista de candidatos até o limite de `MaxFallbackAttempts`.
- **Input → Output:** Erro 429 no provedor primário → Execução bem-sucedida no provedor secundário com log de transição.

### RF-002: Decorador Resiliente de Chat e Embeddings
- **Description:** As chamadas de LLM para síntese de resposta e geração de embeddings devem ser envolvidas por um decorador com suporte a circuit-breaker e fallback.
- **Rules:**
  - Gatilhos válidos: `HttpRequestException` com códigos 429, 502, 503, 504 ou `TimeoutException`.
  - Exceções de cliente como 400 (Bad Request), 401 (Unauthorized) ou 403 (Forbidden) NUNCA acionam fallback de provedor (indicam erro de configuração/autenticação definitivo).
  - O decorador tenta o provedor primário; falhando por motivo elegível, aciona o próximo provedor registrado no pool secundário.
- **Input → Output:** OpenAI falha com 503 → Decorador invoca Ollama local transparente e conclui síntese.

### RF-003: Taxonomia e Mapeamento de Ferramentas Alternativas
- **Description:** O `ToolCapabilityRegistry` deve classificar ferramentas em grupos de capacidade compatíveis para substituição transparente.
- **Rules:**
  - Uma ferramenta só pode substituir outra se pertencer à mesma categoria (ex.: `WebSearch`).
  - Se a ferramenta substituta exigir chaves ou parâmetros ausentes, o fallback é considerado indisponível e a requisição falha com clareza.
  - O resultado entregue ao agente contém a indicação de qual ferramenta efetivamente processou a requisição.

### RF-004: Prevenção de Cascata Infinita e Orçamento de Tempo
- **Description:** O processo de fallback deve ter limites estritos de tempo e iterações.
- **Rules:**
  - `MaxFallbackAttempts` padrão = 2. Nenhuma requisição tentará mais de 2 alternativas.
  - O timeout total da requisição original (`CancellationToken`) rege todo o processo e não é reiniciado entre as tentativas.

## 5. API Contract (if applicable)

Configuração no `appsettings.json`:
```json
{
  "Resilience": {
    "Fallback": {
      "Mode": "Enforce",
      "MaxFallbackAttempts": 2,
      "Chat": {
        "Primary": "OpenAI",
        "Fallbacks": ["Ollama", "Anthropic"]
      },
      "Search": {
        "Primary": "Tavily",
        "Fallbacks": ["Firecrawl", "InternalFts"]
      }
    }
  }
}
```

Resposta de Tool com auditoria de fallback:
```json
{
  "content": "Resultado da pesquisa obtido com sucesso...",
  "metadata": {
    "executedTool": "firecrawl",
    "fallbackFrom": "tavily",
    "fallbackReason": "HttpError_429_RateLimit",
    "attemptNumber": 2
  }
}
```

## 6. Acceptance Criteria

- [ ] **Given** a configuração com `Mode = Enforce` e provedor secundário Ollama ativo **when** OpenAI retorna status 429 Too Many Requests **then** a chamada de chat é roteada com sucesso para Ollama e a resposta contém metadados de fallback.
- [ ] **Given** a configuração com `Mode = Observe` **when** o provedor primário falha com status 503 **then** um log estruturado de aviso é registrado indicando o candidato sugerido e a exceção 503 é propagada.
- [ ] **Given** uma falha de autenticação (HTTP 401) no provedor primário **when** a chamada é realizada **then** nenhum fallback é tentado e o erro 401 é retornado imediatamente.
- [ ] **Given** um cancelamento de requisição pelo cliente via `CancellationToken` **when** o fallback está em andamento **then** a execução aborta imediatamente sem executar provedores terciários.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Todos os provedores em fallback falham | Primário 503, Secundário 503 | Lança `AggregateException` com histórico das tentativas |
| Provedor secundário não possui API Key configurada | Falha no primário, secundário sem secret | Pula secundário inválido e tenta próximo ou falha graciosamente |
| Timeout curto na requisição | CancellationToken expira durante primeira tentativa | Operação abortada com `OperationCanceledException` |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** Avaliar clientes de chat e injeção de dependências em `KnowledgeHub.Server`.
- [ ] **T2 — Models & Options:** Implementar `FallbackOptions.cs` e `FallbackMode.cs`.
- [ ] **T3 — Policy Engine:** Implementar `FallbackPolicyEngine.cs` com classificação de erros e verificação de orçamentos.
- [ ] **T4 — Chat Decorator:** Implementar `ResilientChatClientDecorator.cs` integrando `IChatClient` do .NET 10.
- [ ] **T5 — Tool Registry:** Implementar taxonomia de ferramentas em `ToolCapabilityRegistry.cs`.
- [ ] **T6 — Tests:** Escrever testes cobrindo transições de modo (`Disabled`, `Observe`, `Enforce`), erros 429 e limites de timeout.

## 8. Organization Guardrails

- **Segurança:** Segredos e chaves de API nunca devem ser expostos nos logs de auditoria de fallback.
- **Transparência:** O usuário ou agente chamador sempre deve ser notificado nos metadados quando um fallback for executado.

## 9. Definition of Done

- [ ] Todos os requisitos (RF-001 a RF-004) implementados e verificados.
- [ ] Testes unitários com simulação de erros HTTP (429, 503, 401) passando com 100% de sucesso.
- [ ] Nenhuma regressão no comportamento padrão quando `Mode = Disabled`.
- [ ] Métricas e logs Serilog estruturados sem vazamento de PII.
