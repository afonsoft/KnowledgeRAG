# Knowledge MCP Hub

**[English](README.md) · [Português (pt-BR)](README.pt-br.md)**

[![CI Build & Test](https://github.com/afonsoft/LangGraph-UI/actions/workflows/ci-build-test.yml/badge.svg?branch=main)](https://github.com/afonsoft/LangGraph-UI/actions/workflows/ci-build-test.yml)
[![Code Quality](https://github.com/afonsoft/LangGraph-UI/actions/workflows/code-quality.yml/badge.svg?branch=main)](https://github.com/afonsoft/LangGraph-UI/actions/workflows/code-quality.yml)
[![Security Scan](https://github.com/afonsoft/LangGraph-UI/actions/workflows/security-scan.yml/badge.svg?branch=main)](https://github.com/afonsoft/LangGraph-UI/actions/workflows/security-scan.yml)
[![SonarCloud](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_LangGraph-UI&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=afonsoft_LangGraph-UI)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_LangGraph-UI&metric=coverage)](https://sonarcloud.io/summary/new_code?id=afonsoft_LangGraph-UI)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Blazor WASM](https://img.shields.io/badge/Blazor-WASM%20PWA-512BD4)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![Licença: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

Plataforma de conhecimento standalone tudo-em-um: UI administrativa Blazor WebAssembly, API REST, servidor MCP nativo (Streamable HTTP + SSE legado), persistência SQLite, embeddings e vector stores plugáveis, retrieval híbrido (FTS5 + RRF vetorial + loop corretivo), extração de entidades/relações GraphRAG, chat agêntico com aprovações HITL, defesa contra prompt-injection, rate limiting particionado, fila de ingestão assíncrona, conectores de cloud storage e observabilidade OpenTelemetry — tudo em um único processo Kestrel hospedado .NET 10.

## Endpoints

| Rota | Propósito |
|---|---|
| `/` | UI administrativa Blazor WASM (`/sources`, `/mcp-monitor`, `/playground`, `/chat`, `/approvals`, `/settings`, `/api-keys`) — PWA instalável, sidebar colapsável com icon-rail, layout responsivo para mobile |
| `/api/sources`, `/api/search`, `/api/ask`, `/api/agent`, `/api/approvals`, `/api/threads` | API REST — `POST /sources/{id}/sync` é assíncrono (`202 + jobId`; `?wait=true` para o contrato síncrono legado) |
| `/api/ingestion/jobs`, `/api/ingestion/jobs/{id}`, `/api/ingestion/jobs/{id}/cancel` | Jobs de ingestão em background — status, contadores por documento, cancelamento |
| `/api/eval/baselines` | Baselines nomeadas de eval para gates de regressão (promover um run, comparar runs futuros automaticamente) |
| `/api/settings/chat`, `/api/settings/chat/test`, `/api/settings/embeddings`, `/api/settings/graph`, `/api/settings/integrations*`, `/api/settings/database`, `/api/settings/log-level` | Config persistida de chat + embeddings, settings de runtime do GraphRAG (ativar, budgets), chaves de integração mascaradas (firecrawl, deepwiki, tavily, context7), estatísticas do banco, nível de log em runtime |
| `/api/api-keys/{id}/settings/chat`, `/api/api-keys/{id}/settings/integrations/{provider}`, `/api/api-keys/{id}/rate-limit`, `/api/api-keys/{id}/scopes` | Sobrescrições por chave de API: chat, integrações, rate limits, sources/tools permitidos |
| `/api/security/events`, `/api/eval/run`, `/api/eval/runs` | Auditoria de prompt-injection e harness de eval de retrieval (Recall@K/P@K/MRR/faithfulness) |
| `/api/agent/resume`, `/api/mcp/capabilities`, `/api/diagnostics/vectorstore` | Retoma um run do agente após aprovação HITL; modo de sessão MCP anunciado; diagnósticos do vector store (provider, dims, estado do índice) |
| `/api/ask/stream`, `/api/agent/stream` | REST SSE — eventos `token`/`tool_start`/`tool_end`/`awaiting_approval`/`done`/`error`, heartbeat 15s, `X-Accel-Buffering: no` |
| `/mcp` | MCP — Streamable HTTP, sessões híbridas: clients com handshake `initialize` (≤2025-11-25) obtêm sessões stateful completas incluindo push `tools/list_changed`; clients `2026-07-28` são atendidos statelessly (sem sessão, re-list sob demanda). Configuração `Mcp:SessionMode`: `Stateless`/`Stateful`/`StatefulForInitializeClients` (padrão) |
| `/mcp/sse` + `/mcp/message` | MCP — HTTP/SSE legado (Cursor, Claude Desktop) |
| `/hubs/mcp` | Feed SignalR para o monitor MCP |

## Autenticação

Todas as superfícies exceto as sondas de health (`/health/*`) e `POST /api/auth/login` requerem autenticação — a SPA, a API REST, `/mcp`, `/mcp/sse` e `/hubs/mcp`.

**Navegador (cookie).** A SPA faz login em `/login`; a sessão é um cookie HttpOnly (`SameSite=Lax`, `Secure`, 12h sliding). Na primeira inicialização, um usuário `admin` é criado com a senha `123qwe` (sobrescreva via `Auth__AdminInitialPassword`) e `mustChangePassword` força a tela de troca de senha antes de qualquer outra página ou chamada de API. Política de senha: ≥8 caracteres, diferente da atual. Cinco tentativas consecutivas falhas bloqueiam a conta por 5 minutos (`423 Locked`); credenciais erradas retornam `401` genérico (sem enumeração de usuário).

| Rota de auth | Propósito |
|---|---|
| `POST /api/auth/login` | `{ username, password }` → define o cookie de sessão |
| `GET /api/auth/me` | `{ username, mustChangePassword }` |
| `POST /api/auth/logout` | limpa o cookie de sessão |
| `POST /api/auth/change-password` | `{ currentPassword, newPassword }` → `204`, limpa a flag |
| `GET /api/apikeys` · `POST /api/apikeys` · `DELETE /api/apikeys/{id}` | gerenciar chaves de API (sessão por cookie apenas) |
| `GET /api/apikeys/{id}/usage` · `GET /api/apikeys/{id}/secret` | auditoria de uso por chave e reveal do secret (UX de cópia) |

**Chaves de API (`aft_*`) para clientes não-navegador.** Crie uma em `/api-keys` (ou `POST /api/apikeys`); o segredo completo `aft_<32-hex>` é mostrado na criação, e chaves com cópia criptografada via DataProtection podem ser reveladas depois via `GET /api/apikeys/{id}/secret` (`canReveal` na resposta) — a verificação sempre usa o hash SHA-256. Envie como bearer token:

```bash
curl https://rag.afonsoft.dev/mcp \
  -H "Authorization: Bearer aft_..." \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{...}}'
```

Aceito em `/mcp`, `/mcp/sse`, `/api/*` e `/hubs/mcp` (clients SignalR que não podem enviar headers podem usar `?access_token=`). Chaves de API podem chamar tudo **exceto** os endpoints de gerenciamento de chaves de API, que requerem sessão por cookie. Revogar uma chave (`DELETE /api/apikeys/{id}` ou pela UI) tem efeito imediato. Cada chave pode também ter suas **próprias configurações de endpoint/modelo/chave de chat e chaves de integração** (firecrawl, tavily) — configure na UI `/api-keys`, via `/api/api-keys/{id}/settings/*`, ou através da ferramenta MCP `set_api_key_settings`.

> **Mudança quebrante para clients MCP externos** (Cursor, Claude Desktop, …): agora devem enviar `Authorization: Bearer aft_...`. Gere a chave na UI administrativa primeiro.

## Ferramentas MCP

## Fontes de conhecimento & ingestão

| Conector | `SourceType` | Notas |
|---|---|---|
| Vault Obsidian | `ObsidianVault` | Caminho local ou WebDAV; sync incremental |
| Página web | `WebPage` | Fetch + extração para markdown |
| Arquivo de documento | `DocumentFile` | Arquivos enviados (md/txt/pdf/docx/…) |
| Notion | `Notion` | REST read-only, token criptografado, incremental via `last_edited_time` |
| REST API | `RestApi` | Endpoint `GET` JSON; `itemsPath` dot-path + mapeamento de campos (`titleField`/`contentFields`/`idField`/`urlField`), paginação `pageParam`; headers criptografados (`restapi:{id}`) |
| Banco SQL | `SqlDatabase` | `sqlite` / `postgres`; guard SELECT-only (sem keywords de escrita fora de literais/comentários), SQLite `Mode=ReadOnly`, Postgres transação `READ ONLY`; connection string criptografada (`sql:{id}`); `maxRows`/`Truncated` |
| RSS / Atom | `RssFeed` | RSS 2.0 + Atom 1.0; sync incremental por fingerprint `guid`; `fetchFullContent` opcional (baixa a página linkada); `forceRefresh` reprocessa tudo |
| AWS S3 | `AwsS3` | Staging de bucket, sync incremental por ETag |
| Azure Files | `AzureFiles` | Crawl de share com staging incremental |
| OCI Object Storage | `OciStorage` | Endpoint S3-compatível, mesmo modelo de staging |
| Google Drive | `GoogleDrive` | Links compartilhados de pasta/arquivo |
| YouTube | `YouTube` | Transcrições de vídeo/canal (`urls`), auto-captions + timestamps, `maxVideos`, `language` |
| Repositório Git | `GitRepository` | GitHub/GitLab/Gitea REST read-only (`repoUrl`, `branch`, `path`, `includePatterns`/`excludePatterns`); fast-path por commit-SHA + fingerprint por blob; PAT opcional `git:{id}`; SSRF guard (`allowPrivateHosts` opt-in) |
| Documento Unstructured | `UnstructuredDocument` | Pasta/arquivos parseados via Unstructured API (`apiUrl`, `strategy` — `auto`/`fast`/OCR forçado para imagens, `pdf_infer_table_structure`); chave opcional `unstructured:{id}` |
| Áudio / reuniões | `AudioTranscription` | AssemblyAI ou Whisper self-hosted (`provider`, `endpoint`, `language`, capítulos + diarization); chave opcional `audio:{id}` |
| Proxy MCP | `McpProxy` | Somente catálogo — passthrough de `tools/list` upstream, não ingerível |

O sync roda numa **fila assíncrona persistida**: `POST /sources/{id}/sync` retorna `202 + jobId` (contadores por documento, `cancel`, `?wait=true` para o contrato síncrono legado); `POST /sources/{id}/reindex` força re-chunk/re-embed, opcionalmente seletivo por versão do chunker. Conectores de cloud fazem staging local dos objetos e fazem diff por ETag; seus secrets ficam no integration-secret store. O chunking é structure-aware (markdown/código/config) e cada fonte pode optar por **chunking semântico** (fronteiras por breakpoints de embeddings) via `{"chunking":"semantic"}` no dialog da fonte.


## MCP tools

O catálogo é dinâmico — `tools/list` reconstrói sempre que fontes, keys ou
integrações mudam, e sessões ativas recebem `tools/list_changed`.

| Tool | Descrição |
|---|---|
| `search_knowledge` | Busca semântica unificada em todas as fontes ativas — trechos ranqueados com fonte, título, score e URI |
| `ask_knowledge` | Responde perguntas sobre a base indexada; com provider de chat retorna resposta sintetizada com citações `[n]`, senão contexto bruto |
| `agent_chat` | Loop de agente multi-step (modelo → tools → modelo) sobre o catálogo ao vivo; read-only por padrão, `allowWrite` libera tools de escrita |
| `write_knowledge` | Persiste conteúdo na base — arquivo `.md` para fontes vault, documento indexado nas demais |
| `read_document` | Lê um documento markdown completo de um vault ativo pelo caminho relativo |
| `write_note` | Escreve uma nota markdown num vault ativo e re-indexa |
| `set_chat_settings` | Overrides por API key: endpoint, modelo e chave de API do chat LLM |
| `set_api_key_settings` | Overrides por API key: chaves de integração upstream (DeepWiki, Firecrawl, Tavily, Context7) |
| `query_{source_slug}` | Busca semântica escopada — uma tool por fonte ativa |
| `find_dependencies` / `find_dependents` | Travessia GraphRAG outbound/inbound com evidência por aresta (chunk + doc + fonte) |
| `find_path` / `analyze_impact` | Caminho mais curto entre entidades; blast radius de 1 hop com documentos de suporte |
| `search_graph_temporal` / `search_graph_recent` | GraphRAG temporal — janela por `timeStart`/`timeEnd` (RFC3339) ou recente (`1h`/`6h`/`24h`/`7d`) sobre `ObservedAt`/`ValidFrom`/`ValidTo` |
| `search_graph_relationships` / `search_graph_diverse` | Expansão de relacionamentos em 2 hops; resultados diversificados por cluster (low/med/high) |
| `search_graph_episode` | Retrieval escopado a um episódio de ingestão (`KgEpisode`) |
| `ask_question`, `read_wiki_structure`, `read_wiki_contents` | Proxy upstream DeepWiki |
| `firecrawl_*` | Upstream Firecrawl (scrape, search, crawl, map…) |
| `tavily_*` | Upstream Tavily (search, extract, map, crawl, research) |
| `resolve-library-id`, `query-docs` | Upstream Context7 — consulta de docs de bibliotecas |
| `Tools de McpProxy` | Passthrough de `tools/list` de servidores MCP arbitrários registrados como fontes |

Integrações podem ser ligadas/desligadas em **Settings → Integrações** —
um provider desligado some do catálogo sem reiniciar.

`search_knowledge`/`ask_knowledge` também aceitam `subQueries` (≤4 variantes de
query em paralelo fundidas via RRF), `windowSize` + `limitMode`/`autocutSensitivity`
(expansão por janela de vizinhos e poda autocut do tail no cotovelo do score) e
relaxamento hierárquico de filtros (`Search:Relaxation`). `ask_knowledge` suporta
`enableLiveActions` — Action-Augmented RAG: chunks com markers `mcp-tool` ou a
própria pergunta disparam chamadas live de tools MCP fundidas na resposta como
citações `[Live Tool]`; `search_knowledge` retorna `suggestedActions` para as
mesmas tools como candidatos de follow-up. Tabela completa: `docs/pt/API.md`.

## Conectando Agentes de IA (Prompt para o LLM)

Para conectar um agente de IA ou assistente de código (como Claude Code, OpenCode, Cursor ou Devin) ao Knowledge MCP Hub, copie e envie o prompt abaixo para o assistente:

```text
Configure o servidor MCP do Knowledge Hub no seu ambiente para acessar a base de conhecimento e ferramentas RAG da organização:

1. Parâmetros de Conexão:
- URL: http://<host>:5000/mcp (ou SSE legado em http://<host>:5000/mcp/sse?access_token=aft_SUA_CHAVE)
- Transporte: Streamable HTTP
- Cabeçalho de Autenticação: Authorization: Bearer aft_SUA_CHAVE
(Gere uma chave em /api-keys se ainda não tiver).

2. Exemplos de Configuração por Cliente:
- Claude Code:
  claude mcp add --transport http knowledge http://<host>:5000/mcp --header "Authorization: Bearer aft_SUA_CHAVE"

- OpenCode (opencode.json):
  {
    "mcp": {
      "knowledge": {
        "type": "remote",
        "url": "http://<host>:5000/mcp",
        "headers": {
          "Authorization": "Bearer aft_SUA_CHAVE"
        }
      }
    }
  }

- Cursor / Devin / Genérico:
  Adicione o servidor MCP HTTP com URL "http://<host>:5000/mcp" e cabeçalho "Authorization: Bearer aft_SUA_CHAVE".

3. Protocolo de Onboarding (obrigatório na primeira sessão):
- Primeiro use ask_question para perguntar sobre o repositório, consultando a
  documentação com read_wiki_contents antes de assumir regras de negócio.
- Ao concluir cada tarefa, registre o que foi feito com write_note.
- Quando precisar criar conhecimento ou memória persistente, use write_knowledge.

4. Recomendações de Uso das Ferramentas:
- search_knowledge(query, topK): Execute buscas semânticas e híbridas (BM25 + vetorial) para obter contexto antes de implementar código.
- ask_knowledge(question, topK): Faça perguntas conceituais para obter respostas sintetizadas e fundamentadas com citações.
- find_dependencies / analyze_impact: Avalie o grafo de entidades e dependências (GraphRAG) ao planejar refatorações.
- read_document / write_note / write_knowledge: Acesse documentos e registre notas ou conhecimento no cofre Obsidian conectado.
- set_chat_settings / set_api_key_settings: Configure seu modelo de chat ou chaves de integração upstream se desejar overrides para sua sessão.
```

## Configuração

```jsonc
{
  "Database": { "Path": "knowledgehub.db" },   // ou KnowledgeHub:DatabasePath
  "Embeddings": {
    "Provider": "deterministic",               // deterministic | ollama | openai | onnx
    "Endpoint": "http://localhost:11434",
    "ApiKey": "",
    "Model": "nomic-embed-text",
    "Dimensions": 384,
    "ModelPath": "models/all-MiniLM-L6-v2"     // Provider=onnx — model.onnx + vocab.txt dir
  },
  "VectorStore": {
    "Provider": "sqlite",                      // sqlite | sqlite-vec | postgres (pgvector)
    "ConnectionString": "",
    "Postgres": {                              // VectorStore:Postgres — tuning do pgvector
      "HnswThreshold": 1000,                   // linhas antes do índice HNSW ser criado
      "HnswM": 16, "HnswEfConstruction": 64, "HnswEfSearch": 40,
      "IterativeScan": false,                  // pgvector ≥0.8 — scans filtrados preservam topK
      "StorageType": "vector",                 // vector | halfvec (pgvector ≥0.7, ≤2000 dims)
      "AllowStorageMigration": false,          // gate de consent p/ ALTER COLUMN … TYPE halfvec
      "MinPoolSize": 5, "MaxPoolSize": 50
    }
  },
  "Cache": {
    "Provider": "memory",                      // memory | redis
    "Redis": { "ConnectionString": "" },       // host.docker.internal:6379 ao usar Redis
    "ToolCacheEnabled": true,                  // cache de resultados de tools MCP (região mcp:tool)
    "ToolCacheTtlMinutes": 60,
    "L1Enabled": true,                         // L1 in-process na frente do redis L2
    "L1MaxTtlMinutes": 5,                      // teto de staleness do L1
    "DefaultTtlMinutes": 10,                   // fallback para regiões desconhecidas
    "RegionTtlMinutes": {                      // TTLs por região de key (prefixo mais longo)
      "emb": 1440, "search": 5, "ans": 10, "mcp:tool": 60,
      "rewrite": 1440, "expand": 60, "index": 10080, "secret": 60
    },
    "AnswerCache": { "Enabled": false, "TtlSeconds": 600 }
  },
  "Search": {
    "Lexical": { "Enabled": true },            // perna FTS5 do retrieval híbrido
    "QueryRewrite": { "Enabled": false, "LexicalToo": false },
    "Rerank": { "Enabled": false, "MaxCandidates": 50 },
    "LimitMode": "autocut",                    // fixed | autocut — poda o tail do score no cotovelo
    "Autocut": { "Sensitivity": 1, "MaxClamp": 20 },
    "Expansion": { "WindowThresholdPercent": 80 }, // gate de score do windowExpand (% do top normalizado)
    "Relaxation": { "Enabled": true, "MinResults": 1 } // queda hierárquica de filtro: pathPrefix → sourceId → global
  },
  "Agent": {
    "EnableDynamicActionBridge": true,         // ask_knowledge executa tools MCP live (markers mcp-tool)
    "MaxChainedDynamicCalls": 3,
    "ContextManagement": {                     // ChainAst repair + compactação de histórico no agent_chat
      "EnableChainCompaction": true,
      "MaxTotalHistoryBytes": 65536,
      "MaxBodyPairBytes": 16384,
      "KeepMinLastSections": 2,
      "AutoRepairBrokenToolCalls": true
    }
  },
  "Resilience": {
    "Fallback": {                              // Resilience:Fallback — motor de fallback provider/tool
      "Mode": "disabled",                      // disabled | observe | enforce
      "MaxFallbackAttempts": 2,
      "ChatFallbacks": [                       // alternates ordenados; apiKey write-only (criptografada)
        { "provider": "openai", "endpoint": "https://api.openai.com/v1", "model": "gpt-4o-mini", "apiKey": "sk-..." },
        { "provider": "ollama", "endpoint": "http://localhost:11434", "model": "llama3.2" }
      ],
      "ToolCapabilities": {                    // capacidade → providers equivalentes ordenados
        "WebSearch": ["tavily", "firecrawl", "duckduckgo"],
        "DeepDocLookup": ["deepwiki", "context7", "internal_fts"]
      }
    }
  },
  "RateLimiting": {                            // por partição: api key → usuário → IP
    "Enabled": true,
    "LlmPermitLimit": 20, "LlmWindowSeconds": 60,
    "AnonymousLlmPermitLimit": 5,
    "SyncPermitLimit": 10, "SyncWindowSeconds": 3600,
    "GeneralPermitLimit": 300, "GeneralWindowSeconds": 60,
    "TrustForwardedHeaders": false
  },
  "Security": {
    "Injection": { "ExcludeFlagged": true }    // remove chunks flagados dos resultados
  },
  "Graph": {                                   // GraphRAG — também editável em /settings
    "Enabled": true,
    "MaxChunksPerSync": 200,
    "MaxChunkChars": 2000,
    "MaxResults": 200
  },
  "Telemetry": {
    "Otlp": { "Endpoint": "" },                // exporter OTLP de traces/metrics
    "Metrics": { "Prometheus": false }         // endpoint de scrape /metrics
  },
  "Chat": {                                    // opcional global default
    "Endpoint": "http://localhost:11434",
    "Model": "llama3.2",
    "ApiKey": ""
  },
  "DeepWiki": {                                // proxy MCP upstream
    "Enabled": true,
    "Endpoint": "https://mcp.deepwiki.com/mcp",
    "PrivateEndpoint": "https://mcp.devin.ai/mcp",
    "ApiKey": ""
  },
  "Firecrawl": {
    "Enabled": true,
    "Endpoint": "https://mcp.firecrawl.dev/v2/mcp",
    "ApiKey": "",
    "TimeoutSeconds": 300
  },
  "Tavily": {
    "Enabled": true,
    "Endpoint": "https://mcp.tavily.com/mcp",
    "ApiKey": "",
    "TimeoutSeconds": 120
  },
  "Context7": {
    "Enabled": true,
    "Endpoint": "https://mcp.context7.com/mcp",
    "ApiKey": "",
    "TimeoutSeconds": 60
  },
  "Mcp": {
    "SessionMode": "StatefulForInitializeClients"  // Stateless | Stateful | StatefulForInitializeClients
  },
  "Auth": {
    "AdminInitialPassword": "123qwe"           // senha seed para usuário admin
  },
  "Serilog": {
    "MinimumLevel": {                          // ajustável em runtime via /api/settings/log-level
      "Default": "Information",
      "Override": { "Microsoft": "Warning", "System": "Warning" }
    },
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": {            // rolling diário, retenção de 14 dias
        "path": "logs/knowledgehub-.log",
        "rollingInterval": "Day",
        "retainedFileCountLimit": 14 } }
    ]
  }
}
```

Variáveis de ambiente sobrescrevem `appsettings.json` (duplo underscore → chave aninhada). Veja `.env.example` para a lista completa.

**Postgres via `.env` (compose).** O `docker-compose.yml` lê o `.env` (`env_file`, `required: false`) e compõe `VectorStore__ConnectionString` a partir das vars discretas `POSTGRES_HOST/PORT/DB/USER/PASSWORD` — `VECTORSTORE_CONNECTIONSTRING` sobrescreve a composição quando definida. `host.docker.internal` alcança o Postgres do host Docker via `extra_hosts`; o `pg_hba.conf` do host precisa autorizar a subnet do container para o banco/usuário alvo, e a extensão `vector` precisa existir no banco (`PostgresVectorStore` roda `CREATE EXTENSION IF NOT EXISTS vector` no init — conceda `CREATE` no banco ao usuário da app ou pré-crie a extensão como superuser).
**Um backend para tudo.** `DATABASE_PROVIDER` (`auto`|`postgres`|`sqlite`, padrão `auto`) escolhe um único armazenamento para o catálogo EF Core *e* o vector store: `auto` faz probe no alvo `POSTGRES_*`/`DATABASE_CONNECTIONSTRING` e cai em SQLite com erro logado quando inalcançável; `postgres` sem connection string é erro de startup. O primeiro boot em Postgres preenche o catálogo a partir do arquivo SQLite existente (one-shot, GUIDs preservados). A busca lexical usa `tsvector`+GIN no Postgres e FTS5 no SQLite. `VECTORSTORE_PROVIDER` permanece como override explícito para modo misto.


**Logging.** Request logging estruturado (Serilog): ruído de `/health` e assets estáticos em Debug, 5xx em Error; toda request carrega `x-request-id`/`RequestId`. Secrets são mascarados por um enricher — keys `*key*`/`*token*`/`*secret*`/`*password*`/`*connectionstring*` e padrões `aft_*`/`ctx7sk-*`/`sk-*`/`Bearer` saem como `***REDACTED***` em toda propriedade. Com `Telemetry:Otlp:Endpoint` configurado, os logs também seguem para o mesmo backend OTLP de traces/metrics. O nível pode subir temporariamente via `PUT /api/settings/log-level` (`minutes` 0–120). No Docker, o file sink escreve em `./logs` (volume montado — ver abaixo).

**Segurança do Redis.** Um `Cache:Provider=redis` sem autenticação expõe resultados de busca e embeddings cacheados a qualquer um que alcance a porta — o servidor avisa no startup. Hardening: configure `requirepass`/ACLs na instância e adicione `password=...` em `Cache:Redis:ConnectionString`, faça bind do Redis apenas em localhost/interfaces privadas, ou permaneça em `Cache:Provider=memory` (padrão).

## Estrutura do Repositório

```text
KnowledgeHub/
├── src/
│   ├── KnowledgeHub.Shared/      # DTOs, contratos JSON-RPC 2.0 / MCP, enums
│   ├── KnowledgeHub.Client/      # SPA Blazor WASM (BootstrapBlazor)
│   ├── KnowledgeHub.Server/      # host Kestrel, Minimal APIs, EF Core SQLite, sync
│   └── KnowledgeHub.McpEngine/   # MCP nativo: sessões SSE, dispatcher JSON-RPC
├── tests/
│   ├── KnowledgeHub.Tests.Unit/
│   └── KnowledgeHub.Tests.Integration/
├── .specs/                       # SPECs SDD — fonte da verdade para features
├── docs/architecture/            # Diagramas e ADRs de arquitetura
├── .claude/skills/               # skills versionadas (afonsoft/skills)
├── skills-lock.json              # hashes SHA-256 das skills instaladas
├── backup.sh / restore.sh        # backup/restore SQLite + uploads
├── docker-compose.yml            # deploy containerizado
└── LICENSE                       # MIT — Afonso Dutra Nogueira Filho, 2026
```

## Stack Tecnológica

| Camada | Tecnologia | Versão |
|-------|-----------|---------|
| Linguagem | C# | 14 (.NET 10) |
| Framework | ASP.NET Core (Kestrel) | 10.x |
| Frontend | Blazor WebAssembly (BootstrapBlazor) | 10.x |
| Banco de Dados | SQLite (EF Core) + sqlite-vec | 10.x |
| Vector Store | sqlite-vec / pgvector | 0.1.9 / 0.3.2 |
| Embeddings | ONNX Runtime (all-MiniLM-L6-v2) | 1.29.0 |
| AI SDK | Microsoft.Extensions.AI.Abstractions | 10.9.0 |
| Protocolo MCP | ModelContextProtocol | 2.2.0 |
| CI/CD | GitHub Actions | — |
| Container | Docker Compose | — |

## Começando

### Pré-requisitos

- .NET SDK 10.0.x ([global.json](global.json))
- Node.js ≥ 20.x (para ferramentas de desenvolvimento)
- Docker & Docker Compose (opcional, para deploy containerizado)

### Instalar

```bash
git clone https://github.com/afonsoft/LangGraph-UI.git
cd LangGraph-UI
```

### Configurar

```bash
cp .env.example .env
# Edite .env com seus valores (chaves de API, endpoints, etc.)
```

### Executar (desenvolvimento)

```bash
dotnet build KnowledgeHub.slnx
dotnet ef database update -p src/KnowledgeHub.Server
dotnet run --project src/KnowledgeHub.Server
```

Abra http://localhost:5000 e faça login com `admin` / `123qwe`.

### Executar (Docker)

```bash
mkdir -p data logs && chown -R 1654:1654 data logs   # o container roda como uid 1654 (app) — ver nota
docker compose up -d
```

Acesse em http://localhost:5000.

> **Caveat de permissão `./data` e `./logs`.** Quando os diretórios do host não existem, o Docker os cria como `root`, mas o container roda como `app` (uid 1654) — o file sink do Serilog **e** a migração do SQLite falham (a migração impede o boot). Pré-crie com `mkdir -p data logs && chown -R 1654:1654 data logs` (ou corrija uma vez após o primeiro `up`). Logs persistem entre recriações/upgrades no volume `./logs` — rolling diário, retenção de 14 dias, secrets redigidos (`***REDACTED***`).

## Testes & Cobertura

```bash
dotnet test                                          # testes unitários + integração
dotnet test --collect:"XPlat Code Coverage"          # com cobertura Coverlet
dotnet format KnowledgeHub.slnx --verify-no-changes  # gate de formatação
```

| Métrica | Valor |
|---|---|
| **Total de testes** | 961 (716 unitários + 245 integração) |
| **Taxa de aprovação** | 100% |
| **Cobertura de linhas** | 78% (23 961 / 30 697 linhas cobráveis) |
| **Cobertura de branches** | 58,1% (5 192 / 8 934 branches) |
| **Cobertura de métodos** | 79,8% (1 752 / 2 194 métodos) |
| **Data (contagem/cobertura)** | testes 2026-09-26 · cobertura 2026-09-24 |

Gates do CI: Build (0 warnings), Testes Unitários, Testes de Integração (SQLite), Validação Cliente Blazor WASM, Build Imagem Docker, Qualidade de Código (SonarQube), Security Scan, `dotnet format --verify-no-changes` (0 arquivos alterados de 371).

## Arquitetura

Knowledge MCP Hub segue um padrão de arquitetura limpa com quatro camadas:

- **Shared**: DTOs, contratos MCP, enums — consumido por todos os projetos
- **Client**: SPA Blazor WASM com componentes BootstrapBlazor, suporte PWA, SignalR para atualizações em tempo real
- **Server**: Host Kestrel com Minimal APIs, persistência EF Core SQLite, autenticação, validação de configuração
- **McpEngine**: Servidor MCP nativo implementando transportes Streamable HTTP e SSE legado, dispatcher JSON-RPC 2.0, gerenciamento de sessões

Decisões arquiteturais chave:

- Deploy single-process: SPA + API + servidor MCP em um único processo Kestrel
- Modo de sessão MCP híbrido: stateful para clients com handshake initialize, stateless para clients modernos
- Embeddings plugáveis: determinístico, Ollama, OpenAI, ou ONNX Runtime local
- Múltiplos backends de vector store: sqlite-vec (KNN) ou PostgreSQL com pgvector (HNSW, upserts em lote, metadados de provenance)
- Retrieval híbrido: FTS5 + RRF vetorial com reescrita e reranking opcionais; medido pelo harness de eval embutido
- Profundidade de retrieval: diversidade MMR + quota por documento + score floor, corrective-RAG (grading → retry → abstenção), reescrita history-aware, expansão multi-query + HyDE, enriquecimento contextual de chunks (`SectionPath`), expansão hierárquica de contexto (`contextExpand`), braço de knowledge-graph opcional (`useGraph`), embeddings assimétricos query/documento
- Fila de ingestão assíncrona: jobs persistidos com contadores por doc, cancelamento, reindex seletivo por versão do chunker, auto-sync roteado pela fila; chunking semântico por fonte (opt-in)
- GraphRAG em tabelas de adjacência SQLite: extração LLM na ingestão (opt-in por source), tools de travessia com provenance
- Segurança: guarda de prompt-injection (flag → excluir → auditar), escopo de sources/tools por chave, rate limiting particionado com overrides por chave
- Cache distribuído opt-in: in-memory (padrão) ou Redis — híbrido L1(in-process)→L2(Redis), TTLs por região, invalidação distribuída via pub/sub `kh:invalidate`
- Logging estruturado: request logging Serilog (health/static → Debug, 5xx → Error, `x-request-id`), file sink rolling diário + sink OTLP opcional, redaction de secrets (`***REDACTED***`), nível em runtime via `/api/settings/log-level` com auto-reset
- OpenTelemetry: traces + métricas com exporters OTLP/Prometheus opt-in
- Proxies MCP upstream: DeepWiki, Firecrawl, Tavily, Context7, mais sources `McpProxy` arbitrários com secrets criptografados
- Customização por chave de API: chat, integrações, escopos e rate limits por chave

Veja [docs/architecture/](docs/architecture/README.md) para ADRs, o diagrama editável e a visão de runtime interativa — e [docs/pt/ARCHITECTURE.md](docs/pt/ARCHITECTURE.md) para o design detalhado.

## Visões de Negócio e Técnica

### Valor de Negócio

Knowledge MCP Hub resolve o problema de conhecimento organizacional fragmentado fornecendo uma plataforma unificada que:

- Ingera conhecimento de múltiplas fontes (vaults Obsidian, páginas web, documentos, Notion, APIs, bancos de dados SQL, AWS S3, Azure Files, OCI Object Storage, Google Drive)
- Permite consultas em linguagem natural com retrieval híbrido (busca full-text + similaridade vetorial com reranking RRF)
- Fornece capacidades agênticas com tool-calling, aprovações HITL e threads de conversação com sumarização
- Expõe conhecimento tanto via API REST quanto via Model Context Protocol para integração com agentes de IA
- Roda standalone sem dependências externas (SQLite, modelos embarcados) para fácil deploy

### Decisões Técnicas

- **.NET 10**: Framework mais recente com minimal APIs, suporte AOT compilation e melhorias de performance
- **Blazor WASM**: Frontend type-safe compartilhando DTOs com backend, suporte PWA para capacidade offline
- **Model Context Protocol**: Protocolo padrão para integração de ferramentas de agentes de IA, permitindo integração seamless com Cursor, Claude Desktop e outros clients MCP
- **SQLite + sqlite-vec**: Persistência zero-config com busca vetorial KNN nativa, PostgreSQL/pgvector opcional para escala
- **ONNX Runtime embeddings**: Provedor de embeddings local (all-MiniLM-L6-v2) para privacidade e embeddings sem custo
- **Clean Architecture**: Separação clara de responsabilidades com projetos Shared/Client/Server/McpEngine

## Licença & Status

- **Licença**: MIT — veja [LICENSE](LICENSE)
- **Status**: Desenvolvimento ativo
- **Autor**: Afonso Dutra Nogueira Filho

## Links

### Documentação

- [English](README.md) — versão em inglês · [docs/en/](docs/en/) para os guias em inglês
- [Documentação de Arquitetura](docs/pt/ARCHITECTURE.md) — Design detalhado do sistema · [ADRs](docs/architecture/README.md) — registros de decisão
- [Guia de Contribuição](docs/pt/CONTRIBUTING.md) — Como contribuir
- [Guia de Instalação](docs/pt/INSTALL.md) — Setup específico por plataforma
- [Documentação da API](docs/pt/API.md) — Referência da API REST
- [Changelog](CHANGELOG.md) — Mudanças notáveis
- [Issues no GitHub](https://github.com/afonsoft/LangGraph-UI/issues) — Relatórios de bugs e solicitações de features
- [Releases](https://github.com/afonsoft/LangGraph-UI/releases) — Histórico de versões

### Referências

- [Model Context Protocol](https://modelcontextprotocol.io) — especificação do protocolo implementado pelo `KnowledgeHub.McpEngine`
- [pgvector](https://github.com/pgvector/pgvector) — extensão vetorial do Postgres (`vector`/`halfvec`, HNSW, scans filtrados iterativos)
- [sqlite-vec](https://github.com/asg017/sqlite-vec) — extensão KNN do SQLite para o vector store embarcado
- [Npgsql](https://www.npgsql.org/) — driver .NET do Postgres usado pelo store pgvector
- [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/) — abstrações `IChatClient`/`IEmbeddingGenerator` para os providers
- [ONNX Runtime](https://onnxruntime.ai/) + [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2) — provider de embeddings local
- [BootstrapBlazor](https://www.blazor.zone/) — biblioteca de componentes da SPA
- [SQLite FTS5](https://sqlite.org/fts5.html) — braço lexical do retrieval híbrido
