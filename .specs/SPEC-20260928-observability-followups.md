# SPEC-20260928-observability-followups

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `observability-followups` |
| Type | `Feature` (observability) |
| Stack | `.NET 10 / OTel (ActivitySource + Meter) / job progress feed` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260928-observability-followups` |
| Ticket | `#388` — https://github.com/afonsoft/LangGraph-UI/issues/388 |
| Status | `Done` |

## 1. User Story

**As a** operador/debugger do Knowledge MCP Hub
**I want** que jobs órfãos de ingestão emitam evento terminal e que os braços novos do pipeline RAG (temporal graph, live-action bridge, evidence) emitam spans/métricas
**So that** o feed de progresso não mostre jobs travados para sempre e o tracing cubra o pipeline completo — não só rewrite/rrf/mmr/rerank.

**Problem context:**
1. `IngestionWorker.FailOrphanedJobsAsync` (`src/KnowledgeHub.Server/Ingestion/IngestionWorker.cs:52-80`) marca jobs `queued`/`running` como `failed` + `"interrupted by restart"` mas **não publica evento terminal** — quem acompanha o job-progress feed nunca recebe conclusão (edge case registrado em `.claude/memory/memory.md` 2026-09-27).
2. SPEC-20260925-otel-pipeline-spans (Done) estabeleceu spans `search.rewrite/rrf/mmr/rerank` + `ingestion.job/embed` — mas os braços entregues depois não emitem nada: `TemporalGraphRetriever.cs` e `McpDynamicRagActionBridge.cs` têm 0 ocorrências de `StartActivity`/`ActivitySource`; `KnowledgeHubMetrics.cs` não tem counter para `temporal`, `episode`, `live_tool` ou `evidence` (grep: 0 matches).

## 2. Scope

**In scope:**
- `FailOrphanedJobsAsync`: publicar o mesmo evento terminal que `FailStrandedJobAsync`/path normal usa (terminal status + `FinishedAt`), para que o feed SSE/SignalR encerre o job na UI.
- Spans OTel nos novos braços: `search.temporal_graph` (window/recent/relationships/diverse — tag `mode`), `search.live_actions` (tag `tool`, `outcome`), `evidence.emit`/`evidence.verify` — mesmos padrões de `KnowledgeHubActivity` (naming, tags, error status).
- Counters: `knowledgehub.graph.temporal_queries` (tag `mode`), `knowledgehub.live_tool.executions` (tags `tool`, `outcome`), `knowledgehub.evidence.records` — seguindo convenção `knowledgehub.*` existente.
- Atualizar docs de observabilidade se existir lista de spans/métricas (docs/architecture ou CLAUDE.md — via SPEC docs-sync se for só doc).

**Out of scope:**
- Dashboards/alerts (Grafana) — só emissão.
- Mudar schema de métricas existentes ou nomes — aditivo apenas.
- Tracing de conectores individuais — já cobertos por `ingestion.job`/`sync.duration`.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Ingestion/IngestionWorker.cs` — sweep de órfãos.
- `src/KnowledgeHub.Server/Graph/TemporalGraphRetriever.cs` — spans + counter.
- `src/KnowledgeHub.Server/Mcp/Bridge/McpDynamicRagActionBridge.cs` — span + counter por execução.
- `src/KnowledgeHub.Server/Audit/Evidence/EvidenceEmission.cs` / `EvidenceChainService.cs` — counter por receipt.
- `src/KnowledgeHub.Server/Telemetry/{KnowledgeHubActivity,KnowledgeHubMetrics}.cs` — registrar novos instrumentos.
- `tests/KnowledgeHub.Tests.Unit` — telemetry tests + worker test do sweep.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Telemetry/{KnowledgeHubActivity,KnowledgeHubMetrics}.cs`
- `src/KnowledgeHub.Server/Ingestion/IngestionWorker.cs` (`FailStrandedJobAsync` para o padrão do evento terminal)
- `tests/KnowledgeHub.Tests.Unit/Telemetry/TelemetryTests.cs` (padrão de assert de spans)

## 4. Requirements

### RF-001: Evento terminal em órfãos
- **Description:** sweep de órfãos publica o mesmo evento terminal por job (status `failed`, reason `interrupted by restart`) que o caminho normal usa.
- **Rules:** sem evento → bug; cancelamento legítimo segue distinguível; sweep falho continua só warning (nunca crash startup).
- **Input → Output:** restart com job queued → job aparece `failed` E evento entregue ao feed.

### RF-002: Spans nos braços novos
- **Description:** `search.temporal_graph`, `search.live_actions`, `evidence.emit` como filhos do span da request/search existente.
- **Rules:** erros marcam `Status=Error` + tag `exception.type` (padrão OTel); sem PII em tags.
- **Input → Output:** query temporal → span `search.temporal_graph` com `mode=window` medido.

### RF-003: Counters
- **Description:** 3 counters novos registrados no `Meter` existente e incrementados nos pontos certos.
- **Input → Output:** execução de live tool → `live_tool.executions{tool=X,outcome=success}++`.

## 5. Acceptance Criteria

- AC-1: teste do worker prova evento terminal publicado no sweep (não só status no DB).
- AC-2: span tree inclui os novos nós como filhos do root correto (telemetry test).
- AC-3: counters incrementam — teste com `MetricListener` ou equivalente do padrão existente.
- AC-4: 0 warnings; suite verde.

## 6. Task Plan

1. RED: teste do sweep esperando evento terminal → fix `FailOrphanedJobsAsync`.
2. Spans + counters nos 3 pontos → telemetry tests.
3. Suite verde.

## 7. Organization Guardrails

- **Aditivo:** nenhum instrumento existente renomeado/re-movido.
- **Sem PII** em tags/spans; secrets nunca em attributes.
- **Performance:** spans só nos pontos quentes do request path, nunca por-chunk em loop de ingestão.
