# Instalação

O KnowledgeHub roda de três formas: a partir do código-fonte (.NET SDK), como container Docker, ou como binário único autocontido.

## Docker (recomendado)

```bash
cp .env.example .env   # edite os valores
mkdir -p data logs && chown -R 1654:1654 data logs   # o container roda como uid 1654 (app)
docker compose up -d
```

- Gera a imagem `knowledgehub:latest` e serve na porta mapeada (`docker-compose.yml` — `5550:8080` por padrão).
- `./data` é bind-mount e persiste `knowledgehub.db` + uploads.
- `./logs` é bind-mount e persiste o file sink do Serilog — rolling diário (`knowledgehub-YYYYMMDD.log`), retenção de 14 dias, secrets redigidos como `***REDACTED***`. **Se `./logs` não existir o Docker o cria como `root` e o file sink não consegue escrever** (o container roda como `app`, uid 1654) — pré-crie com o `chown` acima (incluindo `./data` — o SQLite migra ali) ou corrija uma vez após o primeiro `up`.
- Migrations EF Core aplicadas automaticamente no startup.
- Health probe: `GET /healthz`.

## A partir do código-fonte

```bash
dotnet build KnowledgeHub.slnx
dotnet ef database update -p src/KnowledgeHub.Server   # opcional — roda no startup também
dotnet run --project src/KnowledgeHub.Server           # http://localhost:5000
```

Requer .NET SDK 10.0.x (`global.json`). Primeiro login: `admin` / `123qwe` (troca de senha obrigatória; sobrescreva via `Auth__AdminInitialPassword`).

## Binário standalone + systemd

```bash
dotnet publish src/KnowledgeHub.Server -c Release -r linux-x64   # ou win-x64 / osx-arm64
sudo ./install.sh --host --systemd
```

Produz um binário autocontido de ~120 MB — sem runtime .NET. O instalador cria uma unit systemd hardened; `knowledgehub.db` é criado ao lado do executável. `backup.sh`/`restore.sh` cobrem banco e uploads.

## Configuração

`appsettings.json` + variáveis de ambiente (nesting com `__`) + `.env`. Configurações editáveis em runtime (Chat, Graph, chaves de integração, overrides por API key) ficam no SQLite via `/settings` — sem restart. Veja `docs/architecture/system-architecture.md` §5 para a superfície completa.

## Backends opcionais

| Opção | Configuração | Propósito |
|---|---|---|
| PostgreSQL + pgvector | `VectorStore:Provider=postgres` + `VectorStore:ConnectionString` | Busca vetorial HNSW em escala |
| Redis | `Cache:Provider=redis` + `Cache:Redis:ConnectionString` | Cache distribuído de busca/embedding/resposta |
| Ollama / OpenAI-compat | `Embeddings:*` / `Chat:*` | Modelos de embedding e chat |
| OTLP / Prometheus | `Telemetry:Otlp:Endpoint` / `Telemetry:Metrics:Prometheus` | Traces + `/metrics` (+ sink de logs OTLP) |
| Tuning pgvector | `VectorStore:Postgres:*` | `StorageType` vector\|halfvec (pgvector ≥0.7 + `AllowStorageMigration`), `IterativeScan` (≥0.8), `Hnsw*`, pool — ver README §Configuração |
| Tuning de cache | `Cache:RegionTtlMinutes`, `Cache:L1*` | TTLs por região + L1 em processo à frente do Redis (invalidação via pub/sub `kh:invalidate`) — ver README §Configuração |
| Nível de log em runtime | `GET/PUT /api/settings/log-level` | `LoggingLevelSwitch` com `minutes` opcional (0–120) |
| Voyage AI / Cohere | `Embeddings:Provider=voyage\|cohere` + `Embeddings:Voyage|Cohere:{ApiKey,Model}` | Providers hospedados de embeddings (sub-seção vence; `ApiKey`/`Model`/`Dimensions` top-level são o fallback, ex.: UI de settings) |
| Resilience fallback | `Resilience:Fallback` | `Mode` `disabled`\|`observe`\|`enforce`, `MaxFallbackAttempts`, `ChatFallbacks[]` (alternates ordenados `{endpoint,model,apiKey}`), mapa `ToolCapabilities` (`WebSearch`/`DeepDocLookup`) |
| Agent loop | `Agent:*` | `EnableDynamicActionBridge` (tools MCP live dentro do `ask_knowledge`), `MaxChainedDynamicCalls` (default 3), `ContextManagement:*` (compactação ChainAst + repair de tool calls órfãs) |
| Tuning de retrieval | `Search:*` | `LimitMode` `fixed`\|`autocut`, `Autocut:Sensitivity`/`MaxClamp`, `Expansion:WindowThresholdPercent`, `Relaxation:Enabled`/`MinResults` |
| Secrets por conector | `restapi:{id}` · `sql:{id}` · `git:{id}` · `unstructured:{id}` · `audio:{id}` | Secrets por source no integration-secret store (headers, connstring, PAT, API keys) — nunca em `configuration` |

