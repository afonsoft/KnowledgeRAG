# API REST

Todos os endpoints `/api/*` exigem autenticação (sessão por cookie ou `Authorization: Bearer aft_...`), exceto `POST /api/auth/login` e `/health*`. Rate limits ativos (policies `llm`/`sync`/`general`; 429 + `Retry-After` quando excedido).

## Auth

| Rota | Propósito |
|---|---|
| `POST /api/auth/login` | `{username, password}` → cookie de sessão |
| `GET /api/auth/me` | usuário atual + `mustChangePassword` |
| `POST /api/auth/logout` | limpa a sessão |
| `POST /api/auth/change-password` | `{currentPassword, newPassword}` → 204 |

## API keys (somente sessão por cookie)

| Rota | Propósito |
|---|---|
| `GET/POST /api/apikeys` · `DELETE /api/apikeys/{id}` | gerencia keys (secret exibido na criação) |
| `GET /api/apikeys/{id}/usage` · `GET /api/apikeys/{id}/secret` | feed de auditoria de uso por key · reveal do secret quando `canReveal` (cópia protegida via DataProtection) |
| `PUT/DELETE /api/api-keys/{id}/rate-limit` | overrides de rate limit LLM/sync por key (campo `null` = herda global) |
| `PUT/DELETE /api/api-keys/{id}/scopes` | restringe a key a sources/tools permitidos |
| `GET/PUT/DELETE /api/api-keys/{id}/settings/chat` | endpoint/modelo/key de chat por key |
| `PUT/DELETE /api/api-keys/{id}/settings/integrations/{provider}` | secrets de integração por key |

## Sources e ingestão

| Rota | Propósito |
|---|---|
| `GET/POST /api/sources` · `GET/PUT/DELETE /api/sources/{id}` | CRUD de sources (configs redigidos) |
| `POST /api/sources/{id}/sync` | **assíncrono** — `202 {jobId,status,existing}`; `?wait=true` mantém o `SyncResultDto` síncrono legado |
| `POST /api/sources/{id}/reindex` | `202` — força re-chunk/re-embed mesmo sem alteração de conteúdo |
| `POST /api/sources/{id}/activate` · `/deactivate` | ativação |
| `GET /api/sources/{id}/documents` · `/usage` | documentos + estatísticas |

### Jobs de ingestão

| Rota | Propósito |
|---|---|
| `GET /api/ingestion/jobs?sourceId=&status=&limit=` | lista jobs (filtro por fonte + status) |
| `GET /api/ingestion/jobs/{id}` | um job — status, contadores por doc, erro |
| `POST /api/ingestion/jobs/{id}/cancel` | cancela job em fila (`409` se já em execução/terminal) |

## Busca, respostas, agente

| Rota | Propósito |
|---|---|
| `GET/POST /api/search` | busca híbrida (`mode`, `topK`, filtros `sourceType`/`pathPrefix`/`indexedAfter`/`language`, `expand`, `contextExpand`, `useGraph`) |
| `POST /api/ask` · `POST /api/ask/stream` | resposta com citações; stream SSE |
| `POST /api/agent` · `POST /api/agent/stream` | loop de agente com tools; SSE (`token`/`tool_start`/`tool_end`/`awaiting_approval`/`done`/`error`, heartbeat 15 s) |
| `POST /api/agent/resume` | retoma um run de agente pausado após decisão de aprovação (`{approvalId}`) |
| `GET/POST /api/threads` · `GET/PUT/DELETE /api/threads/{id}` | threads de conversação (listar, criar, obter, renomear, deletar) |
| `GET/POST /api/threads/{id}/messages` | mensagens da thread — histórico e append |

## Aprovações e tools

| Rota | Propósito |
|---|---|
| `GET /api/approvals` · `POST /api/approvals/{id}/approve|deny` | fila de aprovação HITL |
| `GET /api/tools` · `POST /api/tools/{name}` | fachada REST sobre o catálogo MCP vivo (mesmas tools de `tools/list`/`tools/call`) |

## Settings (policy Operational)

