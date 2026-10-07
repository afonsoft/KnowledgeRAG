# Short-term memory — session state (overwritten each session, ≤100 lines)

- **Branch ativa**: `feature/devin-20261007-knowledge-review` (pushed) — base `main` atualizada.
- **SPEC-20261007-knowledge-review**: `Approved`, Ticket [#567](https://github.com/afonsoft/KnowledgeRAG/issues/567) (labels `feature`+`todo`).
- **Decisões travadas**: `src/KnowledgeHub.Review.Cli` no slnx · motor híbrido (gates determinísticos + `IChatClient`; `--reasoning=hub` via `agent_chat`) · `APPROVE`+`enablePullRequestAutoMerge` · triggers `pull_request`+`workflow_dispatch` · stacked→diff por camada · pr-agent opcional · `REVIEW_LANGUAGE` default pt-BR · genérico via `GITHUB_REPOSITORY`.
- **Bloqueio conhecido**: `.github/workflows/` protegido — `knowledge-review.yml` entregue no PR, commit final pelo owner.
- **Baseline**: build 0 warnings · 1129+ unit · 306+ integration verdes (carry-over).
- **Deploy produção**: `knowledgehub` container healthy em `0.0.0.0:5550->8080`, `rag.afonsoft.dev` OK. `.env`: `DATABASE_PROVIDER=postgres`, `CACHE_PROVIDER=redis` (DB3).
- **Pendências conhecidas**: Cloudflare bloqueia `/.well-known/agent-card.json` na edge (ação do usuário); `.env` `required:false` = deleção silenciosa.

## Session summary (2026-10-07 — SPEC knowledge-review)

- Pedido: CLI .NET console p/ GitHub Action que replica Devin Review — lê PRs, ingere comentários de bots (Devin/Sonar/CodeQL), LLM decide SOLID/Clean Arch, approve+auto-merge ou comentário detalhado, persistência via MCP do hub.
- Pesquisa: docs devin-review + stacked-prs mapeadas (bug catcher, security CWE, REVIEW.md, auto-merge, stack por camada); repo reusa `KnowledgeHub.Sdk` (MCP facade) + `IChatClient`.
- Entregue: `.specs/SPEC-20261007-knowledge-review.md` (9 RFs), Issue #567, branch pushed (commits `24b1fd9`, `de81315`).
- Próximo passo: `/execute-specs` na SPEC-20261007 ou abertura de PR com a SPEC.
