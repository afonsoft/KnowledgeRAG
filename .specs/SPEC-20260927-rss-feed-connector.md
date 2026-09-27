# SPEC-20260927-rss-feed-connector

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `rss-feed-connector` |
| Type | `Feature` |
| Stack | `.NET 10 / HttpClient + System.Xml.Linq / HtmlTextExtractor (existente)` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260927-rss-feed-connector` |
| Ticket | `#264` — https://github.com/afonsoft/LangGraph-UI/issues/264 |
| Status | `Approved` |

## 1. User Story

**As a** administrador do Knowledge MCP Hub
**I want** registrar um feed RSS/Atom como fonte de conhecimento (`SourceType = RssFeed`)
**So that** o RAG indexe os itens do feed (posts, artigos, releases, notícias) e responda perguntas sobre eles — com sync incremental natural: itens novos entram, itens inalterados são pulados quase de graça.

**Problem context:**
Feeds RSS/Atom são o formato universal de publicação de conteúdo (blogs, changelogs, security advisories, docs release notes). O conector `WebPage` indexa uma única página; um feed agrega N itens novos ao longo do tempo — caso ideal para `IIncrementalSourceConnector` (fingerprint por item). O parser é implementado à mão com `System.Xml.Linq` (RSS 2.0 + Atom 1.0 são formatos simples e estáveis) para **manter zero dependências novas**; `HtmlTextExtractor` já existente resolve tanto o HTML embutido nos itens quanto o full-content opcional das páginas linkadas.

## 2. Scope

**In scope:**
- `SourceType.RssFeed = 13` no enum `src/KnowledgeHub.Shared/Contracts/SourceType.cs`.
- `FeedParser` estático (`System.Xml.Linq`): RSS 2.0 (`<rss><channel><item>`) e Atom 1.0 (`<feed><entry>`), tolerante a namespaces (`LocalName` matching — cobre `content:encoded`, `dc:date`, `atom:` prefixado) e a variações de data (`pubDate` RFC822, `published`/`updated` ISO 8601).
- `RssFeedConnector` (`IIncrementalSourceConnector` + `IItemFetchConnector`): `GET {feedUrl}` → parse → item → `RawDocument`.
- Config da source: `feedUrl` (req, http(s) absoluto), `maxItems` (default 100, clamp 1–500), `fetchFullContent` (bool, default `false` — quando `true`, baixa a página linkada de cada item e extrai texto com `HtmlTextExtractor`), `forceRefresh` (bool, default `false` — ignora fingerprints e re-baixa tudo).
- Incremental: `Fingerprint = "rss:{guid}"` quando o item tem `guid`/`id`, senão `"rss:{sha256(link+pubDate)}"`; itens com fingerprint idêntico ao armazenado viram stub (`TextContent=""`) sem processamento.
- `KnowledgeSourceService`: `RequiredKeys[RssFeed] = ["feedUrl"]`; nenhum segredo.
- Auto-sync: `SourceType.RssFeed` no whitelist de `ScheduledSyncBackgroundService` (polling — feeds são o caso de uso canônico).
- UI `SourceEditDialog.razor`: seção RSS — `feedUrl`, `maxItems`, checkboxes `fetchFullContent`/`forceRefresh`; `ManagedKeys`, `Validate()` e model.

