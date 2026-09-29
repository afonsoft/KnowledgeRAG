# SPEC-20260929-codeql-quality-debt

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `codeql-quality-debt` |
| Type | `Infra` (lint/security estático) |
| Stack | `CodeQL` — findings acumulados dos últimos 20 PRs |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-codeql-quality-debt` |
| Status | `Approved` |
| Source | `github-advanced-security[bot]` code-scanning comments (PRs #369, #373, #374, #377, #378, #380, #381, #391, #392) |

## 1. User Story

**As a** mantenedor
**I want** os findings recorrentes do CodeQL varridos num só PR
**So that** alertas reais (log injection, Path.Combine que descarta args) parem de se misturar com ruído e a baseline fique limpa.

## 2. Findings agrupados (frequência no corpus)

| Finding | Ocorrências | Severidade |
| --- | --- | --- |
| `Path.Combine` pode descartar argumentos anteriores | ~8 | 🟡 bug real quando arg é rooted path |
| Log entries created from user input | 2 (PR #391) | 🟥 log injection |
| Generic catch clause | ~8 | 🟨 swallowing |
| Useless assignment / upcast / cast-to-same-type | ~15 | 🟨 dead code |
| Unnecessarily complex Boolean / constant condition | ~8 | 🟨 readability |
| Missed `Where`/`Select` | ~15 | 🟨 LINQ style |
| Missing Dispose on local IDisposable | ~3 | 🟡 resource leak |
| Container contents never accessed | 2 | 🟨 dead code |

## 3. Requirements

- RF-001: `Path.Combine(a, b, rooted)` → `Path.GetFullPath` ou rebase intencional com comentário; casos de teste (`AutocutSearchApiTests`, etc.) revisados.
- RF-002: User input sanitizado antes de `Log*` (CR/LF strip) — achados do PR #391 e qualquer novo `logger.Log*($"...{userInput}...")`.
- RF-003: Generic catch revisado — manter apenas onde há logging com contexto; rethrow ou narrow `when` nos demais.
- RF-004: Dead code (useless assignment, upcast, constant condition) removido.
- RF-005: LINQ `Where`/`Select` opportunities aplicadas onde a leitura fica melhor (não obrigatório nos pontos em que `foreach` é mais claro — justificar com comentário Sonar `// NOSONAR`? preferir refatorar).
- RF-006: `IDisposable` local em teste/código — `using`/`await using`.
- RF-007: Meta: GitHub code-scanning dashboard limpo para as novas classes de finding; `queries` no workflow podem ser elevadas a `security-and-quality` se o debt zerar.

## 4. Acceptance Criteria

- AC-1: `gh api repos/afonsoft/LangGraph-UI/code-scanning/alerts?state=open` reduzido ≥80% para as regras listadas.
- AC-2: 0 novos alerts CodeQL no PR da correção.
- AC-3: suite verde.
