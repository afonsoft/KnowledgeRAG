# SPEC-20261004-context-snapshots (v4)

## 0. Metadata

| Field | Value |
|---|---|
| Feature | `context-snapshots` — persistência e recuperação de contexto compactado por repositório, com 3 tools MCP: `context_save`, `context_recovery`, `context_compact` |
| Type | `Feature` (Backend: MCP tools + REST + EF Core + cache) |
| Stack | `.NET 10 / ASP.NET Core` Minimal APIs, EF Core (SQLite + Postgres), `HybridCache`/`ICacheInvalidationBus`, `Microsoft.Extensions.AI` (`IChatClient` via `IAssistantChatClientProvider`) |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261020-context-snapshots` |
| Ticket | [#585](https://github.com/afonsoft/KnowledgeRAG/issues/585) |
| Status | `Approved` |
| Supersedes | SPEC v1 (`6aae991c-…`), v2 (`318fde74-…`), v3 (draft `20261004`) — ver seção 10 |

## 1. User Story

- **As a** agente de IA que usa o Knowledge MCP Hub (`agent_chat`, Claude Code, Cursor, Devin, etc.),
- **I want** salvar o estado de contexto de um trabalho, recuperá-lo depois e compactá-lo sob demanda, sempre dentro do escopo de um repositório,
- **So that** sessões longas continuem sem perder decisões e sem estourar a janela, e o custo caia por reaproveitamento via cache.

### Problem context
- Hoje não existe persistência de contexto de trabalho: o histórico vive em `ConversationMessage` (por thread) e o `ChainCompactor` só atua na janela enviada ao modelo, sem salvar nada.
- Não há parâmetro de repositório nas tools. Toda busca é global sobre as fontes ativas.
- A compactação no loop do agente (`ChainCompactor`) opera sobre `ChainAst` (árvore de `ChatMessage` parseada) com truncamento + fold de seções — **não existe elision por item** (`[Output elided: …]`). Não há `ElisionStub` no código; a elision da RF-006 é implementação nova sobre a lista de itens.
- Decisão de OQ-3 (v3): o contexto salvo **não** vai para o vault nem para `write_note`. Vai para a tabela nova `ContextSnapshots` no banco do catálogo (SQLite e Postgres).

## 2. Scope

### In scope
1. Tabela `ContextSnapshots` com migrations **nas duas pastas**: `Migrations/` (SQLite, `KnowledgeHubDbContext`) e `Migrations/Postgres/` (`PostgresKnowledgeHubDbContext`).
2. Tool MCP `context_save(repo, …)`: grava um snapshot versionado do contexto do repo.
3. Tool MCP `context_recovery(repo, …)`: retorna o snapshot mais recente (ou um específico / por `label`) já ajustado ao orçamento pedido.
4. Tool MCP `context_compact(repo, …)`: compacta um conjunto de itens (ou um snapshot) e, opcionalmente, persiste o resultado via `save=true`.
5. Parâmetro obrigatório `repo` nas três tools, normalizado (case-insensitive, trim, `owner/name`; URL GitHub aceita).
6. **Camada de cache própria** (`ContextCacheService` sobre `HybridCache`) com chaves `ctx:*` e tag de invalidação por repo — ver RF-007. **Não** usar `IToolCacheService` (ver decisão de design D-1).
7. REST espelho: `POST /api/context/snapshots`, `GET /api/context/snapshots/latest`, `GET /api/context/snapshots/{version}`, `GET /api/context/snapshots`, `POST /api/context/compact`.
8. Idempotência opcional em `context_save` via `idempotencyKey`.

### Out of scope
- Recall de elisão (`recall_event`) — sem ganho evidenciado.
- Salvar em vault (`write_note`) — descartado (OQ-3).
- Alterar `ConversationMessage` ou o histórico persistido de threads.
- ACL por repositório na `ApiKey` (o modelo de escopo atual cobre fontes e tools, não repos — ver RF-001).
- Sync entre instâncias ou multi-tenant além do que `CallerScope` já dá.
- Parâmetro `repo` em `search_knowledge`/`ask_knowledge` — SPEC separada.
- `DELETE` de snapshots (retenção RF-002 já cobre higiene; reavaliar se surgir demanda).
- Alterar `.github/workflows/`.

## 3. Technical Context

### Arquivos a ler (verificados nesta revisão)
- `src/KnowledgeHub.Server/Data/KnowledgeHubDbContext.cs` — `DbSet`s + `OnModelCreating` com blocos `modelBuilder.Entity<T>`; usar o padrão `Database.IsNpgsql() ? "jsonb" : "TEXT"` para `PayloadJson` (mesmo padrão de `DocumentChunk.Embedding` bytea/BLOB).
- `src/KnowledgeHub.Server/Data/PostgresKnowledgeHubDbContext.cs` — subclasse que possui o segundo conjunto de migrations (`Migrations/Postgres/`); EF resolve por tipo concreto anotado na migration. `ProviderAwareModelCacheKeyFactory` já chaveia o modelo por provider.
- `src/KnowledgeHub.Server/Data/DesignTimeDbContextFactories.cs` — factories design-time dos dois contexts (permitem `dotnet ef migrations add` para ambos).
- `src/KnowledgeHub.Server/Migrations/` + `src/KnowledgeHub.Server/Migrations/Postgres/` — **OQ-A resolvida**: duas pastas, uma por provider. Últimas: `20261003210458_AddFlowTriggersAndApproval` (SQLite) e `20261003210526_AddFlowTriggersAndApproval` (Postgres).
- `src/KnowledgeHub.Server/Domain/Entities/` — **pasta correta das entidades** (o draft citava `Data/Entities/`, que não existe).
- `src/KnowledgeHub.Server/Domain/Entities/ApiKey.cs` — escopo real da chave: `AllowedSourceIdsJson`, `AllowedToolsJson`, `AllowWrite`. **Não há escopo por repo.**
- `src/KnowledgeHub.Server/Auth/CallerScope.cs`, `Auth/ScopeAudit.cs`, `Auth/ICallerScopeProvider` — `AllowsTool`, `AllowWrite`, `RecordToolDeniedAsync`.
- `src/KnowledgeHub.Server/Mcp/CatalogTool.cs` — contrato da tool (`ReadOnly`, `DestructiveHint`, `IdempotentHint`, `InputSchema`, `OutputSchema`, `Handler`).
- `src/KnowledgeHub.Server/Mcp/ToolProviders/KnowledgeToolsProvider.cs` — padrão `IToolProvider`: `JsonObject` schema com `examples`, handlers `static ValueTask<CallToolResult>(ToolCallContext, CancellationToken)`, `ToolArgs.*`, `ToolResults.Structured`, budget clamp 256–32768.
- `src/KnowledgeHub.Server/Mcp/DynamicToolCatalog.cs` — filtro por `CallerScope.AllowsTool` (tools fora da allowlist da key nem aparecem).
- `src/KnowledgeHub.Server/Caching/ToolCacheService.cs` — `IsCacheable(name, isReadOnly)`: **todo tool `ReadOnly=true` é auto-cacheada** por `(tool, args, indexVersion)`, TTL mínimo 1 h. RF-007 detalha por que `context_*` precisa ser excluída.
- `src/KnowledgeHub.Server/Caching/TrackingHybridCache.cs` — `HybridCache` com `RemoveByTagAsync(tag)`.
- `src/KnowledgeHub.Server/Caching/EndpointCache.cs` — precedente exato da invalidação por tag: `cache.RemoveByTagAsync(tag)` + `bus.PublishAsync($"cache-tag:{tag}")`.
- `src/KnowledgeHub.Server/Caching/ICacheInvalidationBus.cs` + `InvalidationSubscriber.cs` — canal `kh:invalidate`; o subscriber **já** trata `cache-tag:{tag}` derrubando L1 remoto via `HybridCache.RemoveByTagAsync`.
- `src/KnowledgeHub.Server/Configuration/CacheOptions.cs` — `RegionTtlMinutes` (dict por região: `RegionTtlMinutes["ctx"]`), `ToolCacheEnabled`.
- `src/KnowledgeHub.Server/Agent/CatalogToolAIFunction.cs` + `Api/ToolsEndpoints.cs` — ambos consultam `IToolCacheService` para tools `ReadOnly` (o “cache automático” que motiva D-1).
- `src/KnowledgeHub.McpEngine/Agents/ChainAst/ChainCompactor.cs` — `BuildSummary` é `private static` (não reutilizável como está); opera sobre `ChainAst`, não sobre itens.
- `src/KnowledgeHub.Server/Assistant/IAssistantChatClientProvider.cs` — `ForSubtask("summarize", main)`; usado em `Services/AgentService.cs:433` e `Search/QueryRewriting.cs`.
- `src/KnowledgeHub.Server/RateLimiting/McpToolRateLimiter.cs` + `RateLimitOptions.cs` — **duas partições fixas** (`LlmTools`, `SyncTools`); partição `context` exige novo `PartitionedRateLimiter` + novas chaves de config.
- `src/KnowledgeHub.Server/Mcp/Bridge/ToolActionAnnotationDetector.cs` — **não é** registro de annotations: detecta live-actions (`<!-- mcp-tool: -->` + menções). `context_*` deve entrar em `NeverLiveTools`.
- `src/KnowledgeHub.Server/Api/ToolsEndpoints.cs` — passthrough REST gratuito: `POST /api/tools/{name}` já invoca qualquer CatalogTool. As rotas `/api/context/*` existem para dar **status HTTP reais** (400/403/404/413) e semântica GET.
- `src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs` — `Validate*` + `RequirePositiveInt`, lista `problems`.
- `src/KnowledgeHub.Server/appsettings.json` — `Agent:ContextManagement` (KeepMinLastSections=2 — semântica de *seções* AST, distinta dos *itens* desta SPEC).

### Arquivos a criar
- `src/KnowledgeHub.Server/Domain/Entities/ContextSnapshot.cs`
- `src/KnowledgeHub.Server/Context/RepoScope.cs` — normalização/validação de `repo`.
- `src/KnowledgeHub.Server/Context/ContextOptions.cs` — `SectionName = "Context"`.
- `src/KnowledgeHub.Server/Context/ContextSnapshotService.cs` — save/latest/get-by-version/get-by-label/list (EF + retenção + invalidação).
- `src/KnowledgeHub.Server/Context/ContextCacheKeys.cs` — chaves `ctx:v1:*` e tag `ctx:{repoKey}`.
- `src/KnowledgeHub.Server/Context/ContextCacheService.cs` — wrapper sobre `HybridCache` + `ICacheInvalidationBus` (reporta `cacheHit`).
- `src/KnowledgeHub.Server/Context/ContextCompactionService.cs` — elision por item (novo) + summarization com fallback.
- `src/KnowledgeHub.Server/Context/ContextPayload.cs` — DTOs `items[]` + validação (limites, caracteres de controle, secret scan).
- `src/KnowledgeHub.Server/Mcp/ToolProviders/ContextToolsProvider.cs` — `IToolProvider` com as 3 tools.
- `src/KnowledgeHub.Server/Api/ContextEndpoints.cs` — REST espelho com ProblemDetails.
- `src/KnowledgeHub.Server/Migrations/{ts}_AddContextSnapshots.cs` + `.Designer.cs` (SQLite) e `Migrations/Postgres/{ts}_AddContextSnapshots.cs` + `.Designer.cs`.
- `tests/KnowledgeHub.Tests.Unit/Context/RepoScopeTests.cs`, `ContextPayloadTests.cs`, `ContextCompactionServiceTests.cs`, `ContextCacheKeysTests.cs`.
- `tests/KnowledgeHub.Tests.Integration/Context/ContextToolsTests.cs` (SQLite) + teste Postgres condicional a `Postgres:ConnectionString`.

### Arquivos a modificar
- `KnowledgeHubDbContext.cs` — `DbSet<ContextSnapshot>` + bloco `Entity<ContextSnapshot>` no `OnModelCreating`.
- `KnowledgeHubServiceCollectionExtensions.cs` — registrar `ContextSnapshotService` (scoped), `ContextCompactionService`, `ContextCacheService` (singletons), `ContextToolsProvider` em `AddToolCatalog`, `MapContextEndpoints`.
- `Caching/ToolCacheService.cs` — exclusão de `context_*` em `IsCacheable` (D-1).
- `RateLimiting/McpToolRateLimiter.cs` + `RateLimitOptions.cs` — partição `context` (RF-009).
- `Mcp/Bridge/ToolActionAnnotationDetector.cs` — adicionar as 3 tools a `NeverLiveTools`.
- `Configuration/ConfigurationValidator.cs` — `ValidateContext` (RF-010).
- `src/KnowledgeHub.Server/appsettings.json` — bloco `Context` + `RateLimiting:Context*`.
- `CLAUDE.md`, `README.md`, `README.pt-br.md`, `docs/{en,pt}/` — catálogo de tools e config.

## 4. Requirements

### RF-001 — Parâmetro `repo` obrigatório (`RepoScope`)
- **Description:** toda tool de contexto exige o repositório de escopo.
- **Rules:**
  - Formato: `owner/name`, 1–100 chars por segmento, `[A-Za-z0-9._-]`.
  - Normalização: trim, lowercase; armazenado e comparado em lowercase. `afonsoft/KnowledgeRAG` ≡ `afonsoft/knowledgerag`.
  - URL completa `https://github.com/{owner}/{name}[.git]` é aceita e reduzida a `owner/name`.
  - Ausente/vazio/inválido → `invalid_repo` (MCP `isError`; REST `400`).
  - **Escopo da chave (correção sobre a v3):** não existe escopo por repo na `ApiKey`. O que se aplica: (a) `AllowedToolsJson` — a tool some do catálogo se não permitida (filtro do `DynamicToolCatalog`); (b) `AllowWrite=false` nega **`context_save`** pelo gate padrão (`ReadOnly=false`); **`context_compact save=true` executa mesmo com `AllowWrite=false`** — snapshots são estado caller-scoped de trabalho, não escrita na base de conhecimento/vault (decisão de produto, OQ-H); (c) `ScopeAudit.RecordToolDeniedAsync` em qualquer negação.
- **Input → Output:** string → `RepoScope { Owner, Name, Key = "owner/name" }`.

### RF-002 — Tabela `ContextSnapshots`
- **Schema:**

| Coluna | Tipo (SQLite / Postgres) | Regra |
|---|---|---|
| `Id` | `TEXT` / `uuid` | PK (`Guid`) |
| `RepoKey` | `TEXT(201)` / `varchar(201)` | `owner/name` lowercase, NOT NULL |
| `Version` | `INTEGER` / `int` | monotônico por `RepoKey`, começa em 1 |
| `Label` | `TEXT(200)` / `varchar(200)` NULL | rótulo opcional |
| `IdempotencyKey` | `TEXT(128)` / `varchar(128)` NULL | dedup de retries de `context_save` |
| `PayloadJson` | `TEXT` / `jsonb` | itens (RF-003), máx. `Context:MaxPayloadBytes` (512 KB) |
| `PayloadBytes` | `INTEGER` / `int` | bytes UTF-8 do payload serializado |
| `TokenEstimate` | `INTEGER` / `int` | `ceil(chars/4)` do payload |
| `Compacted` | `INTEGER` / `boolean` | true se gravado já compactado |
| `Strategy` | `TEXT(32)` / `varchar(32)` | `none` \| `elision` \| `elision+summary` |
| `Source` | `TEXT(64)` / `varchar(64)` | `mcp` \| `rest` |
| `CreatedAt` | `TEXT` / `timestamptz` | NOT NULL (`DateTimeOffset`) |
| `CreatedByKeyId` | `TEXT` / `uuid` NULL | `ApiKey.Id`; **sem FK** (mesmo padrão de `EvidenceReceipt.ApiKeyId` — a chave pode ser revogada/apagada sem orfanar snapshot) |

- **Índices:** `UX_ContextSnapshots_RepoKey_Version` (único — também serve lookup de latest por `Version DESC`); `UX_ContextSnapshots_RepoKey_IdempotencyKey` (único, filtrado `WHERE IdempotencyKey IS NOT NULL`); `IX_ContextSnapshots_RepoKey_CreatedAt` (retenção/list).
- **Retenção:** após inserir, `DELETE WHERE RepoKey=@r AND Version NOT IN (SELECT Version … ORDER BY Version DESC LIMIT @max)` na **mesma transação**; `pruned` retornado na resposta.
- **Migration:** `AddContextSnapshots` nas duas pastas (comandos na T3). Não altera tabelas existentes.

### RF-003 — Formato do payload
- `items`: array de até `Context:MaxItemsPerSnapshot` (200) de:
  - `{ "kind": "decision"|"note"|"passage"|"tool_output"|"summary", "id"?, "title"?, "path"?, "tool"?, "content": string ≤16 000 chars, "score"? }`
  - `tool` (string ≤200) carrega o nome da tool para o marcador de elision (`Tool: <name|unknown>`).
  - `kind=tool_output`/`passage` podem ser elididos/removidos; `decision`/`note`/`summary` nunca.
  - Campos desconhecidos → `invalid_payload` (rejeita payload arbitrário).
  - `content` com caracteres de controle (exceto `\n`, `\t`) → `invalid_payload`.
  - **Secret scan** em `content`, `title`, `path`, `tool` e `label`: padrões `aft_`, `sk-`, `ctx7sk-`, `ghp_`, `gho_`, `github_pat_`, `xox[bap]-`, `AIza`, `-----BEGIN` → `secret_detected`. Conteúdo nunca é logado (nem no erro — só o índice do item).
- Serialização canônica (ordem de propriedades fixa) — é a entrada do hash de cache de RF-007.

### RF-004 — `context_save`
- **Description:** grava um snapshot novo para o repo (idempotente com `idempotencyKey`).
- **Rules:**
  - Entrada: `repo`, `items` (RF-003), `label?`, `idempotencyKey?`, `compact?` (`none` default \| `elision` \| `elision+summary`), `tokenBudget?` (default `Context:DefaultTokenBudget`, clamp 256–32768).
  - `idempotencyKey` presente e já existente para o repo → retorna o snapshot existente (`200`, `deduplicated: true`) sem inserir nem incrementar versão.
  - `compact != none` → aplica RF-006 antes de gravar; `Compacted=true`, `Strategy` gravado.
  - `Version = max(Version do repo)+1` dentro de transação; conflito de unique → retry até 3×.
  - Após gravar: `ContextCacheService.InvalidateRepo(repoKey)` (`RemoveByTagAsync("ctx:{repoKey}")` + `PublishAsync("cache-tag:ctx:{repoKey}")`) + retenção (RF-002).
  - Resposta: `{ id, repo, version, createdAt, tokenEstimate, compacted, strategy, pruned, deduplicated }`.
- **Erros:** `invalid_repo` (400), `invalid_payload` (400), `secret_detected` (400), `payload_too_large` (413: >`MaxPayloadBytes` ou >`MaxItemsPerSnapshot`), negação de escopo (isError/403).

### RF-005 — `context_recovery`
- **Description:** retorna o contexto salvo ajustado ao orçamento.
- **Rules:**
  - Entrada: `repo`, `version?` (default: mais recente), `label?` (resolve o snapshot mais recente com aquele label; exclusivo com `version`), `tokenBudget?` (default 4096, clamp 256–32768), `strategy?` (`none` default \| `elision` \| `elision+summary`).
  - `version` e `label` juntos → `invalid_payload`. `version` ≤ 0 → `invalid_payload`.
  - Sem match → `context_not_found` (MCP `isError`; REST `404`).
  - `strategy=none` → itens como estão, cortados por `tokenBudget` (remove do fim `tool_output` primeiro, depois `passage`; `decision`/`note`/`summary` ficam).
  - Demais strategies → RF-006 sobre **cópia**; o snapshot persistido nunca muda.
  - Resposta: `{ repo, version, label, createdAt, items, stats: { tokensBefore, tokensAfter, budget, droppedCount, elidedCount, summaryUsed, summaryFallback, cacheHit }, withinBudget }`.

### RF-006 — Núcleo de compactação (Tier 4)
- **Description:** lógica compartilhada por `context_compact`, `context_recovery` e `context_save(compact=…)`. **Implementação nova em `ContextCompactionService`** — `ChainCompactor` não se aplica (opera em `ChainAst` de `ChatMessage`, e `BuildSummary` é `private`).
- **Rules:**
  - Estimativa: `ceil(chars/4)` por item (mesma aproximação de `ApplyTokenBudget`; imprecisa para CJK — documentado).
  - **Estágio 1 (elision, determinística, idempotente):** `tool_output`/`passage` fora das últimas `Context:KeepMinLastItems` (default 3) posições e acima de `Context:ElisionTokenLimit` (150 tokens) viram stub `[Output elided: N tokens, M lines. Tool: <name|unknown>. Ref: none]`. `decision`/`note`/`summary` intocados.
  - **Estágio 2 (summarization):** só se após estágio 1 `tokens > tokenBudget`. Entrada: itens elegíveis mais antigos até `SummaryInputMaxChars` (24 000). Saída ≤ `SummaryMaxTokens` (512) como `kind=summary` inserido na posição do primeiro item sumarizado. Cliente: `IAssistantChatClientProvider.ForSubtask("summarize", main)` (padrão de `AgentService.cs:433`), timeout `Context:SummaryTimeoutSeconds` (30 s). Erro/timeout/sem provider → fallback determinístico (mesma fórmula de `ChainCompactor.BuildSummary` — extrair para util compartilhado ou replicar) com `summaryFallback=true`. **Nunca** propaga erro HTTP/isError por causa da summarization.
  - **Estágio 3 (drop):** ainda excedendo → remove `passage` de menor `score` (empate: mais antigo), depois `tool_output` mais antigo. `decision`/`note`/`summary` nunca removidos; se só restarem esses e exceder → `withinBudget=false` (sem erro).
- **Invariantes:** ordem relativa dos itens restantes preservada; nunca grava no vault; nunca toca `ConversationMessage`; summarization recebe conteúdo **já escaneado** por RF-003 (nenhum secret chega ao LLM).

### RF-007 — Cache de contexto (camada própria, **não** `IToolCacheService`)
- **Description:** leituras frequentes evitam recompactação e I/O.
- **Design decision D-1 (correção crítica sobre a v3):** `IToolCacheService.IsCacheable(name, readOnly)` cacheia **qualquer** tool `ReadOnly` com chave `(tool, args, indexVersion)` e TTL mínimo de 1 h — e `indexVersion` **não** muda em `context_save`. Se `context_recovery`/`context_compact` passassem por ela, respostas `latest` ficariam stale por ≥1 h. Portanto:
  - `ToolCacheService.IsCacheable` ganha exclusão por prefixo `context_` (as 3 tools nunca entram no tool cache).
  - `ContextCacheService` usa `HybridCache` (`TrackingHybridCache` já registrado) diretamente, com tags próprias e TTL de região.
- **Rules:**
  - Chave recovery: `ctx:v1:{repoKey}:{version|"latest"}:{strategy}:{tokenBudget}`. Chave compact: `ctx:v1:{repoKey}:cmp:{sha256(canonicalItems)[0..16]}:{strategy}:{tokenBudget}` (hash via `CacheKeys.Hash`).
  - Tag de invalidação: `ctx:{repoKey}` em **todas** as entradas. `context_save` (e retenção) chamam `HybridCache.RemoveByTagAsync(tag)` + `ICacheInvalidationBus.PublishAsync($"cache-tag:{tag}")` — o `InvalidationSubscriber` já derruba L1 remoto para esse tópico (precedente `EndpointCache`).
  - Ponteiro `ctx:v1:{repoKey}:latestVersion` (TTL 1 h) atualizado no save, para a chave `latest`.
  - TTL: `Cache:RegionTtlMinutes:ctx` (default 60 min).
  - **Nunca cachear:** `context_save`; `context_compact(save=true)` (a resposta carrega `savedVersion` — cachear reexecutaria write-report sem write); respostas com `summaryFallback=true` (não fixar resultado degradado).
  - Chaves contêm apenas `repoKey` + hashes — nunca conteúdo bruto.
  - `Context:CacheEnabled` (default `true`) → desligado, mesma resposta sem cache.
  - `cacheHit` é reportado por `ContextCacheService` (o `IToolCacheService` nunca expõe hit/miss ao caller).
  - Falha do cache (Redis down) → warning + resposta normal (padrão `SafeCache`).
  - Telemetria: `context.cache.hit`/`context.cache.miss` por tool.
- **Invariantes:** chave sempre inclui `repoKey` (nunca vaza entre repos); versão inexistente nunca é servida do cache (chave inclui version/latestVersion resolvido).

### RF-008 — `context_compact`
- **Description:** compacta `items` inline ou um snapshot existente.
- **Rules:**
  - Entrada: `repo`, `items?` **xor** `fromVersion?`, `strategy` (default `elision+summary`), `tokenBudget` (default 4096, clamp), `save?` (default false), `label?`, `idempotencyKey?` (só com `save=true`).
  - `save=true` → grava snapshot `Compacted=true` (RF-004 completo, incl. retenção, invalidação e dedup) e retorna `savedVersion`.
  - `save=true` **executa mesmo com `CallerScope.AllowWrite=false`** — a persistência de snapshot é estado caller-scoped, não escrita na base de conhecimento (decisão de produto, OQ-H). `context_save` continua gated por `AllowWrite` (`ReadOnly=false`).
  - Resposta: `{ repo, compacted: items[], droppedCount, elidedCount, stats (RF-005), savedVersion? }`.
  - Erros: `400` se ambos/nenhum de `items`/`fromVersion`; `404` se `fromVersion` inexistente.

### RF-009 — Registro das tools MCP
- **Rules:**
  - Nomes: `context_save`, `context_recovery`, `context_compact` no `ContextToolsProvider` (singleton `IToolProvider` em `AddToolCatalog`).
  - `InputSchema` (JsonObject) com `repo` required e `examples` contendo `"repo":"afonsoft/knowledgerag"`; `OutputSchema` para `structuredContent` (padrão `SearchOutputSchema`).
  - Annotations via campos de `CatalogTool` (projetados pelo `tools/list` — **não** via `ToolActionAnnotationDetector`):
    - `context_save`: `ReadOnly=false` (write; bloqueado por `AllowWrite=false` no gate padrão).
    - `context_recovery`: `ReadOnly=true`, `IdempotentHint=true`.
    - `context_compact`: `ReadOnly=true` (a operação default `save=false` é pura; `save=true` persiste estado caller-scoped, permitido mesmo a chaves read-only — RF-008/OQ-H). `IdempotentHint` omitido (null) — `save=false` é idempotente, `save=true` não.
  - `ToolActionAnnotationDetector.NeverLiveTools` += as 3 tools — contexto é meta-superfície, nunca live-action (markers em documentos não devem disparar `context_save` de outro repo).
  - Rate limit: nova partição `context` no `McpToolRateLimiter` (`ContextTools` name-set + terceiro `PartitionedRateLimiter`, sliding window). Config: `RateLimiting:ContextPermitLimit` (default 60) / `ContextWindowSeconds` (default 60). Razão: `context_save` é barato mas `context_compact` pode gastar LLM; nem o bucket `sync` (10/h) nem o `llm` (20/min) servem.
  - Respostas sempre `ToolResults.Structured(text, structured)` para Playground + `structuredContent`.

### RF-010 — Configuração
```json
"Context": {
  "CacheEnabled": true,
  "MaxSnapshotsPerRepo": 50,
  "MaxItemsPerSnapshot": 200,
  "MaxPayloadBytes": 524288,
  "DefaultTokenBudget": 4096,
  "KeepMinLastItems": 3,
  "ElisionTokenLimit": 150,
  "SummaryInputMaxChars": 24000,
  "SummaryMaxTokens": 512,
  "SummaryTimeoutSeconds": 30
}
```
- `ConfigurationValidator.ValidateContext`: `MaxSnapshotsPerRepo` 1–500; `MaxItemsPerSnapshot` 1–1000; `MaxPayloadBytes` 16 KB–4 MB; `DefaultTokenBudget` 256–32768; `SummaryTimeoutSeconds` 1–120; `ElisionTokenLimit` ≥32; `KeepMinLastItems` 1–20; `RateLimiting:Context*` positivos.
- `SummaryTimeoutSeconds` vira `CancellationTokenSource` por chamada — nunca herda o timeout do request inteiro.

## 5. API Contract

### MCP tools (JSON-RPC `tools/call` — erros como `isError` + `error.code` no conteúdo estruturado)

**`context_save`**
- Request: `{"repo":"afonsoft/knowledgerag","label":"sprint-42","idempotencyKey":"turn-17","items":[{"kind":"decision","title":"Usar Tier 4","content":"..."}],"compact":"none"}`
- Response: `{"id":"…","repo":"afonsoft/knowledgerag","version":3,"createdAt":"…","tokenEstimate":1820,"compacted":false,"strategy":"none","pruned":0,"deduplicated":false}`

**`context_recovery`**
- Request: `{"repo":"afonsoft/knowledgerag","version":null,"tokenBudget":2048,"strategy":"elision"}`
- Response: `{"repo":"afonsoft/knowledgerag","version":3,"items":[…],"stats":{"tokensBefore":5400,"tokensAfter":1990,"budget":2048,"droppedCount":2,"elidedCount":4,"summaryUsed":false,"summaryFallback":false,"cacheHit":true},"withinBudget":true}`

**`context_compact`**
- Request: `{"repo":"afonsoft/knowledgerag","fromVersion":3,"strategy":"elision+summary","tokenBudget":2048,"save":true,"label":"pos-compact"}`
- Response: `{"repo":"afonsoft/knowledgerag","compacted":[…],"droppedCount":1,"elidedCount":3,"stats":{…},"savedVersion":4}`

### REST espelho (ProblemDetails; cookie ou `aft_*`)
| Método | Rota | Corpo/Query | Resposta |
|---|---|---|---|
| POST | `/api/context/snapshots` | igual `context_save` | `201` + RF-004 |
| GET | `/api/context/snapshots/latest?repo=…&tokenBudget=…&strategy=…` | query | `200` RF-005 |
| GET | `/api/context/snapshots/{version}?repo=…` | path+query | `200` RF-005 / `404` |
| GET | `/api/context/snapshots?repo=…` | query | `200` lista `{id,version,label,createdAt,tokenEstimate,compacted,strategy}` (sem payload) |
| POST | `/api/context/compact` | igual `context_compact` | `200` |

> `POST /api/tools/{name}` já invoca as 3 tools automaticamente (passthrough existente); as rotas dedicadas existem para status HTTP reais e semântica de query.

### Erros comuns
| Status / MCP | Código | Quando |
|---|---|---|
| 400 / isError | `invalid_repo` | `repo` ausente/inválido |
| 400 / isError | `invalid_payload` | campo desconhecido, char de controle, `version`+`label`, `version`≤0, ambos/nenhum `items`/`fromVersion` |
| 400 / isError | `secret_detected` | padrão de secret em qualquer campo string |
| 403 / isError | `tool_denied` | `AllowWrite=false` em save, ou tool fora de `AllowedToolsJson` |
| 404 / isError | `context_not_found` | sem snapshot, versão/label inexistente |
| 413 / isError | `payload_too_large` | > `MaxPayloadBytes` ou > `MaxItemsPerSnapshot` |
| 429 / isError | — | partição `context` estourada (`retryAfterSeconds` no hint) |

Summarization indisponível **não** gera erro: `200` com `summaryFallback=true`.

## 6. Acceptance Criteria

```gherkin
Cenário: save e recovery no mesmo repo
  Dado o repo "afonsoft/knowledgerag" sem snapshots
  Quando chamo context_save com 2 decisões
  E chamo context_recovery com repo "afonsoft/KnowledgeRAG"
  Então recebo version 1 com as 2 decisões e o repo normalizado

Cenário: isolamento entre repos
  Dado um snapshot em "afonsoft/knowledgerag"
  Quando chamo context_recovery para "afonsoft/outro"
  Então recebo context_not_found

Cenário: versões monotônicas sob concorrência
  Quando 5 context_save correm em paralelo no mesmo repo
  Então as versões gravadas são 1..5 sem colisão (retry do unique index)

Cenário: idempotência
  Dado um context_save com idempotencyKey "turn-17"
  Quando repito a mesma chamada
  Então recebo a mesma version com deduplicated=true e nenhuma linha nova

Cenário: retenção
  Dado MaxSnapshotsPerRepo=2 e 2 snapshots existentes
  Quando chamo context_save
  Então o total continua 2, o mais antigo some e pruned=1

Cenário: recovery com elision não destrutiva
  Dado snapshot com tool_output de 3000 tokens fora das 3 últimas posições
  Quando context_recovery strategy "elision" budget 2048
  Então o item vira stub "[Output elided: 3000 tokens, …]"
  E decisions/notes intactos
  E uma nova leitura do snapshot mostra o payload original inalterado

Cenário: cache hit em recovery
  Dado um context_recovery (repo, latest, elision, 2048) já executado
  Quando repito a chamada idêntica
  Então stats.cacheHit=true e o banco não é consultado

Cenário: invalidação após save (single e multi-instância)
  Dado um recovery cacheado
  Quando chamo context_save no repo
  Então o próximo recovery retorna a nova version com cacheHit=false
  E um PublishAsync "cache-tag:ctx:{repoKey}" foi emitido

Cenário: sem stale via tool cache
  Dado context_recovery marcada ReadOnly no catálogo
  Quando consulto IToolCacheService.IsCacheable("context_recovery", true)
  Então retorna false (exclusão por prefixo context_)

Cenário: summarization falha
  Dado summarizer que lança exceção/timeout
  Quando context_compact "elision+summary" acima do budget
  Então 200 com summaryFallback=true e a resposta não é cacheada

Cenário: compact save=true com key read-only
  Dado ApiKey com AllowWrite=false
  Quando context_compact save=true
  Então grava o snapshot e retorna savedVersion (estado caller-scoped permitido)
  Mas context_save na mesma chave continua negado ("requires write access" + ScopeAudit)

Cenário: secret bloqueado
  Quando context_save com content contendo "ghp_" + 36 chars
  Então secret_detected e nada é gravado

Cenário: recovery por label
  Dado snapshots v2 (label "sprint-42") e v3 (sem label)
  Quando context_recovery com label "sprint-42"
  Então retorna v2

Cenário: nunca live-action
  Dado um documento indexado contendo "<!-- mcp-tool: context_save repo=\"x/y\" -->"
  Quando search_knowledge recupera esse chunk com markers habilitados
  Então nenhuma anotação de ação é emitida para context_save
```

| Scenario | Input | Expected |
|---|---|---|
| repo URL | `https://github.com/Afonsoft/KnowledgeRAG.git` | `afonsoft/knowledgerag` |
| repo com espaço | `" afonsoft/knowledgerag "` | trim e aceita |
| repo sem barra | `knowledgerag` | `invalid_repo` |
| repo com path extra | `https://github.com/a/b/pulls` | `invalid_repo` |
| budget fora do range | `10` / `999999` | clamp 256 / 32768 |
| version+label juntos | ambos | `invalid_payload` |
| `fromVersion` inexistente | `99` | `context_not_found` |
| payload 600 KB | item grande | `payload_too_large` |
| Redis indisponível | cache falha | resposta normal + warning |
| Postgres | mesmos cenários | comportamento idêntico (integration test condicional) |
| `strategy=none` | snapshot grande | só corte por budget; `elidedCount=0` |
| cache por repo | mesma key `(latest,elision,2048)` em repo A e B | entradas distintas (tag `ctx:…` separada) |

## 7. Task Plan

- **T1 — Discovery (fechada nesta revisão):** ✅ duas pastas de migrations (`Migrations/` SQLite, `Migrations/Postgres/`); entidades em `Domain/Entities/`; `ElisionStub` inexistente (elision = código novo); `ToolActionAnnotationDetector` = live-actions (annotations vêm de `CatalogTool`); sem repo-scope em `ApiKey`; `IToolCacheService` auto-cacheia ReadOnly (D-1).
- **T2 — Entidade + DbContext:** `ContextSnapshot` em `Domain/Entities/`, `DbSet`, bloco no `OnModelCreating` (índices, `PayloadJson` com `IsNpgsql() ? "jsonb" : "TEXT"`).
- **T3 — Migrations:**
  `dotnet ef migrations add AddContextSnapshots -p src/KnowledgeHub.Server -c KnowledgeHubDbContext -o Migrations`
  `dotnet ef migrations add AddContextSnapshots -p src/KnowledgeHub.Server -c PostgresKnowledgeHubDbContext -o Migrations/Postgres`
  Aplicar em banco SQLite de teste; Postgres quando `Postgres:ConnectionString` disponível.
- **T4 — `RepoScope` + `ContextOptions` + `ContextPayload`:** normalização/validação de repo; DTOs; limites; char-control; secret scan. Testes unitários (URL, case, espaço, path extra, secrets em cada campo).
- **T5 — `ContextCompactionService`:** elision por item (novo, idempotente), summarization via `ForSubtask("summarize")` com timeout CTS e fallback determinístico, drop-order e invariantes. Testes com `IChatClient` fake (sucesso, erro, timeout, ausente) — verificar `summaryFallback` e não-reordenação.
- **T6 — `ContextSnapshotService`:** save (transação, retry de versão, dedup por `idempotencyKey`, retenção), latest, get por version/label, list. Integração SQLite; Postgres condicional.
- **T7 — `ContextCacheService` + exclusão em `IsCacheable`:** chaves/tags/ponteiro latest, invalidação local + `cache-tag:` no bus, `CacheEnabled`, não-cache de fallback/save. Testes: hit, miss, invalidação, isolamento por repo, stale-safety do `latest`.
- **T8 — `ContextToolsProvider`:** 3 tools, schemas+examples, outputSchema, `NeverLiveTools`, partição `context` no rate limiter. Testes `tools/list` (required `repo`, annotations) e `tools/call`.
- **T9 — `ContextEndpoints`:** rotas dedicadas com ProblemDetails + `CallerScope` (mesmas regras do MCP). Integração REST.
- **T10 — Config:** bloco `Context` + `RateLimiting:Context*` em `appsettings.json` + `ValidateContext`.
- **T11 — Observabilidade:** spans `context.save|recovery|compact`, contadores cache/fallback; garantia de nunca logar `content`/`label` (só repoKey, version, bytes, counts).
- **T12 — Eval:** matriz `strategy × tokenBudget` no harness existente; gate: faithfulness e Recall@K não caem >2 pts; `tokensAfter ≤ budget` em 100% dos casos. Default de `strategy` fica `none` até o gate passar.
- **T13 — Docs/PR:** CLAUDE.md, README EN/PT, docs; PR para `main` com checks verdes.

**Validação:** `dotnet build KnowledgeHub.slnx`; `dotnet test`; `dotnet format KnowledgeHub.slnx --verify-no-changes`; integração SQLite obrigatória + Postgres condicional; smoke MCP com `aft_*` de teste (sem imprimir a chave).

## 8. Organization Guardrails

- Branch `feature/Devin-20261020-context-snapshots`; nunca `main`/`master`/`develop`.
- Não tocar `.github/workflows/`.
- Não commitar `.env`, `*.key`, `*.pem`, nem chaves `aft_*`/`sk-*`/`ctx7sk-*`.
- Contexto vai para `ContextSnapshots` — nunca para o vault (OQ-3).
- Camadas: `ContextCompactionService` e `ContextCacheKeys` sem EF; EF só em `ContextSnapshotService`; LLM só via `IAssistantChatClientProvider`; cache via `HybridCache`/`ICacheInvalidationBus` (nunca `IDistributedCache` cru, nunca `IToolCacheService`).
- Secrets: o scan de RF-003 roda **antes** de qualquer chamada LLM ou persistência.
- Não inventar requisitos: o que não está aqui vai para Open Questions.

## 9. Definition of Done

- [ ] T1–T13 concluídas; `Status = Done`.
- [ ] `AddContextSnapshots` aplica em SQLite **e** Postgres.
- [ ] `dotnet build` sem warnings novos; `dotnet test` verde; `dotnet format` verde.
- [ ] As 3 tools em `tools/list` com `repo` obrigatório, examples e annotations corretas.
- [ ] Todos os cenários da seção 6 cobertos por teste.
- [ ] Cache: hit, invalidação por tag (local + pub/sub), isolamento por repo e **exclusão do `IToolCacheService`** verificados.
- [ ] Nenhum `content`/`label`/chave em logs.
- [ ] Eval publicado; default de `strategy` decidido por gate.
- [ ] Docs atualizadas; PR aberto para `main` com checks verdes, sem force-push.

## 10. Histórico de versões

- **v1** (`6aae991c-…`): compact_context apenas, sobre retrieval. Substituída.
- **v2** (`318fde74-…`): Tier 4 no `ChainCompactor` do agente. Absorvida como RF-006 (núcleo) e RF-010 (limiares); limiares 55%/80% não entram — orçamento explícito em `tokenBudget`.
- **v3** (draft `20261004`): tools `context_*` com `repo`, `ContextSnapshots`, cache versionado.
- **v4** (esta): fact-check contra o código — ver seção 11.

## 11. Correções da v3 → v4 (fact-check contra o código)

| # | v3 dizia | Realidade no código | Ação na v4 |
|---|---|---|---|
| 1 | `Data/Entities/ContextSnapshot.cs` | entidades ficam em `Domain/Entities/` | path corrigido |
| 2 | OQ-A: "pasta de migrations por provider?" | `Migrations/` (SQLite) + `Migrations/Postgres/` confirmadas; últimas `202610032*_AddFlowTriggersAndApproval` | OQ-A fechada; comandos `dotnet ef` na T3 |
| 3 | "reuso de `ElisionStub`/`ChainCompactor`" | `ElisionStub` **não existe**; `ChainCompactor` opera em `ChainAst` e `BuildSummary` é `private` | RF-006 = implementação nova; fallback reutiliza a *fórmula* de `BuildSummary` |
| 4 | "`ToolActionAnnotationDetector`: annotations" | a classe detecta **live-actions**; annotations vêm de `CatalogTool.ReadOnly`/`DestructiveHint`/etc. | RF-009 corrigido; as 3 tools entram em `NeverLiveTools` |
| 5 | "escopo da ApiKey cobre o repo" | `ApiKey` só tem `AllowedSourceIdsJson`, `AllowedToolsJson`, `AllowWrite` — sem repo-scope | RF-001 reescrito (tool allowlist + write gate + `ScopeAudit`); ACL-por-repo vira out-of-scope |
| 6 | "cache via `IToolCacheService`/`IDistributedCache`" | `IToolCacheService` auto-cacheia qualquer `ReadOnly` com `(tool,args,indexVersion)` e TTL mín. 1 h; `indexVersion` **não** muda em `context_save` → `latest` stale | **D-1 (RF-007):** camada própria sobre `HybridCache` + tags `ctx:{repoKey}` + `cache-tag:` no bus; `context_*` excluída de `IsCacheable` |
| 7 | "partição `context` no rate limit" | só existem `LlmTools`/`SyncTools` | RF-009: terceiro `PartitionedRateLimiter` + `RateLimiting:Context*` |
| 8 | `context_compact` `readOnlyHint` condicional | hint é estático por tool | `ReadOnly=true`; `save=true` permitido a read-only keys (OQ-H — estado caller-scoped) |
| 9 | REST `POST/GET snapshots` | `POST /api/tools/{name}` já invoca qualquer tool | rotas dedicadas mantidas p/ status HTTP reais; adicionado `GET /{version}` |
| 10 | branch `feature/OpenWebUI-…` | convenção: `feature/{Agent}-{YYYYMMDD}-{slug}` | `feature/Devin-20261020-context-snapshots` |
| 11 | — | retries MCP podem duplicar saves | `idempotencyKey` (RF-004) |
| 12 | — | `label` só servia para display | `context_recovery` aceita `label` (RF-005) |
| 13 | — | marcador de elision precisa do nome da tool | campo `tool` no item (RF-003) |
| 14 | secret scan: `aft_`,`sk-`,`ctx7sk-`,`ghp_`,`-----BEGIN` | idem + só em `content` | +`gho_`,`github_pat_`,`xox[bap]-`,`AIza`; scan em `content`/`title`/`path`/`tool`/`label` |

## Open Questions

- **OQ-B (default assumido: retenção por contagem = 50):** manter só contagem ou adicionar `MaxAgeDays`? Assumido: contagem apenas.
- **OQ-C (assumido: qualquer `owner/name` válido):** `repo` é um rótulo lógico livre — não exige `GitRepository` source cadastrado. Confirmar.
- **OQ-D (assumido: 512 KB):** confirmar ou reduzir para 256 KB.
- **OQ-E (assumido: só `appsettings`):** sem toggle runtime `PUT /api/settings/context` nesta versão.
- **OQ-H (decidida 2026-10-20):** `context_compact` fica `ReadOnly=true` e `save=true` **funciona mesmo com `AllowWrite=false`** — snapshots são estado caller-scoped. `context_save` permanece `ReadOnly=false` e gated.
- **OQ-I (novo):** `GET /api/context/snapshots/{version}` mantido por simetria REST.
- **OQ-G (ticket/branch):** número do ticket via `create-issues` após aprovação.
