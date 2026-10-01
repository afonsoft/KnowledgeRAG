# SPEC-20261001-sonarqube-backlog-cleanup — Backlog residual SonarCloud

| Campo | Valor |
|-------|-------|
| Status | Approved |
| Origem | Skill sonarqube-autofix (sessão 2026-10-01) |
| Pré-requisito | nenhum — aplicável em fatias independentes |

## Contexto

Backlog SonarCloud `afonsoft_LangGraph-UI`: 1364 issues → 1152 resolvidas no PR
sonar-autofix (fixes diretos + exclusão de artefatos gerados `docs/architecture/*.html`).
Esta SPEC cobre as **212 restantes**, agrupadas por regra/tema. Detalhe por issue em
`.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` e `remaining.json` (não-commitados).

## Requisitos funcionais

### RF-01 — Cognitive complexity (S3776) (55 issues)

- src/KnowledgeHub.Client/Pages/Playground.razor:459 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed.
- src/KnowledgeHub.Server/Resilience/ResilientToolInvoker.cs:35 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 22 to the 15 allowed.
- src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs:28 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 80 to the 15 allowed.
- src/KnowledgeHub.Server/Program.cs:None [CRITICAL] Refactor this top-level file to reduce its Cognitive Complexity from 33 to the 15 allowed.
- src/KnowledgeHub.Server/Api/SettingsEndpoints.cs:17 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 110 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/Cloud/GoogleDriveGateway.cs:34 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 20 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:81 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:335 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 39 to the 15 allowed.
- src/KnowledgeHub.Server/Services/AgentService.cs:177 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 31 to the 15 allowed.
- src/KnowledgeHub.Server/Services/AgentService.cs:526 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 35 to the 15 allowed.
- src/KnowledgeHub.Server/Api/EvalEndpoints.cs:41 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 31 to the 15 allowed.
- src/KnowledgeHub.Server/Api/StreamingEndpoints.cs:17 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 26 to the 15 allowed.
- src/KnowledgeHub.Server/Eval/EvalRunner.cs:30 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 16 to the 15 allowed.
- src/KnowledgeHub.Client/Pages/SourceEditDialog.razor:728 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 162 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/Cloud/CloudConnectorBase.cs:33 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 19 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:96 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 36 to the 15 allowed.
- src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs:125 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 24 to the 15 allowed.
- src/KnowledgeHub.Server/Services/SearchService.cs:106 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 55 to the 15 allowed.
- src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs:480 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 72 to the 15 allowed.
- src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs:219 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 53 to the 15 allowed.
- src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs:350 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 38 to the 15 allowed.
- src/KnowledgeHub.Server/Api/AskEndpoints.cs:9 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 17 to the 15 allowed.
- src/KnowledgeHub.Server/Api/SourcesEndpoints.cs:9 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 22 to the 15 allowed.
- src/KnowledgeHub.Server/Api/ToolsEndpoints.cs:16 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 24 to the 15 allowed.
- src/KnowledgeHub.Server/Mcp/ToolProviders/KnowledgeToolsProvider.cs:373 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 31 to the 15 allowed.
- src/KnowledgeHub.Server/Search/MmrSelector.cs:17 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed.
- src/KnowledgeHub.Server/Search/ResolvedSearchFilter.cs:65 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 48 to the 15 allowed.
- src/KnowledgeHub.Server/Search/RrfFuser.cs:32 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 45 to the 15 allowed.
- src/KnowledgeHub.Server/Services/SearchService.cs:593 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 39 to the 15 allowed.
- src/KnowledgeHub.Server/Services/SearchService.cs:738 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 28 to the 15 allowed.
- src/KnowledgeHub.Server/Settings/ApiKeyChatSettingsService.cs:90 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 30 to the 15 allowed.
- src/KnowledgeHub.Client/Pages/Playground.razor:709 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 46 to the 15 allowed.
- src/KnowledgeHub.Server/Program.cs:146 [CRITICAL] Refactor this static local function to reduce its Cognitive Complexity from 16 to the 15 allowed.
- src/KnowledgeHub.Server/Graph/EntityExtractor.cs:65 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 38 to the 15 allowed.
- src/KnowledgeHub.Server/Graph/SqliteKnowledgeGraphStore.cs:192 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:727 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 22 to the 15 allowed.
- src/KnowledgeHub.Server/Api/SearchEndpoints.cs:13 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed.
- src/KnowledgeHub.Server/Services/SearchService.cs:990 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 17 to the 15 allowed.
- src/KnowledgeHub.Server/Mcp/ToolProviders/KnowledgeToolsProvider.cs:560 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 17 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Chunking/ConfigTextChunker.cs:25 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 17 to the 15 allowed.
- src/KnowledgeHub.Server/Eval/EvalCase.cs:28 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionBlockRenderer.cs:26 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 20 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionBlockRenderer.cs:192 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 27 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:273 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 24 to the 15 allowed.
- src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:82 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 21 to the 15 allowed.
- src/KnowledgeHub.Client/Pages/Playground.razor:550 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 23 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:846 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 23 to the 15 allowed.
- src/KnowledgeHub.Server/Api/FrameworkAssetsEndpoints.cs:21 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed.
- src/KnowledgeHub.Shared/Tooling/ToolArgumentBuilder.cs:70 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 25 to the 15 allowed.
- src/KnowledgeHub.Client/Pages/Playground.razor:622 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 25 to the 15 allowed.
- src/KnowledgeHub.Server/Services/AnswerService.cs:141 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 19 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/DocumentFileConnector.cs:40 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 17 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/HtmlTextExtractor.cs:91 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 17 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/Connectors/WebPageConnector.cs:21 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 36 to the 15 allowed.
- src/KnowledgeHub.Server/Ingestion/MarkdownChunker.cs:15 [CRITICAL] Refactor this method to reduce its Cognitive Complexity from 30 to the 15 allowed.

