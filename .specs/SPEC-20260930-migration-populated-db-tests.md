# SPEC-20260930-migration-populated-db-tests

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `migration-populated-db-tests` |
| Type | `Fix` (tests) |
| Stack | `.NET 10 / EF Core` — Postgres + SQLite migrations |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260930-migration-regression-tests` |
| Status | `Done` |
| Source | `GAP-tests-migration-populated-db` (gap-analysis-20260930) |

## 1. User Story

**As a** mantenedor
**I want** um teste de regressão que aplique migrations sobre dados existentes
**So that** `AddColumn NOT NULL` sem default (ou equivalente) falhe em CI antes de chegar ao deploy — o incidente `23502: column "Labels" contains null values` em `rag_db` (2026-09-30) passou por toda a pipeline porque os testes só migram DBs vazios.

## 2. Findings

- 🔴 Incidente real: `20260928193659_TemporalEpisodicGraph` adicionou `KgNodes.Labels text[] NOT NULL` sem default; falhou no deploy contra `rag_db` populado (Postgres). A variante SQLite tinha `defaultValue "[]"` — divergência entre providers não detectada. Fix já aplicado (`defaultValueSql '{}'::text[]`, PR #425).
- 🔴 AS-IS: `tests/KnowledgeHub.Tests.Integration/UnifiedDatabaseProviderTests.cs` é o único arquivo de teste que toca migrations — sempre sobre schema novo/vazio. Nenhum teste semeia linhas em uma versão anterior do schema e depois migra.
- 🟡 TO-BE: teste de integração que (1) cria `KgNodes`/`KgEdges` com o schema pré-`TemporalEpisodicGraph` (DDL mínima ou `EnsureCreated` + drop das colunas novas), (2) insere linhas representativas, (3) roda `MigrateAsync`, (4) afirma que as linhas sobrevivem e os defaults foram aplicados.

## 3. Requirements

1. **RF-001** — Teste SQLite (CI padrão): criar `KgNodes` com schema antigo (sem `Labels`, `ObservedAt`, `ValidFrom`, `ValidTo`, `EpisodeId`), inserir ≥1 linha, aplicar migrations, assert linha preservada + `Labels='[]'`.
2. **RF-002** — Teste Postgres equivalente protegido por flag (mesmo padrão dos `pgvector-live-tests` — pular quando `PG_CONNECTION`/`RUN_LIVE_PG` ausente), assert `Labels='{}'`.
3. **RF-003** — Convenção documentada (comentário no teste + `CLAUDE.md` "Convenções"): `AddColumn nullable:false` em tabela existente exige `defaultValue`/`defaultValueSql` ou sequência nullable→backfill→NOT NULL — checklist na SPEC para futuras migrations.
4. **RF-004** — Sem mocks da migração: o teste deve rodar `DatabaseMigrator`/EF `MigrateAsync` real.

## 4. Acceptance Criteria

- **Given** uma `KgNodes` populada no schema antigo **when** `MigrateAsync` roda **then** todas as migrations aplicam sem `23502`/erro e os dados permanecem.
- Reverter artificialmente o `defaultValueSql` da migration Postgres deve fazer o teste RF-002 falhar (verificação negativa local, não commitada).
- CI: teste SQLite roda no gate padrão; Postgres roda no job live opcional.

## 5. Task Plan

1. Adicionar `MigrationPopulatedDbTests` em `tests/KnowledgeHub.Tests.Integration`.
2. Implementar RF-001..RF-004; rodar suíte integration focada.
3. PR → merge.
