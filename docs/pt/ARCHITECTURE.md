# Arquitetura do KnowledgeHub

## Visão geral

O KnowledgeHub é uma plataforma de conhecimento standalone tudo-em-um em .NET 10. Combina uma SPA administrativa Blazor WebAssembly, uma API REST de gerenciamento, um servidor MCP (Model Context Protocol) nativo, um endpoint A2A v1.0 (Agent-to-Agent) e um hub de atividade SignalR em um único processo Kestrel.

> Diagramas completos, ADRs e a visão de runtime interativa ficam em [`docs/architecture/`](../architecture/) — veja `system-architecture.md` e o `README.md` de lá.

## Camadas do sistema

```
     +---------------------------+   +---------------------------+
     |    External MCP Clients   |   |      Agentes A2A (v1.0)   |
     | (Cursor, Claude Desktop)  |   | (JSON-RPC / HTTP+JSON)    |
     +--------------+------------+   +--------------+------------+
                    | Streamable HTTP / SSE      | /.well-known/agent-card.json
                    |                            | /a2a  (mesmo escopo aft_*)
                    v                            v
+------------------+             +---------------+-----------+
|    Blazor SPA    |             |  KnowledgeHub.Server      |
| (Admin PWA / UI) |             |  (Kestrel, Minimal APIs)  |
+--------+---------+             +---------------+-----------+
         | REST / SignalR                        |
         v                                       v
+------------------+             +---------------+-----------+
| KnowledgeHub.    |             | KnowledgeHub.McpEngine    |
|   Client         |             | (JSON-RPC 2.0 Dispatcher) |
+------------------+             +---------------+-----------+
         |                                       |
         +-------------------+-------------------+
                             |
                             v
                 +-----------+-----------+
                 | KnowledgeHub.Shared   |
                 | (Contracts, DTOs)     |
                 +-----------+-----------+
                             |
                             v
                 +-----------+-----------+
                 |    EF Core SQLite     |
                 | (sqlite-vec / pgvector) |
                 +-----------------------+
```

## Projetos

1. **KnowledgeHub.Shared**: DTOs compartilhados, contratos JSON-RPC 2.0 / MCP, enums.
2. **KnowledgeHub.Client**: SPA Blazor WebAssembly (BootstrapBlazor) — administração, monitor MCP, playground, chat, aprovações, eval, settings.
3. **KnowledgeHub.Server**: host Kestrel — Minimal APIs, EF Core SQLite, engine de conectores/sync, busca híbrida, loops de resposta/agente, GraphRAG, auth (cookie + API keys), rate limiting, proxies MCP upstream, OpenTelemetry.
4. **KnowledgeHub.McpEngine**: servidor MCP nativo — sessões SSE, transporte Streamable HTTP, dispatch JSON-RPC.

## Fluxo de dados e retrieval

- **Ingestão**: conectores ingerem vaults Obsidian (local/WebDAV), páginas web, arquivos, Notion, APIs REST, bancos SQL e cloud storage (AWS S3, Azure Files, OCI Object Storage via endpoint S3-compatível, links compartilhados do Google Drive); o sync roda numa fila de jobs assíncrona persistida (202+jobId, contadores por doc, cancel, reindex seletivo por versão do chunker); chunking consciente de estrutura para markdown/código/config mais chunking semântico opt-in por fonte (fronteiras por breakpoints de embeddings); todo chunk passa pelo sanitizador de prompt-injection (`SuspicionFlags` + auditoria `security_events`) antes do embedding.
- **GraphRAG**: com `Graph:Enabled` (default **ligado**) e opt-in por source (`"graph": true`), um LLM extrai entidades/relações em `KgNodes`/`KgEdges`/`KgAliases` com provenance completa — percorridas por `find_dependencies`, `find_dependents`, `find_path`, `analyze_impact` e integráveis ao retrieval via `useGraph`.
- **Retrieval híbrido**: FTS5 lexical + KNN vetorial (sqlite-vec ou pgvector HNSW) fundidos via RRF (k=60); diversidade MMR + quota por documento + score floor; reescrita por LLM (history-aware) e reranking opcionais; expansão multi-query + HyDE; loop corrective-RAG (grading → retry → abstenção); enriquecimento contextual de chunks (`SectionPath`) e expansão hierárquica de contexto (`contextExpand`); embeddings assimétricos query/documento; filtros de metadados; autorização por API key; exclusão de chunks flagados.
- **Síntese e agente**: `IChatClient` (Ollama/OpenAI) responde com citações `[n]`; loop de agente com tool-calling, aprovações HITL, streaming SSE limitado e threads com sumarização.
- **Observabilidade**: `Meter`/`ActivitySource` instrumentam estágios de busca, chamadas LLM, iterações de agente, syncs e regiões de cache — exportados via OTLP ou Prometheus (`/metrics`) opt-in.

## Decisões de arquitetura

As decisões-chave estão registradas como ADRs em [`docs/architecture/`](../architecture/README.md) — monólito de processo único, SQLite-first, sessões MCP híbridas, auth dupla, retrieval híbrido, GraphRAG em tabelas de adjacência, rate limiting particionado, guarda de injeção, observabilidade OTel, proxies upstream.
