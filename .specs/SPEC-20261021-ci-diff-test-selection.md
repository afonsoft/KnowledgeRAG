# SPEC-20261021-ci-diff-test-selection

| Campo | Valor |
|---|---|
| Status | Done |
| Ticket | [#596](https://github.com/afonsoft/KnowledgeRAG/issues/596) |
| Escopo | Seleção de testes por diff — script que mapeia arquivos alterados para projetos de teste afetados, inspirado no `check_diff.py` do LangChain |

## Contexto técnico

A CI roda a suíte inteira (1567 testes: `KnowledgeHub.Tests.Unit` + `KnowledgeHub.Tests.Integration` + `KnowledgeHub.Tests.Performance` + `KnowledgeHub.Tests.Ui` + `tests/eval`) em todo PR. `.github/workflows/` é **protegido** — a integração no workflow exige admin/owner fora do fluxo de PR normal. O entregável via PR é o script + docs; o wiring fica a cargo do maintainer.

Arquivos-chave: `KnowledgeHub.slnx`, `tests/`, `.github/workflows/ci-build-test.yml` (leitura apenas), `scripts/` (onde o script novo vive).

## Requisitos

- **RF-1 — Script `scripts/select-tests.py`.** Dado `git diff --name-only <base>...HEAD`, emite a lista de projetos de teste a rodar. Regras: mapear `src/<Projeto>/**` → projetos de teste que referenciam `<Projeto>` (resolver via `dotnet sln`/`.csproj` ProjectReference); arquivos fora de `src/`/`tests/` (docs, specs, README) → `none`; mudanças em `tests/<X>/**` → `<X>`; mudanças em arquivos compartilhados (`Directory.Build.props`, `global.json`, slnx, `Shared`) → `all`.
- **RF-2 — Saída machine-readable.** `--format=json` emite `{"projects":[...],"fallback":bool}`; `--format=dotnet` emite argumentos prontos para `dotnet test`. Exit 0 sempre que a seleção resolvida for não-vazia; exit 0 + `fallback=true` quando ambíguo (roda tudo — nunca silencia).
- **RF-3 — Grafo de dependências.** Projeto de teste é afetado se qualquer projeto que ele referencia (transitivamente) mudou. Resolver pelo grafo de `ProjectReference`, não por nome.
- **RF-4 — Docs.** `docs/en/` + `docs/pt/` (ou seção no README dev) documentando uso local (`python scripts/select-tests.py --base origin/main`) e o snippet YAML sugerido para o maintainer aplicar manualmente em `ci-build-test.yml` (o PR não edita workflows — protegido).
- **RF-5 — Testes do script.** `tests/` próprio do script (pytest ou script de shell com asserts) cobrindo: mudança só em docs → `none`; mudança em Server → unit+integration; mudança em Shared → `all`; mudança em um .csproj → projetos dependentes.

## Fora de escopo

Editar `.github/workflows/*` (protegido — entrega fica a cargo do maintainer), cache de resultados, paralelização, impacto em outros gates (SonarCloud, format).

## Validação

Testes do script verdes; rodar `python scripts/select-tests.py` num diff real do repo e conferir a seleção correta manualmente.
