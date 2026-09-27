# SPEC-20260927-youtube-transcript-connector

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `youtube-transcript-connector` |
| Type | `Feature` |
| Stack | `.NET 10 / YoutubeExplode (NuGet) / YouTube closed captions` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-youtube-transcript-connector` |
| Ticket | `#262` — https://github.com/afonsoft/LangGraph-UI/issues/262 |
| Status | `Done` |

## 1. User Story

**As a** administrador do Knowledge MCP Hub
**I want** registrar vídeos, playlists e canais do YouTube como fonte de conhecimento (`SourceType = YouTube`)
**So that** o RAG indexe as transcrições (legendas) dos vídeos e responda perguntas sobre o conteúdo falado — cobrindo uma das maiores fontes de conhecimento informal (talks, tutoriais, aulas, podcasts em vídeo).

**Problem context:**
O pipeline de ingestão (`ISourceConnector` → `FetchResult` → dedup por hash/fingerprint → `MarkdownChunker` → embed) já suporta `WebPage`, `DocumentFile`, `Notion`, cloud storage e Google Drive. YouTube não tem API oficial gratuita para baixar transcrições de vídeos de terceiros (a `captions.download` da Data API v3 só cobre vídeos do próprio canal autenticado). A biblioteca .NET **YoutubeExplode** (madura, MIT, mantida) expõe `Videos.ClosedCaptions.GetManifestAsync`/`GetAsync` sobre a API interna do YouTube, sem credenciais, para qualquer vídeo público — o caminho pragmático e idiomático em .NET.

**Decisão de integração (pesquisa 2026-09-27):** a alternativa ao YoutubeExplode era reimplementar o parsing de `ytInitialPlayerResponse` + endpoints `timedtext` manualmente — frágil e sem ganho real. YouTube Data API v3 não resolve o caso (sem acesso a legendas de terceiros). Não há auth, cookies ou segredos envolvidos: apenas conteúdo público.

## 2. Scope

**In scope:**
- `SourceType.YouTube = 12` no enum `src/KnowledgeHub.Shared/Contracts/SourceType.cs`.
- Nova dependência `YoutubeExplode` (versão pinada `6.6.1` — publicada 2026-08-07, >7 dias; ver `§8 Deps`).
- `YouTubeConnector` (`IIncrementalSourceConnector` + `IItemFetchConnector`) que aceita na config uma lista `urls` com URLs/IDs de vídeo, playlist ou canal; expande playlists/canais; para cada vídeo público baixa a melhor faixa de legendas e emite um `RawDocument`.
- Config da source: `urls` (array, obrigatório), `language` (opcional — código de idioma preferido, ex. `pt`, `en`), `includeAutoCaptions` (bool, default `true`), `includeTimestamps` (bool, default `false`), `maxVideos` (default 100, clamp 1–500), `forceRefresh` (bool, default `false`).
- Sync incremental: `Fingerprint = "yt:{videoId}"` — legendas praticamente não mudam; sem `forceRefresh` o conector emite stub (`TextContent=""`) sem chamar `GetManifestAsync`/`GetAsync` para vídeos já indexados.
- `KnowledgeSourceService`: `RequiredKeys[YouTube] = ["urls"]`; nenhum segredo envolvido.
- Auto-sync: `SourceType.YouTube` entra no whitelist de `ScheduledSyncBackgroundService` (polling por `SyncIntervalMinutes`; sem `FileSystemWatcher`).
- UI `SourceEditDialog.razor`: seção YouTube — textarea de URLs (uma por linha), campo `language`, checkboxes, `maxVideos`; `ManagedKeys`, `Validate()` e `SourceEditModel` atualizados.
- Seam testável: wrapper `IYouTubeClient` fino sobre `YoutubeClient` (GetVideo / GetPlaylistVideos / GetChannelUploads / GetCaptionManifest / GetCaptionTrack) para unit tests sem rede.

