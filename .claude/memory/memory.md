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
