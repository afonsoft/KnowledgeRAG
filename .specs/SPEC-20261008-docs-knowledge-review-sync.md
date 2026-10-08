# SPEC-20261008-docs-knowledge-review-sync

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | `docs-knowledge-review-sync` |
| Type | `Docs` |
| Stack | `Docs` |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/devin-20261008-docs-review-sync` |
| Ticket | GAP-documentation-knowledge-review-cli (gap-analysis 2026-10-08) |
| Status | `Done` — mergeado via PR [#575](https://github.com/afonsoft/KnowledgeRAG/pull/575) (`fc21da2`); RF-002 coberto (READMEs já linkavam docs) |

## 1. User Story

**Como** maintainer do KnowledgeHub,
**quero** que o CLAUDE.md e o README documentem o agente Knowledge Review entregue,
**para que** agentes e contribuidores descubram a CLI `KnowledgeHub.Review.Cli`, seus subcomandos e os workflows do GitHub associados sem ler o código.

**Problem context:** O feature foi entregue e mergeado (PRs #568/#569, SPEC-20261007 `Done`), com workflows `.github/workflows/knowledge-review.yml` e `knowledge-review-reusable.yml` ativos e **documentação dedicada já linkada nos READMEs** (`docs/en/KNOWLEDGE-REVIEW.md` em README.md:617 e README.pt-br.md:613). O gap restante é o **CLAUDE.md** — catálogo canônico de features (convenção do repo: ver SPEC-20260918-claude-md-feature-sync) — que tem **0 menções** a Knowledge Review/Review.Cli.

> **Correção (2026-10-08, pós-publicação do gap):** a evidência original do
> gap-analysis (`grep "knowledge-review\|Review.Cli"`) não cobria a forma com
> espaço ("Knowledge Review"). READMEs já documentam/linkam o feature; o RF-002
> foi reclassificado como **coberto** — só o CLAUDE.md permanece como gap.

## 2. Scope

**In scope:**
- Parágrafo/entrada em `CLAUDE.md` descrevendo o Knowledge Review (CLI self-hosted, subcomandos `collect/review/gate/run`, workflows, requisito de LLM via `IChatClient`).

**Out of scope:**
- `README.md` / `README.pt-br.md` — já linkam a documentação dedicada (`docs/en/KNOWLEDGE-REVIEW.md`, `docs/pt/KNOWLEDGE-REVIEW.md`); RF-002 reclassificado como coberto.
- Alteração de código ou workflows.
- Documentação da SPEC-20261008-sonarqube-backlog-wave3 (tem issue própria #570).

## 3. Technical Context

**Files to read before implementing:**
- `.specs/SPEC-20261007-knowledge-review.md` (fonte da verdade do feature)
- `src/KnowledgeHub.Review.Cli/Program.cs` (subcomandos reais)
- `.github/workflows/knowledge-review.yml`, `knowledge-review-reusable.yml`

**Files to modify:**
```text
CLAUDE.md
README.md
```

## 4. Requirements

### RF-001: CLAUDE.md menciona o Knowledge Review
- **Description:** O catálogo de features do CLAUDE.md inclui o agente de review automatizado e onde ele roda (GitHub Actions + CLI).
- **Rules:** estilo do arquivo atual (prosa densa, sem seção nova se o padrão for parágrafo).

### RF-002: README documenta uso básico — COBERTO
- **Description:** ~~Seção de uso no README~~ — já coberto: README.md:617 e
  README.pt-br.md:613 linkam `docs/{en,pt}/KNOWLEDGE-REVIEW.md` (uso completo
  documentado lá). Nenhuma ação necessária.

## 5. Acceptance Criteria

- [ ] **Given** o CLAUDE.md **when** `grep -i "knowledge-review\|Knowledge Review" CLAUDE.md` **then** ≥1 menção descritiva (não apenas link).
- [ ] **Given** os READMEs **when** verificados **then** nenhum cambio necessário (links para `docs/{en,pt}/KNOWLEDGE-REVIEW.md` já presentes — linhas 617/613).

## 6. Task Plan

- [ ] **T1** — Ler SPEC-20261007 + Program.cs + workflows.
- [ ] **T2** — Atualizar CLAUDE.md e README.md.
- [ ] **T3** — PR docs-only.
