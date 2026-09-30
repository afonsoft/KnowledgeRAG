# SPEC-20260929-docs-and-ux-nits

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `docs-and-ux-nits` |
| Type | `Docs/Fix` (baixa prioridade) |
| Stack | `Markdown`, Blazor Settings/Graph |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-docs-ux-nits` |
| Status | `Done` |
| Source | Comentários `devin-ai-integration` em PRs #382, #389, #390, #391, #392 |

## 1. User Story

**As a** mantenedor
**I want** os nits de documentação/UX apontados pela revisão endereçados
**So that** README-pt fique em paridade, argumentos temporais documentados com os nomes certos e a tela de resiliência tenha controle por categoria.

## 2. Findings

1. 🔍 `README.md` pt-BR não recebeu as mesmas novidades da wave 2026-09-27 (conectores/tools novos ausentes) — paridade en/pt.
2. 🔍 Argumentos temporais documentados com nomes diferentes do implementado (`Search:LimitMode` vs `limitMode`, etc.) — reconciliar.
3. 🔍 Tabela de argumentos mistura MCP (`search_graph_*`) com REST (`/api/graph/*`) sem separar.
4. 🔍 Exemplo de fallback (`Resilience:Fallback`) não mostra como configurar o provedor alternativo — exemplo completo.
5. 🔍 "Definição de pronto" não documentada junto aos novos status de SPEC (`Done` vs `Approved`).
6. 🔍 Settings Resiliência não oferece toggle por capacidade (enable/disable por categoria).
7. 🔍 `/graph`: presets semânticos não documentados (1h/6h/24h/7d — o que significa "recent"?), detalhe de evidências previsto na SPEC não exibido.
8. 🟡 (dup de S4 já fixo) Collection separada não isola listener global — resolvido em #393, confirmar.

## 3. Requirements

- RF-001: README pt atualizado com mesma cobertura da versão en (conectores YouTube/Git/Unstructured/Audio, tools temporais, evidence, live-actions).
- RF-002: Tabela de argumentos/API separada por camada (MCP tool vs REST endpoint).
- RF-003: Exemplo `Resilience:Fallback` completo (modo+chatFallbacks+toolCapabilities).
- RF-004: Seção "Definition of Done" no CONTRIBUTING ou README apontando para statuses de SPEC.
- RF-005: Toggle por capacidade no tab Resiliência (opcional — bom-to-have).
- RF-006: `/graph`: tooltip/docstring nos presets; evidências de chunk opcional por nó.

## 4. Acceptance Criteria

- AC-1: `grep` paridade en/pt nos docs.
- AC-2: docs revisados pelos próprios comentários Devin — referências fechadas.