### RF-02 — Nested ternaries (S3358) (31 issues)

- src/KnowledgeHub.Server/Services/SearchService.cs:356 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Ingestion/Connectors/GitRepositoryConnector.cs:257 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Client/Pages/Settings.razor:1261 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Settings/EmbeddingSettingsService.cs:106 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Settings/EmbeddingSettingsService.cs:107 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Caching/L1L2Cache.cs:72 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Search/RetrievalGrading.cs:108 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Search/RrfFuser.cs:56 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Search/RrfFuser.cs:59 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Search/RrfFuser.cs:62 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Services/SearchService.cs:162 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Ingestion/Chunking/CodeTextChunker.cs:137 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Ingestion/Chunking/ConfigTextChunker.cs:35 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/RateLimiting/McpToolRateLimiter.cs:55 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Client/Pages/Settings.razor:708 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Client/Pages/Settings.razor:708 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Api/SettingsEndpoints.cs:522 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Api/SettingsEndpoints.cs:523 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Mcp/Upstream/Context7UpstreamClient.cs:38 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Settings/ChatSettingsService.cs:113 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Settings/ChatSettingsService.cs:114 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Mcp/Upstream/TavilyUpstreamClient.cs:38 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Api/SettingsEndpoints.cs:475 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Api/SettingsEndpoints.cs:476 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Mcp/Upstream/DeepWikiUpstreamClient.cs:36 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Mcp/Upstream/FirecrawlUpstreamClient.cs:36 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Shared/Tooling/ToolArgumentBuilder.cs:362 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Client/Pages/Chat.razor:69 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Services/AgentService.cs:425 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Services/AgentService.cs:819 [MAJOR] Extract this nested ternary operation into an independent statement.
- src/KnowledgeHub.Server/Services/AgentService.cs:820 [MAJOR] Extract this nested ternary operation into an independent statement.

### RF-03 — Literais repetidos / URIs (S1192, S1075) (32 issues)

