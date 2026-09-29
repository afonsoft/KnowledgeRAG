# SPEC-20260928-test-reliability-and-coverage-gate

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `test-reliability-and-coverage-gate` |
| Type | `Infra` (tests + CI) |
| Stack | `xUnit / OpenTelemetry ActivityListener / GitHub Actions / coverlet` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260928-test-reliability-coverage` |
| Ticket | `#387` — https://github.com/afonsoft/LangGraph-UI/issues/387 |
| Status | `Approved` |

## 1. User Story

**As a** mantenedor do Knowledge MCP Hub
**I want** testes de telemetria deterministicamente isolados sob paralelismo e um gate de cobertura no CI
**So that** o flake esporádico de `Agent_EmitsSpanTree_IterationAndToolChildren` pare de queimar reruns, e o target de 80% registrado deixe de ser apenas coleta passiva.

**Problem context:**
1. `TelemetryTests.CollectActivities` (`tests/KnowledgeHub.Tests.Unit/Telemetry/TelemetryTests.cs:61-71`) registra um `ActivityListener` **process-wide** via `ActivitySource.AddActivityListener`. Sob a paralelização default do xUnit (collections em paralelo), qualquer `Activity` de outro teste é amostrada pelo listener → asserts `Single`/`Equal(2)` falham esporadicamente (observado em `.claude/memory/memory.md`: "falha esporádica sob paralelismo (passa isolado)").
2. CI coleta coverage (`ci-build-test.yml:69` `--collect:"XPlat Code Coverage"`) mas nenhum step avalia o resultado — `coverage_target: 80` está em `.claude/memory/orchestrator_stats.md` e nunca é verificado. Sem gate, cobertura regride silenciosamente.

## 2. Scope

**In scope:**
- Isolamento dos testes de telemetria: `CollectActivities` filtra por `ActivitySource` name e/ou marca por `Activity.Current?.TraceId`; alternativa aceita: `[Collection("Telemetry")]` serializando a classe (xUnit collection = serial dentro da collection). Escolher a opção mais simples que elimine o ruído cross-test.
- Gate de cobertura: step no `ci-build-test.yml` que parseia o `coverage.cobertura.xml` e falha o job se line-rate < threshold. Implementação preferida: script inline (`python`/XPath ou `dotnet` global tool já em cache) — sem nova action de terceiros se evitável. `.github/workflows/` **é protegido** — a mudança requer o mesmo processo de PR + aprovação.
- Threshold inicial: medir cobertura atual e fixar no valor observado arredondado para baixo (ratchet: nunca abaixo de main), não o 80 aspiracional se estiver abaixo — reportar o número real no PR.

**Out of scope:**
- Refatorar `AgentService`/pipeline para testabilidade — só o helper de teste.
- reportgenerator/HTML reports, badges, Codecov — XML parse + fail é suficiente.
- Mudar paralelismo global do xUnit (`xunit.runner.json` disable) — overkill para um helper.
- Cobertura de integration tests no gate — só a unit já coletada.

## 3. Technical Context

**Where the change happens:**
- `tests/KnowledgeHub.Tests.Unit/Telemetry/TelemetryTests.cs` — `CollectActivities` + listener filter.
- `.github/workflows/ci-build-test.yml` — novo step pós-teste no job Unit Tests (⚠️ protected: PR obrigatório, checks valem como gate).
- Baseline: 1043 unit + 288 integration verdes em `main@34670e7`.

**Files to read before implementing:**
- `tests/KnowledgeHub.Tests.Unit/Telemetry/TelemetryTests.cs` (helper + 3 usos)
- `src/KnowledgeHub.Server/Telemetry/KnowledgeHubActivity.cs` (ActivitySource name para filtro)
- `.github/workflows/ci-build-test.yml` (job Unit Tests, upload de trx)

## 4. Requirements

### RF-001: Listener isolado
- **Description:** `CollectActivities` só amostra activities do `ActivitySource` do sistema sob teste (`KnowledgeHubActivity.Source` name) e/ou da árvore raizada no teste — atividades de outros testes nunca entram na lista.
- **Rules:** `ShouldListenTo` filtra pelo `ActivitySource.Name`; `Sample` registra só se `Activity.Current` pertence à árvore criada dentro de `action` (ou documentar por que não dá e usar collection serializada).
- **Input → Output:** `dotnet test --filter TelemetryTests` em loop ×20 e suite completa paralela ×5 → 0 falhas.

### RF-002: Coverage gate no CI
- **Description:** step após `dotnet test --collect` que lê `coverage.cobertura.xml` (`line-rate`) e falha com mensagem clara se < `COVERAGE_MIN` env.
- **Rules:** `COVERAGE_MIN` definido como env no job; valor inicial = cobertura medida arredondada para baixo; mensagem de erro imprime valor observado vs mínimo.
- **Input → Output:** job falha com `Coverage 74.2% < 80%` se regredir; passa em main.

## 5. Acceptance Criteria

- AC-1: `TelemetryTests` nunca falha por poluição de activities de outros testes — evidência: 20× filtrado + 5× suite completa paralela verdes.
- AC-2: CI tem step de gate; PR com cobertura artificialmente baixa falharia (provado via log do step mostrando o parse — não é preciso quebrar de verdade; dry-run do step no PR basta).
- AC-3: `COVERAGE_MIN` documentado no workflow com comentário "ratchet — nunca baixar".

## 6. Task Plan

1. Reproduzir flake: suite paralela em loop até falhar (ou provar poluição via log).
2. Filtro no `ShouldListenTo`/`Sample` → verde em loop.
3. Step de gate no workflow + env `COVERAGE_MIN` com valor medido.
4. PR descreve o valor medido e o mecanismo.

## 7. Organization Guardrails

- **Workflows:** `.github/workflows/` protegido — mudança só via PR com checks verdes (dogfooding do próprio gate).
- **Não calar o teste:** proibido `Skip=` ou `[Trait]` de exclusão — corrigir o isolamento.
- **Ratchet:** `COVERAGE_MIN` só sobe, nunca desce para "passar CI".
