# Knowledge sources

**Route:** `/sources`

![Knowledge sources](../../screenshots/sources.png)

## How it works

`Sources.razor` lists every registered `KnowledgeSource` and drives the whole
ingestion lifecycle:

- **Table** — name, type, Active/Inactive/Failed badge, document count, last
  sync and last job result; failures show an expandable error log.
- **New / Edit** — opens `SourceEditDialog` with per-type fields. Connectors:
  Obsidian (local vault or WebDAV), WebPage, DocumentFile, Notion,
  RestApi, SqlDatabase, RSS/Atom, YouTube, GitRepository,
  UnstructuredDocument, AudioTranscription, and cloud storage
  (S3 / Azure Files / OCI). Secrets (tokens, connection strings) are stored in
  the integration secret store (`notion:{id}`, `sql:{id}`, `git:{id}`…), never
  in the row itself.
- **Sync** — queues a persisted async ingestion job (`202 + jobId`); the job
  runs in `IngestionWorker`, chunks the content and embeds it into the vector
  store. The "Job background" / JobId row links the last run's status.
- **Activate / Delete** — inactive sources are skipped by sync and search;
  delete removes docs, chunks and the graph prefix.
