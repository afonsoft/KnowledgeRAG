# Plano de Consolidação de Tools MCP — Knowledge Hub

## Resumo

O catálogo MCP do Knowledge Hub expõe **~70+ tools** entre built-in, dinâmicas e upstream proxies. Isso cria **cognitive overload** para agentes LLM: muitas tools aumentam o custo de token no `tools/list`, aumentam a probabilidade de tool-call errada, e dificultam onboarding de novos clientes MCP.

O objetivo é reduzir o catálogo de **~70 tools → ~35-40 tools** (redução de ~45-50%), mantendo 100% da funcionalidade via parâmetros `op`/flags em tools unificadas.

---

## Estado Atual (inventário)

| Provider | Tools | Nomes |
|---|---|---|
| KnowledgeToolsProvider | 3 | `search_knowledge`, `ask_knowledge`, `agent_chat` |
| ObsidianToolsProvider | 2 | `read_document`, `write_note` |
| KnowledgeToolsProvider (write) | 1 | `write_knowledge` |
| GraphToolsProvider | 4 | `find_dependencies`, `find_dependents`, `find_path`, `analyze_impact` |
| TemporalGraphToolsProvider | 5 | `search_graph_relationships`, `search_graph_recent`, `search_graph_temporal`, `search_graph_diverse`, `search_graph_episode` |
| ContextToolsProvider | 3 | `context_save`, `context_recovery`, `context_compact` |
| MemoryToolsProvider | ~22 | `memory_retain`, `memory_recall`, `memory_list`, `memory_get`, `memory_update`, `memory_delete`, `memory_bank_list`, `memory_bank_create`, `memory_bank_update`, `memory_bank_merge`, `memory_reflect`, `memory_directives_list`, `memory_directives_create`, `memory_directives_update`, `memory_directives_delete`, `memory_consolidate`, `memory_context`, `memory_timeline`, `memory_stats`, `memory_mental_models_*`, `memory_pages_*`, `memory_session_begin`, `memory_session_end`, `memory_session_list` |
| OperationsToolsProvider | 6 | `operations_list`, `operations_get`, `operations_cancel`, `operations_retry`, `document_update`, `document_delete`, `source_rescan` |
| FlowsToolsProvider | 2 | `list_flows`, `run_flow` |
| SettingsToolsProvider | 2 | `set_chat_settings`, `set_api_key_settings` |
| SourceQueryToolsProvider | N | `query_{slug}` (1 por source ativa) |
| DeepWikiToolsProvider | 3 | `ask_question`, `read_wiki_structure`, `read_wiki_contents` |
| TavilyToolsProvider | 5 | `tavily_search`, `tavily_extract`, `tavily_map`, `tavily_crawl`, `tavily_research` |
| FirecrawlToolsProvider | 6 | `firecrawl_scrape`, `firecrawl_search`, `firecrawl_map`, `firecrawl_crawl`, `firecrawl_check_crawl_status`, `firecrawl_parse` |
| Context7ToolsProvider | 2 | `resolve-library-id`, `query-docs` |
| **TOTAL** | **~70+N** | |

---

## Proposta de Consolidação

### Grupo 1: RAG & Knowledge (3→2 tools) — Impacto: ALTO

#### 1.1 Merge `search_knowledge` + `ask_knowledge` → `search_knowledge`
- **Justificativa**: `ask_knowledge` = `search_knowledge` + síntese LLM. São 90% idênticos.
- **Proposta**: Adicionar `generate: bool` (default `true`) ao `search_knowledge`. Quando `generate=true`, retorna resposta sintetizada com citações (comportamento atual de `ask_knowledge`). Quando `generate=false`, retorna chunks crus (comportamento atual de `search_knowledge`).
- **Migração**: `ask_knowledge` vira alias deprecated que redireciona para `search_knowledge(generate=true)` por 2 releases, depois remove.
- **Economia**: −1 tool.

