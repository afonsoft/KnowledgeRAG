# AGENTS.md

Thin reference — the single source of truth for this repository is [`CLAUDE.md`](CLAUDE.md).

## Project

**Knowledge MCP Hub** — all-in-one standalone .NET 10 platform: Blazor WebAssembly admin SPA + REST management API + native MCP server (HTTP/SSE, JSON-RPC 2.0) in a single Kestrel process. Agentic RAG over user-registered knowledge sources (Obsidian vaults, web pages, documents, APIs, SQL).

## Structure

```
KnowledgeHub.slnx
├── src/
│   ├── KnowledgeHub.Shared/      # DTOs, JSON-RPC 2.0 / MCP contracts, enums
│   ├── KnowledgeHub.Client/      # Blazor WebAssembly SPA (BootstrapBlazor)
│   ├── KnowledgeHub.Server/      # Kestrel host, Minimal APIs, EF Core SQLite, sync
│   └── KnowledgeHub.McpEngine/   # Native MCP: SSE sessions, JSON-RPC dispatcher
└── tests/
    ├── KnowledgeHub.Tests.Unit/
    └── tests/KnowledgeHub.Tests.Integration/
```

## Commands

```bash
dotnet build KnowledgeHub.slnx                    # build all
dotnet test                                       # run tests
dotnet run --project src/KnowledgeHub.Server      # serve http://localhost:5000
```

## Rules

- Never commit to `main`, `master` or `develop` — use `feature/{AgentLLM}-{YYYYMMDD}-{slug}`.
- `.github/workflows/` is protected.
- Never commit `.env`, `*.key`, `*.pem` or secrets.
- Specs live in `.specs/`; approved SPEC is the source of truth for implementation.

## MCP Server & Recomendações

O Knowledge MCP Hub expõe um servidor MCP nativo para agentes de IA (Claude Code, OpenCode, Cursor, Devin, etc.). Detalhes completos em [`CLAUDE.md`](CLAUDE.md).

- **Conexão**: Endpoint HTTP `/mcp` (Streamable HTTP) ou `/mcp/sse`. Header: `Authorization: Bearer aft_*` (gerar em `/api-keys`).
- **Grounding First**: Consulte `search_knowledge` ou `ask_knowledge` antes de assumir regras de negócio, contratos ou criar artefatos duplicados. Na primeira sessão, pergunte sobre o repositório com `ask_question` lendo a wiki via `read_wiki_contents`.
- **Registro de trabalho**: Sempre registre o que foi feito com `write_note`; conhecimento/memória durável com `write_knowledge`.
- **Impacto**: Utilize `find_dependencies` e `analyze_impact` para avaliar o impacto de alterações em componentes compartilhados.
- **Isolamento de Sessão**: Utilize `set_chat_settings` (chat LLM) e `set_api_key_settings` (integrações upstream) para customizar a chave da sua sessão.
- **Segurança**: Nunca exponha, printe ou commite chaves de autenticação `aft_*`.