**Out of scope:**
- RSS 0.91/1.0 (RDF) e JSON Feed — só RSS 2.0 + Atom 1.0 (cobrem ~99% do real); formato não reconhecido → sync `failed` com `LastError` claro.
- Feeds autenticados (Basic auth em URL funciona — `https://user:pass@host/feed` — mas sem campo dedicado; headers auth customizados fora de escopo v1).
- `ETag`/`Last-Modified` HTTP caching (exigiria persistir validators por source — o fingerprint por item já cobre o custo real).
- WebSub/PubSubHubbub push — só polling.
- Enclosures (podcast/audio), comments feeds, e OPML (importação em massa de vários feeds — cada feed é uma source).
- Detecção de "item atualizado sem mudar guid/pubDate" — limitação conhecida do fingerprint; `forceRefresh` é o escape documentado.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Shared/Contracts/SourceType.cs` — `RssFeed = 13`.
- `src/KnowledgeHub.Server/Ingestion/Connectors/` — `FeedParser.cs` (RSS/Atom → `FeedItem` records) + `RssFeedConnector.cs`.
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs` — `RequiredKeys`.
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs` — whitelist (~linha 50).
- `src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs` — `AddHttpClient("feed", 30s)` + registro.
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor` — seção RSS.
- `tests/KnowledgeHub.Tests.Unit` / `tests/KnowledgeHub.Tests.Integration` — parser com fixtures XML, conector com handler fake.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Ingestion/Connectors/{ISourceConnector,ConnectorConfig,WebPageConnector,HtmlTextExtractor}.cs`
- `src/KnowledgeHub.Server/Ingestion/IngestionService.cs` (`SyncViaConnectorAsync`, fingerprint/`Truncated`)
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs` (`RequiredKeys`)
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs`
- `src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs`
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor`
- `.specs/SPEC-20260919-notion-connector.md` (padrão fingerprint/stub)

**Files to create or modify:**
```text
src/KnowledgeHub.Shared/Contracts/SourceType.cs                          (modify — RssFeed = 13)
src/KnowledgeHub.Server/Ingestion/Connectors/FeedParser.cs               (create)
src/KnowledgeHub.Server/Ingestion/Connectors/RssFeedConnector.cs         (create)
src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs               (modify)
src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs (modify)
src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs       (modify)
src/KnowledgeHub.Client/Pages/SourceEditDialog.razor                     (modify)
tests/KnowledgeHub.Tests.Unit/…                                          (create)
tests/KnowledgeHub.Tests.Integration/…                                   (create)
```

## 4. Requirements

### RF-001: `SourceType.RssFeed` e configuração
- **Description:** enum `RssFeed = 13`; `configuration` aceita `feedUrl` (req), `maxItems` (default 100, clamp 1–500), `fetchFullContent` (bool default `false`), `forceRefresh` (bool default `false`).
- **Rules:** `feedUrl` deve ser http(s) absoluto → 400 caso contrário; `RequiredKeys[RssFeed] = ["feedUrl"]`.
- **Input → Output:** POST `/api/sources` type=RssFeed + feedUrl → source criada.

### RF-002: `FeedParser`
- **Description:** `Parse(string xml) → IReadOnlyList<FeedItem>` onde `FeedItem{ Guid?, Title, Link?, Content, PublishedAt? }`.
- **Rules:** detecta formato pelo root (`rss`/`rdf` → itens em `channel/item`; `feed` → `entry`); RSS — `title`, `link`, `description` ou `content:encoded` (preferir `content:encoded` quando presente), `pubDate`/`dc:date`, `guid`; Atom — `title`, `link[@href]` (preferir `rel=alternate`), `content` ou `summary`, `id`, `published`/`updated`; HTML embutido em content/description → `HtmlTextExtractor.Extract` para texto; datas inválidas → `null` silencioso; item sem `link` e sem `guid` e sem `title` → skip; XML malformado ou root desconhecido → `FeedParseException` (→ sync `failed`).
- **Input → Output:** string XML → lista tipada.

### RF-003: Fetch e mapeamento para `RawDocument`
- **Description:** `GET {feedUrl}` via HttpClient `"feed"` (30 s) → `FeedParser` → cada item vira documento.
- **Rules:** `UriReference` ← `link` normalizado (lowercase host, sem fragment) quando presente — URLs canônicas permitem dedup natural contra outras fontes; fallback `rss:{guid}`; `Title` ← title (fallback link/guid/`item {i}`); `TextContent` ← header `# {title}` + `Fonte: {feedUrl}` + `Publicado: {date}` + texto do item; bound `maxItems` (mais recentes primeiro pela ordem do feed) — excesso trunca com warning e `Truncated=true`; `fetchFullContent=true` → GET do `link` + `HtmlTextExtractor.Extract` substituindo o corpo do feed (falha/timeout por item → fallback ao conteúdo do feed + warning); dedup por `UriReference` dentro do sync.
- **Input → Output:** `FetchResult` com documentos + warnings.

