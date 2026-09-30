# Short-term memory — session state (overwritten each session, ≤100 lines)

- **Last verified commit on `main`**: `a14b72b` (PR #378 — temporal episodic KG; stack completa #269–#279).
- **Baseline**: build 0 warnings · 1043 unit + 288 integration verdes (local, 2026-09-28).
- **Done (2026-09-27, sessão pentagi/Verba → execução)**: análise comparativa pentagi+Verba → 13 SPECs aprovadas (#269–#281) + issues conectadas; **#263 implementada** (RestApi + SqlDatabase connectors) — SqlQueryGuard (SELECT-only, keywords fora de literais/comentários), JsonPathResolver (dot-path), RestApiConnector (paginação pageParam/maxPages, headers no secret store `restapi:{id}`), SqlDatabaseConnector (sqlite `Mode=ReadOnly`/postgres `READ ONLY` tx, connstring no secret store `sql:{id}`, maxRows/Truncated), RequiredKeys[SqlDatabase]=["provider","query"], auto-sync whitelist, UI completa no SourceEditDialog; 79 novos testes (48 guard/resolver + 16 restapi + 15 sql + 11 integration − repoint de 1 obsoleto para McpProxy).
- **Ordem de execução aprovada**: #263→#264→#262 | #280→#270→#273→#281 | #278→#276→#272→#277 | #269→#275→#274 | #271→#279 — **fila 100% entregue** (PRs #364–#381 mergeadas em main).
- **Blockers**: nenhum.
- **Reconcile 2026-09-28**: issues #269–#279 estavam abertas com label `todo` apesar de mergeadas — fechadas via orchestrator com evidência (PR+commit) e label `done`. SPEC-20260926-pgvector-live-tests sincronizada → Done (PR #237). Stats/sessions atualizados na branch `feature/Devin-20260928-orchestrator-reconcile`.
- **Next**: fila vazia — aguardar nova direção do usuário (gap-analysis ou nova Epic).

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
- SPEC #275 (stacked #375): `Resilience/` — `FallbackPolicyEngine` (disabled/observe/enforce), `ResilientChatClient` decorator IChatClient com lazy alternates via `ChatClientFactory` + seam interno p/ testes, `ToolCapabilityRegistry`, `ChatProviderException` agora com `StatusCode`/`IsTimeout`. `appsettings` `Resilience:Fallback` opt-in. Settings UI + dispatcher interception = débito documentado.
- SPEC #269 (stacked): `McpEngine/Agents/ChainAst/` — `ChainAST`/`ChainSection`/`BodyPair`/`ToolCallPair`, `ChainAstParser` (seções=header system/user + pares ai/tool por CallId; órfãos→`ast.Orphans`), `ChainAstRepair` (stub `FunctionResultContent` no carrier existente ou novo msg Tool), `ChainCompactor` (trunca tool output >MaxBodyPairBytes → fold seções antigas em `**summarized content:**`, reasoning drop→`TextReasoningContent{ProtectedData="skip_thought_signature"}`). Hook em `AgentService.RunLoopAsync` antes de cada LLM call; transcript DB intacto. `FunctionResultContent(callId,result)` 2 args. `IChainCompactor` singleton.
- SPEC #271 (stacked): `Audit/Evidence/` — `CanonicalJsonSerializer` (keys sorted, compact), `EvidenceChainService` (HMAC-SHA256 key em `evidence:master` no secret store, `rec_{ticks:x}_{guid}`), `EvidenceChainVerifier` (TamperingDetected/BrokenParentLink/BadSignature/OrderingViolation), `EvidenceEmission` best-effort hooks em AskKnowledgeAsync + AskEndpoints + AgentService (ToolExecuted chained). Entidade `EvidenceReceipt` append-only + migrations SQLite/Postgres. Endpoint `GET /api/v1/evidence/sessions/{id}/bundle` (Operational). Assinatura HMAC (não assimétrica — verificação offline exige a key; documentado).
- Flaky: `TelemetryTests.Agent_EmitsSpanTree_IterationAndToolChildren` falha esporádica sob paralelismo (passa isolado).
- SPEC #274 (stacked sobre #271): `KgNode`/`KgEdge` += `ObservedAt`/`ValidFrom`/`ValidTo`/`EpisodeId` (+`Labels`/`Weight`/`PropertiesJson`) como **DateTime** (UTC) — DateTimeOffset não traduz ORDER BY nem `>=` lifted no SQLite (mesma lição do fix #367). Novo `KgEpisode` + `GraphEpisodeService` (episódio por run de ingestão). Store: re-observação bumpa `ObservedAt`, (from,to,kind) com nova evidência → `ValidTo` na anterior (historicização); reads filtram `ValidTo==null`; busca temporal é history-aware. `TemporalDateParser` (RFC3339 + 3 formatos, UTC default; whitelist 1h/6h/24h/7d), `DiversityRanker` (low=5/med=2/high=1 por cluster), `TemporalGraphRetriever` (window/recent/relationships 2-hop/diverse/episode), `TemporalGraphFormatter` (cap 8KB → condensa), `TemporalGraphToolsProvider` (5 tools MCP). 1025u+286i verdes. UI de timeline NÃO existe — débito documentado (SPEC pedia "atualizar visualizador" que não há).
- SPEC #279 (stacked sobre #274): `Mcp/Bridge/` — `ToolActionAnnotationDetector` (markers `<!-- mcp-tool: name k="v" -->` em chunks + menção direta do nome no question; NeverLiveTools exclui meta/RAG + só read-only; chunks flagged ignorados), `McpDynamicRagActionBridge.ExecuteAsync` (args do marker verbatim; argless só preenche se schema tem required único query/question/input/prompt — nunca inventa; cap MaxChainedDynamicCalls≤3; erros capturados como IsError), `HybridCitationFormatter` (`[Live Tool: name @ ts]` + context items sintéticos `live-mcp`/`live://tool/x` p/ AnswerService). `AskResponse.LiveToolExecutions` (novo, Shared). `ask_knowledge` += `enableLiveActions`; `search_knowledge` += `suggestedActions` (loop unificado do agent_chat — modelo chama no turno seguinte). `Agent:EnableDynamicActionBridge|MaxChainedDynamicCalls` defaults true/3. Guardrail: catálogo já chega scope-filtered via `catalog.GetToolsAsync`. 1038u + format verdes.

## Session summary (2026-09-29 — A2A Epic em andamento)

- Reconciliação concluída (sessão anterior): 11 issues #269–#279 fechadas com evidência; PR #382 mergeada.
- Gap-analysis: 7 gaps confirmados → 5 SPECs aprovados → Epic #383 + #384–#388.
- S1 #384 docs-sync mergeada (PR #390). S2–S5 abertas: #391 resilience wiring, #392 graph viewer, #393 coverage gate+flake fix, #394 observability.
- Achado: cobertura real medida 20.6% (não 80%) — ratchet COVERAGE_MIN=20; investimento em testes é debt futuro.
- enforce_admins=false permanece inconclusivo (carried, decisão do dono).

## Session summary (2026-09-29 — review-findings Epic #396 + A2A SPEC)

- Epic #396 (#397–#406): 10 slices dos achados de review (256 comentários em 20 PRs).
  - S1 #397→PR #407 MERGED: arm-failure isolation marca `degraded` (sem cache de parcial), dedup de janelas vizinhas, sanitização de labels em log.
  - S2 #398→PR #408 MERGED: egress block-private-by-default + opt-in por request (`allowPrivateHosts`), redirect manual com strip de credencial cross-host para TODOS os clients conector, GitLab FailedUris p/ oversized, Unstructured URIs homônimos, mass-delete gate com threshold ≥4 docs (correção pós-CI: gate bloqueava deletes legítimos → 429 nos polls).
  - S3 #399→PR #409 MERGED: secrets de fallback bound a provider/endpoint/model (não posicional), migração de legado, provider preservado no save.
  - S4 #400→PR #410 MERGED: compactor pin non-user headers, budget progressivo, structured results truncados, flatten de Unicode separators.
  - S5 #401→PR #411 MERGED: striped locks por sessão, export authz (cookie-session recusado — bucket `mcp:session` unattributable), verify-with-signature, snapshot verify.
  - S6 #402→PR #412 MERGED: CallerScope no TemporalGraphRetriever (edges + episódios + homônimos via FindAllowedNodeAsync, linker overfetch 24→8).
  - S7 #403→PR #413 MERGED: `allowDocumentMarkers` opt-in (doc-marker injection fechado), args vazios rejeitados, pseudo-citations live-only, cache key com fingerprint de contexto.
  - S8 #404→PR #414 MERGED: CodeQL sweep (~45 fixes) + EvalWorker OCE filter (cancel per-task não mata o reader loop).
  - S9 #405→PR #415 MERGED: feed snapshot ring (replay p/ novos subscribers), spans IsError em bridge+evidence, coverage baseline ratchet `.ci/coverage-baseline.txt`, seeded search test.
  - S10 #406→PR #417 (checks): README pt/paridade, graph docs por camada, DoD section, toggles por capacidade no Resiliência, login com card "Conectar Agente de IA ao MCP" primeiro + McpOnboardingPrompt compartilhado (protocolo ask_question/read_wiki_contents + write_note/write_knowledge) + CLAUDE.md.
- A2A: SPEC-20260929-a2a-agent-interop em PR #416 (Draft) — server A2A v1.0 (card das 24 skills afonsoft + tools, JSON-RPC+HTTP+JSON, SSE, assinatura HMAC), client p/ agentes remotos, assistant model low-cost OpenAI-compatible. SPECs antigas deletadas pelo usuário.
- Lições: egPOLICY default-block quebra testes com fake servers locais — fixtures precisam `Security:Egress:AllowPrivateNetworks=true`; mass-delete gate precisa de threshold mínimo (senão bloqueia syncs de fontes pequenas → polls estouram rate limit → `$.status` 429).

- Epic #396 concluída (10/10 slices) + deploy local ok. Epic A2A em execução.
- **Estado do trabalho (worktree limpo — tudo commitado/pushado)**:
  - Branch atual: `feature/Devin-20260930-a2a-assistant`.
  - PR #421 (a2a-server #419): Agent Card `/.well-known/agent-card.json`, `/a2a` JSON-RPC+HTTP+JSON (Operational + llm rate limit), `KnowledgeHubA2AAgent` → `IDynamicToolCatalog` (4 skills), TaskUpdater, evidência, métricas; 5 testes integration verdes; fix S2583 pushado — **aguardando re-check do SonarCloud**.
  - PR #422 (a2a-assistant #420, stacked em #421): `/api/settings/assistant`, `AssistantSettings`+migration, `IAssistantChatClientProvider.ForSubtask` (rewrite/grade/expand/summarize), `AssistantFallbackChatClient` timeout→main, `A2AChatClient` modo remote, tab "Assistente (A2A)"; 8u+6i testes; suite unit 1129 verde.
- **Lições A2A SDK 1.0.0-preview2**: `messageId` required; role wire `ROLE_USER`; `FailAsync` precisa de `SubmitAsync` antes; `A2ACardResolver(uri,http,"/.well-known/agent-card.json",logger)`; `SendMessageResponse.PayloadCase` Task|Message.
- **Pendências**: suite integration completa (interrompida), merge #421→#422 (ordem), SPECs → Done, docs README/API para `/a2a` + `/api/settings/assistant`, write_note/write_knowledge via MCP.
- Formato: `dotnet format` gate local — ConnectorIntegrityTests.cs tem whitespace debt pre-existente na main (não meu diff).
