# SPEC-20261002-sonarqube-backlog-wave2 — SonarCloud backlog wave 2

| Campo | Valor |
|-------|-------|
| Status | Approved |
| Ticket | a preencher pelo /create-issues (epic E25) |
| Origem | Skill sonarqube-autofix (sessão 2026-10-02, run 2) |
| Pré-requisito | SPEC-20261001-sonarqube-backlog-cleanup (E23) — ondas #470–#479 mergeadas |

## Contexto

Re-download do backlog `afonsoft_LangGraph-UI`: **1.093 issues OPEN**. Descobertas
estruturais deste run:

1. **1.036 issues estão em `docs/architecture/runtime-architecture.html`** (archify
   gerado, 825KB). O workflow CI já passa `/d:sonar.exclusions="docs/architecture/**/*.html"`,
   mas o SonarCloud **AutoScan ignora parâmetros `/d:`** — ele só honra
   `.sonarcloud.properties`, que hoje só contém `sonar.cpd.exclusions`. Falta
   `sonar.exclusions` lá → 1 linha resolve ~95% do backlog.
2. Análise base `ea5f083` antecede merges #477/#478/#479 — sites S3776 em
   `Playground.razor` e `KnowledgeToolsProvider.cs` já estão resolvidos na main
   (verificar site a site antes de refatorar).
3. Os 4 sites `S2077` interpolam apenas fragmentos SQL internos (cláusulas
   estáticas `AND ... ANY($n)` e enum `_storageType` validado) com valores já
   parametrizados (`$1..$4`) — são falsos positivos; 2 já têm `// NOSONAR` na
   linha errada (a supressão precisa estar na linha flagueada).
4. `S1172` em `KnowledgeHubA2AAgent.RecordEvidenceAsync` (`success`) e
   `SearchService.RerankAsync` (`breakdowns`) são params mortos introduzidos
   pelos próprios refactors E23 — remoção é behavior-preserving.

Detalhe por issue em `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`.

## Requisitos funcionais

### RF-01 — Exclusão AutoScan de artefatos gerados (resolve ~1.036 issues)

- [ ] `.sonarcloud.properties`: adicionar `sonar.exclusions=docs/architecture/**/*.html`
      (espelha o `/d:` do `code-quality.yml` para o AutoScan).

### RF-02 — Segurança: open redirect S5146 (1 issue, BLOCKER)

