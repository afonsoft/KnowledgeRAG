# SPEC-20261008-hygiene-labels-branches

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | `hygiene-labels-branches` |
| Type | `Infra` |
| Stack | `Docs` (operação de repositório — sem código) |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | n/a (operação direta, sem commit de código) |
| Ticket | GAP-hygiene-issue-567-label + GAP-hygiene-stale-branches (gap-analysis 2026-10-08) |
| Status | `Done` — executado direto (ops de repo): label #567→done, branch a2a removida; issue #573 fechada |

## 1. User Story

**Como** maintainer do KnowledgeHub,
**quero** que os labels das issues e as branches reflitam o estado real,
**para que** o Label Contract do `/create-issues` seja confiável como fonte de status e o repo não acumule branches de PRs já mergeados.

**Problem context:**
1. A Issue **#567** (Knowledge Review) está `CLOSED` — trabalho entregue via PRs #568/#569 — mas o label permanece **`todo`**; pelo Label Contract deveria ser **`done`**. Recorrência do padrão já tratado pela SPEC-20261002-close-done-issues.
2. A branch remota **`origin/feature/devin-20261006-a2a-card-scheme`** (e a cópia local) ficou órfã: o PR #564 foi mergeado por squash (`bf48c40` em `main`), então a ancestry não marca `--merged`, mas o conteúdo está em `main`. A convenção do repo (CLAUDE.md, SPEC-20261002-merged-branch-recleanup) é deletar branches de PRs mergeados após confirmar o merge.

## 2. Scope

**In scope:**
- `gh issue edit 567 --remove-label todo --add-label done`.
- Deletar `origin/feature/devin-20261006-a2a-card-scheme` (remota) e a branch local homônima, após re-verificação de que o diff contra `main` está vazio por conteúdo (`git diff main...branch` vazio ou equivalente por cherry).

**Out of scope:**
- Qualquer outra branch ou issue.
- Mudança em configuração de repo (`delete_branch_on_merge` já ativo).

## 3. Acceptance Criteria

- [ ] **Given** a issue #567 **when** `gh issue view 567 --json labels` **then** contém `done` e não contém `todo`.
- [ ] **Given** a branch a2a-card-scheme **when** `git diff origin/main...origin/feature/devin-20261006-a2a-card-scheme` **then** vazio (conteúdo já em main) antes da deleção.
- [ ] **Given** a deleção **when** executada **then** `git branch -a | grep a2a-card-scheme` vazio.

## 4. Task Plan

- [ ] **T1** — Verificar diff vazio da branch vs main (evidência pré-deleção).
- [ ] **T2** — Corrigir label da #567.
- [ ] **T3** — Deletar branch remota + local com a evidência anexada.
