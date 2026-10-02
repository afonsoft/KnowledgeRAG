# gap-analysis 2026-10-02 r3 — pós-E27

Contexto: run imediato ao fechamento do Epic E27 (#503–#507, PRs #508–#514). Objetivo: verificar o que sobrou e o que E27 introduziu.

## Source inventory
`.specs/` present · `docs/` present · `docs/architecture/` present · `.claude/CONTEXT.md` present · `.claude/MEMORY.md` + `memory/` present · `.claude/rules/`+`agents/` present · `CLAUDE.md`/`AGENTS.md`/`README.md` present · `gh` authed (afonsoft) · main @ `d0435ef`, tree limpa

## Estado verificado
- Issues abertas: 0 · PRs abertos: 0 · Branches remotas: só `main` · SPECs não-Done: 0 (grep hit em spec-status-sync era falso positivo — spec está Done)
- Main CI run `d0435ef`: **6/6 success** — ratchet funcional via variável `COVERAGE_BASELINE=20.55`
- CodeQL: 96 open = 37 em `src/**/obj/**/generated/*.g.cs` + 59 código autoral; 42 dismissed persistem (37 won't_fix + 5 FP)
- SonarCloud: 4 open (era 8 → 4 pós-E27)
- Trivy: 4 open (3 CVEs medium: libpcre2-8-0 ×2, libc6/libc-bin ×1 CVE)
- Dependabot: 0 · secret-scanning: 0

## Candidatos e veredictos
| Key | Veredito | Nota |
|---|---|---|
| GAP-automation-codeql-generated-noise | CONFIRMADO | 37 alertas `obj/`; `paths-ignore` não filtra compiled-lang (empírico: persistiram no scan d0435ef com config ativa) |
| GAP-quality-codeql-residual-r2 | CONFIRMADO | 59 autorais: catch-all×34 (triage pendente — dump r2 truncado), path-combine×12, missed-where×5, dispose×5, log-forging×1 (ICacheInvalidationBus:91), cast×1, ternary×1, complex-block×2 |
| GAP-quality-sonar-residual-r2 | CONFIRMADO | 4 open; S1172 + missed-where são resíduo da própria extração E27-S3 (honestidade registrada) |
| GAP-security-trivy-baseimage-cves | CONFIRMADO | Dockerfile sem `apt-get upgrade`; médios com fix possível no rebuild |
| stale-branches / issues / specs-drift / ci-health / ratchet | REJEITADO | tudo limpo/verde — evidências acima |
| devin-review-trial-expired | REJEITADO | info-only no #512; fora de escopo de gap de repo |

## SPECs Draft
- `.specs/SPEC-20261002-codeql-generated-alerts.md`
- `.specs/SPEC-20261002-static-residual-r2.md`
- `.specs/SPEC-20261002-docker-base-cve-refresh.md`

## Pendências
- Aguardando aprovação do gate para create-issues (Epic E28) + orchestrator.
