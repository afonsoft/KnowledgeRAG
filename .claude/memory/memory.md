# Short-term memory — session state (overwritten each session, ≤100 lines)

- **Last verified commit on `main`**: `3ea3202` (PR #363 — SPEC approval sync).
- **Baseline**: build 0 warnings · 801 unit + 283 integration verdes (local) · `dotnet format --verify-no-changes` limpo.
- **Done (2026-09-27, sessão pentagi/Verba → execução)**: análise comparativa pentagi+Verba → 13 SPECs aprovadas (#269–#281) + issues conectadas; **#263 implementada** (RestApi + SqlDatabase connectors) — SqlQueryGuard (SELECT-only, keywords fora de literais/comentários), JsonPathResolver (dot-path), RestApiConnector (paginação pageParam/maxPages, headers no secret store `restapi:{id}`), SqlDatabaseConnector (sqlite `Mode=ReadOnly`/postgres `READ ONLY` tx, connstring no secret store `sql:{id}`, maxRows/Truncated), RequiredKeys[SqlDatabase]=["provider","query"], auto-sync whitelist, UI completa no SourceEditDialog; 79 novos testes (48 guard/resolver + 16 restapi + 15 sql + 11 integration − repoint de 1 obsoleto para McpProxy).
- **Ordem de execução aprovada**: #263→#264→#262 | #280→#270→#273→#281 | #278→#276→#272→#277 | #269→#275→#274 | #271→#279 (dependências: #263→#279; #278→#276; #269→#279; #270+#273→#271).
- **Blockers**: nenhum.
- **Next**: executar #264 (RSS/Atom) e #262 (YouTube) — fase 1 restante; depois fase 2 (#280 Voyage/Cohere → #270 Window/Autocut → #273 Multi-query/Relaxation → #281 Tríade).

## Session summary (2026-09-27 — pentagi/Verba analysis + #263 implementation)

- Erro CI: `Jobs_List_FiltersBySource` timeout 300s — `last status: queued, running: [], queued count: 1`.
- Análise: `eb` (enfileirado depois de `ea`) completou → canal FIFO single-reader implica que `ea` FOI dequeued mas descartado antes da transição `running` persistir — crash no gap entre dequeue e `SaveChangesAsync` (fora do try/finally de terminal). Provável SQLITE_BUSY (sem WAL/busy_timeout na connstring).
- Por que fixes anteriores (#235/#239/#241/#244) não bastaram: atacavam o mecanismo de espera e a race de canal duplo — nunca o stranding de job dequeued.
- Fix `48f1edc`: `FailStrandedJobAsync` no catch do ExecuteAsync persiste `failed` + publica evento terminal; warning no early-return silencioso (exceto `cancelled`); `PRAGMA journal_mode=WAL` no startup SQLite.
- Testes: `IngestionWorkerTests` (crash determinístico via IIngestionService ausente → failed+evento; cancelled-skip com sentinel FIFO). 719 unit verdes.
- CI #247 todos os checks verdes; squash-merge; branch remota deletada.
- Redeploy: rebuild compose, container healthy, healthz 200, autosync ok (Postgres catalog).
- Detalhe aprendido: `FailOrphanedJobsAsync` varre queued/running no start — jobs enfileirados antes do worker subir morrem como "interrupted by restart" sem evento terminal (edge case, não corrigido).

- 2026-09-27: Análise comparativa aprofundada dos repositórios vxcontrol/pentagi e weaviate/Verba (além de referências a RAGFlow, LlamaIndex e Dify). 13 novas SPECs SDD criadas, aprovadas pelo usuário e registradas como GitHub Issues (#269 a #281):
  - #269: Chain AST, reparo de tool calls órfãs e compactor de histórico conversacional (SPEC-20260927-chain-ast-thread-compactor).
  - #270: Expansão de janela de chunks (Window Retrieval) e Autocut dinâmico (SPEC-20260927-chunk-window-retrieval-and-autocut).
  - #271: Cadeia de evidências criptográficas à prova de adulteração (SPEC-20260927-cryptographic-evidence-provenance-chain).
  - #272: Conector de repositórios Git remotos GitHub/GitLab/Gitea (SPEC-20260927-git-repository-source-connector).
  - #273: Decomposição multi-query paralela e relaxamento hierárquico de filtros (SPEC-20260927-hierarchical-filter-relaxation-and-multiquery).
  - #274: Grafo de conhecimento temporal e episódico (SPEC-20260927-temporal-episodic-knowledge-graph).
  - #275: Motor de políticas de resiliência e fallback para modelos e tools (SPEC-20260927-tool-and-model-resilience-fallback).
  - #276: Conector de documentos não estruturados com OCR/layout analysis Unstructured.io (SPEC-20260927-unstructured-document-parser-connector).
  - #277: Conector de transcrição de áudios e reuniões AssemblyAI/Whisper (SPEC-20260927-audio-transcription-connector).
  - #278: Chunking profundo orientado a visão e preservação de tabelas RAGFlow (SPEC-20260927-ragflow-vision-layout-chunking).
  - #279: Sinergia MCP + RAG: Ponte de ações dinâmicas em tempo real (SPEC-20260927-mcp-dynamic-rag-action-bridge).
  - #280: Provedores de embeddings Voyage AI e Cohere Embed v3 (SPEC-20260927-voyage-and-cohere-embeddings).
  - #281: Avaliador automatizado da Tríade RAG: Relevância, Fidelidade e Resposta (SPEC-20260927-rag-evaluation-triad-metrics).

## Session summary (2026-09-28 — PR #367 follow-ups + SPEC #270)

- Branch `feature/Devin-20260928-pr367-followups` → **PR #368**: 8 achados do Devin Review corrigidos — SQLite DateTimeOffset in-memory filter no stats endpoint, DbContext scoped não mais descartado (ApiKeyUsageMiddleware voltou a gravar), groundedness strip `[n]` + threshold 50% overlap, meter `KnowledgeHub.Server.RagEvaluation` registrado no WithMetrics, cache-hits enfileiram avaliação, amostragem determinística (Random.Shared removido — hotspot Sonar), retenção 90d em RagEvaluations, `NoopRagEvaluationEnqueuer` → `Fakes/`. Stats endpoint agora CookieSession. 907u+285i verdes.
- Branch `feature/Devin-20260928-chunk-window-autocut` → **PR #369** (SPEC #270 Done): índice composto Chunks(Doc,ChunkIndex) ambos providers; `AutocutFilter` (drop >1.4× média, sensibilidade N, MaxClamp 20); `windowSize`/`limitMode`/`autocutSensitivity` em MCP+REST com `OptionalIntOrNull`; gate 80% score normalizado na expansão window; `ExpandedChunkIndices`/`WindowExpanded`/`totalMatches`/`limitModeApplied`; Playground cobre via schema dinâmico. **Decisão**: `Search:LimitMode=autocut` default em appsettings (SPEC) — suite toda verde, poda só em elbows. Settings-UI flag omitida (sem superfície runtime-config de Search). 913u+283i.
- Padrão aprendido: `ToolArgs.OptionalInt` não distingue "ausente" de 0 — usar `OptionalIntOrNull` para params onde 0 é semântico.
- Próximo da fila aprovada: #273 (filter relaxation + multiquery), depois #278→#276 (fase 3), #269→#275→#274, #271+#279 por último.
- SPEC #273 (PR empilhada sobre a de #270): `subQueries` (≤4, blanks filtrados) rodam como braços extras vetor+lexical fundidos via RRF; cascata de relaxamento drop pathPrefix → sourceId→SourceType → global (bounded por CallerScope, sem cruzar auth); penalidade 0.85^nível; itens carregam `IsRelaxed`/`RelaxedScope`; envelope `filterRelaxed`/`originalFilter`/`appliedFilter`; `Search:Relaxation:Enabled|MinResults` (default true/1); contadores `filter_relaxations`/`multiquery_dispatched`. 918u+286i verdes.
- SPEC #276 (PR empilhada #370→#371→esta): `UnstructuredDocumentConnector` + `UnstructuredApiClient` (multipart, `unstructured-api-key`, strategy ocr forçada p/ imagens, `pdf_infer_table_structure`, timeout implícito do factory) + `UnstructuredElementRenderer` (Title→#, Table→GFM via text_as_html com span-scan, Header/Footer→comentário suppressor). `MetadataJson` novo campo ChunkPiece→DocumentChunk (já entregue em #278). Secret `unstructured:{id}` opcional; whitelist autosync; UI dialog completo + `visionlayout` no select de chunking. 934u+286i.
- SonarCloud #368 ficou verde após timeouts nos regexes (S6444). Gate atual: duplicata 0%, security A.
- SPEC #272 (stacked #373): `GitRepositoryConnector` + `GitApiClient` unificado (github/gitlab/gitea REST read-only), fingerprint `git:{prov}:{o}/{r}:{branch}:{sha}:{path}:{blobSha}` — fast-path commit-SHA (0 downloads) + per-blob skip; SSRF via `WebPageConnector.GuardPublicAsync` + `allowPrivateHosts`; PAT `git:{id}` opcional. `ProductInfoHeaderValue` exige ("name","1.0") — arg único é comentário.
- SPEC #277 (stacked): `AudioTranscriptionConnector` + `ITranscriptionClient` (`AssemblyAiClient` upload→submit→poll backoff 2s→15s cap, `WhisperApiClient` multipart verbose_json) + `AudioTranscriptionRenderer` (capítulos + merge speaker<10s). Fingerprint `audio:{sha256}:{provider}`. Key `audio:{id}` opcional p/ whisper self-hosted.
- Testes de conectores: `TestContext.Current.CancellationToken` NÃO existe no xunit do repo — usar `CancellationToken.None`.