- src/KnowledgeHub.Server/Security/EgressPolicyHandler.cs:52 [MINOR] Define a constant instead of using this literal 'https' 4 times.
- src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:21 [MINOR] Define a constant instead of using this literal 'postgres' 4 times.
- src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:55 [MINOR] Define a constant instead of using this literal 'Provider' 6 times.
- src/KnowledgeHub.Server/Program.cs:142 [MINOR] Define a constant instead of using this literal 'general' 14 times.
- src/KnowledgeHub.Server/Ingestion/IngestionWorker.cs:63 [MINOR] Define a constant instead of using this literal 'failed' 9 times.
- src/KnowledgeHub.Server/VectorStore/PostgresVectorStore.cs:35 [MINOR] Define a constant instead of using this literal 'vector' 6 times.
- src/KnowledgeHub.Server/VectorStore/PostgresVectorStore.cs:289 [MINOR] Define a constant instead of using this literal 'halfvec' 6 times.
- src/KnowledgeHub.Server/Program.cs:58 [MINOR] Define a constant instead of using this literal 'ready' 6 times.
- src/KnowledgeHub.Server/Ingestion/IngestionWorker.cs:59 [MINOR] Define a constant instead of using this literal 'running' 5 times.
- src/KnowledgeHub.Server/Api/SourcesEndpoints.cs:26 [MINOR] Define a constant instead of using this literal 'Source not found' 4 times.
- src/KnowledgeHub.Server/Auth/ApiKeyEndpoints.cs:126 [MINOR] Define a constant instead of using this literal 'api key não encontrada' 7 times.
- src/KnowledgeHub.McpEngine/Activity/McpActivityFilters.cs:32 [MINOR] Define a constant instead of using this literal 'tools/call' 4 times.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:55 [MINOR] Define a constant instead of using this literal 'failed' 7 times.
- src/KnowledgeHub.Server/Services/AgentService.cs:572 [MINOR] Define a constant instead of using this literal 'agent' 4 times.
- src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:92 [MINOR] Define a constant instead of using this literal 'Endpoint' 7 times.
- src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:159 [MINOR] Define a constant instead of using this literal 'Enabled' 4 times.
- src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:159 [MINOR] Define a constant instead of using this literal 'false' 4 times.
- src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:168 [MINOR] Define a constant instead of using this literal 'TimeoutSeconds' 5 times.
- src/KnowledgeHub.Server/Configuration/ConfigurationValidator.cs:171 [MINOR] Define a constant instead of using this literal 'ToolsCacheSeconds' 4 times.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionBlockRenderer.cs:86 [MINOR] Define a constant instead of using this literal 'title' 5 times.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:42 [MINOR] Refactor your code not to use hardcoded absolute paths or URIs.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:46 [MINOR] Remove this hardcoded path-delimiter.
- src/KnowledgeHub.Server/VectorStore/SqliteVecVectorStore.cs:54 [MINOR] Define a constant instead of using this literal '$model' 4 times.
- src/KnowledgeHub.Client/Pages/Login.razor:174 [MINOR] Remove this hardcoded path-delimiter.
- src/KnowledgeHub.Shared/Tooling/ToolArgumentBuilder.cs:277 [MINOR] Define a constant instead of using this literal 'array' 4 times.
- src/KnowledgeHub.Shared/Tooling/ToolArgumentBuilder.cs:280 [MINOR] Define a constant instead of using this literal 'string' 4 times.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:293 [MINOR] Define a constant instead of using this literal 'completed' 5 times.
- src/KnowledgeHub.Server/Chat/OllamaChatClient.cs:19 [MINOR] Remove this hardcoded path-delimiter.
- src/KnowledgeHub.Server/Chat/OpenAiChatClient.cs:22 [MINOR] Remove this hardcoded path-delimiter.
- src/KnowledgeHub.Server/Mcp/KnowledgeResourceProvider.cs:18 [MINOR] Refactor your code not to use hardcoded absolute paths or URIs.
- src/KnowledgeHub.Server/Embeddings/OllamaEmbeddingProvider.cs:21 [MINOR] Remove this hardcoded path-delimiter.
- src/KnowledgeHub.Server/Embeddings/OpenAiEmbeddingProvider.cs:25 [MINOR] Remove this hardcoded path-delimiter.

### RF-04 — Higiene de logging (S6667, S2139, S1172) (24 issues)

- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:91 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Program.cs:456 [MAJOR] Either log this exception and handle it, or rethrow it with some contextual information.
- src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs:84 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Caching/SafeCache.cs:105 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Caching/ToolCacheService.cs:64 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Caching/ToolCacheService.cs:97 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Eval/EvalScheduleService.cs:124 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:661 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:693 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Services/SearchService.cs:477 [MAJOR] Remove this unused method parameter 'breakdowns'.
- src/KnowledgeHub.Server/Mcp/Upstream/Context7UpstreamClient.cs:63 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:180 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:202 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:248 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Mcp/Upstream/McpProxySession.cs:107 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Caching/SafeCache.cs:25 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Caching/SafeCache.cs:42 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Caching/SafeCache.cs:73 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Caching/SafeCache.cs:90 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Mcp/Upstream/TavilyUpstreamClient.cs:63 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Mcp/Upstream/FirecrawlUpstreamClient.cs:61 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:716 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Mcp/Upstream/DeepWikiUpstreamClient.cs:66 [MINOR] Logging in a catch clause should pass the caught exception as a parameter.
- src/KnowledgeHub.Server/Ingestion/MarkdownChunker.cs:80 [MAJOR] Remove this unused method parameter '_'.

### RF-05 — Simplificações LINQ/diversas (30 issues)

- src/KnowledgeHub.Server/Embeddings/EmbeddingProviderResolver.cs:67 [MAJOR] This line will not be executed conditionally; only the first line of this 1-line block will be. The rest will execute un
- src/KnowledgeHub.Server/Search/LexicalSearchService.cs:115 [MINOR] Extract this nested code block into a separate method.
- src/KnowledgeHub.Server/Ingestion/Connectors/Cloud/CloudConnectorBase.cs:137 [MINOR] Loops should be simplified using the "Where" LINQ method
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionConnector.cs:30 [MINOR] All 'FetchAsync' method overloads should be adjacent.
- src/KnowledgeHub.Client/Pages/McpMonitor.razor:265 [CRITICAL] Refactor 'AvgMs' into a method, properties should not copy collections.
- src/KnowledgeHub.Client/Pages/McpMonitor.razor:268 [CRITICAL] Refactor 'TopTools' into a method, properties should not copy collections.
- src/KnowledgeHub.Server/Ingestion/Connectors/Cloud/GoogleDriveGateway.cs:87 [MAJOR] Merge this if statement with the enclosing one.
- src/KnowledgeHub.Server/Ingestion/Connectors/GoogleDriveApiClient.cs:146 [MINOR] Loop should be simplified by calling Select(m => m.Groups)
- src/KnowledgeHub.Server/Ingestion/Connectors/GoogleDriveApiClient.cs:195 [MAJOR] Use a format provider when parsing date and time.
- src/KnowledgeHub.Server/Ingestion/IngestionWorker.cs:128 [MAJOR] Resource 'flushTimer = new PeriodicTimer(TimeSpan.FromSeconds(flushEverySeconds))' has already been disposed explicitly 
- src/KnowledgeHub.Server/Search/MmrSelector.cs:63 [MINOR] Loop should be simplified by calling Select(s => s.Vector)
- src/KnowledgeHub.Server/VectorStore/PostgresVectorStore.cs:306 [MINOR] Remove this method and declare a constant for this value.
- src/KnowledgeHub.Server/Auth/ApiKeyEndpoints.cs:263 [MINOR] Loops should be simplified using the "Any" LINQ method
- src/KnowledgeHub.Server/Auth/ApiKeyEndpoints.cs:266 [MINOR] Loops should be simplified using the "Any" LINQ method
- src/KnowledgeHub.Server/Graph/SqliteKnowledgeGraphStore.cs:78 [MINOR] Loop should be simplified by calling Select(sibling => sibling.Id)
- src/KnowledgeHub.Server/Graph/SqliteKnowledgeGraphStore.cs:135 [MAJOR] Use 'Guid.NewGuid()' or 'Guid.Empty' or add arguments to this GUID instantiation.
- tests/KnowledgeHub.Tests.Unit/Telemetry/TelemetryTests.cs:126 [CRITICAL] Remove or correct this assertion.
- tests/KnowledgeHub.Tests.Unit/Telemetry/TelemetryTests.cs:127 [CRITICAL] Remove or correct this assertion.
- src/KnowledgeHub.Server/Search/Reranking.cs:79 [MINOR] Loop should be simplified by calling Select(m => m.Groups)
- src/KnowledgeHub.Server/Ingestion/Chunking/CodeTextChunker.cs:196 [MINOR] Loops should be simplified using the "Where" LINQ method
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionApiClient.cs:146 [CRITICAL] This loop's stop incrementer updates 'attempt' but the stop condition doesn't test any variables.
- src/KnowledgeHub.Server/Ingestion/Connectors/NotionBlockRenderer.cs:158 [MINOR] Loop should be simplified by calling Select(prop => prop.Value)
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:885 [MAJOR] Change this condition so that it does not always evaluate to 'False'.
- src/KnowledgeHub.Client/wwwroot/js/boot.js:47 [MINOR] Expected a `for-of` loop instead of a `for` loop with this simple iteration.
- src/KnowledgeHub.Client/wwwroot/js/boot.js:59 [MINOR] Expected a `for-of` loop instead of a `for` loop with this simple iteration.
- src/KnowledgeHub.Server/Api/StreamingEndpoints.cs:161 [MAJOR] Remove this useless assignment to local variable 'seq'.
- src/KnowledgeHub.Server/Chat/OllamaChatClient.cs:80 [MINOR] Loop should be simplified by calling Select(call => call.Function)
- src/KnowledgeHub.Server/Ingestion/Connectors/DocumentFileConnector.cs:121 [MINOR] Loop should be simplified by calling Select(page => page.Text)
- src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs:490 [MINOR] Loops should be simplified using the "Where" LINQ method
- src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs:633 [MINOR] Loops should be simplified using the "Where" LINQ method

