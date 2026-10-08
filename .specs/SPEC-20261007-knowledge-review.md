# SPEC-20261007-knowledge-review

## 0. Metadata

| Campo | Valor |
|-------|-------|
| Feature | `knowledge-review` |
| Type | `Feature` |
| Stack | `.NET 10 console CLI + GitHub Actions` |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/devin-20261007-knowledge-review` |
| Ticket | [#567](https://github.com/afonsoft/KnowledgeRAG/issues/567) |
| Status | `Done` |

## 1. User Story

**Como** maintainer do KnowledgeHub (e de outros repos .NET),
**quero** um agente de code review automatizado — "Knowledge Review" — que rode em GitHub Actions, colete todos os sinais dos bots de review existentes (Devin Review, SonarCloud, CodeQL, check-runs), analise o diff com LLM contra padrões SOLID/Clean Architecture/convenções do repositório e tome a decisão de aprovar+auto-mergear ou comentar o que precisa melhorar,
**para que** o gargalo de revisão de PRs gerados por agentes/humanos seja eliminado sem perder qualidade, e cada revisão alimente o Knowledge MCP Hub como conhecimento reutilizável.

**Problem context:**
Hoje o fluxo depende de revisão manual ou do Devin Review (SaaS pago) — cada finding (bugs, flags, security) vive espalhado em comentários de vários bots e não há decisão consolidada nem memória entre reviews. O Devin Review (docs: smart diff organization, bug catcher por severidade, security scan com CWE, REVIEW.md, auto-merge, stacked PRs) é a referência de UX a replicar como CLI self-hosted. O repo já possui `sdks/dotnet/KnowledgeHub.Sdk` (facade MCP: `SearchAsync`/`AskAsync`/`AgentChatAsync`/`WriteKnowledgeAsync`/`AsAIToolsAsync`), `IChatClient` (M.E.AI) como abstração de LLM e CI com CodeQL + SonarCloud — todos reutilizáveis.

## 2. Scope

**In scope:**
- Novo projeto console `src/KnowledgeHub.Review.Cli` (.NET 10) na `KnowledgeHub.slnx`, publicável como single-file/self-contained e executável `dotnet run`/`dotnet tool`.
- Subcomandos: `collect` (sinais), `review` (análise LLM + comentários), `gate` (veredito + approve/auto-merge), `run` (pipeline completo = collect→review→gate).
- Ingestão de sinais do GitHub via Octokit.NET (REST + GraphQL): PR metadata, diff por arquivo, check-runs/commit statuses, review comments, issue comments de autores `*[bot]` (Devin Review, SonarCloud, CodeQL/github-advanced-security, CodeRabbit, etc.), annotations, reviews humanos pendentes (`CHANGES_REQUESTED`).
- Detecção de stacked PR (base.ref = head branch de outro PR aberto) → diff por camada (`base..head` do próprio PR).
- Motor híbrido de decisão: gates determinísticos (checks verdes, sem CHANGES_REQUESTED humano, não-draft, sem label skip) + análise LLM do diff via `IChatClient` (OpenAI-compatible/Ollama) classificando findings Devin-style; `agent_chat` do hub como modo alternativo (`--reasoning=hub`).
- Persistência de conhecimento via MCP do hub: `search_knowledge` antes do review (convenções/padrões) e `write_knowledge` após (`review/{repo}/pr-{N}` com veredito + findings) — API key `aft_*` por segredo de Actions.
- Comentários estilo Devin Review: comentário-resumo no PR + review com inline comments por arquivo/linha categorizados (Bug severe/non-severe, Flag investigate/info, Security critical/warning com CWE) + commit status check `knowledge-review/verdict`.
- Workflow `.github/workflows/knowledge-review.yml` com triggers `pull_request` + `workflow_dispatch` (commit via PAT do owner — dir protegido).
- Idioma configurável (`REVIEW_LANGUAGE`, default `pt-BR`), ingestão de `REVIEW.md`/`AGENTS.md`/`CLAUDE.md` como instruções de review (espelhando Devin), `--dry-run`/`REVIEW_DRY_RUN` desativando mutações.
- Skip rules: draft PR, label `knowledge-review:skip`, autor externo sem permissão, diff acima de `REVIEW_MAX_DIFF_KB` (modo resumo somente).
- Suporte a pr-agent (qodo-merge) como step opcional via flag `REVIEW_ENABLE_PR_AGENT`.

**Out of scope:**
- Auto-fix (gerar commits corrigindo findings) — fase 2, espelhando Devin Auto-Fix.
- UI/webapp de review (diff organizado, chat codebase-aware) — Devin Review é webapp; aqui a saída é comentários no PR + status check.
- Merge atômico de stack (stack merge do GitHub API) — v1 revisa por camada; merge continua manual/auto-merge por PR.
- Copy/move detection avançado de diff (Devin reorganiza diffs) — v1 usa o diff do GitHub (renames já vêm anotados).
- GitLab/Azure DevOps/Bitbucket — v1 GitHub apenas.
- Modificação de `.github/workflows/` via PR normal (protegido — ver seção 8).

## 3. Technical Context

**Onde a mudança acontece:**
Novo projeto console na solution, consumindo `KnowledgeHub.Sdk` (MCP) e `Microsoft.Extensions.AI` (LLM), e Octokit.NET para GitHub. Nenhum código existente é alterado exceto `KnowledgeHub.slnx` (novo `<Folder Name="/tools/">` ou entrada em `/src/`) e testes em `tests/KnowledgeHub.Tests.Unit`.

**Files to read before implementing:**
- `CLAUDE.md` (convenções, MCP tools, guardrails)
- `sdks/dotnet/KnowledgeHub.Sdk/KnowledgeHubClient.cs` (facade MCP reutilizada)
- `sdks/dotnet/KnowledgeHub.Sdk/Models.cs` (`HubToolResult`, `HubAgentResult`)
- `src/KnowledgeHub.Server/Agent/` (padrão de loop agente + IChatClient do hub)
- `.github/workflows/ci-build-test.yml`, `security-scan.yml` (nomes dos checks para gates)
- `tests/KnowledgeHub.Tests.Unit/` (convenção xUnit + fakes hand-rolled)

**Files to create or modify:**
```text
src/KnowledgeHub.Review.Cli/
  KnowledgeHub.Review.Cli.csproj        # net10.0, refs: KnowledgeHub.Sdk, Octokit, M.E.AI, System.CommandLine
  Program.cs                            # System.CommandLine: collect|review|gate|run
  GitHub/                               # GitHubSignalsCollector, PullRequestSignal, BotCommentParser, StackedPrDetector
  Review/                               # DiffAnalyzer (IChatClient), FindingClassifier, ReviewPromptBuilder, InstructionFileLoader (REVIEW.md…)
  Gate/                                 # DeterministicGates, VerdictEngine, MergeOrchestrator (approve + auto-merge GraphQL enablePullRequestAutoMerge)
  Knowledge/                            # HubKnowledgeBridge (search_knowledge/write_knowledge via KnowledgeHub.Sdk)
  Output/                               # SummaryCommentRenderer, InlineCommentMapper (position→diff line), StatusCheckPublisher
  appsettings.review.json               # defaults; env REVIEW_* sobrescreve