#### 1.2 Merge `write_knowledge` + `write_note` → `write_document`
- **Justificativa**: Ambas escrevem conteúdo. `write_note` requer vault Obsidian e aceita path; `write_knowledge` persiste sem vault.
- **Proposta**: Unificar em `write_document(title, content, path?, source?, tags?)`. Se `source` é vault e `path` fornecido, cria nota; senão, cria doc conhecimento.
- **Economia**: −1 tool.

### Grupo 2: Grafo (9→3 tools) — Impacto: ALTO

#### 2.1 Merge `find_dependencies` + `find_dependents` + `find_path` + `analyze_impact` → `graph_query`
- **Justificativa**: Todas fazem traversal no grafo com variações de direção e profundidade. `analyze_impact` é superset de `find_dependents`.
- **Proposta**: `graph_query(component, op: "dependencies"|"dependents"|"path"|"impact", target?, depth?)`.
  - `op=dependencies` → find_dependencies
  - `op=dependents` → find_dependents
  - `op=path` + `target` → find_path
  - `op=impact` → analyze_impact
- **Economia**: −3 tools.

#### 2.2 Merge 5× `search_graph_*` → `search_graph`
- **Justificativa**: Todas são variações de query no grafo temporal/episódico. A distinção é apenas o modo de busca.
- **Proposta**: `search_graph(entity?, query?, mode: "relationships"|"recent"|"temporal"|"diverse"|"episode", window?, start?, end?, episodeId?, depth?, diversityLevel?, maxResults?)`.
- **Economia**: −4 tools.

### Grupo 3: Memória (22→6 tools) — Impacto: MUITO ALTO

#### 3.1 Merge `memory_list` + `memory_get` + `memory_update` + `memory_delete` → `memory_manage`
- **Justificativa**: CRUD puro sobre fatos. Agentes raramente precisam de 4 tools separadas para operações CRUD.
- **Proposta**: `memory_manage(op: "list"|"get"|"update"|"delete", factId?, bankKey?, content?, tags?, limit?)`.
- **Economia**: −3 tools.

#### 3.2 Merge `memory_bank_list` + `memory_bank_create` + `memory_bank_update` + `memory_bank_merge` → `memory_bank`
- **Justificativa**: CRUD de banks — raramente usados individualmente.
- **Proposta**: `memory_bank(op: "list"|"create"|"update"|"merge", bankKey?, name?, config?, targetKey?, repo?)`.
- **Economia**: −3 tools.

#### 3.3 Merge `memory_directives_list` + `memory_directives_create` + `memory_directives_update` + `memory_directives_delete` → `memory_directives`
- **Justificativa**: CRUD de diretivas.
- **Proposta**: `memory_directives(op: "list"|"create"|"update"|"delete", directiveId?, bankKey?, content?, scope?)`.
- **Economia**: −3 tools.

#### 3.4 Merge `memory_session_begin` + `memory_session_end` + `memory_session_list` → `memory_session`
- **Justificativa**: Lifecycle de sessão — 3 ops estritamente sequenciais.
- **Proposta**: `memory_session(op: "begin"|"end"|"list", bankKey?, sessionId?, summary?)`.
- **Economia**: −2 tools.

#### 3.5 Merge `memory_mental_models_*` + `memory_pages_*` → `memory_pages`
- **Justificativa**: Mental models e pages são variações do mesmo conceito (páginas projetadas no vault). Estimar ~8 tools aqui → 1.
- **Proposta**: `memory_pages(op: "list"|"get"|"create"|"update"|"delete"|"refresh", pageType: "mental_model"|"page", bankKey?, pageId?, content?)`.
- **Economia**: −~6 tools (estimado).

#### 3.6 Manter separadas (por serem semântica distinta):
- `memory_retain` — write path (alto uso)
- `memory_recall` — search path (alto uso)
- `memory_reflect` — síntese com citações (alto uso)
- `memory_consolidate` — operação batch
- `memory_context` — digest de sessão
- `memory_timeline` — buckets cronológicos
- `memory_stats` — métricas