**Out of scope:**
- Download de áudio/vídeo ou transcrição por whisper — só legendas existentes.
- Vídeos privados, age-restricted, members-only ou que exigem login — viram warning por item.
- Lives/estreias sem legendas publicadas — warning por item.
- Comentários, chapters detalhados, descrição completa do vídeo além de metadados básicos (título, canal, duração, URL).
- OAuth YouTube/Google, legendas do próprio canal via Data API, e `search` no YouTube — o usuário lista explicitamente o que quer indexar.
- Shorts/live-clips: funcionam se o ID resolver como vídeo público com legendas; sem tratamento especial.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Shared/Contracts/SourceType.cs` — novo membro `YouTube = 12`.
- `src/KnowledgeHub.Server/Ingestion/Connectors/` — `YouTubeConnector.cs` + `YouTubeUrlParser.cs` (classifica URL/ID em video|playlist|channel) + `IYouTubeClient`/`YouTubeClientAdapter.cs` (seam sobre `YoutubeClient`) + `TranscriptRenderer.cs` (faixa → texto, dedup de repetições, timestamps opcionais).
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs` — `RequiredKeys`.
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs` — whitelist de auto-sync.
- `src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs` — registro do conector (singleton) + `IYouTubeClient`.
- `src/KnowledgeHub.Server/KnowledgeHub.Server.csproj` — `PackageReference YoutubeExplode`.
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor` + `SourceEditModel` — campos YouTube.
- `tests/KnowledgeHub.Tests.Unit` / `tests/KnowledgeHub.Tests.Integration` — parser de URL, seleção de faixa, renderer, conector com `IYouTubeClient` fake.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Ingestion/Connectors/{ISourceConnector,ConnectorConfig,WebPageConnector,NotionConnector}.cs`
- `src/KnowledgeHub.Server/Ingestion/IngestionService.cs` (`SyncViaConnectorAsync`, fingerprint map, `Truncated`/`FailedUris`)
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs` (`RequiredKeys`, validação)
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs` (whitelist ~linha 50)
- `src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs`
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor`
- `.specs/SPEC-20260919-notion-connector.md` (padrão de conector + fingerprint), `.specs/SPEC-20260926-ingestion-connector-integrity.md` (`Truncated`, `FailedUris`)

**Files to create or modify:**
```text
src/KnowledgeHub.Shared/Contracts/SourceType.cs                          (modify — YouTube = 12)
src/KnowledgeHub.Server/Ingestion/Connectors/YouTubeUrlParser.cs         (create)
src/KnowledgeHub.Server/Ingestion/Connectors/IYouTubeClient.cs           (create — seam)
src/KnowledgeHub.Server/Ingestion/Connectors/YouTubeClientAdapter.cs     (create)
src/KnowledgeHub.Server/Ingestion/Connectors/TranscriptRenderer.cs       (create)
src/KnowledgeHub.Server/Ingestion/Connectors/YouTubeConnector.cs         (create)
src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs               (modify — RequiredKeys)
src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs (modify — whitelist)
src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs       (modify — DI)
src/KnowledgeHub.Server/KnowledgeHub.Server.csproj                       (modify — YoutubeExplode)
src/KnowledgeHub.Client/Pages/SourceEditDialog.razor                     (modify — seção YouTube)
tests/KnowledgeHub.Tests.Unit/…                                          (create)
tests/KnowledgeHub.Tests.Integration/…                                   (create)
```

## 4. Requirements

### RF-001: `SourceType.YouTube` e configuração
- **Description:** enum `YouTube = 12`; `configuration` aceita `urls` (string[] — obrigatório, min 1), `language` (string, opcional), `includeAutoCaptions` (bool, default `true`), `includeTimestamps` (bool, default `false`), `maxVideos` (int, default 100, clamp 1–500 via `ConnectorConfig.Int`), `forceRefresh` (bool, default `false`).
- **Rules:** `RequiredKeys[YouTube] = ["urls"]`; cada entrada de `urls` deve resolver via `YouTubeUrlParser` para video|playlist|channel — entradas inválidas viram warning no sync (não bloqueiam o save; validação do dialog avisa); `language` quando presente deve ter 2–8 chars (`[a-zA-Z-]+`, ex. `pt-BR`) senão 400.
- **Input → Output:** POST `/api/sources` type=YouTube + urls → source criada; GET retorna config sem nenhum segredo (não há).

### RF-002: `YouTubeUrlParser`
- **Description:** classifica cada entrada em `Video(videoId)`, `Playlist(playlistId)` ou `Channel(handle|channelId)`.
- **Rules:** aceita `youtube.com/watch?v=ID`, `youtu.be/ID`, `youtube.com/shorts/ID`, `youtube.com/live/ID`, `youtube.com/playlist?list=ID`, `youtube.com/channel/UC…`, `youtube.com/@handle`, e IDs crus (11 chars video / `PL|UU|OL…` playlist / `UC…` channel). Normaliza para o modelo `YouTubeEntry`. Entrada irreconhecível → warning "URL não reconhecida: {input}".
- **Input → Output:** string → `YouTubeEntry` tipado ou `null`.

