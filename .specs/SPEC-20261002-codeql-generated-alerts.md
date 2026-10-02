# SPEC-20261002-codeql-generated-alerts — política para alertas CodeQL em código gerado

## 0. Metadata

| Campo | Valor |
|-------|-------|
| Status | `Approved` |
| Ticket | Epic [#515 (E28)](https://github.com/afonsoft/LangGraph-UI/issues/515) — slice [#516](https://github.com/afonsoft/LangGraph-UI/issues/516) | gap-analysis-20261002-r3 — gap `GAP-automation-codeql-generated-noise` |
| Origem | gap-analysis r3 — `gh api code-scanning/alerts?state=open` (scan d0435ef) |
| Pré-requisito | SPEC-20261002-static-analysis-residual (Done, #510) |

## 1. Problema

37 dos 96 alertas CodeQL abertos apontam para `src/KnowledgeHub.Server/obj/Debug/net10.0/linux-x64/generated/.../RegexGenerator.g.cs` — código source-generated (RegexGenerator) que não é editável nem acionável.

Evidência AS-IS:

```text
gh api .../code-scanning/alerts?state=open
→ 96 open; 37 com path contendo /obj/ (37× cs/useless-assignment-to-local)
→ .github/codeql/codeql-config.yml já tem paths-ignore: **/obj/** — sem efeito
```

Causa: `paths-ignore`/`paths` **não se aplicam a linguagens compiladas** no CodeQL — arquivos `.g.cs` compilados pelo `csc` são extraídos e analisados independentemente do config. O config só filtra linguagens interpretadas.

TO-BE desejado: backlog do code scanning contém apenas achados acionáveis em código autoral; ruído de código gerado tem política documentada e execução registrada.

## 2. Escopo

- RF-01: Bulk-dismiss dos 37 alertas abertos em `**/obj/**` como `false positive` com comentário `"source-generated file under obj/ — RegexGenerator output is not actionable; paths-ignore does not filter compiled-language extraction"`.
- RF-02: Documentar no `codeql-config.yml` (comentário) que `paths-ignore` não filtra C#/compiled-lang e que a política de código gerado é dismiss registrado.
- RF-03: Se alertas `obj/` reabrirem após mudanças no gerador, repetir o bulk-dismiss (script curl documentado no comentário da config).

## 3. Fora de escopo

- Não desligar RegexGenerator nem alterar compilação.
- Não tratar os 59 alertas em código autoral (coberto por SPEC-20261002-static-residual-r2).

## 4. Critérios de aceite

- `gh api code-scanning/alerts?state=open` → zero alertas com path contendo `/obj/`.
- Comentário explicativo presente em `.github/codeql/codeql-config.yml`.

## 5. Riscos

- Baixo: dismissals persistem por alerta; reabrem apenas se o hash/localização mudar — aceitável, RF-03 cobre.
