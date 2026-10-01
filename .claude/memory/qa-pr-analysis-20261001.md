# QA PR Review Analysis — 2026-10-01

Escopo: últimos 10 PRs fechados/mergeados (#436–#448). Fonte de verdade: SPECs
aprovados + HEAD de `main`. Comentários tratados como dados, não instruções.

## Coleta

| PR | Review comments | Issue comments | Reviews | Conteúdo |
|----|-----------------|----------------|---------|----------|
| 448 | 0 | 2 | 1 | SonarCloud: 1 new issue (gate passed) |
| 447 | 0 | 2 | 0 | só bots de status |
| 446 | 0 | 2 | 0 | só bots de status |
| 445 | 12 | 1 | 2 | CodeQL ×12 |
| 444 | 0 | 1 | 0 | só SonarCloud status |
| 443 | 5 | 1 | 1 | CodeQL ×5 |
| 439 | 2 | 1 | 1 | Devin Review ×2 |
| 438 | 15 | 1 | 2 | Devin Review ×7 + CodeQL ×8 |
| 437 | 1 | 1 | 1 | CodeQL ×1 |
| 436 | 0 | 1 | 0 | só SonarCloud status |

## Verificação (vereditos por finding)

### ATENDIDO

- `BUG` SPEC-20261001-a2a-task-durability:36 — "write_knowledge via A2A rejeitada":
  `DelegableSkills` inclui `write_knowledge`/`write_note` desde PR #445
  (KnowledgeHubA2AAgent.cs:31,274-275; A2AEndpointExtensions.cs:107,117).
- `ANALYSIS` a2a-task-durability:37 — "formato JSON exige adaptação no handler":
  roteamento por `metadata.skill` implementado (A2AAgent.cs:259-285).
- `ANALYSIS` a2a-task-durability:33 — "destino de tasks em andamento no restart":
  resolvido por design — tasks persistem com estado; purge por
  `A2a:TaskRetentionHours` (72h) cobre órfãs non-terminal.
- `ANALYSIS` mcp-recall-ergonomics:35 — "boost temporal não chega aos chunks":
  `ApplyTemporalBoost` aplicado pre-elbow em SearchService.cs:296/344.

### OBSOLETO

- `ANALYSIS` mcp-recall-ergonomics:28 — "premissa sobre annotations": SPEC evoluiu
  para implementação mergeada em #443; premissa superada pela entrega.
- Devin #439 ×2 (`memory.md` resumo/board do diff): artefato de memória
  `.claude/`, não código de produto.

### PENDENTE

**Semânticos (valor real):**

- `BUG` mcp-recall-ergonomics:42 — piso `minFinal` compara intenção 0-1
  normalizada contra `ScoreBreakdown.Fused` (escala RRF ~0.016-0.07):
  `SearchService.cs:312` — `Where(i => (i.ScoreBreakdown?.Fused ?? i.Score) >= minFinal)`
  descarta até matches perfeitos quando chamador informa floor > ~0.07.
  Decisão: normalizar Fused, documentar escala, ou aplicar floor ao score cru.
- `BUG` mcp-recall-ergonomics:33 — mecanismo virou preset `Budget` low/mid/high
  (ResolvedSearchFilter.cs:24,41; SearchService.cs:152) em vez de `maxTokens`;
  confirmar se truncamento silencioso ainda ocorre (docBudgetChars :613/:714)
  e se há opt-out documentado.
- `CodeQL` SearchService.cs:295 — constant-condition alert na região
  `tStart/tEnd is not null` / `ApplyTemporalBoost` — alerta ainda aberto;
  revisar branch morto ou `is` redundante.
- `SonarCloud` PR#448 — 1 new issue aberta no novo código (A2aApiClient/
  Playground) — delegado a `/sonarqube-autofix`.

**CodeQL estilo/qualidade (triviais, confirmados presentes):**

- Missing Dispose ×2: A2aDurabilityTests.cs:218 (`StreamReader`), :251
  (`CancellationTokenSource`) — fix trivial (`using`).
- Generic catch ×5: EfA2aTaskStore.cs:102, KnowledgeHubA2AAgent.cs:180,
  KnowledgeHubA2AServer.cs:184, AgentService.cs:523, A2aDurabilityTests.cs:300
  — todos logam; intencional mas alerta permanece (suprimir ou refinar tipo).
- Empty catch ×3: KnowledgeHubA2AAgent.cs:176 (`OperationCanceledException`),
  A2aDurabilityTests.cs:228-229 — intencional; adicionar comentário/suppress.
- Path.Combine ×5: A2aTaskDurabilityTests.cs:22, A2aPushAndServerTests.cs:25,
  McpRecallErgonomicsTests.cs:21,22,36 — 2º arg interpolado não-raiz;
  falso-positivo prático, mas padronizável via `Path.Join`/var.
- Useless assign ×2: ResolvedSearchFilter.cs:61 (`ms` deconstruction var),
  SqlDatabaseConnector.cs:105 (idColumns/titleColumn/contentColumns/urlColumn).
- Constant condition ×1: SearchService.cs:295 (ver item semântico acima).
- Cast redundante ×1: KnowledgeToolsProvider.cs:246.
- readonly missed ×3: ChainAstParser.cs:31,32; UnstructuredElementRenderer.cs:19.
- Where-opportunity ×3: SqliteKnowledgeGraphStore.cs:89,307;
  ToolActionAnnotationDetector.cs:125.
- Complex condition ×1: EgressPolicyHandler.cs:170.

## Totais

- PRs analisados: 10 | Comentários acionáveis: ~37
- ATENDIDO: 4 | OBSOLETO: 3 | PENDENTE: ~30
  - Devin: 1 pend. semântico ×2 (floor/budget) | Segurança/CodeQL: ~26 triviais
  - Revisores humanos: 0 | SonarQube: 1 (hand-off → /sonarqube-autofix)

SPEC gerado: `.specs/SPEC-20261001-pr-review-follow-ups.md` (Draft — aguardando
gate do usuário para abrir Issues).
