# SPEC-20261001-pr-review-follow-ups

| Campo | Valor |
|-------|-------|
| Status | `Draft` |
| Ticket | — (aguardando aprovação para abrir Issues) |
| Origem | qa-analyst — análise pós-merge PRs #436–#448 (relatório: `.claude/memory/qa-pr-analysis-20261001.md`) |

## Contexto

Auditoria dos review comments deixados por Devin Review, CodeQL
(github-advanced-security) e SonarCloud nos últimos 10 PRs mergeados. A maior
parte dos findings são nits de estilo confirmados ainda presentes em `main`;
dois findings semânticos do Devin Review na SPEC-20261001-mcp-recall-ergonomics
permanecem sem resposta no código.

## Requisitos

### RF-001 — Piso `minFinal` vs. escala RRF (BUG — semântico)

`SearchService.cs:312`: `final.Where(i => (i.ScoreBreakdown?.Fused ?? i.Score) >= minFinal)`
compara `SearchMinScores.Final` (intenção do chamador: score normalizado 0-1)
com `ScoreBreakdown.Fused`, que é RRF puro (Σ 1/(k+rank) ≈ 0.016–0.07). Um
chamador que informe `minFinal ≥ ~0.07` recebe zero resultados mesmo com
matches perfeitos.

- Fonte: Devin Review PR #438, BUG `...b38dd_0002`, SPEC-20261001-mcp-recall-ergonomics:42.
- Critério de aceite: piso aplicado a score na mesma escala documentada do
  campo `MinScores.Final` (normalizar Fused, ou aplicar o floor ao score do
  ranker, ou renomear/documentar a escala), com teste que prova que um match
  perfeito sobrevive a `minFinal` plausível.

### RF-002 — Truncamento silencioso de resposta por budget (BUG — semântico)

SPEC original previa `maxTokens` explícito; a implementação mergeada usa
presets `Budget` low/mid/high (ResolvedSearchFilter.cs:24). Verificar se
`docBudgetChars` (SearchService.cs:613,714) ainda trunca resultado sem
sinalização ao chamador e se existe opt-out documentado.

- Fonte: Devin Review PR #438, BUG `...b38dd_0003`, SPEC-20261001-mcp-recall-ergonomics:33.
- Critério de aceite: truncamento sinalizado (metadado/campo na resposta) ou
  opt-out explícito; teste cobrindo resposta longa.

### RF-003 — CodeQL: recursos não-disposed em testes A2A

`A2aDurabilityTests.cs:218` (`StreamReader`) e `:251`
(`CancellationTokenSource`) criados sem dispose. Fix trivial com `using`.

- Fonte: CodeQL alerts #811/#810 (PR #445).

### RF-004 — CodeQL: catches genéricos/vazios (8 alertas)

Prod (intencionais, logam — suprimir justificando ou refinar exceção):
EfA2aTaskStore.cs:102, KnowledgeHubA2AAgent.cs:176 e :180,
KnowledgeHubA2AServer.cs:184, AgentService.cs:523.
Testes: A2aDurabilityTests.cs:228,229,300.

- Fonte: CodeQL #812–#819 (PR #445). Recomendado: `catch` de tipos concretos
  onde óbvio; caso contrário `SuppressMessage`/comentário explicitando o
  best-effort.

### RF-005 — CodeQL: `Path.Combine` com arg interpolado (5 alertas)

A2aTaskDurabilityTests.cs:22, A2aPushAndServerTests.cs:25,
McpRecallErgonomicsTests.cs:21,22,36 — o 2º argumento é interpolação sem risco
real de path absoluto, mas padronizável (extrair para variável/`Path.Join`).

- Fonte: CodeQL #820/#821/#806–#808 (PRs #445/#443).

### RF-006 — CodeQL: nits restantes (9 alertas)

- ResolvedSearchFilter.cs:61 — `ms` deconstruction var não lida.
- SqlDatabaseConnector.cs:105 — 4 assignments não lidos.
- SearchService.cs:295 — constant condition (revisar branch).
- KnowledgeToolsProvider.cs:246 — cast redundante.
- ChainAstParser.cs:31,32 — `readonly` possível.
- UnstructuredElementRenderer.cs:19 — `readonly` possível.
- SqliteKnowledgeGraphStore.cs:89,307 — `foreach`+`if` → `.Where()`.
- ToolActionAnnotationDetector.cs:125 — idem.
- EgressPolicyHandler.cs:170 — condição complexa (extrair).

- Fonte: CodeQL #792–#805 (PRs #437/#438/#443).

### RF-007 — SonarCloud: 1 new issue no PR #448

Issue aberta pelo SonarCloud sobre o novo código (A2aApiClient/Playground).
Processamento delegado a `/sonarqube-autofix` — não duplicar aqui.

## Fora de escopo

- Refatorações estruturais além do necessário para zerar/atenuar os alertas.
- Novos recursos de busca — o SPEC cobre apenas follow-ups de review.
