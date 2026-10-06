# Deploy com Docker

Guia completo para rodar o Knowledge MCP Hub (`afonsoft/knowledgerag`) com Docker — layout do compose, referência do `.env`, volumes, upgrades e troubleshooting. Para instalação rápida veja [INSTALL.md](INSTALL.md).

## Pré-requisitos

- Docker Engine ≥ 24 (ou Docker Desktop) com o plugin Compose (`docker compose version`)
- Portas/volumes abaixo livres no host

## Início rápido

**Imagem pronta, sem checkout:**

```bash
docker pull afonsoft/knowledgerag
docker run -d --name knowledgerag -p 5000:8080 \
  -v knowledgerag-data:/data \
  -v vault:/vaults/default -e Vault__Path=/vaults/default \
  afonsoft/knowledgerag
```

**Compose (recomendado):**

```bash
git clone https://github.com/afonsoft/KnowledgeRAG.git
cd KnowledgeRAG
cp .env.example .env                 # edite os valores — veja a referência abaixo
mkdir -p data logs && chown -R 1654:1654 data logs
docker compose up -d
```

Abra `http://localhost:5000`, faça login `admin` / `123qwe` — a troca de senha é obrigatória no primeiro acesso.

> O compose também funciona **sem** o checkout além de `docker-compose.yml` + `.env`: remova a linha `build:` (ou rode `docker compose pull && docker compose up -d`) para usar a imagem publicada.

## Container & volumes

| Mount | Propósito | Notas |
|---|---|---|
| `./data` → `/data` | SQLite (catálogo + vetores) e uploads | bind mount — precisa de `chown 1654:1654` |
| `./logs` → `/app/logs` | file sink do Serilog (rolling diário, retenção 14 dias) | bind mount — precisa de `chown 1654:1654` |
| `vault` → `/vaults/default` | **Vault Obsidian padrão** | named volume — criado automaticamente, herda uid 1654 da imagem |

O container roda como `app` não-root (uid **1654**) e escuta na **8080** internamente (publicado como `${KNOWLEDGEHUB_PORT:-5000}:8080`). Health probe: `GET /health/ready` (também o `HEALTHCHECK` da imagem); liveness: `GET /healthz`.

### O volume `vault`

- Declarado como named volume global do host (`volumes.vault.name: vault`) para o nome ficar `vault` independente do diretório do checkout. Remova a linha `name:` se preferir um volume por projeto (`<projeto>_vault`) — ou troque o nome se uma segunda stack KnowledgeHub dividir o mesmo Docker host.
- No primeiro boot — enquanto houver **zero** fontes `ObsidianVault` — o `VaultSeeder` registra o mount como fonte chamada `VAULT_NAME`, então `write_note` / `write_knowledge` / `read_document` funcionam imediatamente e notas escritas por agentes persistem no volume.
- **Opt-out:** defina `VAULT_PATH=` (vazio) no `.env` — nenhuma fonte é semeada. Se você remover a fonte semeada pela UI, ela só é recriada quando não existir nenhuma outra fonte ObsidianVault.
- **Usar seu próprio vault:** monte um dir do host via `docker-compose.override.yml` (ex.: `- /srv/meu-vault:/vaults/default`) — `chown -R 1654:1654` no dir — ou monte em outro caminho (`/vaults/meu`) e registre uma segunda fonte na UI.
- **Inspecionar/fazer backup:** `docker volume inspect vault`, ou `docker run --rm -v vault:/v -v $PWD:/b alpine tar -C /v -czf /b/vault-backup.tgz .`

## Referência do `.env`

O `docker-compose.yml` lê o `.env` duas vezes: (1) `env_file` injeta cada var `Section__Key` no container (o ASP.NET Core lê case-insensitive) e (2) interpolações `${VAR}` no próprio arquivo resolvem a partir dele. `required: false` significa que um `.env` ausente cai silenciosamente nos defaults mostrados — verifique com `docker compose config` antes do `up -d`.

### Deploy

| Var | Default | Notas |
|---|---|---|
| `KNOWLEDGEHUB_PORT` | `5000` | porta do host publicada → `8080` no container |
| `ALLOWED_HOSTS` | `*` | fixe o hostname público atrás de um reverse proxy |
| `AUTH_ADMIN_INITIAL_PASSWORD` | `123qwe` | seed do `admin` de primeiro boot (troca forçada de qualquer forma) |

### Vault padrão

| Var | Default | Notas |
|---|---|---|
| `VAULT_PATH` | `/vaults/default` | vazio desativa o seed do vault no primeiro boot |
| `VAULT_NAME` | `Default Vault` | nome exibido da fonte semeada |

### Embeddings

| Var | Default | Notas |
|---|---|---|
| `EMBEDDINGS_PROVIDER` | `deterministic` | `deterministic` (zero-infra), `ollama`, `openai`, `onnx`, `voyage`, `cohere` |
| `EMBEDDINGS_ENDPOINT` | `http://host.docker.internal:11434` | Ollama do host alcançável via `extra_hosts` |
| `EMBEDDINGS_MODEL` | `nomic-embed-text` | ex.: `text-embedding-3-small` para OpenAI |
| `EMBEDDINGS_APIKEY` | — | obrigatório para providers hospedados |

### Banco & vector store

