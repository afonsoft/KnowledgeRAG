# Short-term memory — session state (overwritten each session, ≤100 lines)

- **Sessão**: implementando Epic E29 (#609) — 8 SPECs aprovadas do hindsight analysis.
- **Branch**: `feature/devin-20261111-agent-memory-e29` (nunca commitar em main).
- **Feito + commitado**:
  - #616 agent-rules-hardening DONE → commit e133352 — LlmJudgeAssert, blackbox suite (`tests/KnowledgeHub.Tests.Blackbox`), MigrationParityTests, EnvExampleTests, CLAUDE.md convenções. Issue→in_review.
  - #614 mcp-ergonomics-wave2 RF-001/002/003/005/006 → commit 40f1bb2 — ToolArgs JSON coercion, OperationsToolsProvider (operations_*, document_*, source_rescan), Mcp:Instructions, catalog lint. **RF-004 (bank scope) pendente → encaixar após #610**. Issue→in_progress.
  - SPECs+analysis commitadas em f717d4.
- **Em curso**: #610 agent-memory-retain COMMITADO `dad0957` — entidades, services, MCP tools, REST, worker async, bank scoping; 22/22 testes memory verdes; RF-004 do #614 entregue junto (bank scoping).
- **Agora**: #611 memory-observations-consolidation — schema FEITO (não commitado): MemoryUnit += SourceMemoryIdsJson/ProofCount; ObservationHistory entity + DbSet + config. **Próximo**: migration `MemoryConsolidation` (dual), MemoryConsolidationService (batch LLM decide create|update|delete|skip, evidence quotes, refine-not-overwrite, ObservationHistory append, dedup adjudicado ≥0.92, triage opt-in), MemoryConsolidationWorker (interval 15min + trigger por bank, um run/bank), preferObservations no recall, tools memory_consolidate+memory_observation_history + REST `/api/memory/banks/{key}/consolidate` + `/observations/{id}/history`, IMemoryConsolidationHook stub.
- **Design decisions**: ApiKeyId=Guid.Empty sentinel para cookie sessions (unique index); MemoryUnitEntity join (UnitId,EntityId,BankId) para graph arm; soft-delete IsDeleted; check constraints FactType/LinkType; `List<string>` Tags = JSON primitive collection (mesmo padrão KgNode.Labels).
- **Pendente T2-T5**: MemoryRetainService (extract via IChatClient + verbatim, delta-retain por ContentHash/ChunkIndex), MemoryRecallService (4 braços+RRF via Search/*), MemoryToolsProvider (11 tools), MemoryEndpoints, tests. Reusar: EntityResolver.Normalize/NormalizeType, IIngestionQueue padrão (ou fila dedicada), ICallerScopeProvider p/ ApiKeyId, embedding via IEmbeddingProvider.
- **Ordem restante**: #611 consolidation → #612 reflect → #613 mental-models → #615 defense → #617 skill; RF-004 do #614 (bank scope header/path) depois do #610.
- **Bugs conhecidos**: xunit 2.9.3 não honra SkipException no console → judge usa early-return; `CatalogTool.Description` agora set-able; McpServerOptions.ServerInstructions existe no SDK 2.2.
- **Lock files** (packages.lock.json em src/tests) sujos por restore local — NÃO commitar (ruído de ambiente).
