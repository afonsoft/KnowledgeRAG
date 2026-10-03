# Plano de melhorias de performance — KnowledgeRAG (2026-10-03)

Auditoria dos hot paths: busca híbrida, ingestão, vector stores, EF Core,
pipeline HTTP/middleware, MCP upstream, agente/conversação e artefatos de
deploy. O codebase já está bem otimizado em vários pontos (cache por região
com index-version, embeddings em batch, KNN com heap bounded no SQLite,
FTS5 com probe cacheado, `AddDbContextPool`+factory, WAL, pgvector HNSW) —
os achados abaixo são os resíduos que restaram.

## Implementadas neste PR

### 1. Auditoria de uso de API key: prune O(N) a cada request — **alto impacto**
`Auth/ApiKeyUsageMiddleware.cs`: cada request autenticado por `aft_*` fazia
`SELECT` de **todos** os eventos da key (até 10 mil linhas) + ordenação em
memória + insert + `SaveChanges`. Em uso intenso de MCP/API isso domina a
latência de cada chamada.

**Fix:** `PruneAsync` só roda quando necessário — `COUNT(*)` indexado por
request + sweep por idade (90d) throttled a 1×/5min por key. O over-cap é
resolvido com `DELETE … ORDER BY Timestamp LIMIT` (a coluna é TEXT ISO-8601
em UTC — ordem lexical = cronológica), sem materializar 10k rows nem no
steady-state; o full-scan de timestamps só roda no sweep amortizado.
Contrato síncrono preservado (testes de integração continuam vendo o
evento na mesma request).

### 2. PRAGMAs de performance do SQLite — **alto impacto, trivial**
`Data/DatabaseMigrator.cs`: hoje só `journal_mode=WAL`. Adicionar no mesmo
bloco:
- `synchronous=NORMAL` — seguro com WAL, elimina fsync por commit;
- `temp_store=MEMORY` — sorts/CTEs de FTS5 e RRF fora do disco;
- `cache_size=-65536` — page cache de 64 MB por conexão;
- `mmap_size=268435456` — leitura via mmap (256 MB) — I/O mais barato;
- `PRAGMA optimize` — atualiza estatísticas do planner no startup.

Afeta o deployment padrão (SQLite) em leitura e escrita.

### 3. `InvalidateToolsCache` bloqueante ×4 — **médio**
`Mcp/Upstream/{Tavily,Firecrawl,DeepWiki,Context7}ToolsProvider.cs`:
`_cacheGate.Wait()` síncrono num singleton chamado por settings — bloqueia
thread pool e pode deadlockar se o fetch estiver lento. Troca para snapshot
atômico (`Interlocked.Exchange` de um único objeto `{tools, expiresAt}`) —
leitura lock-free e invalidação sem bloqueio.

### 4. `SqliteVectorStore.DeleteByDocumentAsync` — **médio**
Carrega todos os chunks do doc no change tracker e atualiza um a um.
Mesma operação que `DeleteBySourceAsync` já faz via `ExecuteUpdateAsync`
(um SQL). Um roundtrip a menos por chunk × doc.

### 5. `EmbeddingProviderResolver` — signature recomputada por chamada — **médio**
`Current`/`Acquire`/`Fingerprint` recomputam `string.Join` + SHA-256 do
ApiKey a cada call — isto é, por **cada** `Embed*` durante ingestão e por
cada search. Memoizar `(optionsRef → signature/fingerprint)` — os options
já vêm de um snapshot imutável cacheado, então `ReferenceEquals` basta.

### 6. Janela de summarização de threads cresce sem bound — **médio**
`AgentService.BuildContextWindow`/`ScheduleSummarization`: os `dropped`
(todos os messages fora da janela) vão inteiros como transcript do
summarizer a cada turno → custo de tokens O(histórico) por turno, i.e.
O(N²) na vida da thread — e cresce além do contexto do modelo. Capar o
transcript às últimas `Agent:SummarizationMaxMessages` (default 100)
mensagens — o `thread.Summary` anterior já cobre o resto.

### 7. Cleanup trivial
`Health/KnowledgeHubHealthChecks.cs`: `Task.FromResult(r).Result` dentro de
catch — retornar o resultado direto.

## Roadmap (documentado, não implementado neste PR)

| Item | Impacto | Por que não agora |
|---|---|---|
| `RunAOTCompilation` no Client WASM | Alto (CPU-bound do SPA) | exige `wasm-tools`, build CI ~2-3× mais lento — avaliar como opt-in Release |
| `Include(t => t.Messages)` carrega thread inteira | Médio | exige cuidado com mensagens antigas nunca sumarizadas; combinar com (6) num próximo passo (carregar só últimas N) |
| STJ source-gen (`JsonSerializerContext`) p/ payloads de cache/MCP | Médio | espalha pelo codebase; ganho real é startup+alloc — medir antes |
| Response compression p/ JSON de API | Baixo | payloads pequenos; assets estáticos já são pré-comprimidos pelo `MapStaticAssets` |
| `UpsertBatchAsync` do sqlite-vec: 1 prepared command/batch | Baixo | já é transação única; ganho marginal |
| Sampling/batching do audit em background (Channel + worker) | Médio | alternativa mais agressiva ao item 1 — avaliar se volume de audit crescer |