### RF-06 — Assinaturas com muitos parâmetros (S107) (11 issues)

- src/KnowledgeHub.Server/Ingestion/Connectors/GitRepositoryConnector.cs:160 [MAJOR] Method has 10 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Ingestion/Connectors/RestApiConnector.cs:69 [MAJOR] Method has 8 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Services/SearchService.cs:106 [MAJOR] Method has 10 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs:14 [MAJOR] Constructor has 8 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Services/SearchService.cs:22 [MAJOR] Constructor has 14 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:22 [MAJOR] Constructor has 10 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Caching/SafeCache.cs:123 [MAJOR] Method has 8 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Eval/EvalRunner.cs:30 [MAJOR] Method has 9 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Ingestion/Chunking/ChunkerSelector.cs:71 [MAJOR] Method has 10 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:335 [MAJOR] Method has 8 parameters, which is greater than the 7 authorized.
- src/KnowledgeHub.Server/Api/SearchEndpoints.cs:17 [MAJOR] Lambda has 16 parameters, which is greater than the 7 authorized.

### RF-07 — CallerInfo explícito (S3236 — avaliar FP) (8 issues)

- src/KnowledgeHub.Server/Chat/OllamaChatClient.cs:17 [MINOR] Remove this argument from the method call; it hides the caller information.
- src/KnowledgeHub.Server/Chat/OllamaChatClient.cs:18 [MINOR] Remove this argument from the method call; it hides the caller information.
- src/KnowledgeHub.Server/Chat/OpenAiChatClient.cs:20 [MINOR] Remove this argument from the method call; it hides the caller information.
- src/KnowledgeHub.Server/Chat/OpenAiChatClient.cs:21 [MINOR] Remove this argument from the method call; it hides the caller information.
- src/KnowledgeHub.Server/Embeddings/OllamaEmbeddingProvider.cs:18 [MINOR] Remove this argument from the method call; it hides the caller information.
- src/KnowledgeHub.Server/Embeddings/OllamaEmbeddingProvider.cs:19 [MINOR] Remove this argument from the method call; it hides the caller information.
- src/KnowledgeHub.Server/Embeddings/OpenAiEmbeddingProvider.cs:22 [MINOR] Remove this argument from the method call; it hides the caller information.
- src/KnowledgeHub.Server/Embeddings/OpenAiEmbeddingProvider.cs:23 [MINOR] Remove this argument from the method call; it hides the caller information.

### RF-08 — Shell/JS (S7688, S3220, S7758, S7924, S7765) (10 issues)

