# Short-term memory — session state (overwritten each session, ≤100 lines)

- **Sessão**: implementando Epic E29 (#609) — 8 SPECs aprovadas do hindsight analysis.
- **Branch**: `feature/devin-20261111-agent-memory-e29` (nunca commitar em main).
- **Feito + commitado**:
  - #616 agent-rules-hardening DONE → commit e133352 — LlmJudgeAssert, blackbox suite (`tests/KnowledgeHub.Tests.Blackbox`), MigrationParityTests, EnvExampleTests, CLAUDE.md convenções. Issue→in_review.
  - #614 mcp-ergonomics-wave2 RF-001/002/003/005/006 → commit 40f1bb2 — ToolArgs JSON coercion, OperationsToolsProvider (operations_*, document_*, source_rescan), Mcp:Instructions, catalog lint. **RF-004 (bank scope) pendente → encaixar após #610**. Issue→in_progress.
  - SPECs+analysis commitadas em f717d4.
- **Em curso**: #610 agent-memory-retain — IMPLEMENTADO (não commitado): 7 entidades Memory* + MemoryOperation (async), migrations duais AgentMemory (SQLite+Postgres), Memory/{Retain,Recall,Bank,Unit}Service + EntityLinker + ConfigResolver + IMemoryDefenseScreen(NoOp) + RetainQueue/Worker + BankContext middleware (RF-004 #614: `/mcp/banks/{key}/` + X-Memory-Bank + arg). MemoryToolsProvider (10 tools) + MemoryEndpoints REST `/api/memory/*` + operations_* cobrem MemoryOperation. 22/22 testes unit memory verdes.
- **Design decisions**: ApiKeyId=Guid.Empty sentinel para cookie sessions (unique index); MemoryUnitEntity join (UnitId,EntityId,BankId) para graph arm; soft-delete IsDeleted; check constraints FactType/LinkType; `List<string>` Tags = JSON primitive collection (mesmo padrão KgNode.Labels).
- **Pendente T2-T5**: MemoryRetainService (extract via IChatClient + verbatim, delta-retain por ContentHash/ChunkIndex), MemoryRecallService (4 braços+RRF via Search/*), MemoryToolsProvider (11 tools), MemoryEndpoints, tests. Reusar: EntityResolver.Normalize/NormalizeType, IIngestionQueue padrão (ou fila dedicada), ICallerScopeProvider p/ ApiKeyId, embedding via IEmbeddingProvider.
- **Ordem restante**: #611 consolidation → #612 reflect → #613 mental-models → #615 defense → #617 skill; RF-004 do #614 (bank scope header/path) depois do #610.
- **Bugs conhecidos**: xunit 2.9.3 não honra SkipException no console → judge usa early-return; `CatalogTool.Description` agora set-able; McpServerOptions.ServerInstructions existe no SDK 2.2.
- **Lock files** (packages.lock.json em src/tests) sujos por restore local — NÃO commitar (ruído de ambiente).
