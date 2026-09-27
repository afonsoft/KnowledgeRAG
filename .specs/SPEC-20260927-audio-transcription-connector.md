# SPEC-20260927-audio-transcription-connector

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `audio-transcription-connector` |
| Type | `Feature` |
| Stack | `.NET 10 / HttpClient / System.Text.Json / AssemblyAI REST API / OpenAI Whisper` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `#277` |
| Status | `Approved` |

## 1. User Story

**As a** usuário ou agente do Knowledge MCP Hub
**I want** cadastrar arquivos de áudio e gravações de reuniões (`.mp3`, `.wav`, `.m4a`, `.mp4`, `.webm`, `.ogg`) como fontes de conhecimento (`SourceType.AudioTranscription = 16`)
**So that** reuniões técnicas, palestras, podcasts e notas de voz sejam transcritos com diarização de interlocutores (speakers), timestamps e capítulos, permitindo que o RAG responda perguntas sobre o conteúdo falado.

**Problem context:**
Reuniões de arquitetura, alinhamentos de projeto e chamadas com clientes frequentemente contêm decisões técnicas cruciais que nunca são transcritas em documentos formais. O conector de YouTube (`SourceType.YouTube = 12`) cobre apenas vídeos públicos hospedados no YouTube.
Inspirado no leitor de áudio do **Weaviate Verba** (`goldenverba/components/reader/AssemblyAIAPI.py`), esta SPEC implementa o conector `AudioTranscriptionConnector` (`SourceType.AudioTranscription = 16`). O conector processa arquivos locais ou URLs remotas de áudio/vídeo, envia para APIs de transcrição e diarização (AssemblyAI ou endpoint OpenAI Whisper), estrutura os diálogos com timestamps e identificadores de locutor (`[Speaker A - 00:02:15]`) e emite documentos limpos para indexação.

## 2. Scope

**In scope:**
- Novo membro no enum `SourceType.AudioTranscription = 16` em `src/KnowledgeHub.Shared/Contracts/SourceType.cs`.
- `AudioTranscriptionConnector` (`ISourceConnector` + `IIncrementalSourceConnector`):
  - Formatos suportados: `.mp3`, `.wav`, `.m4a`, `.mp4`, `.aac`, `.flac`, `.ogg`, `.webm`.
  - Configuração:
    - `provider`: `assemblyai` | `whisper` (default: `assemblyai`).
    - `language`: código opcional (ex.: `pt`, `en` ou `auto`).
    - `enableSpeakerDiarization`: bool (default: `true` — identifica quem falou o quê).
    - `enableAutoChapters`: bool (default: `true` — resume capítulos automaticamente).
    - `files` ou `folderPath`: lista de caminhos de arquivos.
  - Secret Store: chave `audio:{sourceId}` para `apiKey` da API externa.
- Renderizador de Transcrição:
  - Formata o texto final com sumário de capítulos no topo seguido pelas falas sequenciais anotadas por tempo e locutor:
    ```markdown
    # Transcrição: Reunião de Arquitetura 2026-09-27
    ## Capítulos
    - [00:00:00] Abertura e status dos conectores
    - [00:15:30] Discussão sobre pgvector e cache distribuído
    
    ## Diálogo
    **[Speaker A - 00:00:12]**: Bom dia pessoal, vamos iniciar a revisão...
    **[Speaker B - 00:00:35]**: O conector de YouTube já está pronto...
    ```
- Sync incremental por hash de áudio (`audio:{sha256}:{provider}`).
- UI `SourceEditDialog.razor`: formulário Blazor WASM.
- Whitelist do `ScheduledSyncBackgroundService`.

**Out of scope:**
- Processamento local de modelos Whisper pesados na CPU do servidor Kestrel (utiliza APIs externas ou container dedicado).
- Streaming de áudio ao vivo em tempo real via microfone (trabalha sobre arquivos gravados).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Shared/Contracts/SourceType.cs`: adição de `AudioTranscription = 16`.
- `src/KnowledgeHub.Server/Ingestion/Connectors/`:
  - `AudioTranscriptionConnector.cs`: conector.
  - `AudioTranscriptionRenderer.cs`: formatação em Markdown.
  - `Clients/AssemblyAiClient.cs`: cliente HTTP da API AssemblyAI.
  - `Clients/WhisperApiClient.cs`: cliente HTTP OpenAI Audio API.
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs`: `RequiredKeys`.
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs`: whitelist.
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor`: UI.
- `tests/KnowledgeHub.Tests.Unit/Connectors/`: testes unitários.