tests/KnowledgeHub.Tests.Unit/ReviewCli/ # parsers, gates, classifier, renderer, stacked-detector
.github/workflows/knowledge-review.yml  # NOVO — commit pelo owner (dir protegido)
KnowledgeHub.slnx                       # adiciona projeto
docs/en/KNOWLEDGE-REVIEW.md + docs/pt/KNOWLEDGE-REVIEW.md
```

## 4. Requirements

### RF-001: Coleta de sinais do PR (`collect`)
- **Description:** O CLI deve coletar, para um PR alvo (`--pr N` ou `GITHUB_REF`), metadados, diff por arquivo, check-runs + commit statuses, comentários de issue/review de autores `*[bot]`, annotations de check-runs (CodeQL/Sonar) e reviews humanos.
- **Rules:** autores bot casados por sufixo `[bot]` + allowlist configurável (`REVIEW_BOT_AUTHORS`); annotations classificadas por `check_run.name`; reviews humanos com `state=CHANGES_REQUESTED` mantidos em lista de bloqueio; saída JSON única `PullRequestSignal`.
- **Input → Output:** `gh env GITHUB_TOKEN + --pr 123` → `signal.json` (schema versionado).

### RF-002: Detecção de stacked PR
- **Description:** Se `base.ref` do PR for head branch de outro PR **aberto** no mesmo repo, o review deve marcar `isStacked`, computar diff apenas da camada (`{baseSha}..{headSha}` do próprio PR) e listar a cadeia na seção "Stack" do comentário-resumo.
- **Rules:** merge nunca é forçado em stacks — auto-merge habilitado por camada respeita retargeting nativo do GitHub; se a camada inferior tiver checks falhando, `stackBlocked=true`.

### RF-003: Gates determinísticos (`gate`)
- **Description:** Antes de qualquer decisão LLM de aprovação, gates determinísticos devem vetar: PR draft; label `knowledge-review:skip`; `CHANGES_REQUESTED` humano não resolvido; required checks em falha/pendentes além do timeout; ausência de secrets LLM (modo comment-only); `mergeStateStatus` não-mergeable.
- **Input → Output:** `signal.json` → `GateResult { verdict: blocked|proceed, reasons[] }`.

### RF-004: Análise LLM do diff (`review`)
- **Description:** `IChatClient` (endpoint/modelo via `REVIEW_LLM_ENDPOINT|MODEL|API_KEY`, OpenAI-compatible/Ollama) analisa o diff por arquivo em chunks, guiado por prompt que carrega `REVIEW.md`/`AGENTS.md`/`CLAUDE.md` (instruções, escopo por diretório), convenções recuperadas via `search_knowledge` no hub, e a stack (.NET 10/C# 14) — avaliando SOLID, Clean Architecture, segurança (categorias CWE do Devin: injection, auth, secrets, SSRF/path traversal, deserialization, validação, cripto fraca, transport/cookie, misconfig), performance e testes.
- **Rules:** findings classificados `{kind: bug|style|security|flag, severity: severe|non-severe|investigate|info|critical|warning, file, line, cwe?, confidence, rationale, suggestion}`; confiança mínima configurável (`REVIEW_MIN_CONFIDENCE`, default 0.6); consenso multi-pass `REVIEW_PASSES` (1–4, finding sobrevive em ≥⌈passes/2⌉ passadas); `--reasoning=hub` delega a `agent_chat` do hub; chunking por `REVIEW_MAX_DIFF_KB` (default 256KB) com map-reduce.
- **Input → Output:** `signal.json + instruções + conhecimento` → `findings.json`.

### RF-005: Publicação estilo Devin Review
- **Description:** Publicar no PR: (a) commit status `knowledge-review/verdict` (success/failure/pending); (b) comentário-resumo idempotente (marker `<!-- knowledge-review -->` — atualiza o existente) com seções Overview/Stack/Bugs/Flags/Security/Verdict; (c) `pull request review` com inline comments nos arquivos/linhas do diff, severidade no título, CWE quando aplicável, sugestão de fix; (d) evento `APPROVE` quando veredito aprovado, `COMMENT` quando findings existirem (nunca `REQUEST_CHANGES` automático — bloqueio fica no status check + auto-merge ausente).
- **Rules:** linguagem `REVIEW_LANGUAGE` (default `pt-BR`); `REVIEW.md` pode sobrescrever idioma; dry-run imprime tudo sem mutar.

### RF-006: Aprovação + auto-merge
- **Description:** Com `verdict=approved` e gates verdes: submeter review `APPROVE` e habilitar auto-merge via GraphQL `enablePullRequestAutoMerge` (método `SQUASH` ou configurável `REVIEW_MERGE_METHOD`), deixando o GitHub mergear quando os required checks passarem.
- **Rules:** nunca mergear diretamente; nunca habilitar auto-merge com findings `severe`/`critical` não resolvidos; registrar decisão + motivos no comentário-resumo e no `write_knowledge`.

### RF-007: Ponte de conhecimento (MCP hub)
- **Description:** Antes do review: `search_knowledge` ("convenções de review", "findings recorrentes do repo") alimenta o prompt. Depois: `write_knowledge` com título `review/{owner}/{repo}/pr-{N}` contendo veredito, findings, decisões e padrões detectados — fechando o loop "toda revisão vira conhecimento".
- **Rules:** hub via `KNOWLEDGE_HUB_URL` + `KNOWLEDGE_HUB_API_KEY` (`aft_*`); indisponibilidade do hub degrada para modo local sem falhar o gate (warning no comentário).

### RF-008: Agente externo opcional (pr-agent)
- **Description:** Flag `REVIEW_ENABLE_PR_AGENT=true` no workflow executa `the-pr-agent/pr-agent` como step anterior à coleta, cujos comentários entram na ingestão genérica de bots.
- **Rules:** opcional, off por padrão; falha do step não bloqueia a pipeline (continue-on-error).

### RF-009: Modo de espera de sinais
- **Description:** Como o trigger é `pull_request` (antes dos bots postarem), `run` deve suportar `--wait-for-signals` com timeout `REVIEW_SIGNAL_TIMEOUT_MIN` (default 15min) que faz poll de check-runs/comentários até conclusão ou timeout — depois decide com o que houver (registrando `partial=true`).

## 5. API Contract

Não expõe API. Consome:
- **GitHub REST v3** via Octokit (`pulls`, `check-runs`, `issues/comments`, `reviews`, `statuses`) — `Authorization: Bearer GITHUB_TOKEN` (permissões: `contents:read`, `pull-requests:write`, `checks:read`, `statuses:write`).
- **GitHub GraphQL** via Octokit — `enablePullRequestAutoMerge`, resolução de review threads.
- **KnowledgeHub MCP** (`/mcp`) via `KnowledgeHub.Sdk` — `Authorization: Bearer aft_*`: `search_knowledge`, `write_knowledge`, `write_note`, `agent_chat` (modo `--reasoning=hub`), `set_chat_settings`.
- **LLM endpoint** OpenAI-compatible (`POST /chat/completions`) via `IChatClient`.

## 6. Acceptance Criteria

- [ ] **Dado** um PR aberto não-draft com CI verde e sem findings **quando** `run` executa **então** publica comentário-resumo `verdict=approved`, review `APPROVE` e habilita auto-merge.
- [ ] **Dado** um PR com finding `bug/severe` ou `security/critical` **quando** `run` executa **então** publica review `COMMENT` com inline comments categorizados, status check `failure` e NÃO habilita auto-merge.
- [ ] **Dado** um PR draft ou com label `knowledge-review:skip` **quando** `run` executa **então** sai com `verdict=skipped` sem mutar nada.
- [ ] **Dado** um PR cuja base é head de outro PR aberto **quando** `run` executa **então** `isStacked=true`, o diff analisado é só da camada e o resumo lista a stack.
- [ ] **Dado** `REVIEW_DRY_RUN=true` **quando** `run` executa **então** nenhum comentário/status/merge é emitido e o payload planejado vai para o log/artifact.
- [ ] **Dado** hub MCP indisponível **quando** `run` executa **então** o review continua em modo local, com aviso `hub unreachable` no resumo.
- [ ] **Dado** re-execução no mesmo PR **quando** `run` executa **então** o comentário-resumo é atualizado (marker) e não duplicado.

**Edge cases:**

| Cenário | Input | Esperado |
|---|---|---|
| Diff gigante | >`REVIEW_MAX_DIFF_KB` | Modo resumo: overview + findings só nos arquivos de maior risco; sinalizado |
| Check pendente no timeout | required check `in_progress` após `REVIEW_SIGNAL_TIMEOUT_MIN` | `verdict=inconclusive`, status `pending`, sem approve/merge |
| Autor externo (fork) | PR de fork sem permissões | Comment-only; nunca auto-merge |
| Comentário humano pedindo mudanças | review `CHANGES_REQUESTED` | Gate bloqueia até resolvido |
| Secret LLM ausente | sem `REVIEW_LLM_API_KEY` | Gates + ingestão rodam; análise LLM skipada; modo comment-only |

## 7. Task Plan

- [x] **T1 — Discovery:** ler arquivos da seção 3; mapear schema Octokit para check-runs/reviews/annotations; confirmar nomes dos required checks (`Build KnowledgeHub (.NET 10)`, `CodeQL`, `SonarCloud Code Analysis`).
- [x] **T2 — Projeto + coleta:** criar `src/KnowledgeHub.Review.Cli` no slnx; implementar `collect` (RF-001) + `StackedPrDetector` (RF-002) com testes de parser.
- [x] **T3 — Gates + análise:** `DeterministicGates` (RF-003), `DiffAnalyzer`+`FindingClassifier`+`InstructionFileLoader` (RF-004) com testes unitários (LLM mockado via `IChatClient` fake).
- [x] **T4 — Publicação + merge:** renderers/mappers de comentários (RF-005), `enablePullRequestAutoMerge` (RF-006) com mocks GraphQL.
- [x] **T5 — Conhecimento + pr-agent:** `HubKnowledgeBridge` (RF-007), flag pr-agent (RF-008), wait-loop (RF-009).
- [x] **T6 — Workflow + docs:** YAML `knowledge-review.yml` (commit pelo owner — seção 8), `docs/{en,pt}/KNOWLEDGE-REVIEW.md`, README do projeto.
- [ ] **T7 — Validação:** `dotnet build`, `dotnet test` (≥80% no projeto novo), `dotnet format --verify-no-changes`, dry-run real contra um PR aberto do repo.
- [ ] **T8 — Done + PR:** DoD completo → `Status=Done` → PR `feature/devin-20261007-knowledge-review`.

**7.1 Validation (.NET):** unit tests para gates/parsers/classifier/renderer; integração leve com Octokit mockado; cobertura ≥80% no projeto novo.

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261007-knowledge-review`; nunca commitar em `main`/`develop`.
- **Workflows:** `.github/workflows/` é protegido — `knowledge-review.yml` foi commitado nesta branch via PAT do owner (scope `workflow`, enforce_admins=false); futuras alterações seguem a mesma via.
- **Secrets:** `KNOWLEDGE_HUB_API_KEY` (`aft_*`), `REVIEW_LLM_API_KEY` e PAT de fallback só via GitHub Secrets; nunca em logs (redaction `***`) nem commit.
- **Permissões do GITHUB_TOKEN:** mínimas — `contents:read`, `pull-requests:write`, `checks:read`, `statuses:write`, `issues:write`.
- **Escopo:** sem auto-fix, sem UI, sem providers além de GitHub (v1).
- **Arquitetura:** lógica de decisão fora de `Program.cs`; dependências via DI; LLM/GitHub/Hub atrás de interfaces testáveis.

## 9. Definition of Done

- [ ] RF-001..RF-009 implementados e testados (≥80% cobertura no projeto novo).
- [ ] Todos os critérios de aceite (seção 6) cobertos por testes ou dry-run evidenciado.
- [ ] `dotnet build` + `dotnet test` + `dotnet format --verify-no-changes` verdes.
- [ ] Comentário-resumo idempotente validado em PR real (dry-run → live).
- [ ] Nenhum segredo em código/logs; permissões do token mínimas.
- [ ] YAML do workflow + docs entregues; pendência do owner documentada no PR.

## Open Questions / Pending Ambiguity

- Método de merge default (`SQUASH` vs `MERGE`) — default proposto `SQUASH`, configurável via `REVIEW_MERGE_METHOD`.
- Required checks exatos por repo — resolvidos em runtime via branch protection do GitHub (não hardcoded).