- scripts/eval-gate.sh:31 [MAJOR] Use '[[' instead of '[' for conditional tests. The '[[' construct is safer and more feature-rich.
- scripts/eval-gate.sh:40 [MAJOR] Use '[[' instead of '[' for conditional tests. The '[[' construct is safer and more feature-rich.
- scripts/eval-gate.sh:50 [MAJOR] Use '[[' instead of '[' for conditional tests. The '[[' construct is safer and more feature-rich.
- src/KnowledgeHub.Client/Layout/NavMenu.razor.css:38 [MAJOR] Text does not meet the minimal contrast requirement with its background.
- src/KnowledgeHub.Client/Layout/NavMenu.razor.css:79 [MAJOR] Text does not meet the minimal contrast requirement with its background.
- src/KnowledgeHub.Client/wwwroot/js/boot.js:33 [MINOR] Prefer `String#codePointAt()` over `String#charCodeAt()`.
- src/KnowledgeHub.Client/wwwroot/js/boot.js:60 [MINOR] Prefer `String.fromCodePoint()` over `String.fromCharCode()`.
- src/KnowledgeHub.Client/wwwroot/js/boot.js:121 [MINOR] Use `.includes()`, rather than `.indexOf()`, when checking for existence.
- src/KnowledgeHub.Server/Ingestion/Connectors/DocumentFileConnector.cs:148 [MINOR] Review this call, which partially matches an overload without 'params'. The partial match is 'string[] string.Split(char
- src/KnowledgeHub.Server/Ingestion/IngestionService.cs:1056 [MINOR] Review this call, which partially matches an overload without 'params'. The partial match is 'string[] string.Split(char

### RF-09 — Blocos vazios (S108) (5 issues)

- src/KnowledgeHub.Server/Caching/ICacheInvalidationBus.cs:27 [MAJOR] Either remove or fill this block of code.
- src/KnowledgeHub.Server/Caching/ICacheInvalidationBus.cs:27 [MAJOR] Either remove or fill this block of code.
- src/KnowledgeHub.Server/Ingestion/IngestionWorker.cs:245 [MAJOR] Either remove or fill this block of code.
- src/KnowledgeHub.Server/Api/StreamingEndpoints.cs:138 [MAJOR] Either remove or fill this block of code.
- src/KnowledgeHub.Server/Api/StreamingEndpoints.cs:168 [MAJOR] Either remove or fill this block of code.


### RF-10 — Miscelânea (6 issues)

- src/KnowledgeHub.Server/Api/StreamingEndpoints.cs:168 [MINOR] S2486 — catch vazio justificado (conexão já encerrada); adicionar log Debug.
- src/KnowledgeHub.Server/Chat/HttpChatClient.cs:10 [MAJOR] S3881 — implementar padrão IDisposable corretamente.
- src/KnowledgeHub.Server/Chat/HttpChatClient.cs:32 [MINOR] S2737 — catch que re-throws sem tratamento.
- src/KnowledgeHub.Server/Ingestion/Connectors/DocumentFileConnector.cs:20 [MINOR] S2386 — resolvido nesta branch (SupportedExtensions → ImmutableHashSet); fecha no próximo scan.
- install.sh:289 [INFO] S5332 — `ASPNETCORE_URLS=http://+` em mensagem informativa de instalação local; candidato a won't-fix (bind local é escolha do operador).
- src/KnowledgeHub.Server/Program.cs:468 [MINOR] S1118 — método excede limite de variáveis locais.

## Notas de execução

- RF-01: dividir em sub-issues por arquivo/método; refatorar extraindo helpers
  (sem mudança de comportamento). Playground.razor :459 já resolvido no PR.
- RF-07 (S3236): os argumentos explícitos em `ThrowIfNullOrWhiteSpace` carregam a
  chave de configuração real (`"Chat:Endpoint"`) — remover piora a mensagem de erro.
  Candidato a won't-fix salvo se o mantenedor preferir mensagens CallerArgumentExpression.
- RF-03/S1075: URIs default (ollama/openai localhost, notion api) são defaults
  documentados; extrair para consts nomeadas atende à regra sem mudar comportamento.

## Critérios de aceite

- Build `dotnet build KnowledgeHub.slnx` + `dotnet test` verdes.
- Re-scan SonarCloud mostra ~0 issues remanescentes (ou só wont-fix justificadas).
