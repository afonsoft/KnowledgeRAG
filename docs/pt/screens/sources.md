# Fontes de conhecimento

**Rota:** `/sources`

![Fontes de conhecimento](../../screenshots/sources.png)

## Como funciona

`Sources.razor` lista todas as `KnowledgeSource` registradas e controla todo o
ciclo de ingestão:

- **Tabela** — nome, tipo, badge Ativa/Inativa/Falha, quantidade de documentos,
  última sincronização e resultado do último job; falhas têm log de erro
  expansível.
- **Nova / Editar** — abre `SourceEditDialog` com campos por tipo. Conectores:
  Obsidian (vault local ou WebDAV), WebPage, DocumentFile, Notion,
  RestApi, SqlDatabase, RSS/Atom, YouTube, GitRepository,
  UnstructuredDocument, AudioTranscription e cloud storage
  (S3 / Azure Files / OCI). Secrets (tokens, connection strings) ficam no
  secret store de integrações (`notion:{id}`, `sql:{id}`, `git:{id}`…), nunca
  na linha da fonte.
- **Sync** — enfileira um job de ingestão assíncrono persistido (`202 + jobId`);
  o `IngestionWorker` executa, faz chunk do conteúdo e gera embeddings no
  vector store. A linha "Job background" / JobId mostra o status da última run.
- **Ativar / Excluir** — fontes inativas são ignoradas por sync e busca;
  excluir remove documentos, chunks e o prefixo do grafo.