### RF-003: `IYouTubeClient` (seam) e `YouTubeClientAdapter`
- **Description:** wrapper fino e mockável sobre `YoutubeClient` expondo só o necessário:
  - `GetVideoAsync(videoId)` → `VideoInfo{ Id, Title, Author, Duration }`
  - `GetPlaylistVideosAsync(playlistId)` → `IAsyncEnumerable<VideoInfo>`
  - `GetChannelUploadsAsync(channelIdOrHandle)` → `IAsyncEnumerable<VideoInfo>`
  - `GetCaptionManifestAsync(videoId)` → `IReadOnlyList<CaptionTrackInfo{ Language, IsAutoGenerated }>`
  - `GetCaptionTrackAsync(track)` → `IReadOnlyList<Caption{ Text, Offset }>`
- **Rules:** adapter delega a `YoutubeClient.Videos`/`Playlists`/`Channels`/`Videos.ClosedCaptions`; exceções do YoutubeExplode (`VideoUnavailableException`, `PlaylistUnavailableException`, etc.) propagam para o conector mapear em warnings; sem retry interno (falha por item → warning; sync segue).
- **Input → Output:** métodos tipados → records internos.

### RF-004: Descoberta e bound
- **Description:** para cada entrada em `urls`: `Video` → 1 item; `Playlist` → `GetPlaylistVideosAsync`; `Channel` → `GetChannelUploadsAsync`.
- **Rules:** ordem de descoberta preserva a ordem das `urls`; dedup por `videoId` dentro do sync; hard bound global `maxVideos` — ao atingir, interrompe a expansão, adiciona warning "truncated at maxVideos" e marca `FetchResult.Truncated = true` (reconciliation não apaga docs não vistos — RF de integridade SPEC-20260926).
- **Input → Output:** lista de `VideoInfo` únicos + warnings.

### RF-005: Seleção de faixa de legendas
- **Description:** para cada vídeo, escolhe a melhor `CaptionTrackInfo` do manifest.
- **Rules:** prioridade — (1) faixa **manual** (`IsAutoGenerated=false`) com `language` igual à configurada; (2) qualquer faixa manual; (3) se `includeAutoCaptions=true`, auto-gerada com `language` igual; (4) qualquer auto-gerada; (5) nenhuma → warning "vídeo sem legendas: {title}" e skip. Comparação de idioma case-insensitive, `pt` casa `pt-BR`/`pt-PT` (prefixo antes do `-`).
- **Input → Output:** `CaptionTrackInfo?` por vídeo.

### RF-006: `TranscriptRenderer` → texto
- **Description:** converte a faixa em texto legível para chunking.
- **Rules:** concatena `Caption.Text` em ordem; remove tags HTML de formatação (`<i>`, `<b>`) e entidades; **dedup de repetições consecutivas** (auto-captions do YouTube repetem a linha anterior — janela de dedup com comparação normalizada); quebra de linha a cada caption, ou texto corrido em parágrafos quando `includeTimestamps=false` (decisão: **parágrafos** — melhor para chunking; quando `true`, linhas `[mm:ss] texto`). Header do documento: `# {title}`, `Canal: {author}`, `URL: https://youtu.be/{id}`, `Duração: {hh:mm:ss}`.
- **Input → Output:** `IReadOnlyList<Caption>` → `string`.

### RF-007: Documentos e sync incremental
- **Description:** cada vídeo vira `RawDocument(UriReference: "yt://video/{id}", Title: video title, TextContent, Fingerprint: "yt:{videoId}")`.
- **Rules:** com `IIncrementalSourceConnector`, vídeos cujo fingerprint já está no mapa `UriReference→ContentHash` são emitidos como stub (`TextContent=""`) sem chamadas de manifest/track — a menos que `forceRefresh=true`; `IItemFetchConnector.FetchItemAsync` refaz o fetch completo de um `yt://video/{id}` para o caminho de reprocessamento (SPEC-20260926). Falha por vídeo → `Warnings` + `FailedUris` (doc existente preservado); falha na expansão de playlist/canal → warning, demais entradas seguem.
- **Input → Output:** `FetchResult` com documentos + warnings + `FailedUris`/`Truncated` conforme o caso.