## Provider de banco unificado (catálogo + vector store)

`Database:Provider` escolhe um único backend para o catálogo EF Core e o vector
store: `auto` (padrão — faz probe em `POSTGRES_*`/`Database:ConnectionString` e
cai em SQLite quando o Postgres está inalcançável, com log de erro),
`postgres` (exige connection string — erro no startup caso contrário) ou
`sqlite`. No primeiro boot em Postgres, um catálogo vazio é preenchido a partir
do arquivo SQLite existente (one-shot, IDs preservados, marcador `.migrated`).
A busca lexical usa coluna `tsvector` gerada + índice GIN no Postgres e FTS5 no
SQLite. `VectorStore:Provider` continua como override explícito para modo misto.

## PostgreSQL + pgvector (host ou externo)

O `docker-compose.yml` lê o `.env` (`env_file`, `required: false`) e compõe
`VectorStore__ConnectionString` a partir de vars discretas — defina `VECTORSTORE_PROVIDER=postgres` mais:

```dotenv
POSTGRES_HOST=host.docker.internal   # Postgres do host via extra_hosts do compose
POSTGRES_PORT=5432
POSTGRES_DB=rag_db
POSTGRES_USER=rag_user
POSTGRES_PASSWORD=...
# Válvula de escape — prevalece sobre a composição POSTGRES_* quando definida:
# VECTORSTORE_CONNECTIONSTRING=Host=...;Database=...;Username=...;Password=...;SSL Mode=Require
```

Checklist de provisionamento no lado Postgres:

1. **Extensão** — a extensão `vector` precisa existir no banco alvo. `PostgresVectorStore` roda `CREATE EXTENSION IF NOT EXISTS vector` no init, o que funciona quando o usuário da app tem privilégio `CREATE` no banco; caso contrário pré-crie como superuser (`CREATE EXTENSION vector`). O pacote pgvector deve casar com a major do servidor (ex.: `postgresql-18-pgvector`, ou build via source com `PG_CONFIG=<path>/pg_config`).
2. **pg_hba.conf** — quando o Postgres roda no host Docker, `host.docker.internal` resolve para o gateway do host mas o **IP do cliente é o do container** — adicione uma regra host cobrindo a rede do compose (ex.: `host rag_db rag_user 172.22.0.0/16 md5`) e faça reload.
3. **Verificação** — `docker compose config` deve renderizar a connection string interpolada; após o `up`, `GET /api/diagnostics/vectorstore` reporta `postgres`, a dimensão, a contagem de chunks e o estado do índice (HNSW criado automaticamente acima de `HnswThreshold`).

> **Nota sobre Redis.** O mesmo padrão `host.docker.internal` se aplica a `REDIS_CONNECTIONSTRING` (`Cache:Provider=redis`). Um Redis sem autenticação dispara um warning no startup — veja "Segurança do Redis" no README para hardening.