### Grupo 4: Operations & Documents (6→2 tools) — Impacto: MÉDIO

#### 4.1 Merge `operations_list` + `operations_get` + `operations_cancel` + `operations_retry` → `operations`
- **Justificativa**: CRUD de jobs async.
- **Proposta**: `operations(op: "list"|"get"|"cancel"|"retry", operationId?, status?, limit?)`.
- **Economia**: −3 tools.

#### 4.2 Merge `document_update` + `document_delete` + `source_rescan` → `document_manage`
- **Justificativa**: Operações de gestão de documentos/fontes. Estão misturadas no OperationsToolsProvider.
- **Proposta**: `document_manage(op: "update"|"delete"|"rescan", documentId?, sourceId?, title?, kind?)`.
- **Economia**: −2 tools.

### Grupo 5: Eliminar `query_{slug}` dinâmicas — Impacto: ALTO

- **Justificativa**: Cada source ativa gera `query_{slug}` que duplica `search_knowledge(source=slug)`. Com N sources, o catálogo cresce linearmente. Agentes veem 10+ tools idênticas.
- **Proposta**: Remover o SourceQueryToolsProvider. `search_knowledge` já aceita `source` filter — é 100% equivalente.
- **Feature-flag**: `Mcp:EmitSourceQueryTools` = `false` (default), `true` para retrocompatibilidade temporária.
- **Economia**: −N tools (tipicamente 5-15).

### Grupo 6: Upstream Proxy consolidation (16→10 tools) — Impacto: MÉDIO

#### 6.1 Merge Tavily: `tavily_search` + `tavily_extract` + `tavily_map` + `tavily_crawl` + `tavily_research` → `tavily` (ou 2 tools)
- **Justificativa**: `tavily_map` e `tavily_crawl` são muito parecidos (crawl com/sem conteúdo). `tavily_search` e `tavily_research` diferem em profundidade.
- **Proposta conservadora (3 tools)**: `tavily_search` (busca rápida), `tavily_extract` (lê URLs), `tavily_explore` (merge de map+crawl+research).
- **Economia**: −2 tools.

#### 6.2 Merge Firecrawl: `firecrawl_scrape` + `firecrawl_search` + `firecrawl_map` + `firecrawl_crawl` + `firecrawl_check_crawl_status` + `firecrawl_parse` → 3 tools
- **Proposta**: `firecrawl_fetch` (merge scrape+parse), `firecrawl_explore` (merge search+map+crawl), `firecrawl_status` (check_crawl_status — necessário por ser async).
- **Economia**: −3 tools.

#### 6.3 Merge DeepWiki: `read_wiki_structure` + `read_wiki_contents` → `read_wiki`
- **Justificativa**: Structure e contents são sempre usados juntos ou em sequência.
- **Proposta**: `read_wiki(repoName, include: "structure"|"contents"|"both")`. Manter `ask_question` separada (semântica diferente).
- **Economia**: −1 tool.

### Grupo 7: Context (3→2 tools) — Impacto: BAIXO

- Merge `context_compact` como param de `context_save` (`compact: "none"|"elision"|"elision+summary"`) — já existe o param, `context_compact` standalone torna-se redundante.
- Manter `context_save` e `context_recovery`.
- **Economia**: −1 tool.

### Grupo 8: Settings (2→1 tool) — Impacto: BAIXO

- Merge `set_chat_settings` + `set_api_key_settings` → `set_settings(scope: "chat"|"integration", ...)`.
- **Economia**: −1 tool.

### Grupo 9: Flows (2→1 tool) — Impacto: BAIXO

- Merge `list_flows` + `run_flow` → `flows(op: "list"|"run", flowSlug?, inputs?)`.
- **Economia**: −1 tool.

---

## Resumo da Redução

