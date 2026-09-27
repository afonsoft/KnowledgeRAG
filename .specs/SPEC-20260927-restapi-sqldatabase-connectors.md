# SPEC-20260927-restapi-sqldatabase-connectors

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `restapi-sqldatabase-connectors` |
| Type | `Feature` |
| Stack | `.NET 10 / HttpClient + System.Text.Json / Microsoft.Data.Sqlite + Npgsql` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-restapi-sqldatabase-connectors` |
| Ticket | `#263` — https://github.com/afonsoft/LangGraph-UI/issues/263 |
| Status | `Done` |

## 1. User Story

**As a** administrador do Knowledge MCP Hub
**I want** que os `SourceType.RestApi` e `SourceType.SqlDatabase` — já declarados no enum e já presentes na UI — tenham conectores de ingestão funcionais
**So that** eu possa indexar como documentos RAG as linhas de uma query SQL read-only ou os itens JSON de um endpoint REST — fechando a dívida técnica do enum e cobrindo fontes estruturadas genéricas sem conector dedicado.

**Problem context:**
`SourceType.RestApi` (enum `4`) e `SourceType.SqlDatabase` (enum `5`) existem desde a SPEC-02 com `RequiredKeys` configurados (`endpoint`; `connectionString`+`query`) e campos básicos no `SourceEditDialog`, mas **nenhum conector está registrado** — `IngestionService` reporta "connector not implemented" ao sincronizar. O pipeline compartilhado (`FetchResult` → dedup → `MarkdownChunker` → embed) já resolve tudo depois do fetch; o trabalho é implementar os dois `ISourceConnector` com mapeamento campo→documento e guardrails de read-only.

## 2. Scope

**In scope — RestApi:**
- `RestApiConnector` (`ISourceConnector`) que faz `GET` no `endpoint` configurado, extrai itens de um array JSON via `itemsPath` (dot-path), e mapeia cada item para `RawDocument` via `titleField`/`contentFields`/`urlField`/`idField`.
- Config: `endpoint` (req), `headers` (objeto JSON — movido ao `IIntegrationSecretStore`, chave `restapi:{sourceId}`, config persiste só `hasKey`), `itemsPath` (opcional; vazio = root é array, ou body inteiro vira 1 doc), `titleField`, `contentFields` (array de dot-paths), `urlField`, `idField`, `pageParam` (opcional — paginação `?{pageParam}=N`), `maxPages` (default 1, clamp 1–50).
- `JsonPathResolver` interno: dot-path simples `a.b.c` (sem `[]`, filtros ou wildcards — subset deliberado).
- Somente `GET` + resposta JSON (content-type `application/json` ou parse tolerante); qualquer outro formato → sync `failed` com `LastError` claro.

**In scope — SqlDatabase:**
- `SqlDatabaseConnector` (`ISourceConnector`) que abre conexão read-only, executa a `query` configurada (SELECT-only validado) e mapeia cada row para `RawDocument` via `idColumn`/`titleColumn`/`contentColumns`/`urlColumn`.
- Config: `provider` (req — `sqlite` | `postgres`), `connectionString` (req no create — movida ao `IIntegrationSecretStore`, chave `sql:{sourceId}`, config persiste só `hasKey`), `query` (req), `idColumn` (opcional), `titleColumn` (opcional), `contentColumns` (array, opcional — vazio = todas as colunas exceto id), `urlColumn` (opcional), `maxRows` (default 1000, clamp 1–10000), `commandTimeoutSeconds` (default 30, clamp 5–300).
- Providers v1: `sqlite` (`Microsoft.Data.Sqlite` — já transitiva via EF Core) e `postgres` (`Npgsql` — já referenciado). `sqlserver`/`mysql`/`oracle` fora de escopo (novas deps).
- Guardrail read-only: query deve ser statement único iniciado por `SELECT`/`WITH` (case-insensitive) e não conter `;` intermediário nem keywords de escrita (`INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|ATTACH|DETACH|PRAGMA|EXEC|EXECUTE|GRANT|REVOKE|COPY|CALL|TRUNCATE|VACUUM|MERGE|REPLACE`); Postgres executa dentro de transação `READ ONLY` + rollback; SQLite abre com `Mode=ReadOnly` quando o connstring não especifica `Mode`.
- `connectionString`/`headers` saem do `ConfigurationJson` para o secret store (padrão `PersistProxySecretAsync` — estendido para os dois tipos); remoção no `DeleteAsync`.
- Auto-sync: ambos entram no whitelist de `ScheduledSyncBackgroundService`.
- UI `SourceEditDialog.razor`: as seções RestApi/SqlDatabase **já existem** — esta SPEC as estende com os campos de mapeamento, select de `provider`, e trata `connectionString`/`headers` como segredos (placeholder quando `hasKey`).