### RF-004: Sync incremental e `FetchItemAsync`
- **Description:** implementa `IIncrementalSourceConnector`: recebe `UriReference→ContentHash`; fingerprint do item = `"rss:{guid}"` ou `"rss:{sha256(link+pubDateTicks)}"`; match → stub `TextContent=""` mantendo o URI em `seen`.
- **Rules:** `forceRefresh=true` ignora o mapa e re-baixa tudo (item atualizado sem mudar guid é o caso de uso); `IItemFetchConnector.FetchItemAsync` refaz o fetch de um item pelo `UriReference` (reparse do feed; se o item sumiu → null + warning — doc preservado conforme SPEC-20260926); falha HTTP no feed inteiro (não-2xx, timeout, XML inválido) → sync `failed` com `LastError` claro (nunca apaga docs por indisponibilidade temporária — a lista toda falhou, não houve "deleção observada").
- **Input → Output:** re-sync sem itens novos → `DocumentsSkipped=N`, nenhum item reprocessado.

### RF-005: Auto-sync, DI e UI
- **Description:** `RssFeed` no whitelist de `ScheduledSyncBackgroundService`; `AddHttpClient("feed", c => c.Timeout = 30s)` + `AddSingleton<ISourceConnector, RssFeedConnector>`; seção no `SourceEditDialog`.
- **Rules:** `ManagedKeys` += `feedUrl`, `maxItems`, `fetchFullContent`, `forceRefresh`; `Validate()` exige `feedUrl` http(s); model ganha `FeedUrl`, `MaxItems`, `FetchFullContent`, `ForceRefresh`.
- **Input → Output:** dialog RSS salva config válida; GET repopula campos.

## 5. API Contract (outbound)

| Request | Uso |
| --- | --- |
| `GET {feedUrl}` | feed RSS/Atom (30 s timeout) |
| `GET {item.link}` | só quando `fetchFullContent=true` — página do item |

**Erros esperados → comportamento:**
- `404`/DNS inválido/timeout no feed → sync `failed`, `LastError` sanitizado (URL pode conter credenciais Basic — redigir `user:pass@` → `***@`).
- `429` → honrar `Retry-After` 1×, senão `failed`.
- XML inválido/root desconhecido → `failed` "formato de feed não suportado (esperado RSS 2.0 ou Atom)".
- Falha no fetch full-content de um item → warning + fallback ao texto do feed.
- Sem novos endpoints inbound — reuso total de `/api/sources`.

## 6. Acceptance Criteria

- [ ] **CA-001:** **Given** source RssFeed com feed RSS 2.0 fake de 5 itens, **when** `POST /api/sources/{id}/sync`, **then** os 5 itens indexam como documentos e respondem em `search_knowledge`/`query_{slug}`.
- [ ] **CA-002:** **Given** feed Atom 1.0, **when** sync, **then** itens indexam com `entry/id` como guid e `link@href` como UriReference.
- [ ] **CA-003:** **Given** re-sync sem mudanças, **when** sync roda, **then** `DocumentsSkipped=N`, `DocumentsProcessed=0` e nenhum item é reprocessado.
- [ ] **CA-004:** **Given** feed que publica 1 item novo, **when** re-sync, **then** só o novo item é chunkado/embedado; demais viram stubs.
- [ ] **CA-005:** **Given** `forceRefresh=true`, **when** sync, **then** todos os itens são reprocessados.
- [ ] **CA-006:** **Given** `fetchFullContent=true` e item cujo `description` é só um teaser, **when** sync, **then** o doc contém o texto extraído da página linkada.
- [ ] **CA-007:** **Given** feed indisponível (500) num re-sync, **then** sync `failed` e **nenhum** documento existente é apagado.
- [ ] **CA-008:** **Given** item removido do feed (feed só lista os N mais recentes), **when** re-sync sem truncamento, **then** doc e chunks/vetores do item ausente são removidos pela reconciliation normal.
- [ ] **CA-009:** **Given** `feedUrl` inválida (`ftp://x` ou `notaurl`), **then** 400 na validação.
- [ ] **CA-010:** **Given** feed com mais de `maxItems` itens, **then** trunca nos mais recentes + warning + `Truncated=true` (reconciliation não apaga itens fora da janela).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `content:encoded` + `description` | item com ambos | usa `content:encoded` |
| data inválida | `pubDate=blah` | `PublishedAt=null`, fingerprint sem data |
| item sem guid/link/title | entry mínima | skip com warning |
| redirect de feed | 301 → nova URL | HttpClient segue; UriReference usa link do item, não do feed |
| feed com Basic auth na URL | `https://u:p@h/feed` | funciona; `LastError` redige `u:p@` |
| item atualizado, mesmo guid | typo corrigido no post | só reindexa com `forceRefresh` (limitação documentada) |