### RF-008: Auto-sync, DI e UI
- **Description:** `SourceType.YouTube` no whitelist de `ScheduledSyncBackgroundService`; `services.AddSingleton<IYouTubeClient, YouTubeClientAdapter>()` + registro do conector; seção YouTube no `SourceEditDialog`.
- **Rules:** `ManagedKeys` += `urls`, `language`, `includeAutoCaptions`, `includeTimestamps`, `maxVideos`, `forceRefresh`; `Validate()` exige ao menos 1 linha em `urls`; `language` opcional; model ganha `Urls` (textarea, uma URL por linha), `Language`, `IncludeAutoCaptions`, `IncludeTimestamps`, `MaxVideos`, `ForceRefresh`.
- **Input → Output:** dialog YouTube salva config válida; `GET` subsequente repopula os campos.

## 5. API Contract (outbound — YouTube via YoutubeExplode)

Sem endpoints REST próprios: o acesso é via `YoutubeClient` (API interna do YouTube, sem auth). Sem novos endpoints inbound — reuso total de `/api/sources`.

| Operação YoutubeExplode | Uso |
| --- | --- |
| `Videos.GetAsync(videoId)` | título/autor/duração |
| `Playlists.GetVideosAsync(playlistId)` | expansão de playlist |
| `Channels.GetUploadsAsync(handle|id)` | expansão de canal |
| `Videos.ClosedCaptions.GetManifestAsync(videoId)` | faixas disponíveis |
| `Videos.ClosedCaptions.GetAsync(track)` | legendas (texto + offset) |

**Erros esperados → comportamento:**
- `VideoUnavailableException` / vídeo privado/deletado/age-gated → warning por item + `FailedUris` (doc existente preservado).
- Manifest vazio (sem legendas) → warning por item, skip.
- `PlaylistUnavailableException` / canal inválido → warning por entrada; outras entradas continuam.
- `HttpRequestException`/timeout/429 (rate-limit YouTube) → warning por item; se **todas** as entradas falharem na descoberta → sync `failed` com `LastError` claro.
- Nunca lançar cookies/credenciais — conteúdo público apenas.

## 6. Acceptance Criteria

- [ ] **CA-001:** **Given** source YouTube com `urls=[watch?v=X]` e vídeo X público com legendas, **when** `POST /api/sources/{id}/sync`, **then** a transcrição indexa como documento e responde em `search_knowledge`/`query_{slug}`.
- [ ] **CA-002:** **Given** re-sync sem `forceRefresh`, **when** sync roda, **then** vídeos já indexados emitem stubs e **nenhuma** chamada `GetCaptionManifestAsync`/`GetCaptionTrackAsync` ocorre para eles (verificável no `IYouTubeClient` fake).
- [ ] **CA-003:** **Given** `forceRefresh=true`, **when** sync roda, **then** todas as faixas são re-baixadas.
- [ ] **CA-004:** **Given** URL de playlist com N vídeos, **when** sync roda, **then** até `maxVideos` vídeos são indexados; se N > maxVideos, `Truncated=true` + warning.
- [ ] **CA-005:** **Given** vídeo sem legendas e vídeo privado na lista, **when** sync roda, **then** cada um gera warning/FailedUri próprio e os demais indexam normalmente.
- [ ] **CA-006:** **Given** `language="pt"` e vídeo com faixas `en` (manual) + `pt-BR` (auto), **when** `includeAutoCaptions=true`, **then** a faixa `pt-BR` é escolhida; **when** `includeAutoCaptions=false`, **then** a manual `en` é escolhida.
- [ ] **CA-007:** **Given** `includeTimestamps=true`, **when** render, **then** linhas no formato `[mm:ss] texto`; com `false`, texto corrido sem marcas.
- [ ] **CA-008:** **Given** `urls` vazia ou ausente no create, **then** 400 "Configuration key 'urls' is required for YouTube".
- [ ] **CA-009:** **Given** entrada inválida em `urls` (ex. `notaurl`), **when** sync roda, **then** warning por entrada e demais itens processam.
- [ ] **CA-010:** **Given** `AutoSyncEnabled=true`, **then** o sync periódico dispara para a source YouTube.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| playlist que lista vídeo privado | expansão retorna vídeo indisponível | warning + `FailedUris` naquele item |
| auto-captions com repetições | faixa auto-gerada | renderer deduplica linhas consecutivas |
| mesmo vídeo em 2 URLs | `watch?v=X` e `youtu.be/X` | 1 documento só (dedup por videoId) |
| canal com > maxVideos | uploads grandes | trunca + `Truncated=true` |
| `language` inválida | `pt_BR!` | 400 na validação |
| todos os vídeos falham | rede down | sync `failed` com `LastError`, nenhum doc criado/apagado |
| videoId de live sem captions | manifest vazio | warning "sem legendas" |

