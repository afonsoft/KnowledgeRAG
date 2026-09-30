# vectorize-io/hindsight — padrões benchmarkados (análise 2026-10-01)

Clone de referência: `/tmp/hindsight-analysis` (shallow). Stack: Python/FastAPI + Postgres/pgvector; monorepo com SDKs gerados, control-plane Next.js, CLI Rust.

## Conceitos centrais

- **3 operações**: `retain` (ingestão async → fact extraction LLM → entidades/links), `recall` (4 braços paralelos), `reflect` (loop agêntico com retrieval hierárquico: mental models → observations → fatos brutos).
- **Tipos de memória**: world facts, experience facts, **observations** (consolidadas por LLM), **mental models** (páginas de conhecimento sintetizadas, auto-refresh full/delta), **directives** (diretrizes comportamentais injetadas no reflect).
- **Banks**: stores isolados por usuário/agente com **dispositions** (skepticism/literalism/empathy 1–5) que moldam os prompts do reflect; missão do bank configurável.
- **Async operations**: toda operação longa retorna operation id consultável/cancelável (`list_operations`/`get_operation`/`cancel_operation`) — polling determinístico.

## Recall (o que vale copiar)

- 4 braços paralelos: semantic (vetorial), BM25, graph (link expansion), temporal — fusão RRF (k=60) → rerank cross-encoder → orçamento de tokens.
- **Per-arm caps antes da fusão** (`cap_per_source`) — um braço explosivo não domina o pool do reranker.
- **`min_scores` por estágio** (`{semantic, keyword, reranker, final}`) — piso `final` faz o recall abster (não retorna lixo).
- **`budget` low/mid/high** — controla profundidade (pool, expansion, retries) por chamada; custo sob controle do chamador.
- **`max_tokens` de resposta** — trunca por orçamento de tokens, não por contagem.
- **`temporal_window` explícito** `{start,end}` — boost (não filtro) sem depender de parsing da query; `query_timestamp` ancora expressões relativas.
- **`tags`/`tag_groups`** — filtros booleanos compostos (`and`/`or`/`not`) com resolução fuzzy por trigram.
- **`prefer_observations`** — dedup: observação consolidada supersede fatos brutos de onde veio.
- Arm scores por resultado preservados no output (rastreabilidade por braço).

## MCP server

- 38 tools; endpoint **por bank** (`/mcp/{bank_id}/`); allowlist de tools por config; **tolerante a args extras de LLMs** (strip de campos desconhecidos + coerção de JSON stringificado — `_make_tools_tolerant`).
- `annotations` por tool (`_tool_annotations`); auditoria de runs por tool.

## Testes (padrão notável)

- **LLM-as-judge** para comportamento não-determinístico (classificação, atribuição): `hs_llm_core` roda provider real + judge independente (Gemini default); asserts estruturais determinísticos ficam em unit rápido.
- **Blackbox system tests** dirigem o processo real via SDK publicado — pegam bugs "na costura entre passos" que a suíte interna não pega.

## O que NÃO tem

- **A2A** — nenhum suporte agent-to-agent (nosso diferencial).
- Multi-tenancy por schema Postgres (nós usamos por-key CallerScope — equivalente funcional no nosso contexto).

## Aplicado no KnowledgeHub

- `.specs/SPEC-20261001-mcp-recall-ergonomics.md` — budget/maxTokens/minScores/temporalWindow/annotations (RF-001..005).
- `.specs/SPEC-20261001-a2a-task-durability.md` — task store durável, progresso incremental, push webhook HMAC, harness attribution (RF-001..005).
- Não copiado (com razão): banks por usuário (temos per-key), mental models como produto (grande lift; nosso knowledge graph + write_knowledge cobre parcialmente — candidato futuro a "Knowledge Digests").
