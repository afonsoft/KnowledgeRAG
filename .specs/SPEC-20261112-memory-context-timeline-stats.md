# SPEC-20261112-memory-context-timeline-stats

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `memory-context-timeline-stats` |
| Type | `Feature` |
| Stack | `.NET 10`, EF Core, MCP/REST |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/devin-20261112-agent-memory-e30` |
| Status | `In implementation` |
| Ticket | #620 — Epic #619 |
| Source | Benchmark analysis: `Gentleman-Programming/gentle-ai` (`engram` — `mem_context`, `mem_timeline`, `mem_stats`) |
| Depends on | `SPEC-20261111-agent-memory-retain` (#610), `SPEC-20261111-memory-observations-consolidation` (#611), `SPEC-20261111-mental-models-pages` (#613) |

## 1. User Story

**As a** agente de IA que opera um bank de memória com centenas de fatos
**I want** um digest de abertura de sessão, uma view cronológica dos fatos e estatísticas por bank
**So that** eu comece cada sessão grounding no essencial (directives, fatos recentes, models stale, falhas) sem precisar formular uma query — como o `mem_context`/`mem_timeline`/`mem_stats` do Engram.

## 2. Contexto técnico

- **Referência gentle-ai/engram**: `mem_context` é chamado no início de toda sessão e devolve histórico recente para o agente começar grounding; `mem_timeline` expõe observações em ordem cronológica; `mem_stats` dá contagens por projeto. O "automático" do Engram é convenção do agente — o tool em si é uma query determinística.
- **O que já temos**:
  - `memory_list`/`memory_get`/`memory_recall` (flat/query-driven — exigem pergunta);
  - `context_recovery` (snapshots de trabalho por repo — artifact manual, não digest de bank);
  - `StaleIdsAsync` em `MentalModelService` (staleness computado — reusável);
  - `MemoryOperation` (`Kind`, `Status`, `Error`) — falhas recentes consultáveis;
  - `MemoryDirective.Priority`, `MemoryUnit.{FactType,MentionedAt,OccurredStart,EventDate,ProofCount,IsDeleted}`;
  - Resolução efetiva de bank via `/mcp/banks/{key}` + `X-Memory-Bank` + arg (SPEC-20261111-mcp-ergonomics-wave2 RF-004).
- **Sem migration** — tudo composto das tabelas existentes. Zero LLM por default: session-start precisa ser barato e determinístico.

## 3. Requisitos

### RF-001 — `memory_context` (session-start digest)

- **Description:** Tool MCP read-only + REST que monta um digest determinístico do bank: directives por prioridade, fatos recentes, top observations, mental models/pages stale, falhas recentes de operações, mini-stats. `synthesize:true` (opt-in) passa o digest pelo `IChatClient` para um sumário em prosa — default `false` (nunca paga LLM por default).
- **Rules:** ReadOnly + Idempotent; resolução efetiva de bank idêntica ao recall; `topK` default 10 cap 50; `since` opcional (ISO-8601) filtra "recentes"; IsDeleted excluído; `content[0].text` recebe o digest em markdown legível e `structuredContent` o payload completo.
- **Input → Output:**
```text
memory_context(bankKey?, topK?, since?, synthesize?) → {
  bank: { key, name, unitCount },
  directives: [{ name, priority, tags }],
  recentFacts: [{ id, factType, text, occurredStart, mentionedAt }],
  topObservations: [{ id, text, proofCount }],
  staleModels: [{ id, name, path, lastRefreshedAt }],
  recentFailures: [{ kind, error, createdAt }],
  stats: <ver RF-003>
}
```

### RF-002 — `memory_timeline` (view cronológica)

- **Description:** Tool MCP read-only + REST que retorna units do bank agrupadas em buckets temporais (`day|hour|week`), ordenadas por `OccurredStart` quando presente (tempo do fato) com fallback `MentionedAt` (tempo de retenção).
- **Rules:** `from`/`to` ISO-8601 (mesmas formas de `search_graph_temporal` — ranges arbitrários, não a whitelist `1h|6h|24h|7d`); `factType` filtra por um ou mais tipos (mesma sintaxe multi-valor do recall); `limit` cap 200 units; buckets vazios omitidos; IsDeleted excluído.
- **Input → Output:**
```text
memory_timeline(bankKey?, from?, to?, factType?, groupBy=day, limit?) → {
  buckets: [{ bucket: "2026-11-11", count, items: [{ id, factType, textPreview, entities, occurredAt }] }]
}
```

### RF-003 — `memory_stats` (breakdown por bank)

- **Description:** Tool MCP read-only + REST com métricas de saúde do bank — o que um agente precisa para decidir quando consolidar/limpar e um humano precisa para auditar.
- **Rules:** `unitsByFactType` (world/experience/observation), `unitsWithEmbedding` (cobertura do braço semântico), `linksByKind`, `documents`, `directives`, `mentalModels { total, stale }`, `pages` (subtype='page'), `observations { total, avgProofCount, maxProofCount }`, `operationsByStatus`, `lastConsolidatedAt`, `oldestUnconsolidatedFact` (backlog signal), `dedupedRetains` (delta-retain hits quando registráveis). REST global `GET /api/memory/stats` agrega por bank (admin).
- **Input → Output:** `memory_stats(bankKey?) → { ...métricas }` — `bankKey` ausente → agregado de todos os banks do escopo do caller.

### RF-004 — Ergonomia/contrato

- **Description:** As 3 tools entram no catálogo com descriptions + `examples` (lint RF-006 do wave2) e `SchemaExamples` cobre os schemas; pins do `McpContractTests` atualizados (`NewWaveTools`); description do `memory_context` instrui uso no session-start (o "automático" vive na guidance).
- **Rules:** `skills/knowledge-hub/SKILL.md` documenta o padrão "chame `memory_context` ao abrir a sessão"; REST espelha os 3 endpoints sob `/api/memory/banks/{bankKey}/...`; erros seguem o padrão existente (404 bank inexistente, 422 defense).
- **Input → Output:** contrato pinado + documentação de agente.

## 4. Fora de escopo

- Digest LLM default (`synthesize` é opt-in).
- Timeline de `MemoryLink`/history de observations (só units).
- Merge de banks / auto-bank por git remote → SPEC `memory-prompt-provenance` (proveniência) e proposta separada.
- UI/admin dashboard — stats ficam acessíveis via REST; UI pode consumir depois.

## 5. Mudanças técnicas

```text
memory_context(bankKey?, topK?, since?, synthesize?) → digest
memory_timeline(bankKey?, from?, to?, factType?, groupBy?, limit?) → buckets
memory_stats(bankKey?) → métricas
```

REST: `GET /api/memory/banks/{bankKey}/context`, `GET /api/memory/banks/{bankKey}/timeline`, `GET /api/memory/banks/{bankKey}/stats`, `GET /api/memory/stats`.
Erros: `404` bank inexistente; `400` janela/tipo inválido.

## 6. Critérios de aceite

- [ ] **Dado** bank com 2 directives, 5 fatos, 1 observation, 1 model stale, 1 operação falha **quando** `memory_context` roda **então** o digest traz as 5 seções preenchidas, text markdown legível, sem chamada LLM.
- [ ] **Dado** `synthesize:true` **então** o digest vai ao `IChatClient` e retorna prosa — sem provider configurado → `synthesize` ignorado silenciosamente com flag `synthesized:false`.
- [ ] **Dado** fatos com `OccurredStart` em 3 dias distintos **então** `memory_timeline groupBy=day` retorna 3 buckets ordenados; fato sem `OccurredStart` cai no bucket de `MentionedAt`.
- [ ] **Dado** `factType=observation` **então** a timeline só contém observations.
- [ ] **Dado** bank com 3 world + 2 experience + 2 observations (proof 3 e 1) **então** `memory_stats` reporta contagens por tipo, `avgProofCount=2`, `stale` correto em mentalModels.
- [ ] **Edge:** bank vazio → digest com seções vazias (`stats.totalUnits=0`) sem erro; `from>to` → 400.

## 7. Arquivos

**Ler antes:**
- `src/KnowledgeHub.Server/Memory/MentalModels/MentalModelService.cs` (`StaleIdsAsync`)
- `src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs` (padrão de registro)
- `src/KnowledgeHub.Server/Api/MemoryEndpoints.cs`
- `tests/KnowledgeHub.Tests.Integration/McpContractTests.cs` (`NewWaveTools`)
- `docs/engram.md` em `/tmp/gentle-ai` (referência)

**Criar/modificar:**
```text
src/KnowledgeHub.Server/Memory/MemoryContextService.cs   (digest + timeline + stats queries)
src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs (3 tools)
src/KnowledgeHub.Server/Api/MemoryEndpoints.cs (3-4 rotas)
skills/knowledge-hub/SKILL.md (padrão session-start)
tests/KnowledgeHub.Tests.Unit/Memory/MemoryContextTests.cs
tests/KnowledgeHub.Tests.Integration/McpContractTests.cs (pins)
```

## 8. Riscos

- `memory_context` se torna o ponto mais chamado do catálogo (session-start de todo agente) — mitigado por query determinística barata, `synthesize` opt-in e `topK` capado.
- Timeline em banks grandes — mitigado por `limit` e buckets agregados no SQL (GroupBy server-side).
- Stats pesados em banks gigantes — contagens são COUNT/GROUP BY; `oldestUnconsolidatedFact` é MIN indexado por `ConsolidatedAt IS NULL` (índice existente).

## 9. Task Plan + DoD

- [ ] T1: `MemoryContextService` — queries do digest (directives, recentes, observations, stale via `StaleIdsAsync`, failures) + `synthesize` opt-in.
- [ ] T2: timeline — GroupBy server-side por bucket + ordenação OccurredStart→MentionedAt.
- [ ] T3: stats — métricas por bank + agregado global.
- [ ] T4: tools + REST + contract pins + `SKILL.md` session-start pattern.
- [ ] T5: testes unit + integração + blackbox smoke + gates.
- [ ] DoD: RF implementados, AC cobertos, determinístico sem LLM por default, sem migration, contrato pinado.
