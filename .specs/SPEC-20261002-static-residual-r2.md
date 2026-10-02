# SPEC-20261002-static-residual-r2 — residual CodeQL autoral ×59 + SonarCloud ×4

## 0. Metadata

| Campo | Valor |
|-------|-------|
| Status | `Approved` |
| Ticket | Epic [#515 (E28)](https://github.com/afonsoft/LangGraph-UI/issues/515) — slice [#517](https://github.com/afonsoft/LangGraph-UI/issues/517) | gap-analysis-20261002-r3 — gaps `GAP-quality-codeql-residual-r2`, `GAP-quality-sonar-residual-r2` |
| Origem | gap-analysis r3 — CodeQL scan d0435ef + SonarCloud API (issues OPEN/CONFIRMED) |
| Pré-requisito | SPEC-20261002-static-analysis-residual (Done, #510) — r1 corrigiu a maior parte; restam sites não cobertos e resíduo da própria refatoração E27 |

## 1. Problema

Após o merge da E27-S3 e o rescan na main (`d0435ef`), persistem **59 alertas CodeQL em código autoral** e **4 issues SonarCloud**.

Evidência AS-IS (CodeQL, excluindo `/obj/`):

```text
cs/catch-of-all-exceptions ×34   (StreamingEndpoints, EmbeddingSettingsService,
                                  InvalidationSubscriber, ICacheInvalidationBus,
                                  DatabaseStatsBuilder ×5, CacheManagerService ×6, …)
cs/path-combine             ×12  (tests/ mostly — ConnectorIntegrityTests ×4,
                                  GoogleDriveConnectorTests ×3, +5 sites)
cs/linq/missed-where        ×5   (incl. Playground.razor:777 ParseCitations ×2 — resíduo E27)
cs/dispose-not-called-on-throw ×3 + cs/local-not-disposed ×2 (tests)
cs/log-forging              ×1   (ICacheInvalidationBus.cs:91 — topic do redis → LogWarning)
cs/useless-cast-to-self     ×1   (SearchCorrectnessTests.cs:126)
cs/missed-ternary-operator  ×1
cs/complex-block            ×2
```

SonarCloud (4 open):

```text
S3776 CRITICAL  Playground.razor:772  ParseCitations — extração E27 ainda acima do threshold
S1172 MAJOR     KnowledgeToolsProvider.cs:643  param `db` morto em WriteToVaultAsync — resíduo E27
S2971 MAJOR     VaultWatcherService.cs:78      LINQ simplificável
S6607 MINOR     ToolActionAnnotationDetector.cs:119  OrderBy antes de Where
```

Nota de honestidade: S1172 e parte dos missed-where **foram introduzidos pela própria refatoração E27-S3** — a wave moveu complexidade mas deixou resíduo.

## 2. Escopo

- RF-01 (CodeQL mecânico): `Path.Join` nos 12 path-combine restantes; `Where/Select` nos missed-where; cast inútil; ternário; dispose nos 5 sites de teste; sanitizar `topic` (strip `\r\n`) antes de logar em `ICacheInvalidationBus` (log-forging).
- RF-02 (CodeQL catch-all ×34): triagem site a site — boundaries SSE/shutdown/reconnect com comportamento intencional → dismiss `won't fix` com justificativa; sites sem log/tratamento → adicionar `LogWarning` mínimo ou comentário `// intentional`.
- RF-03 (Sonar ×4): remover param `db` de `WriteToVaultAsync` (morto); rebaixar `ParseCitations` abaixo do threshold S3776 (pipeline LINQ `Where(...).Select(...)` resolve S3776 + os 2 missed-where do mesmo site); `S2971` e `S6607` conforme sugestão do analyzer.
- RF-04: rebuild + format gate + unit tests verdes; PR com checks.

## 3. Fora de escopo

- Alertas `obj/` (SPEC-20261002-codeql-generated-alerts).
- CVEs Trivy (SPEC-20261002-docker-base-cve-refresh).

## 4. Critérios de aceite

- `gh api code-scanning/alerts?state=open` excluindo `/obj/` → 0, ou residual apenas com dismiss justificado em `dismissed_comment`.
- SonarCloud `issueStatuses=OPEN,CONFIRMED` → 0 na main pós-merge.
- `dotnet format --verify-no-changes` limpo; unit+integration verdes.

## 5. Riscos

- Médio-baixo: refactors mecânicos cobertos pelos 1246 unit / 322 integration existentes.
