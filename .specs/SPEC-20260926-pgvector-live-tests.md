# SPEC — Cobertura de integração live para PostgresVectorStore (pgvector real)

| Campo | Valor |
|---|---|
| Spec ID | `SPEC-20260926-pgvector-live-tests` |
| Data | 2026-09-26 |
| Autor | Devin |
| Tipo | `Infra` (tests) |
| Stack | `xUnit`, `Testcontainers`, `pgvector/pgvector`, Npgsql 10 |
| Repository | `afonsoft/LangGraph-UI` |
| Status | `Done` — entregue via PR #237 (`f228428`) |
| Ticket | `#233` — https://github.com/afonsoft/LangGraph-UI/issues/233 |
| Origem | gap-analysis 2026-09-26 — `PostgresVectorStore` sem nenhum teste que abra conexão real |

## 1. User Story

**As a** mantenedor que opera pgvector em produção
**I want** testes de integração que exercitem `PostgresVectorStore` contra um Postgres+pgvector real
**So that** bugs de ciclo de vida do Npgsql (reader aberto, transação, batching) são pegos no CI antes de produção.

## 2. Contexto

AS-IS: `tests/KnowledgeHub.Tests.Unit/VectorStore/PostgresVectorStoreTests.cs` tem 14 testes que cobrem apenas seams sem conexão — `AddAsync` com connstring "unused" (expected `DbException`/empty batch), guards de dimensão, e formatação SQL de `BuildBulkUpsertSql`/`BuildUpsertSql`. Nenhum teste abre uma conexão Npgsql real; a suíte de integração (`KnowledgeHub.Tests.Integration`, 245 testes) é SQLite-only.

TO-BE: o bug `NpgsqlOperationInProgressException` em `SearchAsync` — reader aberto ao chamar `CommitAsync` sob Npgsql 10 — escapou todas as camadas de teste e só foi pego em produção (fix 4aa88d4, PR #227). Com pgvector agora como vector store de produção deste host, a classe `PostgresVectorStore` inteira está sem rede de segurança.

## 3. Requisitos Funcionais

### RF-001 — Fixture Testcontainers pgvector
- Fixture xUnit (collection fixture) que sobe `pgvector/pgvector:pg18` (ou versão fixada equivalente) via Testcontainers.
- Cria `kh_embeddings` com `CREATE EXTENSION vector` + schema espelhando a migration real (mesmos nomes/colunas de `rag_db`).
- Skip gracioso (`[Collection]` + `Skip` ou assembly guard) quando Docker indisponível — testes nunca falham em host sem Docker.

### RF-002 — Ciclo de vida do vector store
- `AddAsync` + `SearchAsync` roundtrip: upsert de embeddings com metadados → busca por similaridade retorna os chunks certos ordenados por score.
- `SearchAsync` com reader aberto: a consulta não pode explodir `OperationInProgress` — regressão do bug 4aa88d4.
- `DeleteSource`/`DeleteDocument` removem só os vetores da fonte/documento.
- Counts por modelo e por fonte refletem o estado real após upsert/delete.

### RF-003 — Isolamento e cleanup
- Cada teste usa schema/tabela isolada ou truncate entre testes — sem dependência de ordem.
- Fixture dispose para o container; sem vazamento.

## 4. Requisitos Não-Funcionais

- Tempo total da fixture < ~30s (image pull amortizado; usar `WithCleanUp`/`WithReuse` opcional).
- Rodar tanto em CI (GitHub runner x64) quanto local — sem variação de comportamento.
- Não hardcodar portas (Testcontainers aloca).
- Imagem pinada por tag, não `latest`.

## 5. Restrições / Não-fazer

- Não testar sqlite-vec (já coberto pela suíte atual).
- Não duplicar os 14 unit tests existentes — são complementares.
- Não acoplar a fixture a `WebApplicationFactory` — testar `PostgresVectorStore` diretamente.

## 6. Critérios de Aceite

- `PostgresVectorStoreTests` ganha uma classe `PostgresVectorStoreLiveTests` (ou equivalente) que passa no CI.
- O roundtrip AddAsync→SearchAsync falha se o bug 4aa88d4 for reintroduzido.
- Testes skip limpo em host sem Docker (verificado em host que não tem daemon).
- Total da suíte sobe de 961 e permanece verde.

## 7. Definição de Pronto

- [ ] Fixture + ≥4 testes live passando no CI
- [ ] Skip gracioso sem Docker verificado
- [ ] Regressão do bug reader-commit coberta explicitamente
- [ ] Coverage de `PostgresVectorStore` documentado nos docs de teste
