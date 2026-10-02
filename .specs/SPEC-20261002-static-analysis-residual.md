# SPEC-20261002-static-analysis-residual — SonarCloud wave 3 + CodeQL residual

| Campo | Valor |
|-------|-------|
| Status | `In Review` — PR [#510](https://github.com/afonsoft/LangGraph-UI/pull/510) |
| Ticket | Epic [#503 (E27)](https://github.com/afonsoft/LangGraph-UI/issues/503) — slice [#505](https://github.com/afonsoft/LangGraph-UI/issues/505) (RF-01..03) |
| Origem | gap-analysis + qa-analyst 2026-10-02 (SonarCloud API pública + `gh api code-scanning/alerts`) |
| Pré-requisito | E23 (#470–#479) e E25 (#487–#491) mergeadas — backlog Sonar 1.093 → 8 |

## Contexto

Após as waves E23/E25 restam **8 issues OPEN no SonarCloud** (main) e **29
alertas CodeQL abertos**. Nenhum está mapeado a SPEC/issue viva — `gh issue
list --state open` retorna 0 e as SPECs de débito anteriores estão `Done`.

Sites verificados na main atual (`01c6c7a`) — o refactor E25 corrigiu
`RerankAsync` mas deixou residual em `ApplyFinalTrimAsync` (`SearchService.cs:354`,
param `breakdowns` declarado e não usado no corpo, confirmado lendo o método).

## Requisitos funcionais

### RF-01 — SonarCloud: complexidade residual S3776 (5 sites)

Extração preservando comportamento; sem novos S107; manter comentários SPEC-*.

- [ ] `src/KnowledgeHub.Client/Pages/Playground.razor:725` (CC46)
- [ ] `src/KnowledgeHub.Client/Pages/Playground.razor:557` (CC23)
- [ ] `src/KnowledgeHub.Server/Ingestion/Connectors/NotionBlockRenderer.cs:240` (CC18)
- [ ] `src/KnowledgeHub.Server/Mcp/ToolProviders/KnowledgeToolsProvider.cs:606` (CC17)
- [ ] `src/KnowledgeHub.Server/Services/SearchService.cs:1187` (CC16)

### RF-02 — SonarCloud: code smells residuais (3 sites)

- [ ] `src/KnowledgeHub.Server/Services/SearchService.cs:354` [S1172] —
      remover param `breakdowns` de `ApplyFinalTrimAsync` (morto; residual do
      refactor que removeu o de `RerankAsync`) e atualizar call-sites.
- [ ] `src/KnowledgeHub.Server/Services/SearchService.cs:22` [S107] — ctor com
      14 params (>7): agrupar em options/deps-struct ou justificar supressão.
- [ ] `src/KnowledgeHub.Server/Ingestion/Connectors/NotionBlockRenderer.cs:106`
      [S3358] — desaninhar ternário.

### RF-03 — CodeQL: revisar backlog de 29 alertas

Triagem por classe; corrigir os triviais, suprimir com justificativa os
intencionais (catch-all que logam já são policy-aceito — documentar via
`SuppressMessage` ou config do CodeQL):

- [ ] `cs/unused-collection` `GitRepositoryConnector.cs:168` (severity error) —
      verificar qual coleção da deconstruction é não-lida (candidato a FP de
      padrão tuple; se real, remover).
- [ ] `cs/catch-of-all-exceptions` ×8 — StreamingEndpoints.cs:167/171/176,
      IngestionService.cs:474/531, IngestionWorker.cs:171, AgentService.cs:718,
      SettingsEndpoints.cs:508, Chat.razor:143. Refinar tipo ou suprimir
      justificado.
- [ ] `cs/linq/missed-where`/`missed-select` ×7, `cs/useless-cast-to-self` ×4
      (SearchService.cs:979/986/1013/1035), `cs/path-combine` ×4,
      `cs/missed-using-statement` ×2 (IngestionService.cs:72,
      IngestionWorker.cs:131), `cs/empty-catch-block` (StreamingEndpoints.cs:176),
      `cs/missed-ternary-operator` (Login.razor:179 — mesmo site do S5146 já
      tratado; avaliar se a forma atual ainda precisa do ternário).

## Critérios de aceite

- SonarCloud `issueStatuses=OPEN,CONFIRMED` → 0 (ou só supressões justificadas).
- CodeQL alerts abertos → reduzidos a um conjunto explicitamente suprimido.
- `dotnet build` + `dotnet test` verdes; Quality Gate SonarCloud PASS.

## Fora de escopo

- Mudar o Quality Gate SonarCloud para bloquear merge (processo, não código).
- Renomear/suprimir alertas em massa sem revisão caso-a-caso.
