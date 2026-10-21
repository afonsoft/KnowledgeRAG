# SPEC-20261021-connector-conformance-suite

| Campo | Valor |
|---|---|
| Status | Approved |
| Ticket | [#595](https://github.com/afonsoft/KnowledgeRAG/issues/595) |
| Escopo | Pacote de testes de conformidade compartilhado que toda implementação de `ISourceConnector`/`IIncrementalSourceConnector`/`IItemFetchConnector` deve passar — inspirado no `langchain-tests` |

## Contexto técnico

15+ conectores em `src/KnowledgeHub.Server/Ingestion/Connectors/` implementam `ISourceConnector` (`FetchAsync` → `FetchResult(Documents, Warnings, FailedUris, Truncated)`) com contratos documentados nos XML docs: warnings por item, `Truncated` suspende reconciliação de deleção, incremental emite stubs com fingerprint, segredos em `IIntegrationSecretStore`. Cada conector tem testes bespoke em `tests/KnowledgeHub.Tests.Unit/Ingestion/`.

## Requisitos

- **RF-1 — Fixture abstrata `SourceConnectorConformance`.** Classe base xUnit em `tests/KnowledgeHub.Tests.Unit/Ingestion/Conformance/` com factory abstrata `CreateConnector()` + `CreateValidSource()`/`CreateInvalidSource()` por conector. Testes herdados rodam contra cada implementação concreta.
- **RF-2 — Contrato de `FetchResult`.** Asserções aplicadas a todos: `Documents` nunca null; `Warnings` nunca null; todo `RawDocument` tem `UriReference`/`Title`/`TextContent` não-vazios (ou stub com `Fingerprint` quando incremental-unchanged); `FailedUris` ⊆ URIs upstream conhecidos; `Truncated=true` ⇒ `FailedUris`/`Warnings` explicam o corte quando houver.
- **RF-3 — Contrato incremental.** Para `IIncrementalSourceConnector`: segunda chamada com `existingFingerprints` populadas ⇒ documentos inalterados voltam como stub (`TextContent` vazio + mesmo `Fingerprint`) e permanecem em `seen` — nenhum doc conhecido some.
- **RF-4 — Contrato de segredos.** Conector nunca loga nem expõe em `RawDocument`/`Warnings` o valor de segredos (`apiKey`, tokens, connstrings) — teste com secret sentinel ("S3CR3T-SENTINEL") varre outputs.
- **RF-5 — Adoção progressiva.** Classe concreta por conector já existente no repo (Obsidian/WebPage/DocumentFile/RssFeed/RestApi/SqlDatabase/Notion/YouTube/Git/Unstructured + cloud quando mockável); conectores que exigem rede real podem ser pulados com `Skip` justificado, mas a lista de pulados deve estar explícita no teste.
- **RF-6 — Documentação.** `docs/en/` (ou comentário no fixture) descreve o contrato que novos conectores devem herdar — "como adicionar meu conector à suite de conformidade".

## Fora de escopo

Refatorar conectores para passar no contrato — violações encontradas viram issues/falhas de teste documentadas, não reescrita inline (escopo de cada conector). Testes de integração com serviços reais.

## Validação

`dotnet test` — nova suite verde nos conectores aderentes; falhas de contrato em conectores existentes reportadas (e marcadas explicitamente se precisarem de SPEC própria).
