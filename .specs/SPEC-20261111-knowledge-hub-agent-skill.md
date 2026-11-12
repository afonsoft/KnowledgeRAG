# SPEC-20261111-knowledge-hub-agent-skill

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `knowledge-hub-agent-skill` |
| Type | `Feature` (agent-facing distribution) |
| Stack | `Markdown/Agent Skills spec (agentskills.io) + MCP config snippets` |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261111-knowledge-hub-skill` |
| Status | `Done` |
| Ticket | [#617](https://github.com/afonsoft/KnowledgeRAG/issues/617) — Epic E29 #609 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (`skills/` na raiz + `hindsight-coding-agents` integration) |

## 1. User Story

**As a** agente de IA trabalhando em *qualquer* repo (não só neste)
**I want** uma skill instalável (`npx skills add afonsoft/KnowledgeRAG -s knowledge-hub`) que me ensina a usar o KnowledgeHub MCP da máquina do usuário
**So that** faço grounding antes de assumir regras, registro trabalho/correções no hub e começo cada sessão com contexto recuperado — sem o usuário escrever configuração na mão.

## 2. Contexto técnico

- **Referência hindsight**: shipa skills **user-facing** na raiz (`skills/hindsight-{local,cloud,self-hosted,docs,architect}`) instaláveis via `npx skills add vectorize-io/hindsight -s X` — a skill é o produto: ensina o agente a armazenar/recuperar proativamente. O pacote `hindsight-coding-agents` vai além: bank por repo auto-semeado de git, **reflect injetado no session-start**, **transcript retido no session-end**, pages de arquitetura/convenções vivas, **protocolo de correção** ("Correction: \<topic>" supersede memória stale) e **protocolo de crédito** ("🧠 From memory (\<page>): …").
- **O que já temos**: primitives completas — `search_knowledge`/`ask_knowledge` (grounding), `write_note`/`write_knowledge` (registro), `context_save`/`context_recovery`/`context_compact` (session lifecycle), `read_document`, graph tools. Falta **a skill que orquestra** esses primitives para um agente externo + docs de wiring MCP por harness.
- Nosso repo já distribui skills internas via `.claude/skills/` + `skills-lock.json` — esta SPEC cria skills **para usuários do hub**, em `skills/` na raiz (agentskills.io format: `SKILL.md` + frontmatter `name`/`description`).

## 3. Requisitos

### RF-001 — `skills/knowledge-hub/SKILL.md` (memory/workflow skill)

- **Description:** Skill principal com seções: (a) **Conexão** — detectar MCP `knowledge-hub` configurado (`.mcp.json`/config do harness); se ausente, instruir o snippet correto por harness (Claude Code/Cursor/Devin/OpenCode/Windsurf — tabela de config file por harness, `Authorization: Bearer aft_*` ou `?access_token=`); (b) **Grounding First** — `search_knowledge` antes de assumir regras/contratos/utilitários; `ask_knowledge` para síntese; (c) **Session lifecycle** — session-start: `context_recovery {repo}` para snapshot compactado; session-end/checkpoint: `context_save` com decisions/notes/passages; (d) **Registro de trabalho** — `write_note` para logs de sessão, `write_knowledge` para conhecimento durável; (e) **Correção** — quando memória servida estiver errada: `write_knowledge`/`write_note` com título `Correction: <topic>` contendo (o que a memória dizia, o que é verdade agora, evidência verificada) — nunca só ignorar; (f) **Crédito** — quando resposta usar o hub, marcar trecho (`📚 From KnowledgeHub (<source/doc>): …`).
- **Rules:** frontmatter `description` otimizado para trigger (pt/en); SKILL.md ≤ 300 linhas; sem segredos; exemplos com bank/source genéricos; nunca instruir commit de `aft_*`.
- **Input → Output:** agente externo usa o hub corretamente sem ler o repo inteiro.

### RF-002 — `skills/knowledge-hub-docs/SKILL.md` (docs skill)

- **Description:** Documentação operacional empacotada: endpoints (`/mcp`, `/mcp/sse`, `/a2a`), auth, catálogo de tools com quando-usar, rate limits/CallerScope, erros comuns e troubleshooting (`401`, `409 job`, `SessionMode`), docker quick-start.
- **Rules:** gerado a partir de `CLAUDE.md`/`docs/` por script (ver RF-004) — não escrito à mão.
- **Input → Output:** docs do produto consumíveis por agente (o que `hindsight-docs` é para eles).

### RF-003 — `skills/knowledge-hub-architect/SKILL.md` (adoption skill)

- **Description:** Guia o agente a analisar a app do usuário e propor arquitetura de conhecimento: quais `SourceType` usar (Obsidian/WebPage/DocumentFile/RestApi/SqlDatabase/McpProxy), chunking, banks/sources layout, tag schema, eval baseline inicial. Output = plano, não código (paridade com `hindsight-architect`).
- **Rules:** referencia SPEC/docs reais do hub; pergunta antes de assumir.
- **Input → Output:** onboarding assistido para novos deployments.

### RF-004 — Geração + freshness (CI)

- **Description:** `scripts/generate-skills.sh` gera `skills/knowledge-hub-docs/SKILL.md` a partir de fontes canônicas (seções marcadas `<!-- skill:begin/end -->` em `CLAUDE.md` + `docs/`). Teste `SkillsFreshnessTests` (unit) falha se o arquivo gerado divergir do commitado. Manifest `skills/index.json` (nome → descrição → path) para `npx skills add` discovery.
- **Rules:** paridade com a convenção deles (single-source + stale check); `.claude/skills/` internas **não** mudam de processo.
- **Input → Output:** skills publicadas nunca dessincronizam das docs.

### RF-005 — Snippets de wiring por harness

- **Description:** `skills/knowledge-hub/wiring/` com JSON/TOML de exemplo por harness (claude-code `~/.claude.json`/`claude mcp add`, cursor `~/.cursor/mcp.json`, devin `~/.config/devin/mcp.json`, opencode, windsurf) — validados como JSON parseável em teste.
- **Rules:** placeholder `AFT_KEY_HERE`; SSE legacy + streamable HTTP cobertos; nota sobre `?access_token=` para harnesses sem header.
- **Input → Output:** setup em <2min para os principais agentes.

### RF-006 — Documentação de instalação

- **Description:** `README.md` do repo + `docs/{en,pt}/` — seção "Agent Skill": `npx skills add afonsoft/KnowledgeRAG -s knowledge-hub` + o que a skill faz + wiring MCP manual.
- **Rules:** docs bilíngues seguindo o padrão do repo.
- **Input → Output:** descoberta da skill pelo usuário final.

## 4. Fora de escopo

- Installer automático multi-harness tipo `hindsight-coding-agents install all` (fase 2 — requer empacotamento npm e hooks por harness; candidato a SPEC própria).
- Ingestão automática de git history/transcripts no session-end (depende de hooks do harness — a skill documenta o protocolo manual; automação via MCP quando o harness suportar hooks).
- Telemetria de uso da skill.
- Publicação em marketplace/registry além de `npx skills`.

## 5. Critérios de aceite

- [ ] **Dado** `npx skills add <repo> -s knowledge-hub` num repo externo **então** SKILL.md instala e seu `description` faz trigger em "use o knowledge hub" / "search knowledge before coding" (teste de frontmatter).
- [ ] **Dado** agente seguindo a skill **então** consegue: configurar MCP (wiring snippet), grounding antes de editar, `context_recovery` no start, `context_save` no end, `Correction:` quando encontra memória errada.
- [ ] **Dado** mudança em `CLAUDE.md` seção marcada **então** `generate-skills.sh` regenera e `SkillsFreshnessTests` falha até commitar o diff.
- [ ] **Dado** cada wiring JSON **então** parseia válido e contém placeholder de key (teste parametrizado).
- [ ] **Dado** SKILL.md **então** zero ocorrências de `aft_` real / segredos (grep test).
- [ ] **Edge:** harness desconhecido → skill cai no wiring genérico streamable-HTTP; usuário sem server rodando → skill aponta docker quick-start, não quebra.

## 6. Arquivos

**Ler antes:**
- `CLAUDE.md` (seções marcadas p/ extração), `docs/{en,pt}/`, `AGENTS.md`
- `skills-lock.json` + `.claude/skills/` (formato interno — não confundir com o novo dir `skills/`)
- `/tmp/hindsight/skills/*/SKILL.md`, `/tmp/hindsight/hindsight-integrations/coding-agents/{skill-src/preamble.md,skill/SKILL.md,README.md}`

**Criar/modificar:**
```text
skills/knowledge-hub/{SKILL.md, wiring/*.json}
skills/knowledge-hub-docs/SKILL.md (gerado)
skills/knowledge-hub-architect/SKILL.md
skills/index.json
scripts/generate-skills.sh
tests/KnowledgeHub.Tests.Unit/Conventions/SkillsFreshnessTests.cs
README.md, docs/en/, docs/pt/ (seção Agent Skill)
CLAUDE.md (marcadores skill:begin/end + menção ao novo dir)
```

## 7. Riscos

- Skills são o "produto" para o agente — texto ruim = adoção ruim; revisar com leitura de agente real (dogfood numa sessão Devin/Claude em repo externo antes de Done).
- Duas convenções de skills no repo (`.claude/skills` internas vs `skills/` publicadas) — documentar diferença no CLAUDE.md para não confundir o `skills` CLI.
- Wiring snippets desatualizam com versões de harness — freshness test + revisão periódica.
- `npx skills add` requer o repo público/reachability — documentar fallback (copiar `skills/` manualmente).

## 8. Task Plan + DoD

- [ ] T1: `skills/knowledge-hub/SKILL.md` completo (6 seções) + wiring snippets.
- [ ] T2: `generate-skills.sh` + marcadores em CLAUDE.md + `knowledge-hub-docs` gerado + freshness test.
- [ ] T3: `knowledge-hub-architect` + `skills/index.json`.
- [ ] T4: docs (README + en/pt) + regras anti-segredo (grep test).
- [ ] T5: dogfood — instalar num repo externo e validar o fluxo session-start→grounding→save; ajustar texto.
- [ ] DoD: RF implementados, AC cobertos, skills instaláveis via `npx skills`, freshness test verde, docs bilíngues.
