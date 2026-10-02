# SPEC-20261002-merged-branch-recleanup — stale remote branches (recurring)

| Campo | Valor |
|-------|-------|
| Status | `Approved` |
| Ticket | Epic [#503 (E27)](https://github.com/afonsoft/LangGraph-UI/issues/503) — slice [#506](https://github.com/afonsoft/LangGraph-UI/issues/506) (RF-001..002) |
| Origem | gap-analysis — `git branch -r --no-merged origin/main` |
| Pré-requisito | SPEC-20260917-merged-branch-cleanup + SPEC-20260917-stale-branch-cleanup (Done) — a acumulação recorreu |

## Contexto

~30 branches remotas órfãs acumularam pós-merge (squash merges → `git` as
reporta "unmerged" porque o tip não é ancestral de main; todas correspondem a
PRs já mergeados):

- `origin/devin/*` ×14 (s3776-*, e25-*, specs-done, architecture-a2a)
- `origin/docs/*` ×6 (e26-*, gap-analysis-20261002, spec-*-done, specs-tickets-*)
- `origin/feature/Devin-20261001-*` ×9 (a2a-*, e23-*, e24-*, quality-tests, sonar-autofix)
- `origin/chore/Devin-20261001-skills-sync` ×1
- Local: `feature/Devin-20261001-a2a-task-durability` (PR já mergeado)

As SPECs de cleanup anteriores foram executadas uma vez, mas nada impede a
reacumulação — branches `devin/` e `docs/` são criadas por automação a cada
sessão.

## Requisitos funcionais

### RF-001 — Deleção em massa (one-shot)

- [ ] Confirmar cada branch contra `gh pr list --state merged --head <branch>`;
      deletar remotamente só as que têm PR mergeado correspondente
      (`git push origin --delete <branch>`). Qualquer branch sem PR mergeado →
      listar e perguntar antes de deletar.
- [ ] Deletar local `feature/Devin-20261001-a2a-task-durability` (mergeada via
      PR #445).
- [ ] `git fetch --prune` ao final; `git branch -r` limpo exceto `main`/`HEAD`.

### RF-002 — Decisão de automação (evitar reacumulação)

- [ ] Escolher e implementar UMA opção:
  - (a) GitHub setting "Automatically delete head branches" no merge de PR —
        zero código, nativo (verificar se já está on e por que não pegou
        `devin/*` — provavelmente because branches são criadas pelo app Devin
        fora do fluxo de PR… investigar).
  - (b) Job agendado (cron semanal) no CI que deleta branches com PR mergeado
        há >7 dias — reusa o critério de RF-001.
- [ ] Registrar a decisão na SPEC e no `CLAUDE.md` seção Convenções.

## Critérios de aceite

- `git branch -r --no-merged origin/main` retorna vazio (ou só branches com
  PR ainda aberto — hoje: nenhum).
- Mecanismo de prevenção documentado e ativo (setting ou workflow).

## Fora de escopo

- Tags/releases antigas (não há evidência de acúmulo).
- Proteção de `main`/`develop` (já coberta por branch protection).
