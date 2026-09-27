# SPEC-20260926-split-settings-tools

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `split-settings-tools` |
| Type | `Refactor` |
| Stack | `.NET 10 / Native MCP Engine / C#` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/opencode-20260926-split-settings-tools` |
| Ticket | `#settings-tools-split` |
| Status | `Approved` |

## 1. User Story

**As a** agente ou cliente conectado via MCP autenticado por API key  
**I want** chamar uma ferramenta dedicada `set_chat_settings` para configurar o modelo e endpoint de chat (LLM), e usar `set_api_key_settings` exclusivamente para gerenciar chaves de integrações upstream (Firecrawl, DeepWiki, Tavily, Context7)  
**So that** os esquemas de entrada de cada ferramenta fiquem coesos, claros e especializados, evitando parâmetros ambíguos ou opcionais cruzados na mesma ferramenta.

**Problem context:**  
Atualmente, a ferramenta `set_api_key_settings` acumula duas responsabilidades distintas: quando `provider == "chat"`, ela recebe `endpoint`, `model` e `apiKey`; quando `provider` é uma integração (`firecrawl`, `deepwiki`, `tavily`, `context7`), ela recebe apenas `apiKey`. Isso força o esquema JSON a expor propriedades irrelevantes dependendo do valor de `provider` (ex.: `endpoint` e `model` só fazem sentido para chat). A separação em duas ferramentas especializadas (`set_chat_settings` para chat LLM e `set_api_key_settings` para chaves de integrações) simplifica a validação de parâmetros, melhora o discoverability para agentes LLM e reflete o princípio de responsabilidade única.

## 2. Scope

**In scope:**
- **Refatoração da tool `set_api_key_settings`:**
  - Manter exclusivamente a configuração de chaves de integração upstream (`firecrawl`, `deepwiki`, `tavily`, `context7`).
  - Remover o valor `"chat"` do enum `provider`.
  - Remover os campos `endpoint` e `model` do JSON schema de entrada.
  - Atualizar descrição e exemplos para focar em chaves de integração.
  - Se `apiKey` for informada e não-vazia, salva o segredo da integração para a chave chamadora; se for nula ou vazia, remove o override (fallback para o global).
- **Criação da nova tool `set_chat_settings`:**
  - Registrar no catálogo de ferramentas MCP (`SettingsToolsProvider`).
  - Parâmetros: `endpoint` (string/null), `model` (string/null), `apiKey` (string/null).
  - Execução restrita a sessões autenticadas via Bearer `aft_*` (`authMethod == "apikey"`).
  - Atualizar os settings de chat via `IApiKeyChatSettingsService.SaveAsync` e retornar resumo com os campos em override.
  - Marcar como `ReadOnly = false`, `IdempotentHint = true` e adicionar à lista `WriteTools`.
- **Atualização de Testes e Contratos:**
  - Atualizar o catálogo fixo em `McpContractTests.cs` com os schemas de ambas as ferramentas.
  - Adicionar testes de unidade e integração para `set_chat_settings` e `set_api_key_settings`.

