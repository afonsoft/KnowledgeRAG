# Short-term memory — session state (overwritten each session, ≤100 lines)

- **Last verified commit on `main`**: `db1bc55` (PR #250 — mobile nav toggler).
- **Baseline**: build 0 warnings · 719 unit verdes (local) · prod container healthy :5550 pós-#250 redeploy.
- **Done hoje (2026-09-26, sessões ingestion-deflake + mobile)**: root cause do flake `Jobs_List_FiltersBySource` corrigido (#247); 6 branches locais deletadas; mobile UX — settings tabs alcançáveis (#249) e hambúrguer visível (#250); redeploys healthy.
- **Blockers**: nenhum.
- **Next**: enforce_admins decisão pendente; teste shutdown gracioso×abrupto em aberto; watchdog de jobs `queued` stale continua como follow-up opcional.

## Session summary (2026-09-26 — ingestion orphan-job root cause + fix)

- Erro CI: `Jobs_List_FiltersBySource` timeout 300s — `last status: queued, running: [], queued count: 1`.
- Análise: `eb` (enfileirado depois de `ea`) completou → canal FIFO single-reader implica que `ea` FOI dequeued mas descartado antes da transição `running` persistir — crash no gap entre dequeue e `SaveChangesAsync` (fora do try/finally de terminal). Provável SQLITE_BUSY (sem WAL/busy_timeout na connstring).
- Por que fixes anteriores (#235/#239/#241/#244) não bastaram: atacavam o mecanismo de espera e a race de canal duplo — nunca o stranding de job dequeued.
- Fix `48f1edc`: `FailStrandedJobAsync` no catch do ExecuteAsync persiste `failed` + publica evento terminal; warning no early-return silencioso (exceto `cancelled`); `PRAGMA journal_mode=WAL` no startup SQLite.
- Testes: `IngestionWorkerTests` (crash determinístico via IIngestionService ausente → failed+evento; cancelled-skip com sentinel FIFO). 719 unit verdes.
- CI #247 todos os checks verdes; squash-merge; branch remota deletada.
- Redeploy: rebuild compose, container healthy, healthz 200, autosync ok (Postgres catalog).
- Detalhe aprendido: `FailOrphanedJobsAsync` varre queued/running no start — jobs enfileirados antes do worker subir morrem como "interrupted by restart" sem evento terminal (edge case, não corrigido).

- 2026-09-27: Análise comparativa aprofundada dos repositórios vxcontrol/pentagi e weaviate/Verba (além de referências a RAGFlow, LlamaIndex e Dify). 11 novas SPECs SDD criadas em .specs/ cobrindo:
  - Chain AST, reparo de tool calls órfãs e compactor de histórico conversacional (SPEC-20260927-chain-ast-thread-compactor).
  - Decomposição multi-query paralela e relaxamento hierárquico de filtros (SPEC-20260927-hierarchical-filter-relaxation-and-multiquery).
  - Motor de políticas de resiliência e fallback para modelos e tools (SPEC-20260927-tool-and-model-resilience-fallback).
  - Cadeia de evidências criptográficas à prova de adulteração (SPEC-20260927-cryptographic-evidence-provenance-chain).
  - Grafo de conhecimento temporal e episódico (SPEC-20260927-temporal-episodic-knowledge-graph).
  - Expansão de janela de chunks (Window Retrieval) e Autocut dinâmico (SPEC-20260927-chunk-window-retrieval-and-autocut).
  - Conector de repositórios Git remotos GitHub/GitLab/Gitea (SPEC-20260927-git-repository-source-connector).
  - Conector de documentos não estruturados com OCR/layout analysis Unstructured.io (SPEC-20260927-unstructured-document-parser-connector).
  - Conector de transcrição de áudios e reuniões AssemblyAI/Whisper (SPEC-20260927-audio-transcription-connector).
  - Chunking profundo orientado a visão e preservação de tabelas RAGFlow (SPEC-20260927-ragflow-vision-layout-chunking).
  - Sinergia MCP + RAG: Ponte de ações dinâmicas em tempo real (SPEC-20260927-mcp-dynamic-rag-action-bridge).
  - Provedores de embeddings Voyage AI e Cohere Embed v3 (SPEC-20260927-voyage-and-cohere-embeddings).
  - Avaliador automatizado da Tríade RAG: Relevância, Fidelidade e Resposta (SPEC-20260927-rag-evaluation-triad-metrics).
