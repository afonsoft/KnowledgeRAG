# SPEC-20261008-sonarqube-backlog-wave3 — SonarCloud backlog wave 3 (residual 9 issues)

| Campo | Valor |
|-------|-------|
| Feature | `sonarqube-backlog-wave3` |
| Type | `Refactor` |
| Stack | `.NET 10 / C#` (Review.Cli + Server + Client) |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/devin-20261008-sonar-wave3` |
| Ticket | Epic [#570](https://github.com/afonsoft/KnowledgeRAG/issues/570) |
| Status | `Done` — mergeado via PR [#576](https://github.com/afonsoft/KnowledgeRAG/pull/576) (`192a417`); Sonar backlog 9→0 |
| Origem | Skill sonarqube-autofix (sessão 2026-10-08) |
| Pré-requisito | SPEC-20261002-sonarqube-backlog-wave2 (Done) |

## Contexto

Re-download do backlog `afonsoft_LangGraph-UI` após os merges #568/#569
(knowledge-review CLI): **9 issues OPEN, todas CODE_SMELL** (3 CRITICAL,
5 MAJOR, 1 MINOR; esforço Sonar 101 min). Sete delas foram introduzidas pelo
novo projeto `src/KnowledgeHub.Review.Cli` (#568); as outras 2 são resíduos em
`MainLayout.razor` e `FlowEngine.cs` que sobreviveram às ondas 1 e 2.

Detalhe por issue em `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`
(board com chaves, linhas e mensagens integrais).

## Requisitos funcionais

### RF-01 — S927: nomes de parâmetro coerentes com a interface (2 issues, CRITICAL)

- [ ] `src/KnowledgeHub.Review.Cli/GitHub/OctokitGitHubApi.cs:157` —
      `SubmitReviewAsync(..., ReviewRequest request, ...)` → renomear parâmetro
      para `review` (a interface `IGitHubApi` declara `ReviewRequest review`).
      Colisão com a variável local `review` (`PullRequestReviewCreate`) →
      renomear a local para `create`.
- [ ] `src/KnowledgeHub.Review.Cli/GitHub/OctokitGitHubApi.cs:175` —
      `CreateStatusAsync(..., StatusRequest request, ...)` → renomear parâmetro
      para `status` (interface declara `StatusRequest status`). Colisão com a
      local `status` (`NewCommitStatus`) → renomear a local para `newStatus`.

Behavior-preserving; nenhuma chamada externa afetada (nomes de parâmetro não
fazem parte da assinatura runtime, apenas de interface/leitura).

### RF-02 — S3358: ternários aninhados extraídos (2 issues, MAJOR)

- [ ] `src/KnowledgeHub.Review.Cli/Output/SummaryCommentRenderer.cs:78` —
      `var loc = f.File is { } file ? $"`{file}`{(f.Line is { } l ? $":{l}" : "")}" : "_repo-level_";`
      → extrair o ternário interno para statement independente (ou if/else),
      preservando a saída exata: `` `path` `` / `` `path`:line `` / `_repo-level_`.
- [ ] `src/KnowledgeHub.Review.Cli/Review/FindingClassifier.cs:51` —
      `var verdict = findings.Any(f => f.BlocksMerge) ? "request_changes" : findings.Count > 0 ? VerdictComment : "approved";`
      → extrair para if/else encadeado com a mesma precedência:
      `request_changes` > `VerdictComment` (quando há findings) > `approved`.

### RF-03 — S107: `LoadSignalAsync` com 9 parâmetros (1 issue, MAJOR)

- [ ] `src/KnowledgeHub.Review.Cli/CliRuntime.cs:56` — agrupar as options
      (`inputOption`, `repoOption`, `prOption`, `waitOption`, `dryRunOption`,
      `reasoningOption`) em um record (ex.: `readonly record CliOptionsBundle(...)`)
      ou em um parâmetro de contexto, reduzindo a assinatura para ≤7 parâmetros.
      Atualizar os 2 call sites (`Program.cs` review/gate). Comportamento idêntico.

### RF-04 — S3776: complexidade cognitiva do Program.cs top-level (1 issue, CRITICAL)

- [ ] `src/KnowledgeHub.Review.Cli/Program.cs` — complexidade 17 > 15. Extrair
      os 4 lambdas `SetAction` (collect/review/gate/run) para métodos estáticos
      em classe separada (ex.: `CliCommands`), deixando o top-level apenas com
      wiring de commands. Exit codes e output JSON idênticos.

### RF-05 — S1075: delimiter de path hardcoded (1 issue, MINOR)

- [ ] `src/KnowledgeHub.Review.Cli/Review/OpenAiCompatChatClient.cs:27` —
      `_http.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");` → construir via
      APIs de `Uri` sem literal `"/"` concatenado, preservando a semântica de
      trailing-slash (BaseAddress terminando em `/` para resolução relativa de
      `v1/chat/completions`). Referência: o `OpenAiChatClient` do Server já
      resolve o mesmo problema.

### RF-06 — S108: catch block vazio (1 issue, MAJOR)

- [ ] `src/KnowledgeHub.Client/Layout/MainLayout.razor:183` —
      `catch (TaskCanceledException) { }` → preencher com comentário
      justificado (padrão da linha 182: `/* timeout durante dispose — esperado */`)
      ou fundir com o catch de `JSDisconnectedException` via filter
      `when (ex is JSDisconnectedException or TaskCanceledException)`.

### RF-07 — S1994: loop de retry sem condição de parada testável (1 issue, CRITICAL)

- [ ] `src/KnowledgeHub.Server/Flows/FlowEngine.cs:297` —
      `for (var attempt = 1; ; attempt++)` → tornar a condição explícita
      (`attempt <= attempts`) com `throw` defensivo inalcançável após o loop,
      preservando a semântica exata: `FlowSuspendException` passa direto;
      falha em `attempt >= attempts` re-propaga; backoff entre tentativas.
      Adicionar/regressar teste unitário de contagem de tentativas
      (`ExecuteWithRetryAsync` via `FlowEngine` — cobrir 1 tentativa, N tentativas
      e esgotamento).

## Fora de escopo

- Qualquer refatoração além dos sites flagueados.
- Alteração em `.github/workflows/` (protegido).
- Mudança de comportamento público da CLI (exit codes, schema de signal.json).

## Critérios de aceite

- [ ] **Given** o build Release **when** `dotnet build KnowledgeHub.slnx` **then** compila sem warnings novos.
- [ ] **Given** a suíte existente **when** `dotnet test` **then** verde (inclusive regressão do RF-07).
- [ ] **Given** o SonarCloud scan pós-merge **when** issues pesquisadas **then** nenhuma das 9 chaves listadas no board permanece OPEN.
- [ ] **Given** cada RF **when** revisado **then** behavior-preserving (sem mudança de saída/exit code).

## Task Plan

- [ ] **T1** — Ler arquivos da seção Contexto + board.
- [ ] **T2** — Implementar RF-01..RF-07 (ordem de severidade: RF-07 → RF-01 → RF-04 → RF-02 → RF-03 → RF-06 → RF-05).
- [ ] **T3** — `dotnet build` + `dotnet test` (lembrar `env -u Database__Provider` na VM).
- [ ] **T4** — `dotnet format KnowledgeHub.slnx --verify-no-changes`.
- [ ] **T5** — Commit convencional + PR referenciando o Epic.

## Guardrails

- Branch `feature/devin-20261008-sonar-wave3` — nunca commitar em `main`.
- Refactor: suíte completa deve passar; cobertura não pode cair; sem mudança
  comportamental sem teste novo.
