# SPEC-20261111-memory-reflect-directives

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `memory-reflect-directives` |
| Type | `Feature` |
| Stack | `.NET 10`, MCP, `IChatClient` (agent loop) |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261111-memory-reflect` |
| Status | `Approved` |
| Ticket | [#612](https://github.com/afonsoft/KnowledgeRAG/issues/612) — Epic E29 #609 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (`engine/reflect/`, `engine/directives/`) |
| Depends on | `SPEC-20261111-agent-memory-retain`; recomendada `SPEC-20261111-memory-observations-consolidation` |

## 1. User Story

**As a** agente de IA
**I want** chamar `memory_reflect` para raciocinar sobre as memórias de um bank (em vez de só recuperá-las)
**So that** perguntas "por quê" e análises que exigem conectar fatos ("o que sei sobre a Alice?", "quais riscos do projeto?") recebam respostas fundamentadas com citações — não só uma lista de fatos.

## 2. Contexto técnico

- **Referência hindsight** (`engine/reflect/`): `reflect` é um **loop agêntico** (máx ~10 iterações) onde o LLM usa tools hierárquicas: `search_mental_models → read_mental_models → search_observations → recall (fatos brutos) → expand → done`. Resposta exige evidência antes de responder; **citações validadas** contra IDs realmente recuperados (citação inválida é descartada). Output: `text`, `based_on` (memories/mental_models/directives citados), `structured_output` opcional (segunda passada extrai JSON por `response_schema`; falha → `structured_output_error`, HTTP 200), `trace`, `usage`. Reflect é **read-only** — nunca escreve.
- **Disposition + directives**: `disposition` do bank (`skepticism/literalism/empathy` 1–5) vira descrição de persona no system prompt; `mission` define o objetivo do bank; **directives** são regras duras (`name`, `content`, `priority`, `is_active`, `tags` — escopo por tags) injetadas no prompt e com override `apply_all_directives`.
- **O que já temos**: agent loop com tool-calling (`agent_chat`/`Agent`), `MemoryRecallService` (SPEC retain), dispositions já no schema de `MemoryBank`, structured output via `Microsoft.Extensions.AI`, HITL `ToolApproval` para tools de escrita (reflect não precisa — é read-only).

## 3. Requisitos

### RF-001 — `MemoryDirective` entity + CRUD

- **Description:** `MemoryDirective { Id, BankId, Name, Content, Priority (int), IsActive, Tags[], CreatedAt, UpdatedAt }` — regras comportamentais por bank ("sempre responda em pt-BR", "nunca exponha dados de billing"). Tools: `memory_directives_list|create|delete` (+ REST `/api/memory/banks/{bankKey}/directives`).
- **Rules:** ordenação por `Priority` desc; escopo por `tags` (directive sem tags = global ao bank); update via delete+create é aceitável no MVP.
- **Input → Output:** directives persistidas e injetáveis.

### RF-002 — `memory_reflect` tool

- **Description:** `{bankKey?, query, context?, budget:'low'|'mid'|'high', maxTokens?, responseSchema?, tags?, tagsMatch?, applyAllDirectives?, includeBasedOn?, includeTrace?}`. Loop agêntico com tools internas (chamadas à `MemoryRecallService` filtradas por fact_type): camada 1 = mental models (se SPEC de pages existir; senão skip), camada 2 = observations, camada 3 = fatos brutos. Max iterações configurável (`Memory:Reflect:MaxIterations`, default 10; budget ajusta profundidade).
- **Rules:** reflect nunca escreve estado (read-only garantido — tools internas só de leitura); `context` entra no system prompt; directives filtradas por `tags`/`tagsMatch` (ou todas com `applyAllDirectives`); disposition do bank vira persona; `mission` entra como objetivo.
- **Input → Output:** `{ text, basedOn: [{type, id, text}], structuredOutput?, structuredOutputError?, trace?, usage }`.

### RF-003 — Validação de citações

- **Description:** Todo ID citado em `based_on` deve existir no conjunto efetivamente recuperado nesta chamada — citação inventada é removida e registrada no trace (`droppedCitations`).
- **Rules:** validação determinística (set intersection), não por LLM; se `based_on` ficar vazio mas houver texto, trace marca `uncited:true`.
- **Input → Output:** citações 100% rastreáveis a retrieval real.

### RF-004 — Structured output

- **Description:** `responseSchema` (JSON Schema) → segunda passada LLM extrai JSON conforme o schema a partir da resposta. Falha → `structured_output=null` + `structured_output_error` (nunca erro HTTP).
- **Rules:** usa `IChatClient` com JSON mode/schema quando o provider suportar; retry 1x com prompt corretivo.
- **Input → Output:** respostas reflect consumíveis programaticamente.

### RF-005 — REST + auditoria

- **Description:** `POST /api/memory/banks/{bankKey}/reflect` espelha a tool. Reflect registrado no activity feed (duração, iterations, usage) como os demais tools MCP.
- **Rules:** rate limit por key (CallerScope existente); `budget` afeta iterações e tokens — documentado no schema da tool.
- **Input → Output:** paridade MCP/REST.

## 4. Fora de escopo

- Dispositions alterando recall (no hindsight elas só afetam reflect — manter igual).
- Reflect escrevendo observations (no hindsight reflect é read-only; quem escreve é a consolidação).
- Decision-model collapsing do loop (otimização deles; fica como RF futuro).
- Streaming SSE de iterações do reflect (REST/MCP retornam resultado final; streaming via `agent_chat` já existe para casos interativos).
- Camada mental-models quando SPEC-20261111-mental-models-pages não estiver implementada (degrada graciosamente para observations→fatos).

## 5. Contrato MCP

```text
memory_reflect(bankKey?, query, context?, budget?, maxTokens?, responseSchema?, tags?, tagsMatch?, applyAllDirectives?, includeBasedOn?, includeTrace?)
memory_directives_list(bankKey?) / memory_directives_create(bankKey?, name, content, priority?, tags?) / memory_directives_delete(bankKey?, id)
```

## 6. Critérios de aceite

- [ ] **Dado** bank com fatos "Atlas usa Kubernetes", "cluster teve outage terça" e directive "responda em pt-BR" **quando** `memory_reflect {query:"a Alice foi afetada por problemas recentes?"}` **então** resposta em pt-BR conectando os fatos via graph com `based_on` citando os IDs reais.
- [ ] **Dado** `disposition {skepticism:5}` vs `{skepticism:1}` na mesma pergunta **então** personas produzem respostas com tom diferente (teste com LLM-judge — convenção SPEC-20261111-agent-rules-hardening).
- [ ] **Dado** LLM que alucina citação **então** `droppedCitations` no trace e ID ausente de `based_on`.
- [ ] **Dado** `responseSchema` simples **então** `structuredOutput` parseado conforme schema; schema impossível → `structuredOutputError` preenchido, resposta textual intacta.
- [ ] **Dado** directive com `tags:["billing"]` e query com `tags:["other"]` **então** directive não entra no prompt; com `applyAllDirectives:true` entra.
- [ ] **Edge:** bank vazio → resposta honesta "sem memórias suficientes" (nunca inventa); loop que não converge em `maxIterations` → melhor resposta parcial com `trace.truncated:true`.

## 7. Arquivos

**Ler antes:**
- `src/KnowledgeHub.Server/Agent/` (agent loop existente de `agent_chat`)
- `src/KnowledgeHub.Server/Mcp/ToolProviders/KnowledgeToolsProvider.cs` (`agent_chat`)
- `src/KnowledgeHub.Server/Domain/Entities/ToolApproval.cs`
- `/tmp/hindsight/hindsight-api-slim/hindsight_api/engine/reflect/{tools,tools_schema,prompts,presentation}.py`

**Criar/modificar:**
```text
src/KnowledgeHub.Server/Domain/Entities/MemoryDirective.cs
src/KnowledgeHub.Server/Memory/Reflect/{MemoryReflectService,ReflectPromptBuilder,DispositionPersona,CitationValidator}.cs
src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs
src/KnowledgeHub.Server/Api/MemoryEndpoints.cs
tests/KnowledgeHub.Tests.Unit/Memory/Reflect/*
tests/KnowledgeHub.Tests.Integration/Memory/*
```

## 8. Riscos

- Loop agêntico caro — mitigado por `budget`, `maxIterations` e maxTokens; budget low limita a 2 camadas.
- Prompt injection via conteúdo de memórias/directives — mitigado: directives são escritas pelo dono da key (mesmo trust boundary das fontes), e reflect é read-only (sem tools de escrita no loop).
- Citação válida mas resposta factualmente errada — aceitável (judge/observabilidade; melhora com observations consolidadas).

## 9. Task Plan + DoD

- [ ] T1: `MemoryDirective` entity + migration + CRUD MCP/REST.
- [ ] T2: `MemoryReflectService` — loop com tools internas de retrieval + disposition persona + directives injection.
- [ ] T3: `CitationValidator` + trace + structured output.
- [ ] T4: tool `memory_reflect` + REST + auditoria + budget mapping.
- [ ] T5: testes (unit: persona/directives/citation validator/schema; integração: reflect E2E sobre bank semeado) + gates.
- [ ] DoD: RF implementados, AC cobertos, reflect comprovadamente read-only (teste que nenhuma escrita ocorre), ≥80% coverage novos serviços.