**Out of scope:**
- `RestApi`: POST/PUT/DELETE, OAuth flows, paginação por cursor/`Link` header, JSONPath completo (`$..`, filtros), XML/SOAP, e auth por client certificate.
- `SqlDatabase`: SQL Server/MySQL/Oracle, queries multi-statement, streaming de LOBs grandes (valores são `.ToString()` truncados a 50 KB por coluna), sync incremental por `updated_at` (dedup por content hash basta — a query inteira re-executa a cada sync), e escrita DDL/DML de qualquer natureza.
- `IIncrementalSourceConnector` em ambos — para estes conectores fetch==query; fingerprint não economizaria chamada nenhuma.
- Webhooks/triggers — sync via polling (`SyncIntervalMinutes`) apenas.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Ingestion/Connectors/` — `RestApiConnector.cs` + `JsonPathResolver.cs` + `SqlDatabaseConnector.cs` + `SqlQueryGuard.cs` (validação SELECT-only) + `RestApiConfig`/`SqlConfig` records internos.
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs` — `RequiredKeys` (SqlDatabase perde `connectionString` direto → exceção `hasKey` como Notion), `PersistProxySecretAsync` += `RestApi → ("headers", "restapi:{id}")` e `SqlDatabase → ("connectionString", "sql:{id}")`, remoção no `DeleteAsync`, validação de `hasKey` sem segredo → 400.
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs` — whitelist += os dois tipos.
- `src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs` — `AddHttpClient("restapi", 30s)` + registro dos conectores.
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor` + `SourceEditModel` — campos de mapeamento; `connectionString`/`headers` com comportamento de segredo.
- `tests/KnowledgeHub.Tests.Unit` / `tests/KnowledgeHub.Tests.Integration` — resolver, guard, conectores (HttpMessageHandler fake; SQLite em memória/arquivo temp).

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Ingestion/Connectors/{ISourceConnector,ConnectorConfig,WebPageConnector,NotionConnector}.cs`
- `src/KnowledgeHub.Server/Ingestion/IngestionService.cs` (`SyncViaConnectorAsync`)
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs` (`RequiredKeys`, `PersistProxySecretAsync`, `SensitiveKeys`, `Redact`)
- `src/KnowledgeHub.Server/Settings/{IIntegrationSecretStore,IntegrationSecretStore}.cs`
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs` (~linha 50)
- `src/KnowledgeHub.Server/DatabasePath.cs` (resolução de paths relativos SQLite)
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor` (seções RestApi ~linha 111 / SqlDatabase ~linha 120 já existem)
- `.specs/SPEC-20260919-notion-connector.md` (padrão segredo + conector)

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Ingestion/Connectors/JsonPathResolver.cs         (create)
src/KnowledgeHub.Server/Ingestion/Connectors/RestApiConnector.cs         (create)
src/KnowledgeHub.Server/Ingestion/Connectors/SqlQueryGuard.cs            (create)
src/KnowledgeHub.Server/Ingestion/Connectors/SqlDatabaseConnector.cs     (create)
src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs               (modify)
src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs (modify)
src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs       (modify)
src/KnowledgeHub.Client/Pages/SourceEditDialog.razor                     (modify)
tests/KnowledgeHub.Tests.Unit/…                                          (create)
tests/KnowledgeHub.Tests.Integration/…                                   (create)
```

## 4. Requirements

### RF-001: `RestApiConnector` — fetch e paginação
- **Description:** `GET {endpoint}` com headers resolvidos do secret store (`restapi:{sourceId}`); parse JSON; se `pageParam` configurado, repete `?{pageParam}=1..N` (ou `&` se a URL já tiver query) até página sem itens ou `maxPages`.
- **Rules:** HttpClient nomeado `"restapi"` (timeout 30 s); não-2xx no primeiro fetch → sync `failed` (`401/403` → `LastError` "credenciais/endpoint inválidos", sem ecoar headers); não-2xx em página N>1 → warning + usa o que já coletou; JSON inválido → sync `failed`; sem auth embutida além de `headers` (usuário coloca `Authorization`/`X-Api-Key` lá).
- **Input → Output:** HTTP GETs → `JsonElement`s de itens.

### RF-002: `JsonPathResolver` e mapeamento item→documento
- **Description:** `Resolve(JsonElement, "a.b.c")` navega objetos por segmentos ponto-separados; segmento numérico indexa array. `itemsPath` seleciona o array (vazio → root: se array, usa-o; senão body inteiro = 1 item). Por item: `Title` ← `titleField` (fallback `item {i}`); `TextContent` ← `contentFields` (array de paths, join `\n\n`; vazio → item serializado `chave: valor` em linhas, objetos aninhados compactados em JSON); `UriReference` ← `idField`→`rest:{valor}`, senão `urlField`, senão `rest:{sha256(item json)}`.
- **Rules:** path que não resolve → campo ignorado com warning único por sync (não por item); item sem nenhum conteúdo → warning + skip; `contentFields` com path inválido → warning "field 'x' not found".
- **Input → Output:** `JsonElement` itens → `IReadOnlyList<RawDocument>`.

### RF-003: Segredo `headers` (RestApi)
- **Description:** `configuration.headers` (objeto JSON `{"Authorization":"Bearer …","X-Api-Key":"…"}`) é movido ao `IIntegrationSecretStore` em `restapi:{sourceId}`; config persistida guarda só `hasKey`. Mesmas regras do padrão: `hasKey=true` sem segredo → 400; `DeleteAsync` remove o segredo. Headers nunca em `LastError`/logs/GET.
- **Input → Output:** POST com headers → `hasKey:true`; sync resolve o segredo via `IIntegrationSecretStore`+`IServiceScopeFactory` (conector é singleton); ausência → `InvalidOperationException` clara.

### RF-004: `SqlQueryGuard` — SELECT-only
- **Description:** valida a `query` antes de abrir conexão: trimmed não-vazio; primeiro token `SELECT` ou `WITH` (case-insensitive); no máximo um `;` terminal; sem keywords de escrita (lista §2) como palavra inteira fora de literais/comentários.
- **Rules:** implementação pragmaticamente simples — strip de comentários `--`/`/* */` e literais `'…'`/`"…"` antes da busca de keywords; falha → 400 no save **e** sync `failed` se config burlar a validação (defesa em duas camadas); log registra só a frase de rejeição, nunca a query inteira se contiver dados (query é metadata — logar até 200 chars truncados é aceitável).
- **Input → Output:** string query → `true`/`false` + motivo.

### RF-005: `SqlDatabaseConnector` — execução e mapeamento
- **Description:** resolve `connectionString` do secret store (`sql:{sourceId}`); abre `SqliteConnection`/`NpgsqlConnection` conforme `provider`; executa a query com `CommandTimeout = commandTimeoutSeconds`; mapeia rows.
- **Rules:** `provider` ∈ `sqlite|postgres` (case-insensitive; inválido → 400 no save e sync failed); sqlite — path relativo em `Data Source` resolve via `DatabasePath` e força `Mode=ReadOnly` se `Mode` ausente; postgres — executa em transação `READ ONLY` (`SET TRANSACTION READ ONLY`) com rollback ao final; `maxRows` excedido → para de ler, warning "truncated at maxRows", `FetchResult.Truncated=true`; `UriReference` ← `idColumn`→`sql:{valor}` (multi-col id: join `:`), senão `sql:{sha256(row)}`; `Title` ← `titleColumn` (fallback `row {n}`); `TextContent` ← `contentColumns` (`col: valor` por linha; vazio → todas as colunas exceto id), valores >50 KB truncados com marcador `[…truncated]`; tipos viram `.ToString()` invariante (bytea/bool/data ok).
- **Input → Output:** rows → `IReadOnlyList<RawDocument>`.

### RF-006: Segredo `connectionString` e `RequiredKeys`
- **Description:** `RequiredKeys[SqlDatabase]` muda de `["connectionString","query"]` para `["provider","query"]`; `connectionString` passa ao secret store `sql:{sourceId}` com `hasKey` (mesma exceção documentada do `token` Notion: update com `hasKey=true`+segredo existente passa sem reenviar; `hasKey=true` sem segredo → 400). `RequiredKeys[RestApi]` permanece `["endpoint"]`.
- **Rules:** `PersistProxySecretAsync` += os dois mapeamentos; `DeleteAsync` remove `sql:{id}` e `restapi:{id}`; `SensitiveKeys` já cobre `connectionString`/`headers` na redação de GET — manter como segunda camada.
- **Input → Output:** POST com connstring → `hasKey:true`, GET nunca ecoa.

### RF-007: Auto-sync, DI e UI
- **Description:** ambos os tipos no whitelist de `ScheduledSyncBackgroundService`; `AddHttpClient("restapi", 30s)` + `AddSingleton<ISourceConnector, RestApiConnector/SqlDatabaseConnector>`; UI estende as seções existentes.
- **Rules:** RestApi — campos `itemsPath`, `titleField`, `contentFields` (textarea, um por linha), `urlField`, `idField`, `pageParam`, `maxPages`; `headers` vira textarea com placeholder `"{"Authorization":"Bearer …"}"` e nota "armazenado criptografado" (placeholder quando `hasKey`). SqlDatabase — `provider` (Select sqlite/postgres), `connectionString` textarea (comportamento segredo), `query` textarea (já existe), `idColumn`, `titleColumn`, `contentColumns`, `urlColumn`, `maxRows`, `commandTimeoutSeconds`. `ManagedKeys` += `provider`, `itemsPath`, `titleField`, `contentFields`, `urlField`, `idField`, `pageParam`, `maxPages`, `idColumn`, `titleColumn`, `contentColumns`, `urlColumn`, `maxRows`, `commandTimeoutSeconds` (`endpoint`, `headers`, `connectionString`, `query` já estão).
- **Input → Output:** dialogs salvam configs válidas; GET repopula (segredos nunca).

## 5. API Contract

**Outbound — RestApi:** `GET {endpoint}[?|&]{pageParam}={1..N}` com headers do secret store. Erros: `401/403` → failed; `404` → failed ("endpoint não encontrado"); `429` → honrar `Retry-After` 1×, senão failed; `5xx`/timeout → failed (primeira página) ou warning (páginas seguintes).

**Outbound — SqlDatabase:** conexão `Microsoft.Data.Sqlite`/`Npgsql` + comando texto validado. Erros: connstring inválida/host inalcançável/timeout → sync `failed` com `LastError` sanitizado (sem connstring); erro SQL (tabela inexistente, permissão) → `failed` com mensagem do provider truncada a 300 chars e sem parâmetros de conexão.

**Inbound:** nenhum endpoint novo — reuso de `/api/sources`; a validação de `query`/`provider`/`language` ocorre no `KnowledgeSourceService` (400 com mensagem clara).

## 6. Acceptance Criteria

- [ ] **CA-001:** **Given** source RestApi com endpoint fake retornando `{"items":[{id,title,body}]}`, **when** sync, **then** cada item indexa como documento e responde em `search_knowledge`.
- [ ] **CA-002:** **Given** `itemsPath="data.results"` e `contentFields=["title","body"]`, **when** fetch, **then** docs têm title/conteúdo mapeados; path ausente gera warning único.
- [ ] **CA-003:** **Given** `pageParam="page"`+`maxPages=3` e API com 2 páginas, **when** sync, **then** itens das 2 páginas indexam e a 3ª chamada não ocorre (página vazia encerra).
- [ ] **CA-004:** **Given** `headers` configurados, **then** GET `/api/sources/{id}` nunca ecoa os valores — só `hasKey:true`; e o secret store é consultado no sync.
- [ ] **CA-005:** **Given** source SqlDatabase `sqlite` + `SELECT id,title,body FROM notes` num arquivo temp, **when** sync, **then** cada row vira documento `sql:{id}`.
- [ ] **CA-006:** **Given** query `DELETE FROM x` ou `SELECT 1; DROP TABLE y`, **when** save/sync, **then** rejeitada com 400/`failed` antes de abrir conexão.
- [ ] **CA-007:** **Given** postgres fake (connstring válida, tabela com 1500 rows, `maxRows=1000`), **when** sync, **then** 1000 docs + warning de truncamento + `Truncated=true`.
- [ ] **CA-008:** **Given** `connectionString` no create, **then** config persistida tem `hasKey:true` e a connstring sai do JSON; update sem reenviar connstring mantém o segredo.
- [ ] **CA-009:** **Given** re-sync sem mudanças, **when** sync roda, **then** `DocumentsSkipped=N`, `DocumentsProcessed=0` (dedup por hash — sem refetch parcial pois a query re-executa integralmente).
- [ ] **CA-010:** **Given** `AutoSyncEnabled=true`, **then** sync periódico dispara para sources RestApi e SqlDatabase.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| endpoint retorna HTML | content-type errado | sync `failed`, `LastError` claro |
| `itemsPath` aponta objeto (não array) | `{"data":{"a":1}}` | body/objeto vira 1 doc ou warning claro — decisão: 1 doc serializado |
| row com NULLs | colunas null | `col:` omitida na serialização |
| connstring com senha em erro | host inalcançável | `LastError` sanitizado — nunca ecoa connstring |
| query com keyword em literal | `SELECT 'DROP'` | permitida (keywords só fora de literais/comentários) |
| sqlite path relativo | `Data Source=notes.db` | resolve via `DatabasePath`, `Mode=ReadOnly` aplicado |
| `hasKey:true` sem segredo | update stale | 400 com mensagem clara |

## 7. Task Plan

- [ ] **T1 — `SqlQueryGuard` + `JsonPathResolver`:** helpers puros. **Validação:** unit tests (keywords, literais, paths válidos/inválidos).
- [ ] **T2 — `RestApiConnector`:** fetch, paginação, mapeamento, segredo `headers`. **Validação:** unit tests com `HttpMessageHandler` fake + integration CRUD/sync.
- [ ] **T3 — `SqlDatabaseConnector`:** guard→open→query→map, read-only enforcement por provider, `maxRows`/`Truncated`. **Validação:** unit tests com sqlite temp + unit tests de mapeamento; postgres coberto por fake/`DbConnection` seam quando disponível.
- [ ] **T4 — Serviço/segredos/DI:** `RequiredKeys` ajustado, `PersistProxySecretAsync` estendido, delete cleanup, `AddHttpClient("restapi")`, registro, whitelist auto-sync. **Validação:** integration tests.
- [ ] **T5 — UI:** extensão das seções RestApi/SqlDatabase + `ManagedKeys`/`Validate`/model (segredos com placeholder). **Validação:** build WASM + smoke.
- [ ] **T6 — Docs:** `CLAUDE.md`/README — conectores RestApi/SqlDatabase funcionais, formato de `headers`, guardrail SELECT-only, providers suportados.

**7.1 Validation strategy (.NET):** unit tests de helpers e conectores; integration tests do pipeline; `dotnet build` 0 warnings, `dotnet test` verde, `dotnet format --verify-no-changes`; cobertura ≥80% nos arquivos novos.

## 8. Organization Guardrails

- **Branches:** `feature/Devin-20260927-restapi-sqldatabase-connectors` a partir de `main`; nunca commitar em `main`/`master`/`develop`.
- **Workflows:** `.github/workflows/` protegido — sem alterações.
- **Secrets:** `connectionString` e `headers` **somente** via `IIntegrationSecretStore`; nunca em `ConfigurationJson` persistido, logs, `LastError` ou respostas de API.
- **Scope:** sem métodos HTTP além de GET, sem providers SQL além de sqlite/postgres, sem JSONPath completo — fora de escopo §2.
- **Architecture:** validação de query/provider no service; fetch nos conectores; conectores não acessam `DbContext` do hub — apenas suas próprias conexões read-only.
- **Deps:** **nenhuma dependência nova** — `Npgsql` e `Microsoft.Data.Sqlite` (via EF Core) já estão no projeto; RestApi usa `System.Text.Json` + `HttpClient` existentes.

## 9. Definition of Done

- [ ] Todos os RFs (§4) implementados.
- [ ] CA-001..CA-010 cobertos por testes passando (unit + integration conforme §7.1).
- [ ] Edge cases da tabela tratados.
- [ ] `dotnet build` 0 warnings · `dotnet test` verde · `dotnet format --verify-no-changes` exit 0.
- [ ] Guardrails §8 respeitados — segredos no store, SELECT-only enforced, zero deps novas.
- [ ] Documentação menciona os dois conectores funcionais, formato de `headers` e providers SQL.

**Next action after DoD:** `Status = Done` + PR na branch `feature/Devin-20260927-restapi-sqldatabase-connectors` referenciando o ticket.

## Open Questions / Pending Ambiguity

- Nenhuma — decisões tomadas na rodada de design (2026-09-27): GET-only + dot-path subset no RestApi; SELECT-only + read-only tx no SqlDatabase; segredos seguem o padrão `PersistProxySecretAsync`.
