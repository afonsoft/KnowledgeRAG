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

- `.specs/SPEC-20261001-mcp-recall-ergonomics.md` — budget/maxTokens/minScores/temporalWindow/annotations (RF-001..005). **Done (#441)**.
- `.specs/SPEC-20261001-a2a-task-durability.md` — task store durável, progresso incremental, push webhook HMAC, harness attribution (RF-001..005).
- Não copiado (com razão): banks por usuário (temos per-key), mental models como produto (grande lift; nosso knowledge graph + write_knowledge cobre parcialmente — candidato futuro a "Knowledge Digests").

---

## Segunda onda — análise profunda 2026-11-11 (clone `/tmp/hindsight`)

Foco pedido: regras (CLAUDE.md), skills e MCP + engine de memória. Relatórios detalhados via subagents.

### Engine — novos detalhes para port .NET

- **Retain**: `retain_batch` — memory defense → documents (`content_hash`) → chunks → extração LLM (4 variantes de prompt: concise/verbose/custom/verbatim; fatos com dimensões what/when/where/who/why, `fact_kind` event|conversation, `fact_type` world|assistant→experience; idioma/script do input preservado) → embed (datas augmentadas) → entity resolution fuzzy (pg_trgm + token similarity, `_INTRABATCH_MAX_NAMES=250`) → **1 txn**: units + `unit_entities` + links (temporal: mesmo fact_type, janela h, peso `max(0.3,1-h/w)`, cap 20/unit; causal ≤2 para trás; semantic por threshold) → post-commit: webhook + consolidação + refresh de mental models.
- **Consolidação**: `consolidated_at`/`consolidation_failed_at`; por batch o LLM decide `create|update|delete|skip`; observations levam `proof_count`, `source_memory_ids`, `observation_history`; dedup adjudicado por LLM; decision model tria restate/chatter (~40% menos chamadas, fallback LLM).
- **Recall**: SEM LLM — só embed + `dateparser` temporal (CPU threadpool). 4 braços → RRF k=60 (ordem semantic→bm25→graph→temporal) → trim top-300 → cross-encoder → combined scoring `CE × recency(α.2) × temporal(α.2) × proof(α.1)` → min_scores → budget packing (greedy, oversized skip, nunca vazio). Budget low/mid/high = thinking 100/300/1000 ou adaptativo `max_tokens × {.025,.075,.25}` clamp [20,2000]. Per-arm strategy boosts configuráveis.
- **Reflect**: loop agêntico ~10 iter com tools `search_mental_models→read→search_observations→recall→expand→done`; read-only; citações validadas (drop inválidas); `structured_output` por 2ª passada; disposition traits + mission + directives (regras duras com tag scope) no system prompt; com decision model o loop colapsa para query→retrieval paralelo→judge→answer.
- **Mental models/pages**: `source_query` + trigger JSONB (mode full|delta, cron, `min_refresh_interval_seconds`, escopo tags/fact_types); refresh = reflect `answer_as_document` com snapshot `created_before` + watermark `last_memory_seen_at`; staleness = memories in-scope após watermark; pages = subtype com `path` em árvore, projetadas em markdown (`page_markdown.py`).
- **Banks/config**: `disposition` JSONB, `mission`, `config` JSONB; resolução global env → tenant ext → `banks.config`; `bank_templates` semeiam config+models+directives.
- **MCP**: `/mcp/{bank_id}/` + `/mcp/` multi-bank + `X-Bank-Id`; `_make_tools_tolerant` (strip desconhecidos + **coerção de JSON stringificado**); allowlist per-bank; `HINDSIGHT_API_MCP_INSTRUCTIONS`; ops `FOR UPDATE SKIP LOCKED` com `serialization_key`.
- **Skills shipped**: `.claude/skills/` internas (code-review/triage/ship-it/figure/hs-release) + **`skills/` user-facing** (`hindsight-local|cloud|self-hosted` = skill de memória p/ agentes; `hindsight-docs`; `hindsight-architect`) via `npx skills add`; `hindsight-coding-agents` npm = session lifecycle completo (seed git→reflect no start→transcript no end→pages vivas) + protocolo `Correction:` + crédito `🧠 From Hindsight memory`.

### SPECs geradas (Draft, 2026-11-11)

`SPEC-20261111-{agent-memory-retain, memory-observations-consolidation, memory-reflect-directives, mental-models-pages, mcp-ergonomics-wave2, memory-defense, agent-rules-hardening, knowledge-hub-agent-skill}.md`

Correções do diff vs onda 1: annotations MCP **já entregues** (SPEC-20260926-mcp-sdk-alignment); args desconhecidos **já tolerados** (`ToolArgs.*`) — gap real é coerção de JSON stringificado; secrets scan existe em `ContextPayload` (falta generalizar + políticas).
