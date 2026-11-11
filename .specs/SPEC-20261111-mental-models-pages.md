# SPEC-20261111-mental-models-pages

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `mental-models-pages` |
| Type | `Feature` |
| Stack | `.NET 10`, EF Core, background refresh, Obsidian projection |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261111-mental-models` |
| Status | `Approved` |
| Ticket | [#613](https://github.com/afonsoft/KnowledgeRAG/issues/613) — Epic E29 #609 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (`engine/mental_models`, `engine/page_markdown.py`) |
| Depends on | `SPEC-20261111-agent-memory-retain` (+ `memory-reflect-directives` para o refresh via reflect) |

## 1. User Story

**As a** agente de IA iniciando sessão num projeto
**I want** ler páginas pré-sintetizadas do bank ("arquitetura do projeto", "convenções", "iniciativas em andamento")
**So that** começo cada sessão com conhecimento consolidado — leitura de banco, zero retrieval, zero LLM — em vez de redescobrir tudo por busca.

## 2. Contexto técnico

- **Referência hindsight**: `mental_models` = perguntas permanentes (`source_query`) cujas respostas são sintetizadas e **re-escritas em background** conforme o bank aprende. Triggers: `refresh_after_consolidation`, `refresh_cron`, `full_refresh_cron`, `min_refresh_interval_seconds`, escopo por `fact_types`/`tags_match`/`tag_groups`, `mode` full|delta (delta usa watermark `last_memory_seen_at`). Refresh = chamada reflect limitada (`answer_as_document`, snapshot `created_before`). **Pages** = mental models com defaults opinativos (observations-only, delta refresh) organizadas em **árvore de folders** (`path`) e **projetadas em markdown** (`engine/page_markdown.py`) — wiki navegável e montável.
- **O que já temos**: Obsidian vault conectado (`write_note`/`read_document`, `VaultWatcherService`, WebDAV) — fit natural: pages viram arquivos `.md` no vault (o hub escreve, Obsidian renderiza); `MemoryReflectService` (SPEC reflect) como executor do refresh; `BackgroundServices/` para cron; FTS5 para `search_pages`.
- Staleness = existem memories in-scope com `MentionedAt > LastMemorySeenAt`.

## 3. Requisitos

### RF-001 — `MemoryMentalModel` entity

- **Description:** `{ Id, BankId, Subtype ('mental_model'|'page'), Name, Path (folder tree, ex. 'projeto/convencoes'), SourceQuery, TriggerJson (mode/refreshAfterConsolidation/refreshCron/fullRefreshCron/minRefreshIntervalSeconds/factTypes/tagsMatch/tagGroups/excludeMentalModels), Content (markdown), StructuredContentJson?, LastRefreshedAt, LastMemorySeenAt, LastRefreshedSourceQuery, Tags[] }` + `MentalModelHistory` append-only (versão anterior por refresh). Migration dual.
- **Rules:** `Path` normalizado (sem `..`, lowercase folders, `/` separador); nome único por bank+path; history guarda N últimas versões (`Memory:MentalModels:HistoryLimit`, default 20).
- **Input → Output:** modelo persistido com trigger declarativo.

### RF-002 — Refresh engine

- **Description:** `MentalModelRefreshService` executa refresh = `memory_reflect` limitado (`answerAsDocument:true`, snapshot de `created_before`, escopo por trigger). `mode:'full'` re-sintetiza de todo o escopo; `mode:'delta'` só incorpora memories desde `LastMemorySeenAt` ao conteúdo atual. Após cada consolidação (`IMemoryConsolidationHook`), refresh só de models cujo escopo (tags/factTypes) pode sobrepor — `skip_if_in_flight`.
- **Rules:** `min_refresh_interval_seconds` evita refresh cascata; falha → marca erro no model + history inalterado; watermark avança só em sucesso; refresh manual `memory_mental_model_refresh` ignora intervalo.
- **Input → Output:** `Content` atualizado + linha em `MentalModelHistory`.

### RF-003 — Pages: árvore + projeção no vault Obsidian

- **Description:** Page = mental model com `Subtype='page'` e defaults (source restrito a observations, delta refresh). `Path` define posição na árvore. `Memory:Pages:ProjectToVault=true` projeta cada page em `{vault}/_memory/{bankKey}/{path}.md` via o pipeline de vault já existente (respeitando `Vault:Path`/`VaultSeeder`); mudança de `Path` move o arquivo (delete+create). Pasta `_memory/` é **managed** (banner `<!-- managed-by:knowledge-hub -->`; não sobrescrever edições manuais fora dela).
- **Rules:** projeção best-effort (falha de vault não falha refresh); frontmatter com `bank`, `source_query`, `refreshed_at`; arquivo deletado quando page deletada.
- **Input → Output:** wiki viva do bank navegável no Obsidian.

### RF-004 — Tools MCP + REST

- **Description:** `memory_mental_models_list|get|create|update|delete|refresh` e pages: `memory_pages_tree` (árvore folders+pages), `memory_pages_search` (FTS sobre `Name`+`Content`), `memory_page_get`, `memory_page_create`, `memory_page_update` (rename/move path/edit content manual → marca `manual_edit` no history), `memory_page_delete`. REST `/api/memory/banks/{bankKey}/mental-models/*` e `/pages/*`.
- **Rules:** `create` dispara refresh inicial (sync opcional `wait:true` ou job); `memory_page_get` é leitura pura de banco (custo ~0 — o ponto central da feature).
- **Input → Output:** superfície equivalente ao hindsight (`get_page_tree`, `search_pages`, `get_page`, CRUD de nodes).

### RF-005 — Leitura barata no session-start

- **Description:** `memory_page_get`/`memory_pages_tree` documentados como o caminho recomendado de boot do agente (ver SPEC-20261111-knowledge-hub-agent-skill — session-start lê tree + pages relevantes).
- **Rules:** cache `IDistributedCache` opt-in por `(bankKey,pageId)` invalidado no refresh.
- **Input → Output:** boot do agente sem chamada LLM.

## 4. Fora de escopo

- Mount do filesystem tipo `hindsight fs mount` (projeção no vault já cobre o caso).
- Pages sobre `KnowledgeDocument`/sources (escopo restrito a `MemoryUnit` — decisão consciente; wiki sobre corpus documental já é coberta por `read_wiki_*` upstream).
- UI admin dedicada (lista/leitura via REST basta; viewer pode vir depois).
- Editor colaborativo de pages na UI Blazor.

## 5. Contrato MCP (incrementos)

```text
memory_mental_models_list|get|create|update|delete|refresh
memory_pages_tree(bankKey?) → tree de folders/nodes
memory_pages_search(bankKey?, query, limit?) → hits com snippet
memory_page_get(bankKey?, pageId|path) → { name, path, content, refreshedAt }
memory_page_create|update|delete
```

Erros: `404` page/model inexistente; `409` nome+path duplicado; `400` path inválido (`..`, absoluto) ou trigger inválido (cron malformado).

## 6. Critérios de aceite

- [ ] **Dado** `memory_mental_models_create {name:"Preferências do usuário", sourceQuery:"Quais as preferências do usuário?"}` e bank com fatos de preferência **então** após refresh, `Content` contém resposta sintetizada citando os fatos; `memory_mental_models_get` retorna em ~0ms de LLM.
- [ ] **Dado** novo retain relevante + consolidação **quando** hook dispara **então** só models cujo escopo cobre os tags/factTypes novos são re-refrescadas (não todas).
- [ ] **Dado** `mode:'delta'` **então** segundo refresh processa só memories desde `LastMemorySeenAt`; `full` re-sintetiza tudo.
- [ ] **Dado** page em `projeto/convencoes` **então** `{vault}/_memory/{bank}/projeto/convencoes/<name>.md` existe com frontmatter; mover para `projeto/api` move o arquivo.
- [ ] **Dado** 2 refreshes quase simultâneos **então** segundo é skipped (`skip_if_in_flight`) — sem corridas duplicando history.
- [ ] **Edge:** bank sem memories → page criada com content "sem evidência suficiente"; refresh com LLM indisponível → `Content` anterior preservado + erro registrado.

## 7. Arquivos

**Ler antes:**
- `src/KnowledgeHub.Server/Ingestion/{VaultSeeder}.cs`, conectores Obsidian (`Ingestion/Connectors/`)
- `src/KnowledgeHub.Server/BackgroundServices/`, `src/KnowledgeHub.Server/Caching/`
- `src/KnowledgeHub.Server/Memory/` (SPEC retain/reflect)
- `/tmp/hindsight/hindsight-api-slim/hindsight_api/engine/{response_models.py(MentalModelTrigger), page_markdown.py}` + `docs/developer/{mental-models,pages}.mdx`

**Criar/modificar:**
```text
src/KnowledgeHub.Server/Domain/Entities/{MemoryMentalModel,MentalModelHistory}.cs
src/KnowledgeHub.Server/Memory/MentalModels/{MentalModelRefreshService,TriggerEvaluator,PageTreeBuilder,VaultPageProjector}.cs
src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs
src/KnowledgeHub.Server/Api/MemoryEndpoints.cs
tests/KnowledgeHub.Tests.Unit/Memory/MentalModels/*
tests/KnowledgeHub.Tests.Integration/Memory/*
```

## 8. Riscos

- Refresh churn em banks muito ativos — mitigado por `min_refresh_interval_seconds`, delta mode e escopo por tags.
- Custo de LLM por refresh — documentado; delta + observations-only reduzem drasticamente.
- Conflito vault↔hub (usuário edita `.md` projetado) — mitigado por pasta `_memory/` managed com banner; edição fora dela nunca é tocada.
- Trigger JSON inválido no banco — validado na escrita; leitura tolerante com fallback defaults.

## 9. Task Plan + DoD

- [ ] T1: entities + migrations + trigger validation.
- [ ] T2: `MentalModelRefreshService` (full/delta/watermark/skip-in-flight) + hook de consolidação + cron evaluator.
- [ ] T3: pages tree/search/CRUD + `VaultPageProjector` + frontmatter/managed dir.
- [ ] T4: tools MCP + REST + cache opt-in.
- [ ] T5: testes (unit: trigger eval/delta watermark/tree builder/projection path; integração: create→refresh→vault file; hook pós-consolidação) + gates.
- [ ] DoD: RF implementados, AC cobertos, projeção no vault verificada em Obsidian real ou mock de filesystem, ≥80% coverage.
