# SPEC-20260927-voyage-and-cohere-embeddings

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `voyage-and-cohere-embeddings` |
| Type | `Feature` |
| Stack | `.NET 10 / HttpClient / System.Text.Json / Voyage AI REST / Cohere Embed v3` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-voyage-and-cohere-embeddings` |
| Ticket | `#280` |
| Status | `Done` |


## 1. User Story

**As a** arquiteto de IA ou desenvolvedor utilizando o Knowledge MCP Hub
**I want** utilizar Voyage AI e Cohere Embed v3 como provedores de embeddings nativos
**So that** eu tenha acesso a embeddings de estado-da-arte no benchmark MTEB, com suporte nativo a compressão assimétrica (`search_query` vs `search_document`) e modelos especializados em código técnico (`voyage-code-3`).

**Problem context:**
Atualmente, o Knowledge MCP Hub suporta OpenAI, Ollama e ONNX Runtime local (`all-MiniLM-L6-v2`). No entanto, benchmarks públicos do MTEB demonstram que modelos especializados como **Voyage AI** (`voyage-3`, `voyage-code-3`) e **Cohere** (`embed-multilingual-v3.0`, `embed-english-v3.0`) superam expressivamente os embeddings genéricos em tarefas de recuperação técnica, repositórios de código e documentos corporativos densos.
Inspirado na matriz de provedores do **Weaviate Verba** (`goldenverba/components/embedding/VoyageAIEmbedder.py` e `CohereEmbedder.py`), esta SPEC adiciona `VoyageAiEmbeddingProvider` e `CohereEmbeddingProvider` ao ecossistema do Knowledge MCP Hub, integrando validação de dimensões e prefixação nativa de tipos de entrada.

## 2. Scope

**In scope:**
- Implementação de `VoyageAiEmbeddingProvider` (`IEmbeddingProvider`):
  - Modelos suportados: `voyage-3` (dim: 1024), `voyage-3-large` (dim: 1536), `voyage-code-3` (dim: 1536).
  - Parâmetros: `input_type` (`document` durante indexação, `query` durante busca).
  - Batching eficiente: até 128 textos por requisição com truncamento seguro de bytes.
- Implementação de `CohereEmbeddingProvider` (`IEmbeddingProvider`):
  - Modelos suportados: `embed-multilingual-v3.0` (dim: 1024), `embed-english-v3.0` (dim: 1024), `embed-multilingual-light-v3.0` (dim: 384).
  - Parâmetros: `input_type` (`search_document` durante indexação, `search_query` durante busca).
- Integração no `EmbeddingProviderFactory` e `EmbeddingProviderResolver`.
- Validação automática de compatibilidade de dimensão (`EmbeddingCompatibilityCheck`).
- Configuração em `appsettings.json` sob a chave `Embeddings:Voyage` e `Embeddings:Cohere`.
- UI de Configuração: seleção dos novos provedores na aba de Embeddings em `Settings.razor`.

**Out of scope:**
- Provedores auto-hospedados sem API REST compatível.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Embeddings/`:
  - `VoyageAiEmbeddingProvider.cs`: provedor Voyage AI.
  - `CohereEmbeddingProvider.cs`: provedor Cohere.
  - `EmbeddingProviderFactory.cs`: suporte às novas opções no switch de fábrica.
  - `EmbeddingOptions.cs`: classes de configuração dos novos provedores.
- `src/KnowledgeHub.Client/Pages/Settings.razor`: opções na UI de configurações de embeddings.
- `tests/KnowledgeHub.Tests.Unit/Embeddings/`: testes com respostas simuladas das APIs.

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Embeddings/VoyageAiEmbeddingProvider.cs       (create)
src/KnowledgeHub.Server/Embeddings/CohereEmbeddingProvider.cs         (create)
src/KnowledgeHub.Server/Embeddings/EmbeddingProviderFactory.cs        (modify)
src/KnowledgeHub.Server/Embeddings/EmbeddingOptions.cs                (modify)
src/KnowledgeHub.Client/Pages/Settings.razor                          (modify)
tests/KnowledgeHub.Tests.Unit/Embeddings/VoyageEmbeddingTests.cs      (create)
tests/KnowledgeHub.Tests.Unit/Embeddings/CohereEmbeddingTests.cs      (create)
```

