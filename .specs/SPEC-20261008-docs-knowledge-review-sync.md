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
| Status | `Approved` |

## 1. User Story

**Como** maintainer do KnowledgeHub,
**quero** que o CLAUDE.md e o README documentem o agente Knowledge Review entregue,
**para que** agentes e contribuidores descubram a CLI `KnowledgeHub.Review.Cli`, seus subcomandos e os workflows do GitHub associados sem ler o código.

**Problem context:** O feature foi entregue e mergeado (PRs #568/#569, SPEC-20261007 `Done`), com workflows `.github/workflows/knowledge-review.yml` e `knowledge-review-reusable.yml` ativos — mas `grep -c "knowledge-review\|Review.Cli" CLAUDE.md README.md` retorna **0** nos dois arquivos. O CLAUDE.md é o catálogo canônico de features (convenção do repo: toda feature entregue aparece lá — ver SPEC-20260918-claude-md-feature-sync e SPEC-20260917-readme-feature-sync).

## 2. Scope

**In scope:**
- Parágrafo/entrada em `CLAUDE.md` descrevendo o Knowledge Review (CLI self-hosted, subcomandos `collect/review/gate/run`, workflows, requisito de LLM via `IChatClient`).
- Seção em `README.md` (pt/en conforme estrutura atual) com uso básico: `dotnet run --project src/KnowledgeHub.Review.Cli -- run --repo owner/repo --pr N`.

**Out of scope:**
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

### RF-002: README documenta uso básico
- **Description:** O README traz comando de execução e pré-requisitos (token GitHub, endpoint LLM compatível OpenAI).

## 5. Acceptance Criteria

- [ ] **Given** o CLAUDE.md **when** `grep -i "knowledge-review" CLAUDE.md` **then** ≥1 menção descritiva (não apenas link).
- [ ] **Given** o README **when** `grep -i "review.cli\|knowledge-review" README.md` **then** seção com comando de uso.
- [ ] **Given** a doc **when** conferida contra `Program.cs` **then** subcomandos e flags citados existem de fato.

## 6. Task Plan

- [ ] **T1** — Ler SPEC-20261007 + Program.cs + workflows.
- [ ] **T2** — Atualizar CLAUDE.md e README.md.
- [ ] **T3** — PR docs-only.
