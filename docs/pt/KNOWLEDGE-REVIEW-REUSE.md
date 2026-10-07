# Knowledge Review — reutilizando em outros repositórios

O `Knowledge Review` é distribuído como um **workflow reutilizável do
GitHub Actions** (`workflow_call`) hospedado neste repositório. Qualquer
repo adota o code review self-hosted com IA adicionando um workflow
chamador fino — sem copiar código nem vendorar o projeto.

## TL;DR — workflow chamador

Crie `.github/workflows/knowledge-review.yml` no repositório alvo:

```yaml
name: 🔍 Knowledge Review

permissions: {}

concurrency:
  group: knowledge-review-${{ github.event.pull_request.number || inputs.pr }}
  cancel-in-progress: true

on:
  pull_request:
    types: [opened, synchronize, reopened, ready_for_review]
  workflow_dispatch:
    inputs:
      pr:     { description: "PR number to review", required: true, type: number }
      dry_run: { description: "Plan only — no comments/status/merge",
                 required: false, type: boolean, default: true }

jobs:
  review:
    uses: afonsoft/KnowledgeRAG/.github/workflows/knowledge-review-reusable.yml@main
    with:
      pr: ${{ github.event.pull_request.number || inputs.pr }}
      dry_run: ${{ inputs.dry_run || vars.REVIEW_DRY_RUN == 'true' }}
      wait_for_signals: true
      cli_ref: main        # ref do KnowledgeRAG que fornece o CLI
      enable_pr_agent: ${{ vars.REVIEW_ENABLE_PR_AGENT == 'true' }}
    secrets:
      REVIEW_LLM_API_KEY: ${{ secrets.REVIEW_LLM_API_KEY }}
      REVIEW_LLM_ENDPOINT: ${{ vars.REVIEW_LLM_ENDPOINT }}
      REVIEW_LLM_MODEL: ${{ vars.REVIEW_LLM_MODEL }}
      KNOWLEDGE_REVIEW_TOKEN: ${{ secrets.KNOWLEDGE_REVIEW_TOKEN }}
      KNOWLEDGE_HUB_URL: ${{ vars.KNOWLEDGE_HUB_URL }}
      KNOWLEDGE_HUB_API_KEY: ${{ secrets.KNOWLEDGE_HUB_API_KEY }}
```

Pronto — todo PR aberto na branch padrão recebe o pipeline
collect → review → gate → publish.

## Configuração

A configuração mora **no repositório chamador** (o GitHub resolve
`secrets:`/`vars:` no contexto do chamador, não no KnowledgeRAG).

### Secrets (Settings → Secrets and variables → Actions → Secrets)

| Secret | Obrigatório | Função |
|---|---|---|
| `REVIEW_LLM_API_KEY` | recomendado | chave da API do modelo de review (OpenAI-compatible). Ausente → gate `llm-missing` → modo somente-comentário, nunca aprova. |
| `KNOWLEDGE_REVIEW_TOKEN` | opcional | PAT (scope `repo`) que **substitui o `GITHUB_TOKEN`**. Necessário para ler a branch protection (o token padrão recebe HTTP 403) e publicar com identidade estável. Sem ele, o conjunto de checks obrigatórios degrada para "todo check conta" — conservador, correto, só mais estrito. |
| `KNOWLEDGE_HUB_API_KEY` | opcional | chave `aft_*` gerada em `/api-keys` no hub — habilita convenções via `search_knowledge` + persistência via `write_knowledge`. |
| `KNOWLEDGE_HUB_URL` | opcional | URL base do hub (pode ser var — não é secreta). |

### Variables (aba Variables)