## 4. Requirements

### RF-001: Integração REST com Voyage AI
- **Description:** Enviar requisições POST para `https://api.voyageai.com/v1/embeddings` com autenticação Bearer.
- **Rules:**
  - O parâmetro `input_type` deve ser `"query"` quando chamado por `EmbedQueryAsync` e `"document"` em `EmbedBatchAsync`.
  - Tratamento de rate limit (HTTP 429) com retry exponencial de até 3 tentativas.
- **Input → Output:** Lista de strings → Matriz de floats 1024 ou 1536 dimensões.

### RF-002: Integração REST com Cohere Embed v3
- **Description:** Enviar requisições POST para `https://api.cohere.com/v2/embed` com `model` e `input_type`.
- **Rules:**
  - `input_type = "search_query"` em queries e `"search_document"` em documentos.
  - Suporte a modelos multilíngues com dimensão 1024.
- **Input → Output:** Strings textuais → Vetores float normalizados.

### RF-003: Validação de Dimensões e Migração de VectorStore
- **Description:** O `EmbeddingCompatibilityCheck` deve verificar se a dimensão do modelo selecionado corresponde à tabela vetorial existente antes de permitir a alteração.
- **Rules:**
  - Se o banco estiver configurado com dimensão 384 (ONNX) e o usuário selecionar `voyage-3` (1024), o sistema exige confirmação de re-indexação ou bloqueia a alteração com erro explicativo.

## 5. API Contract (if applicable)

Configuração em `appsettings.json`:
```json
{
  "Embeddings": {
    "Provider": "Voyage",
    "Voyage": {
      "ApiKey": "pa-...",
      "Model": "voyage-3",
      "Dimensions": 1024
    },
    "Cohere": {
      "ApiKey": "...",
      "Model": "embed-multilingual-v3.0",
      "Dimensions": 1024
    }
  }
}
```

## 6. Acceptance Criteria

- [x] **Given** `VoyageAiEmbeddingProvider` configurado **when** `EmbedQueryAsync` é chamado **then** o payload enviado contém `input_type: "query"` e retorna o vetor numérico com a dimensão declarada.
- [x] **Given** `CohereEmbeddingProvider` configurado **when** `EmbedBatchAsync` é chamado com 10 documentos **then** o payload enviado contém `input_type: "search_document"` e 10 vetores são retornados.
- [x] **Given** uma divergência de dimensão entre o provedor e a tabela do banco vetorial **when** o sistema inicia **then** o `EmbeddingCompatibilityCheck` (compartilhado, SPEC-20260914) emite log de erro/aviso alto e não-fatal com mensagem de orientação de re-indexação — consistente com ONNX/OpenAI, sem bloquear o startup nem mutar dados.

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** Analisar `IEmbeddingProvider.cs` e `OpenAiEmbeddingProvider.cs`.
- [x] **T2 — Options:** Adicionar configurações de Voyage e Cohere em `EmbeddingOptions.cs`.
- [x] **T3 — Voyage Provider:** Implementar `VoyageAiEmbeddingProvider.cs`.
- [x] **T4 — Cohere Provider:** Implementar `CohereEmbeddingProvider.cs`.
- [x] **T5 — Factory:** Atualizar `EmbeddingProviderFactory.cs` e `EmbeddingProviderResolver.cs`.
- [x] **T6 — Unit Tests:** Validar serialização de payloads, cabeçalhos de autenticação e tratamento de rate limits.

## 8. Organization Guardrails

- **Segurança:** As chaves de API nunca devem ser hardcoded nem registradas nos logs em texto claro.
- **Controle de Dimensão:** A dimensão vetorial deve ser constante ao longo de todo o ciclo de vida do índice vetorial configurado.

## 9. Definition of Done

- [x] Todos os requisitos (RF-001 a RF-003) implementados.
- [x] Testes unitários com simulação de respostas JSON das APIs Voyage e Cohere passando 100%.
- [x] Interface Blazor WASM atualizada permitindo a seleção dos novos provedores.
