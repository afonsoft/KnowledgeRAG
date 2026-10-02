# Gap Analysis — 2026-10-02 (run 4, pós-E28 + skills refresh)

Continuação de `gap-analysis-20261002-r3.md` (pós-E27). Delta desta run:
(a) skills do repo atualizadas para o HEAD de `afonsoft/skills` (2 novas:
`migration-planner`, `web-design-guidelines`; 24 sem diff);
(b) análise dos últimos 20 PRs (qa-pr-analysis-20261002-r3.md) — só gate
noise, zero review humano;
(c) residuais CodeQL + SonarCloud corrigidos em
`feature/Devin-20261002-pr-review-residual`.

## Estado dos scanners (HEAD pré-fix → pós-fix nesta branch)

| Fonte | Aberto antes | Corrigido | Restante |
|-------|--------------|-----------|----------|
| CodeQL code alerts | 5 | 5 (log-forging ×1 via LogSafe, catch-of-all ×3, missed-select ×1) | 0 |
| Trivy CVEs | 14 | 0 (não actionável — aguarda base image patchada; `apt-get upgrade` já no Dockerfile desde #522) | 14 |
| SonarCloud CODE_SMELL | 14 | 14 (S1192 ×5, S107 ×7, S3267, S2971) | 0 |

## Gaps novos identificados

### GAP-tests-db-provider-env (baixo)
`ConfigurationValidator` rejeita `Database:Provider=PostgreSQL` (case) —
ambientes que exportam o valor do docker-compose com capitalização quebram o
startup. Opções: normalizar casing no validator ou documentar o formato exato.
Sem issue aberta; candidato a SPEC pequena.

### GAP-processo-merge-red-gate (recorrente — já registrado em r3)
PR #490 mergeou com Sonar Quality Gate FAILED; nesta janela nenhum gate
vermelho foi mergeado. Permanece como risco de processo, não novo gap.

## Dedup vs r3

- 96 CodeQL de r3 (37 objetivo + 59 autorais) → consolidados: os 5 code alerts
  abertos agora estão corrigidos; os demais eram comments inline já-fechados.
- 4 Sonar de r3 → agora são 14 (cresceu com a wave E28); todos corrigidos.
- Trivy: inalterado vs r3 (mesma classe de finding, mesma resposta).

## Pós-fix residual (para próximas runs)

- Re-verificar alerts no próximo CodeQL scan do PR desta branch — os 5 alerts
  de código devem transicionar a "fixed".
- Monitorar Trivy após publish dos pacotes Debian patchados.
