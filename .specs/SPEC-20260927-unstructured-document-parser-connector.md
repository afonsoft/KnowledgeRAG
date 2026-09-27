# SPEC-20260927-unstructured-document-parser-connector

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `unstructured-document-parser-connector` |
| Type | `Feature` |
| Stack | `.NET 10 / HttpClient / System.Text.Json / Unstructured.io REST API / Upstage Document Parse` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `[A DEFINIR]` |
| Status | `Draft` |

## 1. User Story

**As a** administrador do Knowledge MCP Hub
**I want** cadastrar diretórios ou arquivos complexos (PDFs escaneados com OCR, apresentações PowerPoint, planilhas Excel e documentos Word) usando o conector `UnstructuredDocument`
**So that** o RAG extraia tabelas limpas, títulos, hierarquias de seções e texto de imagens sem perda de formatação e sem dependências pesadas de bibliotecas nativas de PDF no Kestrel.

**Problem context:**
O conector existente `DocumentFileConnector` atende arquivos em texto claro (`.txt`, `.md`, `.json`, `.csv`), mas falha ao processar arquivos binários corporativos comuns: relatórios em PDF com múltiplas colunas, digitalizações que exigem OCR, apresentações `.pptx` e documentos `.docx`. No ecossistema .NET, incluir drivers nativos locais pesados (como Tesseract e Poppler) aumenta expressivamente o tamanho da imagem Docker e gera vulnerabilidades C++.
Inspirado na arquitetura de conectores do **Weaviate Verba** (`goldenverba/components/reader/UnstructuredReader.py` e `UpstageDocumentParse.py`), esta SPEC implementa o `UnstructuredDocumentConnector` (`SourceType.UnstructuredDocument = 15`), que consome APIs especializadas em layout analysis e parsing de documentos não estruturados (Unstructured.io ou Upstage). O conector envia os arquivos em multipart/form-data com estratégias configuráveis (`auto`, `hi_res`, `ocr_only`, `fast`), recebe elementos estruturados (tabelas em HTML/Markdown, títulos, parágrafos) e emite `RawDocument`s perfeitamente tipados para a esteira de chunking e embeddings.

## 2. Scope

**In scope:**
- Novo membro no enum `SourceType.UnstructuredDocument = 15` em `src/KnowledgeHub.Shared/Contracts/SourceType.cs`.
- `UnstructuredDocumentConnector` (`ISourceConnector` + `IIncrementalSourceConnector`):
  - Suporte a extensões: `.pdf`, `.docx`, `.pptx`, `.xlsx`, `.jpg`, `.jpeg`, `.png`, `.tiff`.
  - Configuração:
    - `apiUrl`: endpoint REST (default: `https://api.unstructured.io/general/v0/general` ou endpoint self-hosted).
    - `strategy`: `auto` | `hi_res` | `ocr_only` | `fast` (default: `auto`).
    - `coordinates`: bool (default: `false` — se true, inclui bounding boxes para auditoria).
    - `tableExtraction`: bool (default: `true` — gera tabelas formatadas em Markdown).
    - `maxFileSizeMb`: limite de segurança (default: 25MB, clamp: 1–100MB).
    - `files` ou `folderPath`: lista de caminhos locais ou arquivos em staging.
- Gerenciamento de Credenciais:
  - `apiKey`: armazenada em `IIntegrationSecretStore` sob `unstructured:{sourceId}`. Se ausente, permite endpoints locais sem auth (ex.: container local de Unstructured).
- Parser de Elementos Estruturados:
  - Conversão de elementos JSON (`Title`, `NarrativeText`, `Table`, `Header`, `Footer`) em Markdown limpo preservando títulos hierárquicos (`#`, `##`) e tabelas em Markdown (`| Col1 | Col2 |`).
- Sincronização Incremental:
  - `Fingerprint` baseado no hash SHA-256 do arquivo original e na data de modificação (`LastWriteTimeUtc`).
- Integração na UI `SourceEditDialog.razor`:
  - Seção com upload/seleção de arquivos, estratégia de extração, endpoint da API e API Key mascarada.
- Whitelist do `ScheduledSyncBackgroundService`.

