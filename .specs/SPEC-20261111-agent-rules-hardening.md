# SPEC-20261111-agent-rules-hardening

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-rules-hardening` |
| Type | `Docs` (+ testes de convenção) |
| Stack | `Docs / xUnit / CI` |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261111-agent-rules` |
| Status | `In implementation` |
| Ticket | [#616](https://github.com/afonsoft/KnowledgeRAG/issues/616) — Epic E29 #609 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (`CLAUDE.md` deles — convenções operacionais) |

## 1. User Story

**As a** agente de IA ou contribuidor humano neste repo
**I want** convenções operacionais explícitas (testes LLM, migrations duais, config hierárquica, gates de PR)
**So that** código novo segue os mesmos padrões sem depender de conhecimento tribal — igual ao CLAUDE.md do hindsight.

## 2. Contexto técnico

Comparativo hindsight↔KnowledgeRAG (análise 2026-11-11): nosso `CLAUDE.md` já é forte em onboarding MCP e Memory Protocol. Convenções do hindsight que faltam ou estão implícitas:

| Convenção | Hindsight | Nosso gap |
|---|---|---|
| LLM-as-judge em testes | marker `hs_llm_core` + judge independente para comportamento não-determinístico | Temos eval gates, não o padrão unit-level |
| Blackbox system tests | suíte que dirige processo real via SDK — obrigatória p/ capability nomeável | Integration tests existem; blackbox MCP-end-to-end não formalizado |
| Migration parity lint | `test_migration_shape.py` falha CI se migration não despacha p/ ambos dialetos | Árvores SQLite+Postgres sem lint de paridade |
| Config hierarchy | regra explícita hierarchical vs static + checklist de 6 passos | Per-key settings existem, convenção não documentada |
| Gate `/code-review` pré-PR | mandatório no CLAUDE.md | Skill existe, não é gate explícito |
| Integration checklist | nova integração: testes + CI job + registro release | Sem checklist formal para conectores/proxies |
| Generated docs freshness | README → SKILL.md/docs gerados + teste de stale | `skills-lock.json` cobre origem, não geração |
| Changelog | entries só via release tooling | Sem regra explícita |

## 3. Requisitos

### RF-001 — Seção "Convenções de teste" no CLAUDE.md

- **Description:** Documentar o padrão: (a) mecânicas determinísticas → unit tests xUnit com asserts diretos; (b) comportamento de LLM não-determinístico (classificação, extração, atribuição, tom) → teste marcado `[Trait("Category","LLMJudge")]` rodando `IChatClient` real + judge independente (`assert_meets_criteria` equivalente: resposta + criteria + contexto; judge ≠ provider testado). Incluir exemplo copiável.
- **Rules:** judge default = provider configurado em `Tests:LlmJudge:*` (skip gracioso sem key — `Skip` attribute, não falha); nunca hard-assert de classificação LLM.
- **Input → Output:** convenção + helper `LlmJudgeAssert` em `tests/KnowledgeHub.Tests.Unit/Testing/LlmJudgeAssert.cs` + 1 teste exemplo.

### RF-002 — Blackbox/smoke test convention

- **Description:** Documentar que capability user-facing nova exige teste blackbox: sobe `KnowledgeHub.Server` real (WebApplicationFactory ou processo) + fala MCP/REST de fora (JSON-RPC real, sem acesso a internals). Criar `tests/KnowledgeHub.Tests.Blackbox/` esqueleto com 1 smoke: `initialize`→`tools/list`→`tools/call search_knowledge` contra banco SQLite temporário.
- **Rules:** projeto separado (não contaminar unit/integration); roda em CI como job próprio ou dentro do job de integration existente (decisão do implementador, documentada).
- **Input → Output:** suite inicial + convenção escrita.

### RF-003 — Migration parity lint

- **Description:** Teste `MigrationParityTests` (unit, sem DB): enumera `Migrations/*.cs` (SQLite tree) e `Migrations/Postgres/*.cs` — toda migration nova deve existir nos dois (matching por nome-base, ignorando timestamp) ou declarar explicitamente `// dialect: sqlite-only|postgres-only` com justificativa no arquivo. Falha CI se assimétrico sem marker.
- **Rules:** marker deliberado documenta intencionalidade (paridade com `run_for_dialect` deles); teste também valida que migrations novas não editam snapshots antigos.
- **Input → Output:** drift de schema vira falha de build, não surpresa em produção.

### RF-004 — Convenção de config hierárquica documentada

- **Description:** Seção em CLAUDE.md: campos são `hierarchical` (global `IConfiguration` → per-key `ApiKey*Settings` → per-escopo `MemoryBank.ConfigJson`/source metadata) ou `static` (server-level: conn strings, portas, limites). Checklist para adicionar config: constante/options class → `_CONFIGURABLE` equivalente → resolver → doc em CLAUDE.md → `.env.example`.
- **Rules:** tabela de exemplos (LLM settings=hierárquico; `Database:Provider`=static).
- **Input → Output:** regra explícita que as SPECs de memória já referenciam.

### RF-005 — Gates de PR e checklists

- **Description:** Em CLAUDE.md: (a) `/code-review` (skill `code-review-and-quality`) é **gate obrigatório** antes de abrir PR — must-fix resolvidos; (b) **checklist de nova integração/conector**: tests + registro no catálogo + docs + (se aplicável) secret-store key + entrada em CLAUDE.md catálogo de tools; (c) regra de changelog: entradas só por release tooling, correções documentadas no PR/commit.
- **Rules:** texto normativo (MUST/SHOULD), não aspiracional.
- **Input → Output:** gates descobríveis por qualquer agente que leia o repo.

### RF-006 — `.env.example` como fonte única

- **Description:** Regra: toda env var nova entra em `.env.example` com comentário; teste `EnvExampleTests` garante que `IConfiguration` keys `*:__*` usadas no código (`IOptions<T>`/`GetValue`) têm entrada ou marker `// env:optional`.
- **Rules:** paridade com o teste `test_bundled_template_matches_repo_root` deles.
- **Input → Output:** template nunca dessincroniza do código.

## 4. Fora de escopo

- Reescrever CLAUDE.md inteiro (adições cirúrgicas nas seções existentes).
- Suite blackbox completa (só esqueleto + convenção; testes por feature virão nas SPECs delas).
- CI workflow changes (`.github/workflows` protegido — propor em texto, implementação separada se aprovada).
- Adoção de ferramentas externas de dead-code (knip/vulture) — mencionar como opcional, não instalar.

## 5. Critérios de aceite

- [ ] **Dado** migration nova só em `Migrations/` **então** `MigrationParityTests` falha com mensagem clara; com marker `// dialect: sqlite-only` justificado → passa.
- [ ] **Dado** teste `[Trait("Category","LLMJudge")]` sem key configurada **então** skip gracioso (não falha CI local).
- [ ] **Dado** env var nova usada via `GetValue`/`IOptions` sem entrada em `.env.example` **então** `EnvExampleTests` falha.
- [ ] **Dado** PR aberto por agente **então** CLAUDE.md indica gate `/code-review` obrigatório + checklist de integração.
- [ ] **Dado** smoke blackbox **então** `initialize`+`tools/list`+`tools/call` funcionam contra o server real em teste.
- [ ] **Edge:** nenhum RF altera comportamento de runtime do produto (docs+testes apenas — `dotnet test` verde).

## 6. Arquivos

**Ler antes:**
- `CLAUDE.md`, `AGENTS.md`, `.claude/MEMORY.md`
- `src/KnowledgeHub.Server/Migrations/` (ambas árvores)
- `tests/{KnowledgeHub.Tests.Unit,KnowledgeHub.Tests.Integration}/`
- `/tmp/hindsight/CLAUDE.md` (seções Testing / Migrations / Config / Integrations)
- `.env.example`

**Criar/modificar:**
```text
CLAUDE.md (seções novas: conv. de testes, config hierárquica, gates de PR, changelog)
.env.example (sincronizar)
tests/KnowledgeHub.Tests.Unit/Conventions/{MigrationParityTests,EnvExampleTests}.cs
tests/KnowledgeHub.Tests.Unit/Testing/LlmJudgeAssert.cs
tests/KnowledgeHub.Tests.Blackbox/ (projeto novo + smoke test)
KnowledgeHub.slnx (registrar projeto blackbox)
```

## 7. Riscos

- Regras normativas demais viram ruído — manter seções curtas com exemplos.
- LLM-judge flake — judge independente + skip sem key + nunca gate de CI bloqueante no MVP.
- Blackbox suite lenta — esqueleto com 1 smoke; crescer por feature.
- `.env.example` test pode gerar falsos positivos (keys dinâmicas) — marker de opt-out.

## 8. Task Plan + DoD

- [ ] T1: `LlmJudgeAssert` + teste exemplo + seção no CLAUDE.md.
- [ ] T2: `MigrationParityTests` + marker convention + doc.
- [ ] T3: blackbox project + smoke MCP + doc.
- [ ] T4: config hierarchy + PR gates + integration checklist + changelog rule em CLAUDE.md.
- [ ] T5: `EnvExampleTests` + sync do `.env.example`.
- [ ] DoD: RF implementados, AC cobertos, `dotnet test` verde, CLAUDE.md revisado por leitura completa (sem seções contraditórias).
