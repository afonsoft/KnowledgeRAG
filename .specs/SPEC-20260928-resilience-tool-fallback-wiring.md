# SPEC-20260928-resilience-tool-fallback-wiring

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `resilience-tool-fallback-wiring` |
| Type | `Feature` (completion of SPEC-20260927-tool-and-model-resilience-fallback scope) |
| Stack | `.NET 10 / C# / Microsoft.Extensions.AI / Blazor WASM` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260928-resilience-tool-fallback` |
| Ticket | `#385` — https://github.com/afonsoft/LangGraph-UI/issues/385 |
| Status | `Done` |

## 1. User Story

**As a** operador do Knowledge MCP Hub
**I want** que falhas transitórias de tools upstream (Tavily, Firecrawl, DeepWiki, Context7) acionem provedores equivalentes da mesma capacidade, e que `Resilience:Fallback` seja configurável pela UI de Settings
**So that** uma quota estourada ou 503 de um provedor não derrube o `agent_chat`/`ask_knowledge`, e a política seja ajustável sem redeploy.

**Problem context:**
SPEC-20260927-tool-and-model-resilience-fallback (#275) foi entregue **parcialmente**: `ResilientChatClient` está wired no registro scoped de `IChatClient` (`KnowledgeHubServiceCollectionExtensions.cs:206-232` — chat/model fallback funciona), mas:
1. `ToolCapabilityRegistry` é registrado como singleton (`:205`) e **não tem nenhum consumidor** — nenhuma interceptação de erro de tool call avalia fallback de capacidade (Tavily↔Firecrawl↔DuckDuckGo, DeepWiki↔Context7↔InternalFts do §37-41 da SPEC original).
2. A SPEC original listava "UI de Settings" para `Resilience:Fallback` (§45-46) — `grep -i 'resilien\|fallback' src/KnowledgeHub.Client/` retorna só binários; nenhuma seção existe.
Débito já registrado em `.claude/memory/memory.md` ("Settings UI + dispatcher interception = débito documentado").

## 2. Scope

**In scope:**
- Interceptação de falha de tool call nos 3 call sites de `CatalogTool.Handler`: `KnowledgeHubServiceCollectionExtensions.cs:658` (MCP `tools/call`), `Api/ToolsEndpoints.cs:98` (REST `/api/tools`), `Agent/CatalogToolAIFunction.cs:50` (agent loop). Implementação preferida: decorator `ResilientToolHandler` aplicado na composição do catálogo (`DynamicToolCatalog`/fábrica de `CatalogTool`), cobrindo os 3 caminhos sem duplicar lógica.
- `ToolCapabilityRegistry` consultado na falha: mapeamento capacidade → ferramentas equivalentes ordenadas; fallback só entre tools de mesma permissão (`ReadOnly` compatível) e mesmo escopo de dados; respeita `FallbackPolicyEngine` (disabled/observe/enforce) e `MaxFallbackAttempts=2`; sem retry no mesmo provider.
- Elegibilidade de erro: reutilizar `FallbackErrorClassifier` (429/503/timeout/`IsTimeout`) — erros de validação/auth nunca disparam fallback.
- Settings UI: seção `Resilience` em `Settings.razor` — enable/disable por categoria (chat, websearch, doclookup), modo (disabled/observe/enforce), lista de alternates por provedor; persiste via settings API existente.
- Telemetria: counter `knowledgehub.tool.fallbacks` (tags: `capability`, `from`, `to`, `outcome`) + log estruturado por transição (auditabilidade exigida pela SPEC original).

**Out of scope:**
- Fallback de embeddings (já coberto por `ResilientChatClient` no caminho de chat; embeddings provider swap tem SPEC própria Done).
- Filas/retries assíncronos — fallback é síncrono na requisição.
- Fallback para `McpProxy` arbitrário fora da CapabilityTaxonomy — só capacidades mapeadas.
- Mudança na `CapabilityTaxonomy` existente — só consumi-la.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Resilience/` — `ResilientToolHandler.cs` (novo), uso de `ToolCapabilityRegistry` (existe) e `FallbackErrorClassifier` (existe).
- `src/KnowledgeHub.Server/Mcp/DynamicToolCatalog.cs` / composição de `CatalogTool` — ponto único para wrap do `Handler`.
- `src/KnowledgeHub.Client/Pages/Settings.razor` + `src/KnowledgeHub.Server/Settings/` — nova seção.
- `src/KnowledgeHub.Server/Telemetry/KnowledgeHubMetrics.cs` — counter novo.
- `tests/KnowledgeHub.Tests.Unit/Resilience/` — suites existentes (`FallbackPolicyEngineTests`, `ResilientChatClientTests`) + novos.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Resilience/{FallbackPolicyEngine,ToolCapabilityRegistry,FallbackErrorClassifier,FallbackOptions,ResilientChatClient}.cs`
- `src/KnowledgeHub.Server/Mcp/{CatalogTool,DynamicToolCatalog}.cs`
- `src/KnowledgeHub.Server/Agent/CatalogToolAIFunction.cs`, `src/KnowledgeHub.Server/Api/ToolsEndpoints.cs`
- `.specs/SPEC-20260927-tool-and-model-resilience-fallback.md` (escopo original)

## 4. Requirements

### RF-001: `ResilientToolHandler`
- **Description:** decorator `Func<ToolCallContext,CancellationToken,ValueTask<CallToolResult>>` que envolve `CatalogTool.Handler` na composição do catálogo.
- **Rules:** em `IsError=true` ou exceção classificada transitória → resolve capacidade via `ToolCapabilityRegistry` → tenta próximo equivalente (policy `enforce` executa; `observe` só loga/conta; `disabled` passa reto); `MaxFallbackAttempts=2`; nunca retenta o mesmo tool; fallback só se `ReadOnly` igual ou mais restritivo; caller scope (`CallerScope`/auth) propagado intacto ao alternate.
- **Input → Output:** tools/call Tavily 429 → mesma chamada executada em Firecrawl, resultado retornado com marker de fallback.

### RF-002: Respeito a auth/scope
- **Description:** alternate só é elegível se permitido pelo escopo do chamador (mesma filtragem do catálogo); secrets por integração (`firecrawl`, `tavily`…) resolvidos como no caminho primário.
- **Rules:** alternate sem credencial configurada → skip para próximo; nenhum fallback cruza `CallerScope`.

### RF-003: Telemetria e auditoria
- **Description:** counter `knowledgehub.tool.fallbacks` + `ILogger` estruturado (from/to/capability/mode/outcome) em toda decisão — inclusive `observe`.
- **Input → Output:** fallback bem-sucedido incrementa counter com outcome=success; esgotado incrementa outcome=exhausted.

### RF-004: Settings UI
- **Description:** seção "Resilience" em Settings.razor — toggles por categoria + modo + edição de alternates (lista ordenada de provider names); round-trip via endpoints de settings existentes.
- **Rules:** defaults ausentes → seção renderiza disabled com hint; validação: modo ∈ {disabled, observe, enforce}.

## 5. Acceptance Criteria

- AC-1: tools/call com upstream 429 → alternate de mesma capacidade executa e retorna resultado; teste unitário prova cadeia Tavily→Firecrawl.
- AC-2: `observe` não executa alternate (só loga+conta); `disabled` é no-op — testes por modo.
- AC-3: `MaxFallbackAttempts` respeitado; fallback nunca retenta o provedor que falhou.
- AC-4: os 3 call sites (MCP, REST, agent loop) exibem fallback — provado por teste ou wrap único no catálogo.
- AC-5: UI salva/recarrega `Resilience:Fallback`; `dotnet build` 0 warnings; suite unit+integration verde.

## 6. Task Plan

1. `ResilientToolHandler` + wrap na composição do catálogo (RED: testes de cadeia/modo/limite primeiro).
2. Counter + logs; classificação via `FallbackErrorClassifier`.
3. Settings UI seção + endpoints wiring.
4. Suite verde; docs knob (SPEC docs-sync cobre o resto).

## 7. Organization Guardrails

- **Branches:** `feature/Devin-20260928-resilience-tool-fallback` a partir de `main`.
- **Security:** fallback nunca cruza `CallerScope`; nunca loga secret values; alternates sem credencial são skipped, não inventados.
- **Scope:** só interceptação de tool call + UI — `ResilientChatClient` já entregue não é retrabalho.
