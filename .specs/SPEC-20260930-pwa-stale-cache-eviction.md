# SPEC-20260930-pwa-stale-cache-eviction

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `pwa-stale-cache-eviction` |
| Type | `Fix` (operation/UX) |
| Stack | `.NET 10 / Blazor WASM PWA` — `service-worker.published.js` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260930-pwa-cache-retention` |
| Status | `Approved` |
| Source | `GAP-implementation-pwa-stale-cache-eviction` (gap-analysis-20260930) |

## 1. User Story

**As a** usuário com a aplicação aberta em uma aba durante um deploy
**I want** que o service worker novo não evicte imediatamente o cache da versão em uso
**So that** a aba antiga não quebra com 404s de assets (ex.: `blazor.boot.json`, `dotnet.wasm`) até o próximo reload.

## 2. Findings

- 🔴 Incidente real (2026-09-30): após redeploy .NET 10, logs do container mostraram `GET /framework-assets/blazor.boot/json → 404` e `GET /framework-assets/dotnet.wasm/wasm → 404` vindos de um browser — o app antigo em cache do SW anterior requisitou assets que já não existem no servidor novo; `Blazor.start` falhou e a página não abriu.
- 🔴 AS-IS: `service-worker.published.js` `onActivate` deleta **todos** os caches `knowledgehub-cache-*` diferentes do atual (`src/KnowledgeHub.Client/wwwroot/service-worker.published.js:51-56`). Se uma aba ainda usa o SW/cache da versão anterior no momento da ativação, os fetches dela vão à rede e 404am.
- 🟡 TO-BE: manter a geração anterior do cache por uma transição (N=2 gerações retidas), evictando somente versões mais antigas — padrão recomendado para PWAs com múltiplas abas/versões concorrentes.

## 3. Requirements

1. **RF-001** — `onActivate` deve reter as duas gerações mais recentes de `knowledgehub-cache-*` (corrente + anterior) e deletar apenas as demais. A ordenação deve ser derivada do manifest atual (corrente) e do conjunto de caches existentes (anterior = mais recente diferente do corrente).
2. **RF-002** — Comportamento de `onFetch` inalterado: network-only paths (`/api`, `/hubs`, `/mcp`, `/health`, `/framework-assets`) continuam fora de cache.
3. **RF-003** — Documentar a retenção em comentário no arquivo e em `docs/*/INSTALL.md` seção PWA (se existir) ou README — uma linha explicando por que duas gerações são mantidas.

## 4. Acceptance Criteria

- **Given** uma instalação com caches `knowledgehub-cache-A` (ativo) e `knowledgehub-cache-B` (anterior) **when** o SW `C` ativa **then** `A` e `C` permanecem… — *nota:* a retenção garante que o cache da versão que o cliente antigo ainda usa (`B` do ponto de vista da nova versão = a geração imediatamente anterior a `C`) sobreviva à ativação de `C`. Critério prático: após ativar, existem no máximo 2 caches `knowledgehub-cache-*` e o mais antigo retido é sempre o predecessor imediato do corrente.
- `dotnet build` + tests verdes; nenhuma mudança funcional nas rotas network-only.

## 5. Task Plan

1. Reescrever `onActivate` para computar a lista ordenada de caches por data de criação (ou manter um registro de geração anterior via `clients.claim()`/storage) — abordagem mínima: ordenar `caches.keys()` (sufixos são timestamps de build → ordenação lexical da versão funciona como proxy; documentar o pressuposto) e deletar tudo exceto os 2 mais recentes.
2. `dotnet build` do Client; smoke: simular duas versões de cache via DevTools.
3. PR → merge → próximo deploy valida com aba antiga aberta.
