# Short-term memory — session state (overwritten each session, ≤100 lines)

- **Last verified commit on `main`**: `45e8404` (PR #438 — hindsight SPECs + sonar batch D: S3776×25, S107×9, S1006×7, S2325×1 — new-code smells zerados; gate main OK, duplicação 0.3%).
- **Baseline**: build 0 warnings · 1129+ unit · 306+ integration verdes.
- **Deploy produção**: `knowledgehub` container healthy em `0.0.0.0:5550->8080`, `rag.afonsoft.dev` OK via Cloudflare. `.env` recriado: `DATABASE_PROVIDER=postgres` (`host.docker.internal:5432`, `rag_db`/`rag_user`), `CACHE_PROVIDER=redis` (`host.docker.internal:6379,defaultDatabase=3`), porta 5550.
- **Epic #428 (gap-analysis-20260930) entregue**: #429 PWA meta-cache retention (#432), #430 migration-populated-db tests (#433, negativo verificado), #431 spec-status-sync 15 SPECs (#434). SPECs 20260930 → Done (#436).
- **Sonar autofix**: 11 gate-blockers corrigidos (#426) — Quality Gate deve voltar a A/A. 121 smells restantes = backlog documentado em `.sonar_devin_auto_fix/`.
- **Pendências conhecidas**: Cloudflare bloqueia `/.well-known/agent-card.json` na edge (404 — regra de zona, ação do usuário, documentado em docs/*/INSTALL.md); `.env` `required:false` = deleção silenciosa → checklist de deploy documentado.

## Session summary (2026-09-30 — deploy recovery + gap-analysis + sonar autofix)

- `.env` deletado → container recriado em porta default 5000 → 502 em rag.afonsoft.dev. Recriado com Postgres/Redis host + porta 5550.
- Crash startup: `23502 Labels null` — migration Postgres sem `defaultValue` vs SQLite (`defaultValue "[]"`). Fix `defaultValueSql '{}'::text[]` → deploy OK, 2136 chunks preservados.
- Login: `align-items-center` → `align-items-start` (card no topo) — pedido do usuário.
- Redis DB3 via `defaultDatabase=3` na connstring (SE.Redis `ConfigurationOptions.Parse`).
- PWA: SW antigo em cache servia index velho → 404 em `blazor.boot.json`/`dotnet.wasm` (.NET 10 renomeou). Fix: meta-cache `knowledgehub-meta` guarda geração anterior; `onActivate` retém predecessor.
- Lição migrations: `AddColumn nullable:false` em tabela populada exige default — agora coberto por `MigrationPopulatedDbTests` (SQLite CI + Postgres live).
- SonarCloud API pública funciona sem token para o projeto (`api/issues/search?componentKeys=afonsoft_LangGraph-UI&sinceLeakPeriod=true`).
- `gh pr merge` não aceita `-q` (erro silencioso — flag desconhecida aborta a chain).

## Session summary (2026-09-28 — PR #367 follow-ups + SPEC #270)

- Branch `feature/Devin-20260928-pr367-followups` → **PR #368**: 8 achados do Devin Review corrigidos — SQLite DateTimeOffset in-memory filter no stats endpoint, DbContext scoped não mais descartado (ApiKeyUsageMiddleware voltou a gravar), groundedness strip `[n]` + threshold 50% overlap, meter `KnowledgeHub.Server.RagEvaluation` registrado no WithMetrics, cache-hits enfileiram avaliação, amostragem determinística (Random.Shared removido — hotspot Sonar), retenção 90d em RagEvaluations, `NoopRagEvaluationEnqueuer` → `Fakes/`. Stats endpoint agora CookieSession. 907u+285i verdes.
- Branch `feature/Devin-20260928-chunk-window-autocut` → **PR #369** (SPEC #270 Done): índice composto Chunks(Doc,ChunkIndex) ambos providers; `AutocutFilter` (drop >1.4× média, sensibilidade N, MaxClamp 20); `windowSize`/`limitMode`/`autocutSensitivity` em MCP+REST com `OptionalIntOrNull`; gate 80% score normalizado na expansão window; `ExpandedChunkIndices`/`WindowExpanded`/`totalMatches`/`limitModeApplied`; Playground cobre via schema dinâmico. **Decisão**: `Search:LimitMode=autocut` default em appsettings (SPEC) — suite toda verde, poda só em elbows. Settings-UI flag omitida (sem superfície runtime-config de Search). 913u+283i.

### 2026-09-30 (cont.) — SonarCloud smell sweep — PR #437
- ~85 de 121 smells resolvidos (batches mecânico/literal/estrutural). Restantes: S3776×26 (complexidade 18-42) + S107×9 (>7 params) → precisam SPEC própria de refactor.
- **Lição:** replace global de literal de string pode tocar JSON embutido em `"""..."""` — `TemporalGraphToolsProvider` quebrou 71 testes de integração (schema JSON inválido → provider não carrega → tools/list vazio). Sempre verificar contexto do literal antes de bulk-replace.
- Deploy estável em `:5550` via `.env` (Postgres+Redis host, DB3).

## Session summary (2026-10-01 — hindsight benchmark + SPECs MCP/A2A)

- PR #437 mergeada (`b167e1b`) após dedup final: `HostedEmbeddingProvider` base compartilhada (Cohere/Voyage) + `ExpandRelationshipFrontierAsync` reusado no BFS do `TemporalGraphRetriever`. SonarCloud gate **pass** na PR e na main (duplicação 0.7% ≤ 3).
- Análise `vectorize-io/hindsight` (clone `/tmp/hindsight-analysis`): recall 4 braços + RRF + rerank, `budget` low/mid/high, `min_scores` por estágio (piso final → abstenção), `temporal_window` explícito, async operations consultáveis, MCP tolerante a args de LLM, annotations por tool, LLM-as-judge tests. Sem A2A (nosso diferencial).
- **2 SPECs criadas** (branch `feature/Devin-20261001-mcp-a2a-improvements`, Draft): `SPEC-20261001-mcp-recall-ergonomics` (budget/maxTokens/minScores/temporalWindow/annotations) e `SPEC-20261001-a2a-task-durability` (ITaskStore EF durável, progresso incremental, push webhook HMAC, harness attribution, skills metadata).
- Padrões salvos em `.claude/knowledge/hindsight-patterns.md`.
- Pendente: S3776×26 + S107×9 (Batch D) — refactors de complexidade/params.

## Session summary (2026-10-01 cont. — sonar batch D completo — PR #438)

- **Todos os 42 new-code smells resolvidos** e mergeados em #438: S1006×7+S2325×1 (EfMcpTaskStore defaults da interface), S107×9 (parameter objects: InvocationRoute, EmissionContext, ItemMapping, QuerySpec+ColumnMapping, FallbackProbe, AgentDiagnostics, RelaxationQuery), S3776×25 (extrações por fase em 15 arquivos).
- Padrão dos parameter objects: records privados agrupando config/estado; ctor DI do AgentService usa `AgentDiagnostics` registrado na factory DI.
- **Lição heredoc:** `<<'PY'` preserva `<`/`>` literal — âncoras com entidades HTML (`&lt;img&gt;`) não casam; construir âncora lendo o arquivo. E: gravar arquivo só após TODOS os asserts (evitou corrupção parcial 3×).
- **Verificação:** 1143 unit + 308 integration local; CI completo PASS; SonarCloud gate PASS na PR e OK na main (0.3% duplicação).
- SPECs 20261001 seguem **Draft** — aguardando aprovação do usuário para virar Epic/Issues.
