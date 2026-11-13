# SPEC-20261114-bench-v2-journeys

## Metadata

| Campo | Valor |
|---|---|
| Status | `Done` |
| Ticket | #633 — Epic #630 |
| Epic | E32 — agent-hardening wave 4 (gentle-ai parity) |
| Date | 2026-11-14 |

## Contexto

O journey bench (E31) cobre só a superfície de memória. A superfície de conhecimento/RAG — a mais antiga do produto — não tem jornada medida, e `memory_bank_merge` (E31) também não. Extender as journeys existentes garante que fricção na superfície de conhecimento e o fluxo merge→redirect virem regressões detectáveis.

Determinismo: todas as novas jornadas rodam sem LLM e sem dados pré-semeados — cada assert verifica *estrutura* da resposta (não-`isError`, shape do payload), nunca conteúdo que dependa de índice ou LLM. `agent_chat` fica fora: exige LLM configurado, violaria o modo determinístico.

## Requisitos

### RF-001 — Journey `knowledge-search`
Fluxo: `memory_bank_create` (bank próprio da journey) → `search_knowledge {query}` → `ask_knowledge {question}` → `search_knowledge {query, topK:1}`. ≤4 calls; asserts `isError=false` + presença dos campos estruturais (`results`/`context` conforme shape real — verificar antes de escrever o assert). Sem vault nem docs: respostas vazias válidas desde que `isError=false`.

### RF-002 — Journey `bank-merge-flow`
Fluxo: `memory_bank_create` a → `memory_bank_create` b → `memory_retain` a (verbatim, conteúdo único) → `memory_bank_merge {sourceKey:b, targetKey:a}` (b vazio → merge trivial) → `memory_recall` **via bankKey b** provando redirect do tombstone → `memory_bank_merge` repetido provando `alreadyMerged:true` → `memory_bank_list` (b ausente). ≤7 calls — cobre E31 end-to-end pela superfície pública.

### RF-003 — Journey `session-lifecycle` (depende da SPEC session-lifecycle do mesmo epic)
Fluxo: `memory_session_begin` → `memory_retain` com `sessionId` da etapa 1 (arg factory — prova a costura sessionId→retain) → `memory_context` (seção Sessions presente) → `memory_session_end` com summary → `memory_context` (lastSession com summary) → `memory_session_list`. ≤6 calls. Se a SPEC de session lifecycle não estiver mergeada nesta branch, a journey fica fora desta SPEC e entra em follow-up — marcar explicitamente.

### RF-004 — baseline.json atualizado
Novas entradas `knowledge-search`, `bank-merge-flow`, `session-lifecycle` com `maxCalls` exato (4/7/6), `budgetMs` generoso, `warnMs` ~metade do budget. Gerados via `BENCH_UPDATE_BASELINE=1` observando o valor real e então editando para o valor exato esperado (call count é estrutural — não "o que observou", "o que deve ser").

## Fora de escopo

- `agent_chat` (LLM-dependent) — documentado como exclusão consciente.
- Token metrics (env-gated, já previsto na SPEC do bench v1).
- Journeys de conectores/ingestão (precisariam de fixtures de vault — follow-up).

## Acceptance Criteria

- [ ] `dotnet test tests/KnowledgeHub.Tests.Bench` roda 7 journeys verdes vs baseline.
- [ ] `bank-merge-flow` falharia se o redirect quebrasse (o recall via source key é o canário — coberto pelo assert `isError=false` + results).
- [ ] `session-lifecycle` prova a costura `sessionId` begin→retain→context.
- [ ] Baseline entries com `maxCalls` = número exato de steps da journey.

## Arquivos

- `tests/KnowledgeHub.Tests.Bench/Journeys/MemoryJourneys.cs` (novas journeys; opcional `KnowledgeJourneys.cs` novo se o arquivo crescer)
- `tests/eval/journeys/baseline.json`
- `docs/en/TESTING.md` (tabela de journeys atualizada)

## Riscos

- `ask_knowledge`/`search_knowledge` sem LLM e sem dados → verificar `isError=false` antes de fixar o assert (rodar uma vez e inspecionar).
- `memory_bank_list` sem `includeMerged` → b (tombstone) ausente é o assert correto; se list retornar b marcado merged=true, ajustar o assert para `merged==false` ausente.

## Task Plan

- [ ] T1 — sondar shapes de `search_knowledge`/`ask_knowledge`/`memory_bank_list` (isError + campos) num host in-process.
- [ ] T2 — implementar journeys + asserts reais.
- [ ] T3 — baseline update + docs.