**Out of scope:**
- Alterações nos endpoints REST `/api/api-keys/{id}/settings/*` existentes (a API de gerenciamento REST já é separada em `/settings/chat` e `/settings/integrations/{provider}`).
- Alterações no modelo de dados ou migrações de banco (a persistência subjacente via `IApiKeyChatSettingsService` já suporta ambos os fluxos).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Mcp/ToolProviders/SettingsToolsProvider.cs`: Divisão da tool em `set_api_key_settings` (integrações) e `set_chat_settings` (chat).
- `tests/KnowledgeHub.Tests.Integration/McpContractTests.cs`: Atualização do catálogo fixo de schemas e conjunto `WriteTools`.
- `tests/KnowledgeHub.Tests.Unit/Server/PerKeyIntegrationSecretTests.cs`: Validação dos testes unitários da ferramenta refatorada.
- `tests/KnowledgeHub.Tests.Integration/PerKeyIntegrationApiTests.cs`: Teste de integração chamando as duas ferramentas via `/api/tools/*`.

**Files to read before implementing:**
- `CLAUDE.md`
- `src/KnowledgeHub.Server/Mcp/ToolProviders/SettingsToolsProvider.cs`
- `src/KnowledgeHub.Server/Settings/IApiKeyChatSettingsService.cs`
- `tests/KnowledgeHub.Tests.Integration/McpContractTests.cs`

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Mcp/ToolProviders/SettingsToolsProvider.cs
tests/KnowledgeHub.Tests.Integration/McpContractTests.cs
tests/KnowledgeHub.Tests.Unit/Server/PerKeyIntegrationSecretTests.cs
tests/KnowledgeHub.Tests.Integration/PerKeyIntegrationApiTests.cs
```

## 4. Requirements

### RF-001: Tool `set_api_key_settings` — Exclusiva para Chaves de Integrações
- **Description:** A ferramenta `set_api_key_settings` deve configurar exclusivamente chaves de API para integrações upstream (`firecrawl`, `deepwiki`, `tavily`, `context7`).
- **Input Schema:**
  ```json
  {
    "type": "object",
    "properties": {
      "provider": {
        "type": "string",
        "enum": ["firecrawl", "deepwiki", "tavily", "context7"],
        "description": "Which integration provider to configure"
      },
      "apiKey": {
        "type": ["string", "null"],
        "description": "API key override (null or empty = inherit from global)"
      }
    },
    "required": ["provider"],
    "examples": [
      {"provider": "deepwiki", "apiKey": "dw-secret-key"},
      {"provider": "firecrawl", "apiKey": null}
    ]
  }
  ```
- **Rules:**
  - Apenas sessões autenticadas por API Key (`authMethod == "apikey"`) podem invocar a ferramenta; caso contrário retorna `McpErrorCode.InvalidParams`.
  - Se `apiKey` for fornecida e não vazia, invoca `service.SaveIntegrationKeyAsync(keyId, provider, apiKey, ct)`.
  - Se `apiKey` for `null` ou vazia, invoca `service.RemoveIntegrationKeyAsync(keyId, provider, ct)`.
  - Se `provider` não estiver no enum permitido, retorna erro `InvalidParams`.
  - `ReadOnly = false`, `IdempotentHint = true`.

### RF-002: Tool `set_chat_settings` — Exclusiva para Configurações de Chat LLM
- **Description:** Criar a ferramenta `set_chat_settings` especializada na configuração de endpoint, modelo e API key do provedor de chat para a chave atual.
- **Input Schema:**
  ```json
  {
    "type": "object",
    "properties": {
      "endpoint": {
        "type": ["string", "null"],
        "description": "OpenAI-compatible base URL (null = inherit from global)"
      },
      "model": {
        "type": ["string", "null"],
        "description": "Model name (null = inherit from global)"
      },
      "apiKey": {
        "type": ["string", "null"],
        "description": "Chat provider API key override (null = inherit from global)"
      }
    },
    "examples": [
      {"endpoint": "http://localhost:11434", "model": "llama3", "apiKey": null}
    ]
  }
  ```
- **Rules:**
  - Apenas sessões autenticadas por API Key (`authMethod == "apikey"`) podem invocar a ferramenta; caso contrário retorna `McpErrorCode.InvalidParams`.
  - Invoca `service.SaveAsync(keyId, endpoint, model, apiKey, ct)`.
  - Em seguida, obtém a descrição atualizada via `service.DescribeAsync(keyId, ct)` e retorna mensagem textual descritiva com o status dos campos herdados/sobrescritos.
  - `ReadOnly = false`, `IdempotentHint = true`.

### RF-003: Alinhamento de Contrato e Segurança
- **Description:** O catálogo de ferramentas MCP exposto e os testes de conformidade contratual devem refletir a nova tool e o schema atualizado.
- **Rules:**
  - `McpContractTests.cs` deve conter a definição do schema exato de `set_api_key_settings` e `set_chat_settings`.
  - Ambas as ferramentas devem constar em `WriteTools` no `McpContractTests.cs`.
  - As ferramentas nunca devem expor o segredo completo nas mensagens de retorno de texto nem em logs.

## 5. API Contract

### MCP Tools:

#### `set_api_key_settings`
- **Method:** `tools/call` ou `POST /api/tools/set_api_key_settings`
- **Auth:** `Bearer aft_*`
- **Parameters:**
  ```json
  {
    "provider": "firecrawl",
    "apiKey": "fc-test-key"
  }
  ```
- **Response:**
  ```json
  {
    "content": [
      {
        "type": "text",
        "text": "firecrawl API key saved for API key '550e8400-e29b-41d4-a716-446655440000'."
      }
    ]
  }
  ```

#### `set_chat_settings`
- **Method:** `tools/call` ou `POST /api/tools/set_chat_settings`
- **Auth:** `Bearer aft_*`
- **Parameters:**
  ```json
  {
    "endpoint": "https://api.openai.com",
    "model": "gpt-4o",
    "apiKey": "sk-test"
  }
  ```
- **Response:**
  ```json
  {
    "content": [
      {
        "type": "text",
        "text": "Chat settings updated for API key '550e8400-e29b-41d4-a716-446655440000'.\nProvider: openai\nEndpoint: https://api.openai.com\nModel: gpt-4o\nHas override: True\nOverride fields: Endpoint, Model, ApiKey"
      }
    ]
  }
  ```

## 6. Acceptance Criteria

- [ ] **Given** uma sessão autenticada com API Key `aft_*` **when** invoca `set_api_key_settings` com `provider="deepwiki"` e `apiKey="dw-key"` **then** a chave de integração é salva com sucesso para a API key chamadora.
- [ ] **Given** uma sessão autenticada com API Key `aft_*` **when** invoca `set_api_key_settings` com `provider="deepwiki"` e `apiKey=null` **then** o override de chave de integração é removido, voltando a herdar o valor global.
- [ ] **Given** uma sessão autenticada com API Key `aft_*` **when** invoca `set_api_key_settings` passando `provider="chat"` **then** a chamada falha com erro de validação de parâmetros (`McpErrorCode.InvalidParams`), pois chat não é mais aceito nessa ferramenta.
- [ ] **Given** uma sessão autenticada com API Key `aft_*` **when** invoca `set_chat_settings` com `endpoint="http://localhost:11434"` e `model="llama3"` **then** as configurações de chat são salvas para a API key chamadora e o retorno detalha os campos configurados.
- [ ] **Given** uma sessão sem autenticação por API key (ex.: anônimo ou cookie de admin) **when** invoca `set_chat_settings` ou `set_api_key_settings` **then** a chamada é rejeitada informando que a ferramenta é exclusiva para sessões autenticadas por API key.
- [ ] **Given** a suíte de testes de integração **when** executado `dotnet test` **then** `McpContractTests`, `PerKeyIntegrationSecretTests` e `PerKeyIntegrationApiTests` passam 100% verdes.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Chamada para `set_api_key_settings` com provider inválido | `provider: "unknown"` | Erro `InvalidParams` ("Unknown provider 'unknown'"). |
| Chamada para `set_chat_settings` com todos os campos nulos | `{ endpoint: null, model: null, apiKey: null }` | Salva remoção dos overrides de chat, herdando todos os valores do padrão global. |
| Invocação de `tools/list` | Listagem de catálogo | Ambas as ferramentas aparecem com seus respectivos schemas especializados. |

## 7. Task Plan (agent execution)

- [ ] **T1 — Atualização de `SettingsToolsProvider.cs`:**
  - Redefinir `SetApiKeySettingsSchema` removendo `chat`, `endpoint` e `model`.
  - Criar `SetChatSettingsSchema` com `endpoint`, `model` e `apiKey`.
  - Separar a lógica do handler: `set_api_key_settings` atende apenas integrações; nova tool `set_chat_settings` atende o chat.
- [ ] **T2 — Atualização de Testes Contratuais (`McpContractTests.cs`):**
  - Atualizar o schema fixado de `set_api_key_settings`.
  - Adicionar o schema fixado de `set_chat_settings`.
  - Adicionar `"set_chat_settings"` em `WriteTools`.
- [ ] **T3 — Atualização de Testes Unitários e de Integração:**
  - Atualizar `PerKeyIntegrationSecretTests.cs` e `PerKeyIntegrationApiTests.cs`.
  - Adicionar testes cobrindo `set_chat_settings` (salvar endpoint/model, herança de campos e rejeição para chamadas não-apikey).
- [ ] **T4 — Validação e DoD:**
  - Rodar `dotnet build` e `dotnet test`.
  - Verificar que não há warnings de compilação ou regressões de contrato.

**7.1 Validation strategy by type/stack**

| Type / Stack | Required evidence |
|---|---|
| **.NET / Native MCP** | `dotnet build KnowledgeHub.slnx` 0 erros; `dotnet test` 100% verde; validação dos schemas JSON em `McpContractTests`. |

## 8. Organization Guardrails

- Nunca commitar diretamente em `main`, `master` ou `develop`. Trabalhar na branch `feature/opencode-20260926-split-settings-tools`.
- Proibido expor API keys e segredos em texto pleno em logs ou mensagens de resposta.
- Não alterar `.github/workflows/`.

## 9. Definition of Done

- [ ] Tool `set_api_key_settings` refatorada e validada para integrações apenas.
- [ ] Tool `set_chat_settings` criada e registrada no catálogo.
- [ ] `McpContractTests` atualizado e passando.
- [ ] Testes unitários e de integração cobrindo ambas as tools passando.
- [ ] `dotnet build` e `dotnet test` 100% verdes.

## Open Questions / Pending Ambiguity

*Nenhuma pendência em aberto. Requisitos claros e bem delimitados.*
