# Knowledge Review

Code review com IA self-hosted para PRs no GitHub — um CLI console .NET 10 (`src/KnowledgeHub.Review.Cli`) que replica o fluxo do Devin Review dentro das suas próprias GitHub Actions: coleta todos os sinais de review já presentes no PR, analisa o diff com LLM contra as convenções do repositório, publica um veredito no estilo Devin Review e persiste cada revisão como conhecimento no Knowledge MCP Hub.

## Pipeline

```
collect → review → gate → publish          (`run` encadeia todos os estágios)
```

| Estágio | O que faz |
|---|---|
| `collect` | Monta o `PullRequestSignal` (schema v1): metadados do PR, diff por arquivo, check-runs + commit statuses, comentários de bots (`*[bot]` + allowlist), annotations de check-runs, reviews humanos, cadeia de stacked PRs. |
| `review` | Analisa o diff via `IChatClient` em chunks limitados por `REVIEW_MAX_DIFF_KB`, guiado por `REVIEW.md`/`AGENTS.md`/`CLAUDE.md` + convenções via `search_knowledge` no hub. Findings: `{kind: bug\|style\|security\|flag, severity, file, line, cwe?, confidence, rationale, suggestion}`. |
| `gate` | Vetos determinísticos — draft, label `knowledge-review:skip`, `CHANGES_REQUESTED` humano, required checks falhando → `block`; required checks pendentes → `inconclusive`; PR de fork e camada de stack falhando → `block` (comment-only). |
| `publish` | Commit status `knowledge-review/verdict` + comentário-resumo idempotente (marker `<!-- knowledge-review -->`) + PR review com inline comments (`APPROVE` quando limpo, `COMMENT` caso contrário — nunca `REQUEST_CHANGES` automático) + `enablePullRequestAutoMerge` (SQUASH por padrão) + `write_knowledge` no hub. |

## Uso

```bash
dotnet run --project src/KnowledgeHub.Review.Cli -- <comando> [opções]

knowledge-review collect --repo owner/repo --pr 123 [--wait-for-signals]   # → signal.json no stdout
knowledge-review review  --repo owner/repo --pr 123 [--input signal.json]  # → findings + veredito
knowledge-review gate    --repo owner/repo --pr 123                        # → outcome do gate (exit 0/2)
knowledge-review run     --repo owner/repo --pr 123 --wait-for-signals     # pipeline completo
```

Opções: `--repo`, `--pr`, `--input <signal.json>` (reexecuta um sinal já coletado), `--wait-for-signals` (faz poll dos required checks até `REVIEW_SIGNAL_TIMEOUT_MIN`), `--reasoning local|hub` (`hub` delega a análise ao `agent_chat`), `--dry-run` (imprime as mutações planejadas sem alterar nada).

## Configuração (variáveis de ambiente)

| Variável | Default | Finalidade |
|---|---|---|
| `GITHUB_TOKEN` | — | Auth do GitHub (Actions injeta) |
| `REVIEW_LLM_ENDPOINT` / `REVIEW_LLM_MODEL` / `REVIEW_LLM_API_KEY` | — | Endpoint de chat OpenAI-compatible; ausente → modo comment-only |
| `KNOWLEDGE_HUB_URL` / `KNOWLEDGE_HUB_API_KEY` | — | Endpoint MCP do hub + chave `aft_*`; ausente → modo local (aviso no resumo) |
| `REVIEW_REASONING` | `local` | `local` = `IChatClient` em chunks; `hub` = `agent_chat` |
| `REVIEW_LANGUAGE` | `pt-BR` | Idioma dos findings + resumo (linha `language:` do `REVIEW.md` tem prioridade) |
| `REVIEW_PASSES` | `1` | Passadas do LLM por chunk (1–4); finding sobrevive quando visto em ≥⌈passes/2⌉ passadas |
| `REVIEW_MIN_CONFIDENCE` | `0.6` | Confiança mínima para manter um finding |
| `REVIEW_MAX_DIFF_KB` | `256` | Orçamento por chunk; >4× o orçamento → modo resumo |
| `REVIEW_SIGNAL_TIMEOUT_MIN` | `15` | Timeout do `--wait-for-signals`; expirou → `partial=true` |
| `REVIEW_MERGE_METHOD` | `SQUASH` | Método do `enablePullRequestAutoMerge` |
| `REVIEW_SKIP_LABEL` | `knowledge-review:skip` | Label que pula o review |
| `REVIEW_STATUS_CONTEXT` | `knowledge-review/verdict` | Contexto do commit status |
| `REVIEW_BOT_AUTHORS` | lista interna | Logins de bots extras ingeridos (separados por vírgula) |
| `REVIEW_DRY_RUN` | `false` | Sem mutações — plano impresso no stdout |
| `REVIEW_NO_KNOWLEDGE` | `false` | Pula o `write_knowledge` |
| `REVIEW_ENABLE_PR_AGENT` | `false` | Var do workflow: roda `the-pr-agent/pr-agent` antes (seus comentários entram na ingestão de bots) |

## GitHub Actions

`.github/workflows/knowledge-review.yml` roda em `pull_request` (opened/synchronize/reopened/ready_for_review) e `workflow_dispatch` (com input `pr`). Permissões do token: `contents:read`, `pull-requests:write`, `issues:write`, `checks:read`, `statuses:write` — auto-merge exige a config **Allow auto-merge** habilitada no repo.

Secrets obrigatórios: `REVIEW_LLM_API_KEY`. Opcionais: `KNOWLEDGE_HUB_URL`/`KNOWLEDGE_HUB_API_KEY` (vars/secret), vars `REVIEW_*` para ajuste.

## Regra rígida contra prompt injection

O system prompt trata título, descrição, mensagens de commit e cada linha do diff do PR como **dados não confiáveis** — instruções embutidas neles nunca são seguidas (a postura "cético por padrão" somada a essa regra é o que impede um PR malicioso de convencer o revisor a aprová-lo).

## Taxonomia dos findings

| Kind | Severidades | Impacto no merge |
|---|---|---|
| `bug` | `severe`, `non-severe` | `severe` bloqueia aprovação + auto-merge |
| `security` | `critical`, `warning` | `critical` bloqueia; CWE marcado quando aplicável |
| `style` | `info`, `warning` | nunca bloqueia |
| `flag` | `investigate`, `info` | nunca bloqueia — pede revisão humana |

## Loop de conhecimento

Antes da análise, `search_knowledge` traz convenções do repo para o prompt. Após a publicação, `write_knowledge` grava `review/{owner}/{repo}/pr-{N}` com veredito, findings e justificativas — findings recorrentes viram conhecimento buscável para o próximo review.

## Import de fluxo

`docs/flows/knowledge-review.flow.json` é um fluxo pronto do Knowledge Hub (cole em `/flows` → visão JSON): `search_knowledge` → fetch do PR via GitHub REST (`secretRef:"github"`) → review LLM cético → gate de aprovação humana em findings severe/critical → comentário-resumo opcional → `write_knowledge` → output.