| Rota | Propósito |
|---|---|
| `GET/PUT/DELETE /api/settings/chat` · `POST /api/settings/chat/test` · `DELETE /api/settings/chat/apikey` | config de chat persistida + teste de conectividade; `/apikey` limpa só a key armazenada |
| `GET/PUT/DELETE /api/settings/embeddings` · `DELETE /api/settings/embeddings/apikey` | config de embeddings persistida — GET carimba `stampedModelId`, `providerError`, `storeDimensions`; `/apikey` limpa só a key armazenada |
| `GET/PUT/DELETE /api/settings/graph` | GraphRAG `enabled`, `maxChunksPerSync`, `maxChunkChars`, `maxResults` — aplica sem restart |
| `GET/PUT/DELETE /api/settings/assistant` · `POST /api/settings/assistant/test` · `DELETE /api/settings/assistant/apikey` | provider assistente de baixo custo (`enabled`, `mode` local|remote, `endpoint`, `model`, `route`, `timeoutSeconds`); modo remote resolve um Agent Card A2A; key mascarada — `/apikey` limpa só a key armazenada |
| `GET/PUT/DELETE /api/settings/integrations/{provider}` | chaves de integração mascaradas (firecrawl, deepwiki, tavily, context7) |
| `GET /api/settings/cache` · `POST /api/settings/cache/clear` · `DELETE /api/settings/cache/keys/{*key}` | stats de cache (keys rastreadas do processo + overlay do servidor Redis via SCAN/INFO — `serverReported`/`partial`) + limpeza de todas as regiões + remoção de um padrão de key |
| `GET /api/settings/database` | estatísticas do banco (contagem por tabela, tamanhos, resumo do vector store) |
| `GET/PUT /api/settings/log-level` | nível de log em runtime (`LoggingLevelSwitch`); `PUT {level, minutes}` — `minutes` 0–120 agenda reset automático ao nível configurado |

## Segurança e eval

| Rota | Propósito |
|---|---|
| `GET /api/security/events` | auditoria de flags de prompt-injection (só metadados — nunca conteúdo bruto) |
| `POST /api/eval/run` · `GET /api/eval/runs` · `GET /api/eval/runs/{id}` | harness de eval de retrieval (Recall@K/P@K/MRR/faithfulness, latências p50/p95/p99) — request aceita `baseline` (nome) e `gate` (regras `[{metric,direction,threshold}]`), report traz `gateResult` + `topRegressions` |
| `GET/POST /api/eval/baselines` | lista baselines nomeados; `POST {name, runId}` promove um run |
| `GET /api/v1/evaluation/stats` | stats da tríade de qualidade RAG — médias de context relevance, groundedness e answer relevance, taxa de alucinação e queries flagged recentes (últimos 7 dias); alimenta o dashboard `/rag-quality` |
| `GET /api/v1/evidence/sessions/{sessionId}/bundle` | bundle de evidências à prova de adulteração da sessão — cadeia append-only `EvidenceReceipt` assinada com HMAC-SHA256 (secret `evidence:master`); policy Operational |

### Args das tools search/ask

`search_knowledge` e `ask_knowledge` (tools MCP e fachada REST) aceitam, além de
`mode`/`topK`/filtros de metadados:

| Arg | Valores |
|---|---|
| `expand` | `off` (default) · `multi` (N rewrites fundidos via RRF) · `hyde` (doc hipotético no braço vetorial) · `both` |
| `contextExpand` | `none` · `window` (chunks vizinhos) · `section` (seção-pai) — anexa `context` a cada hit sem mudar o ranking |
| `useGraph` | bool — ativa o braço de knowledge graph (entity linking + evidência de 1 hop) |
| `subQueries` | string[] ≤4 — variantes de query buscadas em paralelo e fundidas via RRF (multi-query); blanks filtrados |
| `windowSize` | int 0–3 — expansão por janela de chunks vizinhos; `>0` implica `contextExpand=window`; hits abaixo de 80% do score normalizado do topo não expandem (`Search:Expansion:WindowThresholdPercent`) |
| `limitMode` | `fixed` (topK clássico) · `autocut` (default `Search:LimitMode` — poda a cauda longa no N-ésimo corte abrupto de score) |
| `autocutSensitivity` | int 1–3 — sensibilidade do autocut (N-ésimo corte), limitada por `Search:Autocut:MaxClamp` |
| `enableLiveActions` | bool — só `ask_knowledge`: Action-Augmented RAG executa tools MCP live indicadas pelos chunks (`<!-- mcp-tool: -->` markers) ou pela pergunta; saídas fundem como citações `[Live Tool]` e `liveToolExecutions` |

Respostas podem carregar `suggestedActions` (search — tool calls que o modelo pode emitir no turno seguinte), `expandedChunkIndices`/`windowExpanded` e os campos de relaxamento `isRelaxed`/`relaxedScope`/`appliedFilter`/`originalFilter`/`filterRelaxed` quando `Search:Relaxation` derruba um nível de filtro.

### Grafo — tools MCP

