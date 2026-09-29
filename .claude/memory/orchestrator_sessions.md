# Orchestrator Sessions

Append-only session log. Most recent first. Read during Phase 0 to recover
context — see `.agents/skills/orchestrator/SKILL.md` Phase 8.

---

## Session — 2026-09-28 (orchestrator reconcile)

**Scope**: Resume orchestrator após a fila pentagi/Verba (#262–#281) —
reconciliação de issues abertas vs. código mergeado + Phase 7 final check.

**Decisions**:
- Fila aprovada 100% entregue: #263/#264/#262/#280/#281 já fechadas;
  #269–#279 estavam abertas com label `todo` apesar de mergeadas — fechadas
  com comentário pt-BR citando PR+commit e label `todo`→`done`.
- SPECs: 15× SPEC-20260927 = Done; stale `In implementation` em
  SPEC-20260926-pgvector-live-tests corrigido → `Done` (PR #237 `f228428`).
- `orchestrator_stats.md` estava congelado em 2026-09-19 — session block,
  Pending Tasks (TASK-024/025 stale), Backlog 8–13 e Metrics resincronizados.
- Framework afonsoft/skills up-to-date (`9958c42` = origin/main, fetch limpo).
- Branch `feature/Devin-20260928-temporal-episodic-graph` é tree-idêntica a
  origin/main (trabalho todo mergeado via squash); reconcile commitado em
  `feature/Devin-20260928-orchestrator-reconcile`.

**Delivered**: 11 issues fechadas (#269–#279) com evidência de merge;
SPEC status fix + stats sync em branch dedicada (PR pendente).

**Remaining**: PR do reconcile; `dotnet format --verify-no-changes` gate;
deletar branches mergeadas (temporal-episodic-graph e antigas).

**Lessons**:
- Squash-merge de stack faz `git log --grep='#N'` casar todas as issues do
  corpo — confirmar merge por `gh pr view --json mergeCommit` + ls-tree.
- Issues fechadas não voltam o label contract sozinhas — reconciliar
  `todo`→`done` explicitamente no fechamento.

## Session — 2026-09-19

**Scope**: SPEC-20260918-mcp-v2-hybrid-transport (#126) — MCP SDK v2 hybrid
transport + 5 Draft SPECs do gap-analysis-20260918.

**Decisions**:
- `SessionMode = StatefulForInitializeClients` (híbrido) atrás do knob
  `Mcp:SessionMode` — clients `initialize`/SSE mantêm sessão completa;
  `2026-07-28` servidos stateless no mesmo `/mcp` (sem downgrade -32022).
- SessionCallGate: bucket dedicado `_sessionless` (SemaphoreSlim próprio,
  não chave de dicionário) para calls sem sessão — antes era unbounded.
- Wire 2026-07-28 descoberto via SDK: exige headers `MCP-Protocol-Version`
  + `Mcp-Method` (+`Mcp-Name` p/ tools/call) E `params._meta`
  protocolVersion + clientCapabilities; `ping` removido em 2026-07-28.
- SDK rejeita `EnableLegacySse` em `Stateless` → SSE só habilitado para
  modos com sessão (fix descoberto no smoke de edge case).
- `Enum.TryParse` aceita numéricos ("0"→Stateless) — guard `char.IsLetter`.

**Delivered**: PR #127 mergeado `6a35d45` (feat `1d8edbc`); #126 fechada;
281 unit + 158 integration verdes; format 0; live curl smoke ambos os paths
+ Stateless startup. SPEC → Done. 6 SPECs novas commitadas em main
(`60a1e9b`/`cce159a` — push direto autorizado pelo owner, bypass registrado).
PR #125 (skills-lock) segue aberta.

**Remaining**: 5 Draft SPECs do gap-analysis aguardando aprovação (release
pipeline NETSDK1098 é prioridade alta); redeploy p/ :5550; prod smoke
`?access_token=` em rag.afonsoft.dev.

**Lessons**:
- Não assumir wire contract do protocolo novo — o SDK ensina via erros
  -32602/-32020 progressivos; escrever teste que captura o body primeiro.
- Testar `SessionMode=Stateless` no startup real pega incompatibilidades
  de options que unit tests não veem.

## Session — 2026-09-17 14:00

**Scope**: Epic #90 (gap-analysis-20260917) — execução dos 5 SPECs aprovados
(#91–#95) + fechamento da sessão anterior (PR #87 format gate).

**Decisions**:
- Skills migradas para layout canônico skills.sh: `.agents/skills/` real
  (gitignored) + `.claude/skills/*` symlinks + `skills-lock.json` pins.
- `main` protegida via GitHub: PR obrigatório + 5 status checks (Build, Unit,
  Integration, Blazor, Docker); `enforce_admins=false` = escape hatch do owner.
- Harness-files: usuário escolheu opção (a) — harness `.claude/` completo
  provisionado (settings.json, rules/, agents/, CONTEXT/RULES/MEMORY/TOOLS/
  WORKFLOWS/README, memory protocol).

**Delivered**:
- PRs mergeadas: #87 (`5c1f79b` format gate + SPEC sync), #88 (`cd662d0`
  skills update), #89 (`7f3c261` SPECs + report), #96 (`00f1c18` README sync),
  #97 (branch-protection doc). PR #98 (harness) aberta.
- Issues: #91, #92, #93, #95 fechadas; #94 fecha com merge da #98.
- Branches: 13 locais + 16 remotas mergeadas deletadas; 0 residuais merged.
- SPECs 20260917 ×5 marcados `Done` com evidência.

**Remaining**:
- Merge da PR #98 + fechar #94 e Epic #90.
- Backlog pending_approval: ONNX embeddings, sqlite-vec, McpProxy SourceType,
  tools passthrough, `install.sh --systemd` (host mutation), SSE E2E prod
  (credencial).
- 2 branches unmerged mantidas: `feature/Devin-20260915-rename-mcp-monitor`,
  `feature/Devin-20260916-specs-mobile-apikey-settings`.

**Lessons**:
- `gh pr merge --auto` exige auto-merge habilitado no repo — não está; mesclar
  manualmente após checks.
- `enforce_admins=false` → push direto do owner NÃO é bloqueado; verificar o
  gate via API, não via test push.
- `dotnet format` não aceita `--nologo`.
- Todo push reinicia CI — batchar mudanças num commit antes do merge.

## 2026-09-17 — Epic #106 feature SPECs

- Executadas 4/6 SPECs aprovadas via execute-specs: #110→PR#113, #109→PR#114, #107→PR#115, #108→PR#116. Todas mergeadas; issues fechadas com evidência.
- main @ 906a11a — 271 unit + 153 integration verdes, format gate 0.
- Re-deploy: `install.sh --docker` rebuildou imagem; container recriado via `docker compose up -d` (install.sh não propaga .env/volumes — registrado como limitação conhecida). Healthy em :5550.
- Bloqueados: #111 (host systemd+sudo), #112 (aft_* prod).
- Incidente: commit inicial da #108 incluiu models/*.onnx (90MB) porque a branch veio de main sem o .gitignore do ONNX — revertido via amend + force-push antes do merge.

## 2026-09-17 — Epic #106 closure (ops verification)

- #111 systemd verify: ran install.sh --host --systemd on this host (systemd 255, arm64). Found+fixed 2 bugs — missing WorkingDirectory (SPA 404 under systemd) and sudo_if_needed mkdir -p false-positive on root-owned prefix. PR #119.
- #112 SSE E2E prod: Bearer path fully verified on rag.afonsoft.dev (handshake→initialize→tools/list 42 tools→tools/call). Found ?access_token= 401 gap → fixed handler scope (+/mcp/sse) in PR #120, re-verified 200 post-deploy.
- Epic #106 closed — 6/6 slices. Prod redeployed via docker compose (image rebuilt from main).
- Note: install.sh --docker does not propagate .env/compose volumes — deploys on this host should use `docker compose up -d --build`.

## 2026-09-19 — MCP v2 hybrid + Epic #128 (gap-analysis SPECs) + release repair

- SPEC-20260918-mcp-v2-hybrid-transport: híbrido `StatefulForInitializeClients`
  + knob `Mcp:SessionMode` + bucket compartilhado no SessionCallGate. PR #127
  merged 6a35d45. QA achou crash Stateless+EnableLegacySse — corrigido.
- Epic #128 (5 SPECs aprovadas): #129 release-publish (PRs #135+#141+#142 —
  3 bugs no workflow: props globais NETSDK1098, gha cache sem buildx, assets
  soltos→rate limit), #130 install env passthrough (#137), #131 claude-md
  sync (#138), #132 orchestrator-state (#140), #133 compose chat example (#139).
- Bug UI #134: FontAwesome nunca carregado → ícones invisíveis. PR #136.
- Release repair: v0.0.2 recriada com 2 archives + GHCR 0.0.2/latest;
  v.0.0.1 tag+release deletada (confirmação explícita, ancestor de main).
- Incidentes: NU1403 (ILLink re-publish → lockfile regen); rate limit no
  upload de 222 assets soltos → motivou o fix de archives.
- Final: main f282c43, 0 PRs, prod healthy :5550, backlog zerado.

## 2026-09-29 — Epic #383 (gap-analysis follow-ups, 5 slices)

- Gap-analysis 2026-09-28: 12 candidatos → 7 confirmados → 5 SPECs Draft aprovados
  ("Sim, aprovar todos"); Epic #383 + issues #384–#388 criadas; PR #389 (SPECs)
  mergeada `b4c678f`.
- S1 #384 docs-sync: README/CLAUDE/API(en+pt)/INSTALL(en+pt) sincronizados com a
  wave 2026-09-27 (conectores YouTube/Git/Unstructured/Audio, 5 tools temporais,
  evidence bundle, live-actions, knobs Resilience/Agent/Search) + AD-0015.
  PR #390 mergeada.
- S2 #385 resilience tool-fallback: ResilientToolInvoker decora handlers read-only
  em DynamicToolCatalog.GetToolsAsync (candidatos do catalog scope-filtered —
  nunca cruza CallerScope); ToolErrorClassifier para IsError; engine.EvaluateReason;
  ResilienceSettings single-row + migrations duplas + /api/settings/resilience +
  aba "Resiliência" no Settings; counter tool.fallbacks. PR #391.
- S3 #386 graph timeline viewer: /graph (grid de nós, edges por nó, presets
  1h/6h/24h/7d + range, includeHistorical, badges de episódio) + REST
  /api/graph/{nodes,nodes/{id}/edges,episodes,timeline} CookieSession + 6 testes
  de integração. PR #392.
- S4 #387 test reliability + coverage gate: CollectActivities escopado por TraceId
  do root de teste (fim do flake de spans paralelos); step "Coverage Gate" no
  ci-build-test.yml — COVERAGE_MIN=20 (ratchet; medido 20.6% em main — o target
  80% é aspiracional, debt registrado). PR #393.
- S5 #388 observability: FailOrphanedJobsAsync publica evento terminal por job;
  spans search.temporal_graph{mode}/search.live_actions/evidence.emit/verify;
  counters graph.temporal_queries, live_tool.executions, evidence.records;
  TelemetryTags allowlist estendida. PR #394.
- Erros resolvidos: wrap do catálogo quebrava Assert.Same do cache → cache de
  wrapped por version; init-only DTOs vs @bind → FallbackRow mutável.
