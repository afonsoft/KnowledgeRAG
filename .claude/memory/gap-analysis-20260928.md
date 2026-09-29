# Gap Analysis — 20260928 (pós-fila pentagi/Verba #262–#281)

- Repository: /home/ubuntu/repos/LangGraph-UI | Branch: main @ 34670e7 (clean)
- Driver: usuário executou `/gap-analysis` após reconcile orchestrator (issues #269–#279 fechadas, PR #382)
- Mode: fresh run. Prior runs: 20260914/16/17/17-r2/22/23/25/26 (todas ENTREGUE)
- gh: OK (afonsoft). Sibling skills: write-specs/create-issues/orchestrator presentes.
- Baseline verificado na sessão: build 0 warnings · 1043 unit + 288 integration verdes.

## 1. Source inventory

| Source | Status | Notes |
| --- | --- | --- |
| .specs/ | present | 148 SPECs — todas `Done` após sync de pgvector-live-tests (PR #382) |
| GitHub Issues | gh OK | 0 abertas (reconcile desta sessão) |
| docs/ (en/pt, architecture) | present | drift grande vs wave 2026-09-27/28 |
| CLAUDE.md / README.md | present | Estado Atual não cobre wave pentagi/Verba |
| .claude/memory/ | present | débitos documentados: resilience dispatcher/UI, timeline viewer, flaky TelemetryTests, orphan jobs edge |
| Working tree | clean | — |
| Branches | clean | 6 remotas + 13 locais stale removidas nesta sessão |

## 2. Candidates and verdicts

| Key | Category | Verdict | Priority | Evidence |
| --- | --- | --- | --- | --- |
| GAP-documentation-post-pentagi-wave-sync | documentation | CONFIRMADO | alta | grep=0 em CLAUDE.md/README/docs para YouTube/GitRepository/Unstructured/AudioTranscription/ChainAst/EvidenceChain/TemporalGraph/McpDynamicRagActionBridge/RagEvaluation/Voyage; endpoint `/api/v1/evidence/sessions/{id}/bundle` ausente de docs/{en,pt}/API.md; knobs `Resilience:Fallback`, `Agent:EnableDynamicActionBridge`, `Search:Relaxation:*`, `Search:LimitMode` não documentados. Mesmo padrão de SPEC-20260926-post-wave-docs-sync (Done) — wave nova |
| GAP-implementation-resilience-tool-fallback | implementation | CONFIRMADO | média | `ToolCapabilityRegistry` registrado (`KnowledgeHubServiceCollectionExtensions.cs:205`) sem nenhum consumidor; SPEC #275 §37-41 previa fallback de tools (Tavily↔Firecrawl↔DuckDuckGo) e §45-46 UI de Settings — ambos ausentes (Client: só binários). Chat fallback entregue (`ResilientChatClient.Wrap` :227) — gap é a metade de tools + UI. Débito em memory.md |
| GAP-implementation-graph-timeline-viewer | implementation | CONFIRMADO | média | SPEC #274 §50-51 exige "visualizador de grafo com filtro de linha do tempo e badges de episódio"; nenhuma página de grafo existe em `src/KnowledgeHub.Client/Pages/` (14 páginas, sem Graph). Backend temporal completo (KgNode temporal fields, KgEpisode, 5 tools) — gap é só UI |
| GAP-tests-flaky-telemetry-span-tree | tests | CONFIRMADO | média | `TelemetryTests.cs:61-71` `CollectActivities` registra `ActivityListener` process-wide; sob paralelismo xUnit amostra activities de outros testes → asserts `Single`/`Equal(2)` flakam (memory.md: "falha esporádica sob paralelismo, passa isolado") |
| GAP-observability-orphaned-job-terminal-event | observability | CONFIRMADO | média-baixa | `IngestionWorker.FailOrphanedJobsAsync` (:52-80) marca failed mas não publica evento terminal — feed de progresso nunca encerra o job (edge case registrado 2026-09-27) |
| GAP-observability-new-arms-telemetry | observability | CONFIRMADO | baixa | `TemporalGraphRetriever.cs` e `McpDynamicRagActionBridge.cs`: 0 `StartActivity`; `KnowledgeHubMetrics.cs`: 0 counters para temporal/episode/live_tool/evidence — convenção de SPEC-20260925-otel-pipeline-spans cobre só rewrite/rrf/mmr/rerank |
| GAP-automation-coverage-gate | automation | CONFIRMADO | baixa | `ci-build-test.yml:69` coleta XPlat coverage mas nenhum step avalia; `coverage_target: 80` (orchestrator_stats) nunca verificado — cobertura regride silenciosamente |
| enforce_admins escape hatch | security | INCONCLUSIVO (carried ×4) | — | Decisão do dono desde 20260917-r2; não implementável por agente |
| SPEC archive → docs/specs/ | hygiene | REJEITADO | — | CLAUDE.md do repo designa `.specs/` como fonte canônica; convenção de archive do orchestrator nunca adotada aqui |
| Connector UI coverage (novos SourceTypes) | implementation | REJEITADO | — | YouTube/RssFeed/GitRepository/UnstructuredDocument/AudioTranscription presentes em 4 arquivos do Client cada (SourceEditDialog + model) |
| install.sh env propagation | operation | REJEITADO | — | Entregue via PR #137 (SPEC-20260918 Done) |
| Stale branches | hygiene | REJEITADO | — | Removidas nesta sessão (6 remotas + 13 locais, tree-verified) |
| Open issues drift | process | REJEITADO | — | 0 abertas após reconcile; #269–#279 fechadas com evidência |

## 3. Consolidation → Draft SPECs

| SPEC | Cobre |
| --- | --- |
| `.specs/SPEC-20260928-post-pentagi-wave-docs-sync.md` | gap 1 (docs/README/CLAUDE/API/architecture da wave) |
| `.specs/SPEC-20260928-resilience-tool-fallback-wiring.md` | gap 2 (interception nos 3 call sites + Settings UI) |
| `.specs/SPEC-20260928-graph-timeline-viewer.md` | gap 3 (página /graph + REST read endpoints) |
| `.specs/SPEC-20260928-test-reliability-and-coverage-gate.md` | gaps 4 + 7 (isolamento do listener + gate CI) |
| `.specs/SPEC-20260928-observability-followups.md` | gaps 5 + 6 (evento terminal de órfãos + spans/counters novos braços) |

Ordem sugerida (deps): docs-sync ∥ test-reliability ∥ observability independentes; resilience e graph-viewer independentes entre si. Nenhuma depende de outra — todas podem paralelizar (parallel_limit=2) ou sequenciar por prioridade: docs-sync (alta) → resilience (média) → graph-viewer (média) → test-reliability (média) → observability (média-baixa).

## 4. Non-spec pendencies

- `enforce_admins` — decisão do dono (carried ×4).

## 5. Gate

Aguardando aprovação antes de Issues/execução (apresentado ao usuário em pt-BR).

## 2026-09-29 — Review-comments corpus (PRs #369–#394)

Segunda onda: análise dos comentários `devin-ai-integration`/`github-advanced-security`/`sonarqubecloud` nos 20 PRs mais recentes. ~256 comentários → ~90 findings acionáveis → **10 SPECs Draft** em `.specs/SPEC-20260929-*` (PR #395).

Destaques confirmados em código antes de escrever:
- `ResilienceSettings.ChatFallbacksJson` → `ApiKey` em claro (S2 PR #391).
- `TemporalGraphRetriever` sem `CallerScope`/`SourceId` — facts vazam entre fontes.
- Expansão de janela ignora `SuspicionFlags` — flagged chunk volta ao prompt.
- `Provider ?? "openai"` no SaveAsync corrompe alternates Ollama (S2 bug).

SonarQube: PRs anteriores todos "Quality Gate Passed"; só #391 falhou (duplication 3.1% > 3%) — resolvido com `.sonarcloud.properties` cpd.exclusions de Migrations.
Coverage gate (#393) já dogfoodou: PR #391 falhou com 19.95% < 20% → testes do ResilienceSettingsService adicionados.