| Tool | Args | Propósito |
|---|---|---|
| `search_graph_temporal` | `query`, `timeStart`/`timeEnd` (RFC3339), `topK` | Retrieval de entidades/arestas por janela temporal sobre `ObservedAt`/`ValidFrom`/`ValidTo` |
| `search_graph_recent` | `query`, `window` (`1h`/`6h`/`24h`/`7d`), `topK` | Retrieval de grafo por janela recente |
| `search_graph_relationships` | `entity`, `depth` | Expansão de relacionamentos em 2 hops |
| `search_graph_diverse` | `query`, `diversity` (low/med/high) | Resultados de grafo diversificados por cluster |
| `search_graph_episode` | `episodeId` | Retrieval escopado a um episódio de ingestão (`KgEpisode`) |

### Grafo — endpoints REST

| Rota | Propósito |
|---|---|
| `GET /api/graph/nodes` | Listagem/filtro de nós para o viewer `/graph` |
| `GET /api/graph/nodes/{id}/edges` | Arestas de um nó (evidência por aresta) |
| `GET /api/graph/episodes` | Episódios de ingestão (`KgEpisode`) |
| `GET /api/graph/timeline` | Timeline temporal para os presets do viewer (1h/6h/24h/7d) |

## Outros

| Rota | Propósito |
|---|---|
| `GET /api/diagnostics/vectorstore` | diagnóstico do vector store — provider, host, banco, dimensão, contagem de chunks, tipo de storage (`vector`\|`halfvec`), estado do índice |
| `GET /api/mcp/capabilities` | modo de sessão MCP anunciado (`sessionMode`, `legacySse`) — anônimo |
| `/mcp` (+ `/mcp/sse`, `/mcp/message`) | transportes MCP — ver README |
| `/.well-known/agent-card.json` | Agent Card A2A — anônimo; skills `ask_knowledge`/`search_knowledge`/`agent_chat`/`read_document`, bearer `aft_*` |
| `/a2a` | bindings A2A v1.0 — JSON-RPC `message/send`·`tasks/*` e HTTP+JSON (`/a2a/message:send`); policy Operational + rate limit `llm` |
| `/hubs/mcp` | feed de atividade SignalR |
| `/metrics` | endpoint de scrape Prometheus (`Telemetry:Metrics:Prometheus=true`) |
| `/framework-assets/{stem}/{ext}` | espelho de assets `_framework` seguro para proxy |

## A2A (Agent-to-Agent v1.0)

A descoberta é anônima; a execução exige `Authorization: Bearer aft_*` e roda sob o escopo da chave (tools/fontes permitidas, gates de escrita, rate limit `llm`).

- `GET /.well-known/agent-card.json` — Agent Card (`protocolVersion` 1.0, bindings `JSONRPC` + `HTTP+JSON`, streaming ativo).
- `POST /a2a` — JSON-RPC `SendMessage`/`SendStreamingMessage`/`GetTask`/`ListTasks`/`CancelTask`; ou HTTP+JSON `POST /a2a/message:send`.
- Seleção de skill via metadata `{"skill": "ask_knowledge|search_knowledge|agent_chat|read_document"}` na message (default `ask_knowledge`); `{"arguments": {...}}` opcional complementa o texto (`question`/`query`/`message`/`path`).

```jsonc
// POST /a2a  (JSON-RPC)
{ "jsonrpc": "2.0", "id": 1, "method": "SendMessage",
  "params": { "message": { "messageId": "m1", "role": "ROLE_USER",
    "parts": [{ "text": "o que mudou na política de auth?" }],
    "metadata": { "skill": "search_knowledge" } } } }
```

### Durabilidade e push notifications

- **Tasks persistidas** — tasks de `SendMessage` ficam no banco do catálogo (`EfA2aTaskStore`), então `tasks/get` e `tasks/cancel` continuam funcionando após restarts e deploys. Tasks mais antigas que `A2a:TaskRetentionHours` (default `72`) são removidas periodicamente.
- **Webhooks de push** — inscreva-se via `SendMessageConfiguration.PushNotificationConfig` (inline no `SendMessage`) ou pelo CRUD de push-config (`tasks/pushNotificationConfig/{set,get,list,delete}`). A cada atualização da task o servidor faz POST do payload assinado com `X-KH-Signature` (HMAC da chave da evidence-chain) — verifique antes de confiar. Retry limitado (3× backoff exponencial); URLs são validadas por egress antes da primeira chamada (sem destinos privados/bloqueados, salvo `EgressPolicy:AllowPrivateAddresses`).
- **Proveniência de origem** — `write_knowledge`/`write_note` invocadas via A2A (ou MCP) gravam `origin: {channel, keyId, agentName, at}` no frontmatter do documento.
- `contextId` é um identificador do protocolo A2A (hex-32) — ele **não** é mapeado para threadIds do `agent_chat`.
