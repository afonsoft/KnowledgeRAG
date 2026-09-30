# SPEC-20260928-post-pentagi-wave-docs-sync

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `post-pentagi-wave-docs-sync` |
| Type | `Docs` |
| Stack | `Markdown docs (en/pt), README, CLAUDE.md, docs/architecture/` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260928-post-pentagi-wave-docs-sync` |
| Ticket | `#384` — https://github.com/afonsoft/LangGraph-UI/issues/384 |
| Status | `Done` |

## 1. User Story

**As a** usuário ou agente operando o Knowledge MCP Hub
**I want** que CLAUDE.md, README.md e docs/ (en/pt + architecture) reflitam as 13 SPECs entregues na wave pentagi/Verba (2026-09-27/28)
**So that** a documentação descreva o sistema real — conectores, tools MCP, endpoints e knobs de configuração existentes — em vez de parar na wave de 2026-09-26.

**Problem context:**
Gap-analysis 2026-09-28: a wave inteira (#262–#281) mergeada em `main` não aparece em nenhum doc. Evidence: `grep -ci` em CLAUDE.md/README/docs = 0 matches para `YouTube`, `GitRepository`, `Unstructured`, `AudioTranscription`, `ChainAst`, `EvidenceChain`, `TemporalGraph`, `McpDynamicRagActionBridge`, `RagEvaluation`, `Voyage`; `RssFeed` tem 1 menção no README; `Resilience`/`Cohere` têm 2 arquivos em docs/ sem detalhe. O endpoint `GET /api/v1/evidence/sessions/{id}/bundle` (`src/KnowledgeHub.Server/Api/EvidenceEndpoints.cs`) tem 0 entradas em `docs/{en,pt}/API.md`. Mesmo padrão do SPEC-20260926-post-wave-docs-sync (Done) — recorrência após wave nova.

## 2. Scope

**In scope:**
- `CLAUDE.md` "Estado Atual": parágrafo cobrindo conectores RssFeed/YouTube/GitRepository/UnstructuredDocument/AudioTranscription, ChainAst+compactor, evidence chain (HMAC-SHA256), Resilience fallback (`ResilientChatClient`), Temporal/episodic KG, MCP dynamic action bridge, RAG triad evaluator, Voyage/Cohere providers, window/autocut, filter relaxation/subQueries.
- `README.md`: tabela de SourceTypes atualizada + seção de tools MCP (5 tools temporais, `enableLiveActions`, `suggestedActions`) + knobs novos.
- `docs/en/API.md` + `docs/pt/API.md`: endpoint `/api/v1/evidence/sessions/{id}/bundle`, params novos de `search_knowledge`/`ask_knowledge` (`subQueries`, `enableLiveActions`, `windowSize`, `limitMode`, `autocutSensitivity`), campos de resposta (`IsRelaxed`, `LiveToolExecutions`, `ExpandedChunkIndices`, `suggestedActions`).
- `docs/{en,pt}/INSTALL.md` e config surface: `Resilience:Fallback`, `Agent:EnableDynamicActionBridge|MaxChainedDynamicCalls`, `Search:Relaxation:Enabled|MinResults`, `Search:LimitMode`, `Unstructured`/`AssemblyAI`/`Git` secret keys (`unstructured:{id}`, `audio:{id}`, `git:{id}`).
- `docs/architecture/`: atualizar diagrama(s) Mermaid/ADRs com módulos `Resilience/`, `Audit/Evidence/`, `Mcp/Bridge/`, `Agents/ChainAst/`, `Graph/` temporal.

**Out of scope:**
- Mudanças de código — somente documentação.
- Tradução integral nova — manter paridade en/pt do que já existe.
- Reescrita histórica de SPECs — SPECs ficam em `.specs/` (convenção do repo; `docs/specs/` não é adotado).

## 3. Technical Context

**Where the change happens:**
- `CLAUDE.md` (Estado Atual), `README.md`, `docs/en/{API,INSTALL,ARCHITECTURE}.md`, `docs/pt/*` mirrors, `docs/architecture/*.{md,mmd}`.

**Evidence anchors (AS-IS):**
- `src/KnowledgeHub.Server/Api/EvidenceEndpoints.cs` — endpoint ausente de API.md.
- `src/KnowledgeHub.Server/Mcp/ToolProviders/TemporalGraphToolsProvider.cs` — 5 tools não documentadas.
- `src/KnowledgeHub.Server/Mcp/Bridge/McpDynamicRagActionBridge.cs` — `enableLiveActions`/`suggestedActions` sem doc.
- `src/KnowledgeHub.Server/Resilience/` — `FallbackOptions` bound via `Resilience:Fallback` sem doc de config.
- `appsettings*.json` — `Search:LimitMode`, `Search:Relaxation:*`, `Agent:*` novos defaults.

## 4. Requirements

### RF-001: CLAUDE.md Estado Atual
- **Description:** um parágrafo (ou bullet list) cobrindo cada capability da wave com nome canônico do tipo/tool.
- **Input → Output:** `grep -c 'TemporalGraph\|EvidenceChain\|ResilientChatClient\|Voyage' CLAUDE.md` > 0 para cada um.

### RF-002: API docs en/pt
- **Description:** entrada de endpoint + params novos de `search_knowledge`/`ask_knowledge` documentados em ambos os idiomas com mesma estrutura das entradas existentes.
- **Input → Output:** `grep 'evidence/sessions' docs/en/API.md docs/pt/API.md` → 1 match cada.

### RF-003: Config surface
- **Description:** README/INSTALL documentam `Resilience:Fallback`, `Agent:EnableDynamicActionBridge`, `Search:Relaxation:*`, `Search:LimitMode` e as chaves de secret por conector (`restapi:{id}`, `sql:{id}`, `git:{id}`, `unstructured:{id}`, `audio:{id}`).
- **Input → Output:** cada chave `grep`-ável em README.md ou docs/*/INSTALL.md.

### RF-004: Architecture docs
- **Description:** `docs/architecture/` ganha entrada/ADR curto ou atualização de diagrama cobrindo `Resilience/`, `Audit/Evidence/`, `Mcp/Bridge/`, `ChainAst/` e braço temporal do grafo.
- **Input → Output:** `grep -l 'TemporalGraph\|ChainAst\|Evidence' docs/architecture/*.md docs/architecture/*.mmd` → ≥1 arquivo novo ou atualizado.

## 5. Acceptance Criteria

- AC-1: `grep -ci` dos termos da wave em CLAUDE.md ≥ 1 cada (lista do §1).
- AC-2: API.md en/pt cobre evidence endpoint + novos params de busca.
- AC-3: Knobs de config documentados; `dotnet build` segue 0 warnings (docs não quebram nada).
- AC-4: Paridade en/pt mantida (mesmos tópicos nos dois idiomas).

## 6. Task Plan

1. Levantar lista canônica (nomes de tools/endpoints/knobs) via grep no código — não inventar.
2. CLAUDE.md → README → API.md en/pt → INSTALL → architecture.
3. `grep` de verificação por termo; commit `docs:`.

## 7. Organization Guardrails

- **Branches:** `feature/Devin-20260928-post-pentagi-wave-docs-sync` a partir de `main`; nunca em `main`.
- **Docs-only:** nenhum `.cs` alterado; format gate não se aplica.
- **Paridade:** qualquer entrada nova em `en/` tem espelho em `pt/`.