**Files to create or modify:**
```text
src/KnowledgeHub.Shared/Contracts/SourceType.cs                           (modify — AudioTranscription = 16)
src/KnowledgeHub.Server/Ingestion/Connectors/AudioTranscriptionConnector.cs (create)
src/KnowledgeHub.Server/Ingestion/Connectors/AudioTranscriptionRenderer.cs  (create)
src/KnowledgeHub.Server/Ingestion/Connectors/Clients/AssemblyAiClient.cs   (create)
src/KnowledgeHub.Server/Ingestion/Connectors/Clients/WhisperApiClient.cs    (create)
src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs                 (modify)
src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs (modify)
src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs         (modify)
src/KnowledgeHub.Client/Pages/SourceEditDialog.razor                       (modify)
tests/KnowledgeHub.Tests.Unit/Connectors/AudioTranscriptionConnectorTests.cs (create)
```

## 4. Requirements

### RF-001: Upload e Iniciação de Job de Transcrição Assíncrono
- **Description:** O conector deve enviar o arquivo de áudio e aguardar o término do processamento da API externa com polling exponencial.
- **Rules:**
  - AssemblyAI: upload do stream em `/v2/upload`, posterior POST em `/v2/transcript` com `speaker_labels=true`, polling em `/v2/transcript/{id}` até status `completed`.
  - Timeout máximo de espera configurável por arquivo (default: 10 minutos).
- **Input → Output:** Arquivo `reuniao.mp3` → JSON com transcrição, utterances e capítulos.

### RF-002: Formatação Estruturada com Diarização e Capítulos
- **Description:** A saída deve ser renderizada em Markdown limpo com metadados para otimizar o chunking semântico.
- **Rules:**
  - Falas consecutivas do mesmo locutor dentro de 10 segundos são agrupadas em um único bloco.
  - Cada bloco inclui timestamp legível no formato `[HH:MM:SS]`.
- **Input → Output:** Utterances da API → Documento Markdown formatado.

### RF-003: Sincronização Incremental por Hash
- **Description:** Arquivos de áudio já transcritos não devem gerar novos custos de API externa.
- **Rules:**
  - O `Fingerprint` é calculado a partir do SHA-256 do arquivo original.
  - Se o hash coincidir, o arquivo é mantido e marcado como inalterado.

## 5. API Contract (if applicable)

Configuração no `KnowledgeSource`:
```json
{
  "name": "Reuniões Semanais de Arquitetura",
  "type": "AudioTranscription",
  "configuration": {
    "folderPath": "/data/recordings",
    "provider": "assemblyai",
    "language": "pt",
    "enableSpeakerDiarization": true,
    "enableAutoChapters": true,
    "hasKey": true
  }
}
```

## 6. Acceptance Criteria

- [ ] **Given** um arquivo `.mp3` de reunião **when** a sincronização roda **then** o texto final gerado contém identificação de speakers e capítulos legíveis.
- [ ] **Given** um arquivo de áudio já processado sem alteração de bytes **when** o auto-sync roda **then** nenhuma chamada de upload é emitida.
- [ ] **Given** falha no serviço de transcrição (HTTP 500) **when** o polling falha **then** o item é reportado em `FailedUris` com mensagem clara de erro.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Áudio mudo ou sem fala detectada | Arquivo de ruído branco | Emite documento com aviso "Nenhuma fala detectada no áudio" |
| Arquivo corrompido | Bytes inválidos | Rejeita arquivo com erro 400 antes do upload |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** Analisar conectores de mídia e secret store.
- [ ] **T2 — Enum:** Adicionar `SourceType.AudioTranscription = 16`.
- [ ] **T3 — Client:** Implementar `AssemblyAiClient.cs` com upload e polling assíncrono.
- [ ] **T4 — Renderer:** Implementar `AudioTranscriptionRenderer.cs` para formatação Markdown.
- [ ] **T5 — Connector Core:** Implementar `AudioTranscriptionConnector.cs`.
- [ ] **T6 — UI & Tests:** Adicionar formulário no Blazor e testes unitários com mocks.

## 8. Organization Guardrails

- **Custos:** Alertar nos logs sobre a duração em minutos de áudio processado para monitoramento de custos.
- **Segurança:** Chaves de API nunca devem ser expostas em logs nem enviadas na UI.

## 9. Definition of Done

- [ ] Todos os requisitos (RF-001 a RF-003) implementados.
- [ ] Testes unitários com mocks de API cobrindo happy path e edge cases de timeout.
- [ ] Documentação e formulário Blazor atualizados.
