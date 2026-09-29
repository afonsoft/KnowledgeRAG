# SPEC-20260929-resilience-fallback-hardening

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `resilience-fallback-hardening` |
| Type | `Fix` (bugs + segurança — revisão da S2 #391 e SPEC #275) |
| Stack | `.NET 10 / C#` — Resilience, Settings, DynamicToolCatalog |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-resilience-fallback-hardening` |
| Status | `Draft` |
| Source | Comentários `devin-ai-integration` em PRs #375, #389, #391 (verificados em código) |

## 1. User Story

**As a** operador do KnowledgeHub
**I want** o fallback de chat/tools sem vazamento de segredo, sem troca de provedor silenciosa e com substituição de argumentos compatível
**So that** `enforce` não corrompa a cadeia de alternates, não exponha `apiKey` em SQLite e não envie args do provedor A ao provedor B.

## 2. Findings (verificados)

1. 🟥 **SECURITY** — `ResilienceSettings.ChatFallbacksJson` persiste `ChatProviderOptions.ApiKey` em texto claro (o `***` é só masking de saída). Migrar para `IIntegrationSecretStore` (`resilience:{n}`) ou criptografia de campo.
2. 🔴 `SaveAsync` assume `Provider = f.Provider ?? "openai"` — a UI não envia `Provider` nos rows → alternates Ollama viram OpenAI ao salvar (PR #391, confirmado).
3. 🔴 `ResilientToolInvoker` substitui o tool mas repassa os `Arguments` do original — `tavily_search{query,max_results}` → `firecrawl_search` recebe args incompatíveis. Precisa de mapeamento por capacidade (ou só alternates com schema compatível / `query`-shaped).
4. 🟥 **SECURITY** — fallback para `search_knowledge` (capacidade `internal_fts`) pode consultar fontes fora do `CallerScope` do request original — o substitute precisa herdar o escopo, não o catálogo irrestrito.
5. 🔴 Cadeias de fallback permanecem antigas até reiniciar — catálogo cached por version não rewrap após `SaveAsync`/`ClearAsync`; invalidar cache do catálogo na mutação de settings.
6. 🔴 (SPEC #275 residual) Falha de conexão do primário não aciona chat fallback em alguns paths; erro definitivo do secundário vira genérico; usuário não vê indicação de fallback (UI/response flag).
7. 🟡 `ToolErrorClassifier` classifica `IsError=true` permanente como fallbackável — restringir a transitório (429/5xx/timeout) via parse do conteúdo ou heurística documentada.
8. 🟡 Cache de resposta mantém a resposta do alternate depois que o primário se recupera (cache key não inclui provedor efetivo).
9. 🔍 Counter `tool.fallbacks` não mede sucesso/esgotamento/observe — adicionar `outcome` e `mode`.
10. 🔍 Tela não oferece controle por categoria (enable/disable por capacidade).

## 3. Requirements

- RF-001: `ApiKey` de alternates nunca em claro na tabela — mover para secret store (`resilience:{idx}`) com a mesma convenção `***` no round-trip; migração de dados existentes.
- RF-002: `SaveAsync` preserva `Provider` do row anterior quando ausente no payload; UI ganha campo provider (select openai|ollama|openai-compatible).
- RF-003: Substituição de tool só ocorre quando os args do original são compatíveis com o schema do substitute (mesmo required scalar `query`/`question`/etc.) ou via `CapabilityArgsMap` configurável; caso contrário propaga o erro.
- RF-004: Substitute resolve contra `CallerScope` efetivo — tool que varre fontes globais (`search_knowledge`) só é candidato se o escopo do caller for irrestrito, ou a execução herda o filtro do caller.
- RF-005: Salvar/limpar settings invalida o snapshot **e** o cache do catálogo (`DynamicToolCatalog` version bump) — chains novas sem restart.
- RF-006: `IsError` só fallbackável quando classificado transitório; decisão exposta via `reason` tag.
- RF-007: Cache de resposta incorpora provider efetivo (ou bypassa quando fallback ocorreu).
- RF-008: Resposta/telemetry marca quando a resposta veio de alternate (`X-Fallback-Provider` header ou campo no DTO + metric tag `outcome`).

## 4. Acceptance Criteria

- AC-1: `ApiKey` ausente do JSON persistido; round-trip `***` funciona — teste.
- AC-2: salvar com alternate Ollama não muda `Provider` — teste.
- AC-3: fallback `tavily_search → firecrawl_search` não envia `max_results` ao substitute; args mapeados corretamente — teste.
- AC-4: caller com `sourceIds` restrito + `search_knowledge` como substitute → sem vazamento — teste.
- AC-5: PUT `/api/settings/resilience` altera chain visível na próxima execução sem restart — teste.