## 7. Task Plan

- [ ] **T1 — Contratos + parser:** `SourceType.YouTube`, `YouTubeUrlParser` (unit tests de todos os formatos), `YouTubeEntry`/records internos. **Validação:** unit tests do parser.
- [ ] **T2 — Seam `IYouTubeClient`:** interface + `YouTubeClientAdapter` sobre `YoutubeClient`; PackageReference pinada. **Validação:** `dotnet build` 0 warnings.
- [ ] **T3 — `TranscriptRenderer`:** texto com/sem timestamps, dedup de repetições, header do doc. **Validação:** unit tests com fixtures de captions.
- [ ] **T4 — `YouTubeConnector`:** descoberta (video/playlist/channel), bound `maxVideos`/`Truncated`, seleção de faixa, fingerprint skip, `FetchItemAsync`, warnings/`FailedUris`. **Validação:** unit + integração com `IYouTubeClient` fake.
- [ ] **T5 — Serviço/DI/auto-sync:** `RequiredKeys`, validação de `language`, registro DI, whitelist `ScheduledSyncBackgroundService`. **Validação:** integration tests de CRUD/sync.
- [ ] **T6 — UI:** seção YouTube no `SourceEditDialog` + `ManagedKeys`/`Validate`/model. **Validação:** build WASM + smoke manual.
- [ ] **T7 — Docs:** `CLAUDE.md`/README — novo conector, formatos de URL aceitos, limitação "só vídeos públicos com legendas".

**7.1 Validation strategy (.NET):** unit tests para parser/renderer/seleção de faixa; integration tests do pipeline com `IYouTubeClient` fake; `dotnet build` 0 warnings, `dotnet test` verde, `dotnet format --verify-no-changes`; cobertura ≥80% nos arquivos novos.

## 8. Organization Guardrails

- **Branches:** `feature/Devin-20260927-youtube-transcript-connector` a partir de `main`; nunca commitar em `main`/`master`/`develop`.
- **Workflows:** `.github/workflows/` protegido — sem alterações.
- **Secrets:** nenhum segredo envolvido (YouTube público, sem auth); não introduzir cookies/credenciais.
- **Scope:** não baixar mídia, não transcrever com whisper, não fazer OAuth — fora de escopo §2.
- **Architecture:** lógica de fetch no conector (Ingestion), sem regra de negócio em endpoints/UI; conector não acessa `DbContext` — mapa de fingerprints vem do `IngestionService`.
- **Deps:** nova dependência `YoutubeExplode` pinada em `6.6.1` (publicada 2026-08-07; >7 dias, versão com adoção comprovada — 6.6.2 existe mas preferimos a mais madura). Justificativa: reimplementar o parsing de `ytInitialPlayerResponse`+`timedtext` é frágil e sem ganho; YoutubeExplode é MIT, mantida e padrão de fato em .NET. Se uma versão mais nova for escolhida, justificar na revisão.

## 9. Definition of Done

- [ ] Todos os RFs (§4) implementados.
- [ ] CA-001..CA-010 cobertos por testes passando (unit + integration conforme §7.1).
- [ ] Edge cases da tabela tratados.
- [ ] `dotnet build` 0 warnings · `dotnet test` verde · `dotnet format --verify-no-changes` exit 0.
- [ ] Guardrails §8 respeitados — sem credenciais, dep pinada.
- [ ] Documentação menciona o conector YouTube e formatos de URL aceitos.

**Next action after DoD:** `Status = Done` + PR na branch `feature/Devin-20260927-youtube-transcript-connector` referenciando o ticket.

## Open Questions / Pending Ambiguity

- Nenhuma — decisões tomadas na rodada de design (2026-09-27): YoutubeExplode como transporte, fingerprint por videoId, parágrafos como formato default.