| Var | Default | Notas |
|---|---|---|
| `DATABASE_PROVIDER` | `auto` | `auto` testa Postgres e cai para SQLite; `postgres`/`sqlite` forçam |
| `POSTGRES_HOST/PORT/DB/USER/PASSWORD` | `host.docker.internal:5432` / `rag_db` / `rag_user` | compostos em `VectorStore__ConnectionString` |
| `DATABASE_CONNECTIONSTRING` | — | escape hatch — a string Npgsql completa vence `POSTGRES_*` |
| `VECTORSTORE_PROVIDER` | — | vazio segue `DATABASE_PROVIDER`; defina para modo misto (`sqlite-vec`/`postgres`) |
| `VECTORSTORE_CONNECTIONSTRING` | composta | override completo do vector store |

Postgres no **host Docker**: `host.docker.internal` alcança, mas o `pg_hba.conf` precisa liberar a subnet do container (ex.: `host rag_db rag_user 172.22.0.0/16 md5` — confira a subnet com `docker network inspect`). A extensão `vector` precisa existir no banco alvo (`CREATE EXTENSION vector` — a app cria se o usuário tiver permissão).

### Chat (LLM server-side para `ask_knowledge` / `agent_chat`)

`Chat__*` propositalmente **não** é mapeado no compose base — a configuração de chat fica no `docker-compose.override.yml` (copie de `docker-compose.override.yml.example`):

| Var | Notas |
|---|---|
| `CHAT__PROVIDER` | `ollama` ou `openai` |
| `CHAT__ENDPOINT` | ex.: `http://host.docker.internal:11434` |
| `CHAT__MODEL` | ex.: `llama3.1` |
| `CHAT__APIKEY` | só providers hospedados |

Overrides por chave (tool MCP `set_chat_settings`) e o store persistido (`ChatSettings`) têm precedência sobre env.

### Proxies MCP upstream (opcional)

`DEEPWIKI_*`, `FIRECRAWL_*`, `TAVILY_*`, `CONTEXT7_*` — cada um tem `_ENABLED`, `_ENDPOINT`, `_APIKEY`, `_TIMEOUT_SECONDS`. Funcionam sem chave ou desabilitados; veja `.env.example` para os defaults.

### Cache, MCP, misc

| Var | Default | Notas |
|---|---|---|
| `CACHE_PROVIDER` | `memory` | `redis` ativa o L2 distribuído — precisa de `REDIS_CONNECTIONSTRING` (ex.: `host.docker.internal:6379`; acrescente `,password=...` se exigido) |
| `MCP__SESSIONMODE` | `StatefulForInitializeClients` | `Stateless` para clients puro `2026-07-28` |
| `MCP__MAXCONCURRENTCALLSPERSESSION` | `8` | concorrência de tools/call por sessão |

## docker-compose.override.yml

O Compose faz merge automático de `docker-compose.override.yml` (gitignored) sobre o arquivo base — é o lugar certo para mounts específicos do host e o mapeamento `Chat__*`:

```yaml
services:
  knowledgerag:
    volumes:
      - /srv/meu-vault:/vaults/obsidian        # vault extra do host (chown 1654:1654)
      # - /srv/webdav/vault:/vaults/webdav:ro  # mount WebDAV — somente leitura
    environment:
      Chat__Provider: ${CHAT__PROVIDER:-none}
      Chat__Endpoint: ${CHAT__ENDPOINT:-}
      Chat__Model: ${CHAT__MODEL:-}
      Chat__ApiKey: ${CHAT__APIKEY:-}
```

Veja `docker-compose.override.yml.example` para o template anotado.

## Reverse proxy

- Defina `ALLOWED_HOSTS` com o hostname público (ou mantenha `*` quando o túnel é a fronteira de confiança).
- Repasse `X-Forwarded-Proto`/`X-Forwarded-For` — o Agent Card A2A e URLs geradas respeitam scheme/host encaminhados.
- WebSocket/SSE precisam ser permitidos (MCP `/mcp`, `/mcp/sse`, SignalR `/hubs/*`, streams A2A) — desative response buffering nesses paths.

## Upgrade & rollback

```bash
docker compose pull            # atualiza :latest
docker compose up -d           # recria; migrations EF rodam no startup
```

Fixe a versão editando o compose: `image: afonsoft/knowledgerag:0.1.1`. Rollback apontando para a tag anterior e `up -d` — os dados de `./data` e `vault` sobrevivem.

## Troubleshooting

| Sintoma | Correção |
|---|---|
| Container sobe, logs vazios / erros SQLite | `./data` e `./logs` criados como `root` — `chown -R 1654:1654 data logs` e recrie |
| `write_note` falha / sem fonte vault | verifique `Vault__Path` renderizado — `docker compose config`; `VAULT_PATH` vazio desativa o seed |
| Fonte vault reaparece após deletar | ela é re-semeada enquanto houver zero fontes ObsidianVault — defina `VAULT_PATH=` vazio |
| Health check falhando | `docker compose logs -f` → `GET /health/ready` reporta qual check está degradado |
| Postgres inalcançável | `host.docker.internal` resolve mas o `pg_hba.conf` precisa liberar a subnet do container |
| Drift de env / defaults inesperados | `docker compose config` renderiza o env final — um `.env` ausente cai nos defaults em silêncio |

Valide mudanças antes do deploy: `docker compose config -q`.