**Out of scope:**
- Execução de OCR local in-process via C++ nativo (o conector delega para o endpoint Unstructured/Upstage configurado).
- Edição de documentos binários.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Shared/Contracts/SourceType.cs`: `UnstructuredDocument = 15`.
- `src/KnowledgeHub.Server/Ingestion/Connectors/`:
  - `UnstructuredDocumentConnector.cs`: conector principal.
  - `UnstructuredApiClient.cs`: cliente HTTP multipart/form-data.
  - `UnstructuredElementRenderer.cs`: renderizador de elementos JSON para Markdown estruturado.
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs`: registro de `RequiredKeys`.
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs`: whitelist de auto-sync.
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor`: formulário UI.
- `tests/KnowledgeHub.Tests.Unit/Connectors/`: testes com respostas simuladas da API Unstructured.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Ingestion/Connectors/ISourceConnector.cs`
- `src/KnowledgeHub.Server/Ingestion/Connectors/DocumentFileConnector.cs`
- `src/KnowledgeHub.Server/Settings/IIntegrationSecretStore.cs`

**Files to create or modify:**
```text
src/KnowledgeHub.Shared/Contracts/SourceType.cs                               (modify — UnstructuredDocument = 15)
src/KnowledgeHub.Server/Ingestion/Connectors/UnstructuredDocumentConnector.cs(create)
src/KnowledgeHub.Server/Ingestion/Connectors/UnstructuredApiClient.cs        (create)
src/KnowledgeHub.Server/Ingestion/Connectors/UnstructuredElementRenderer.cs  (create)
src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs                    (modify)
src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs (modify)
src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs            (modify)
src/KnowledgeHub.Client/Pages/SourceEditDialog.razor                          (modify)
tests/KnowledgeHub.Tests.Unit/Connectors/UnstructuredConnectorTests.cs       (create)
```

## 4. Requirements

### RF-001: Envio Multipart e Estratégia de Extração
- **Description:** O conector deve enviar o arquivo via `multipart/form-data` para a API Unstructured com os parâmetros de estratégia e formato.
- **Rules:**
  - Se a extensão for imagem (`.png`, `.jpg`, `.tiff`), a estratégia deve ser forçada para `ocr_only` ou `hi_res`.
  - O header `unstructured-api-key` é adicionado caso uma chave esteja configurada no secret store.
- **Input → Output:** Arquivo `balanco-patrimonial.pdf` → Payload JSON com lista de elementos tipados (`Table`, `Title`, `NarrativeText`).

### RF-002: Renderização de Elementos em Markdown Rico
- **Description:** Transformar o array de elementos retornado pela API em um documento Markdown perfeitamente legível para LLMs.
- **Rules:**
  - Elementos do tipo `Table` contendo HTML ou texto delimitado devem ser convertidos em tabelas padrão GFM (`| a | b |`).
  - Elementos `Header` e `Footer` repetitivos de páginas devem ser suprimidos para evitar poluição dos chunks de embedding.
  - Títulos (`Title`) são prefixados por hashes Markdown proporcionais à hierarquia de profundidade.
- **Input → Output:** JSON com elementos heterogêneos → String Markdown unificada e estruturada.

### RF-003: Sincronização Incremental por Hash de Arquivo
- **Description:** Evitar reprocessamento de arquivos pesados que não sofreram alteração.
- **Rules:**
  - O `Fingerprint` é composto por `unstructured:{fileName}:{sha256}:{strategy}`.
  - Se o hash do arquivo local for idêntico e a estratégia não mudou, o arquivo é ignorado sem requisições à API.

## 5. API Contract (if applicable)

Configuração no `KnowledgeSource`:
```json
{
  "name": "Relatórios Técnicos PDF",
  "type": "UnstructuredDocument",
  "configuration": {
    "folderPath": "/data/reports",
    "apiUrl": "https://api.unstructured.io/general/v0/general",
    "strategy": "hi_res",
    "tableExtraction": true,
    "maxFileSizeMb": 20,
    "hasKey": true
  }
}
```

## 6. Acceptance Criteria

- [ ] **Given** um arquivo PDF contendo uma tabela financeira **when** a sincronização roda com `tableExtraction = true` **then** o documento Markdown resultante contém uma tabela GFM com pipes e traços válidos.
- [ ] **Given** um documento escaneado **when** processado com `strategy = "hi_res"` **then** o texto OCR é extraído e incluído no documento final.
- [ ] **Given** um arquivo cujo hash SHA-256 não mudou desde a última sincronização **when** o auto-sync roda **then** nenhuma chamada HTTP externa é emitida.
- [ ] **Given** uma falha de autenticação (HTTP 401) com a API Unstructured **when** o conector executa **then** a sincronização marca `status = failed` com mensagem explícita de chave inválida.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Arquivo excede maxFileSizeMb | Arquivo de 35MB com limite de 20MB | Rejeita arquivo com warning nos logs e continua os demais |
| Resposta da API vazia | Documento em branco | Emite RawDocument vazio com metadado sem crash |
| API Unstructured indisponível (HTTP 502/503) | Timeout de rede | Retorna falha de sync com sugestão de retry posterior |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** Analisar modelos de conector de arquivos e secret store.
- [ ] **T2 — Enum & DI:** Atualizar `SourceType.cs` e registrar novo conector na injeção de dependência.
- [ ] **T3 — Client:** Implementar `UnstructuredApiClient.cs` com suporte a multipart/form-data e headers de auth.
- [ ] **T4 — Element Renderer:** Implementar `UnstructuredElementRenderer.cs` para conversão JSON -> GFM Markdown.
- [ ] **T5 — Connector Core:** Implementar `UnstructuredDocumentConnector.cs` integrando hash e deduplicação.
- [ ] **T6 — UI & Tests:** Adicionar seção no `SourceEditDialog.razor` e testes unitários com mocks de payloads JSON.

## 8. Organization Guardrails

- **Segurança:** A chave de API Unstructured é tratada como segredo de integração (`hasKey = true`) e nunca persistida no `ConfigurationJson` público.
- **Isolamento de Recursos:** O stream de upload de arquivos deve ser descartado imediatamente após o envio para não vazar memória no Kestrel.

## 9. Definition of Done

- [ ] Todos os requisitos (RF-001 a RF-003) implementados.
- [ ] Testes unitários cobrindo parsing de tabelas, texto narrativo e tratamento de erros de API.
- [ ] UI Blazor WASM validada para edição e salvamento do novo tipo de fonte.