| Variable | Default | Efeito |
|---|---|---|
| `REVIEW_LLM_ENDPOINT` | — | URL base OpenAI-compatible, ex.: `https://omniroute.example/v1` |
| `REVIEW_LLM_MODEL` | — | id do modelo usado nas passadas de review |
| `REVIEW_LANGUAGE` | `pt-BR` | idioma do comentário-resumo (`pt-BR`/`en-US`; `language:` do `REVIEW.md` vence) |
| `REVIEW_SKIP_LABEL` | `no-review` | label que faz o PR pular o review |
| `REVIEW_MAX_DIFF_KB` | `256` | teto do diff — acima dele o review roda em modo resumo |
| `REVIEW_MIN_CONFIDENCE` | `0.6` | findings abaixo disso são descartados |
| `REVIEW_PASSES` | `1` | passadas do LLM (1–4) + consenso ⌈passes/2⌉ |
| `REVIEW_SIGNAL_TIMEOUT_MIN` | `15` | quanto o collect espera checks obrigatórios/bots |
| `REVIEW_MERGE_METHOD` | `SQUASH` | `SQUASH`\|`MERGE`\|`REBASE` no auto-merge |
| `REVIEW_BOT_AUTHORS` | ver defaults | CSV de logins de bots extras cujos comentários são ingeridos |
| `REVIEW_WRITE_KNOWLEDGE` | `true` | persiste o review no hub como `review/{repo}/pr-{N}` |
| `REVIEW_DRY_RUN` | `false` | `true` → só plano, zero mutações |
| `REVIEW_ENABLE_PR_AGENT` | `false` | `true` → roda o pr-agent antes; comentários entram como sinais de bot |

### Settings do repo

- **Allow auto-merge** (Settings → General) — necessário pro passo
  `enablePullRequestAutoMerge`; sem ele, a aprovação continua publicada.
- **Workflow permissions** (Settings → Actions → General): o job
  reutilizável já declara `pull-requests:write`, `issues:write`,
  `checks:read`, `statuses:write`, `contents:read` — basta o
  `GITHUB_TOKEN` padrão não estar como *read-only* no repo.

## O que aparece no PR

1. Commit status `knowledge-review/verdict` (pending → success/failure).
2. Comentário-resumo idempotente `<!-- knowledge-review -->` (atualizado, nunca duplicado).
3. PR review com comentários inline (`APPROVE` ou `COMMENT` — nunca
   `REQUEST_CHANGES` automático).
4. Auto-merge habilitado quando o veredito aprova e o repo permite.
5. Review persistido no hub como `review/{owner}/{repo}/pr-{N}` quando
   os secrets do hub existem.
6. Artefatos `signal.json` + `run.json` (14d) no run do workflow.

## workflow_dispatch (execução manual / dry-run)

Actions → **Knowledge Review** → *Run workflow*: informe `pr`
(obrigatório) e `dry_run` (default **true**) para ver o plano completo —
status, resumo, evento de review, comentários inline, auto-merge — sem
nenhuma mutação.

## Pinning e atualizações

- `@main` segue o CLI mais recente; use `@<tag>` ou `@<sha>` na linha
  `uses:` (e/ou `cli_ref`) para fixar.
- O `REVIEW.md` → `AGENTS.md`/`CLAUDE.md` do repo alimenta o prompt.
- Alternativa por flow: `docs/flows/knowledge-review.flow.json` importa
  na UI `/flows` do hub (modo JSON) como pipeline nativo do hub em vez
  de GitHub Actions.

## Troubleshooting

| Sintoma | Causa | Correção |
|---|---|---|
| Comenta mas nunca faz auto-merge | veredito achou findings, ou `Allow auto-merge` desligado | ative a setting do repo; veja os motivos no resumo |
| Status preso em `pending`, veredito `Inconclusive` | checks obrigatórios ainda rodando | aguarde ou aumente `REVIEW_SIGNAL_TIMEOUT_MIN` |
| Checks obrigatórios tratados como "todos" | `GITHUB_TOKEN` padrão não lê branch protection (403) | crie o PAT `KNOWLEDGE_REVIEW_TOKEN` |
| `_hub unreachable_` no comentário | URL/key do hub ausente ou fora | configure `KNOWLEDGE_HUB_URL` + `KNOWLEDGE_HUB_API_KEY`, ou aceite o modo local |
| `_summary mode_` no comentário | diff acima de `REVIEW_MAX_DIFF_KB` | aumente o teto ou quebre o PR |