| Grupo | Antes | Depois | Economia |
|---|---|---|---|
| RAG & Knowledge | 4 | 2 | −2 |
| Grafo | 9 | 3 | −6 |
| Memória | ~22 | ~8 | −~14 |
| Operations & Documents | 6 | 2 | −4 |
| Source Query (dinâmicas) | N | 0 | −N |
| Upstream Tavily | 5 | 3 | −2 |
| Upstream Firecrawl | 6 | 3 | −3 |
| Upstream DeepWiki | 3 | 2 | −1 |
| Context | 3 | 2 | −1 |
| Settings | 2 | 1 | −1 |
| Flows | 2 | 1 | −1 |
| **TOTAL** | **~70+N** | **~35** | **−35−N** |

---

## Arquivos a Modificar

| Arquivo | Ação |
|---|---|
| `src/KnowledgeHub.Server/Mcp/ToolProviders/KnowledgeToolsProvider.cs` | Merge ask_knowledge → search_knowledge; merge write_knowledge + write_note → write_document |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/ObsidianToolsProvider.cs` | Absorver write_note no write_document unificado |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/GraphToolsProvider.cs` | 4 tools → 1 `graph_query` |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/TemporalGraphToolsProvider.cs` | 5 tools → 1 `search_graph` |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs` | ~22 tools → ~8 tools (CRUD unificados) |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/OperationsToolsProvider.cs` | 6 tools → 2 tools |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/SourceQueryToolsProvider.cs` | Remover (feature-flagged) |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/ContextToolsProvider.cs` | Remover context_compact (absorver em context_save) |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/SettingsToolsProvider.cs` | 2→1 |
| `src/KnowledgeHub.Server/Mcp/ToolProviders/FlowsToolsProvider.cs` | 2→1 |
| `src/KnowledgeHub.Server/Mcp/Upstream/TavilyToolsProvider.cs` | 5→3 |
| `src/KnowledgeHub.Server/Mcp/Upstream/FirecrawlToolsProvider.cs` | 6→3 |
| `src/KnowledgeHub.Server/Mcp/Upstream/DeepWikiToolsProvider.cs` | 3→2 |
| Testes unitários correspondentes | Adaptar todos os testes que referenciam tools renomeadas/removidas |
| CLAUDE.md, README.md, docs/ | Atualizar documentação do catálogo |

---

## Verificação

1. `dotnet build KnowledgeHub.slnx` — 0 erros
2. `dotnet test` — suíte verde (ou validação parcial se timeout)
3. Verificar que `tools/list` retorna ~35 tools (sem source queries)
4. Teste blackbox: `initialize → tools/list → tools/call` para cada tool unificada
5. Verificar aliases deprecated funcionam (ask_knowledge → search_knowledge)

---

## Riscos

| Risco | Mitigação |
|---|---|
| Breaking change para clientes MCP existentes | Aliases deprecated por 2 releases; feature flag `Mcp:LegacyToolAliases=true` |
| Schemas `op` mais complexos → LLMs erram a tool-call | Cada `op` tem schema parcial com `required` condicional; descriptions claras por op |
| Tests blackbox/bench referenciam tool names antigos | Atualizar em batch; baseline.json regenerado |
| Upstream proxy tools têm schemas definidos externamente | Consolidar apenas no nosso wrapper, não no upstream |
| Memory tools são SPECs recentes (E29-E32) | Consolidar com cuidado; manter SPEC compliance |

---

## Ordem de Execução Sugerida

1. **Wave 1 — Quick wins, baixo risco**: Settings (2→1), Flows (2→1), Context (3→2), query_{slug} removal (feature-flagged)
2. **Wave 2 — Grafo**: graph_query + search_graph unificados (9→3)
3. **Wave 3 — Knowledge**: search_knowledge absorve ask_knowledge; write_document unificado
4. **Wave 4 — Memory**: 22→8 tools (maior volume de mudança)
5. **Wave 5 — Operations**: 6→2 tools
6. **Wave 6 — Upstream proxies**: Tavily 5→3, Firecrawl 6→3, DeepWiki 3→2
7. **Wave 7 — Cleanup**: remover aliases deprecated, atualizar docs