## 7. Task Plan

- [ ] **T1 — `FeedParser`:** RSS 2.0 + Atom 1.0, namespace-tolerant, `FeedItem`, `FeedParseException`. **Validação:** unit tests com fixtures XML reais (RSS/Atom/malformado/datas).
- [ ] **T2 — `RssFeedConnector`:** fetch, mapeamento, bound/`Truncated`, fingerprint/stub, `fetchFullContent` via `HtmlTextExtractor`, `FetchItemAsync`, sanitização de URL em erros. **Validação:** unit + integração com handler fake.
- [ ] **T3 — Serviço/DI/auto-sync:** `RequiredKeys`, validação de `feedUrl`, `AddHttpClient("feed")`, registro, whitelist. **Validação:** integration tests de CRUD/sync.
- [ ] **T4 — UI:** seção RSS no `SourceEditDialog` + `ManagedKeys`/`Validate`/model. **Validação:** build WASM + smoke.
- [ ] **T5 — Docs:** `CLAUDE.md`/README — conector RSS/Atom, `fetchFullContent`, `forceRefresh`, limitação do fingerprint.

**7.1 Validation strategy (.NET):** unit tests de parser e conector; integration tests do pipeline; `dotnet build` 0 warnings, `dotnet test` verde, `dotnet format --verify-no-changes`; cobertura ≥80% nos arquivos novos.

## 8. Organization Guardrails

- **Branches:** `feature/Devin-20260927-rss-feed-connector` a partir de `main`; nunca commitar em `main`/`master`/`develop`.
- **Workflows:** `.github/workflows/` protegido — sem alterações.
- **Secrets:** nenhum campo de segredo dedicado; URLs podem conter Basic auth — sempre redigir `user:pass@` em logs/`LastError`.
- **Scope:** só RSS 2.0 + Atom 1.0, só polling, sem WebSub/OPML/enclosures — fora de escopo §2.
- **Architecture:** parse e fetch no conector (Ingestion); conector não acessa `DbContext` — fingerprint map vem do `IngestionService`.
- **Deps:** **nenhuma dependência nova** — `System.Xml.Linq` + `HttpClient` + `HtmlTextExtractor` existentes bastam.

## 9. Definition of Done

- [ ] Todos os RFs (§4) implementados.
- [ ] CA-001..CA-010 cobertos por testes passando (unit + integration conforme §7.1).
- [ ] Edge cases da tabela tratados.
- [ ] `dotnet build` 0 warnings · `dotnet test` verde · `dotnet format --verify-no-changes` exit 0.
- [ ] Guardrails §8 respeitados — zero deps novas, credenciais de URL redigidas.
- [ ] Documentação menciona o conector RSS/Atom e a limitação do fingerprint/`forceRefresh`.

**Next action after DoD:** `Status = Done` + PR na branch `feature/Devin-20260927-rss-feed-connector` referenciando o ticket.

## Open Questions / Pending Ambiguity

- Nenhuma — decisões tomadas na rodada de design (2026-09-27): parser próprio para zero deps, fingerprint por guid, `forceRefresh` como escape documentado para itens atualizados.