- [ ] `src/KnowledgeHub.Client/Pages/Login.razor:179` — `Nav.NavigateTo(returnPath)`
      flagueado mesmo com a sanitização custom (#468). Reestruturar para padrão
      reconhecido pelo taint engine (ex.: `Nav.ToAbsoluteUri(path)` + validação
      de host/base) preservando o comportamento: só paths locais, sem scheme/`%`.

### RF-03 — Segurança: S2077 parameterized query (4 issues)

- [ ] `src/KnowledgeHub.Server/Search/LexicalSearchService.cs:135`
- [ ] `src/KnowledgeHub.Server/Search/LexicalSearchService.cs:182` — NOSONAR existe na linha errada; mover para a linha flagueada
- [ ] `src/KnowledgeHub.Server/VectorStore/PostgresVectorStore.cs:249` — adicionar justificativa na linha flagueada
- [ ] `src/KnowledgeHub.Server/VectorStore/PostgresVectorStore.cs:468` — NOSONAR existe na linha errada; mover para a linha flagueada

Interpolações são fragmentos internos (cláusula estática `AND ... ANY($n)`,
`TableName` interno, enum `_storageType` ∈ {vector|halfvec}) com todos os valores
de usuário já parametrizados — supressão justificada na linha correta, ou
reformulação equivalente se mais limpa.

### RF-04 — Bugs corretivos (6 issues)

- [ ] `src/KnowledgeHub.Server/Services/SearchService.cs:1252` [BUG/S2583] condição sempre false — revisar e corrigir/remover dead path
- [ ] `src/KnowledgeHub.Server/Services/AgentService.cs:86` [BUG/S2583] idem
- [ ] `src/KnowledgeHub.Server/Api/StreamingEndpoints.cs:134` [BUG/S8949] passar `http.RequestAborted` como CancellationToken
- [ ] `src/KnowledgeHub.Server/Api/StreamingEndpoints.cs:170` [BUG/S8949] idem
- [ ] `src/KnowledgeHub.Server/A2A/KnowledgeHubA2AAgent.cs:148` [S1172] remover param `success` não usado de `RecordEvidenceAsync`
- [ ] `src/KnowledgeHub.Server/Services/SearchService.cs:553` [S1172] remover param `breakdowns` não usado de `RerankAsync`
- [ ] `src/KnowledgeHub.Server/Services/SearchService.cs:128` [S1481] remover local `relaxLevel` não usado

### RF-05 — Cognitive complexity residual S3776 (~20 sites)

Verificar cada site na main atual antes de refatorar (staleness pós-merge).
Extração preservando comportamento; novas assinaturas ≤7 params (sem novos S107);
não tocar construtores; manter comentários SPEC-*/RF-*.

- [ ] `src/KnowledgeHub.Server/Settings/ApiKeyChatSettingsService.cs:119` (CC16)
- [ ] `src/KnowledgeHub.Server/Services/AgentService.cs:551` (CC17)
- [ ] `src/KnowledgeHub.Server/Graph/EntityExtractor.cs:65` (CC38)
- [ ] `src/KnowledgeHub.Server/Graph/SqliteKnowledgeGraphStore.cs:193` (CC18)
- [ ] `src/KnowledgeHub.Server/Ingestion/IngestionService.cs:862` (CC22)
- [ ] `src/KnowledgeHub.Server/Ingestion/IngestionService.cs:981` (CC22)
- [ ] `src/KnowledgeHub.Server/Api/SearchEndpoints.cs:32` (CC18)
- [ ] `src/KnowledgeHub.Server/Ingestion/Chunking/ConfigTextChunker.cs:25` (CC18)
- [ ] `src/KnowledgeHub.Server/Eval/EvalCase.cs:28` (CC18)
- [ ] `src/KnowledgeHub.Server/Ingestion/Connectors/NotionBlockRenderer.cs:28` (CC20)
- [ ] `src/KnowledgeHub.Server/Ingestion/Connectors/NotionBlockRenderer.cs:192` (CC27)
- [ ] `src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:287` (CC24)
- [ ] `src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:93` (CC18)
- [ ] `src/KnowledgeHub.Server/Api/FrameworkAssetsEndpoints.cs:21` (CC18)
- [ ] `src/KnowledgeHub.Shared/Tooling/ToolArgumentBuilder.cs:73` (CC25)
- [ ] `src/KnowledgeHub.Server/Services/AnswerService.cs:141` (CC19)
- [ ] `src/KnowledgeHub.Server/Ingestion/Connectors/DocumentFileConnector.cs:39` (CC17)
- [ ] `src/KnowledgeHub.Server/Ingestion/Connectors/HtmlTextExtractor.cs:91` (CC17)
- [ ] `src/KnowledgeHub.Server/Ingestion/Connectors/WebPageConnector.cs:21` (CC36)
- [ ] `src/KnowledgeHub.Server/Ingestion/MarkdownChunker.cs:15` (CC30)

### RF-06 — Batch mecânico residual (18 issues)

- [ ] S3358 nested ternary ×6: SearchService.cs:426,177; ToolArgumentBuilder.cs:365; AgentService.cs:448,858,859
- [ ] S107 >7 params ×2 (exceto ctor SearchService:22 — fora de escopo): SearchService.cs:264, SafeCache.cs:134
- [ ] S8969 null-forgiving redundante ×3: SettingsEndpoints.cs:535,586; SettingsToolsProvider.cs:77
- [ ] S1192 literais duplicados ×3: IngestionWorker.cs:21–23 ('queued','running','failed' → consts)
- [ ] S3878 array creation ×2: DocumentFileConnector.cs:143; IngestionService.cs:1184
- [ ] S3267 loop→Select ×2: GoogleDriveApiClient.cs:148; SqliteKnowledgeGraphStore.cs:79
- [ ] S1118 ×1: Program.cs:111 (protected ctor ou static)
- [ ] S6607 ×1: ToolActionAnnotationDetector.cs:119 (Where antes de OrderByDescending)
- [ ] css:S7924 ×1: NavMenu.razor.css:79 (contraste mínimo)

## Critérios de aceite

- `dotnet build` 0W/0E nos projetos tocados; suíte unit verde.
- Nova análise SonarCloud na main reporta 0 issues OPEN nas regras acima
  (excluídas as NOSONAR justificadas e o ctor fora de escopo).
- Issues geradas pelo AutoScan no HTML gerado somem sem alterar o arquivo.
