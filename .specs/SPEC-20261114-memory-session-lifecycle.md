# SPEC-20261114-memory-session-lifecycle

## Metadata

| Campo | Valor |
|---|---|
| Status | `Approved` |
| Ticket | #632 — Epic #630 |
| Epic | E32 — agent-hardening wave 4 (gentle-ai parity) |
| Date | 2026-11-14 |

## Contexto

O KnowledgeHub tem snapshots versionados (`context_save`/`context_recovery`) e proveniência por `sessionId` (E30), mas não há **eventos de lifecycle** first-class: nada registra "a sessão X abriu às T com objetivo Y" nem "fechou às T' com resumo Z". Engram modela isso com `mem_session_start`/`mem_session_end` e usa para ancorar o digest de abertura. Sem eventos de sessão, `memory_context` mostra fatos recentes mas não responde "onde a última sessão parou".

Design: **zero migration** — sessões são `MemoryOperation` rows `kind:"session"`: `begin` cria `status:"running"` com `PayloadJson {sessionId, label}`; `end` fecha `status:"done"`, `FinishedAt`, `ResultJson {summary}`. Sessão aberta = `kind=session AND status=running` — queryável com infra existente (operations_list já enxerga).

## Requisitos

### RF-001 — `memory_session_begin`
Args: `bankKey?`/`repo?` (resolução normal), `sessionId?` (default `sess-{guid8}` — caller pode passar o `threadId` do `agent_chat`), `label?` (objetivo da sessão, cap 500).
- `sessionId` já aberto no bank → retorna `{sessionId, alreadyOpen:true}` (idempotente).
- Cria `MemoryOperation kind=session status=running` com payload; retorna `{sessionId, bankId, startedAt, alreadyOpen:false}`.

### RF-002 — `memory_session_end`
Args: `bankKey?`/`repo?`, `sessionId` (obrigatório), `summary?` (cap 2000, defense-screened igual content).
- Sessão aberta encontrada → `status=done` + `FinishedAt` + `ResultJson {summary}` → `{sessionId, ended:true, durationMs}`.
- Não encontrada ou já fechada → `{sessionId, ended:false, alreadyClosed:true}` (idempotente, não erro).
- `sessionId` que nunca existiu → `alreadyClosed:true` igual (não vaza existência além do bank).

### RF-003 — `memory_session_list`
Args: `bankKey?`/`repo?`, `openOnly` (default false), `limit` (default 20, cap 100).
Retorna `{count, sessions:[{sessionId, label, status, startedAt, finishedAt, summary}]}` ordenado por `startedAt desc`.

### RF-004 — `memory_context` ganha seção de sessões
`MemoryContextResult` += `OpenSessions` (lista, mesmo digest do list) + `LastCompletedSession` (o `kind=session status=done` mais recente com summary). O digest textual ganha seção "Sessions" quando houver dados — o "onde paramos" do session start.

### RF-005 — REST mirror
`POST /api/memory/banks/{key}/sessions/begin`, `POST /sessions/end`, `GET /sessions?openOnly=` delegando aos handlers MCP (padrão E30/E31).

## Fora de escopo

- Auto-begin/auto-end por transporte (o client chama — como Engram).
- Vincular units à session automaticamente (já coberto pelo `sessionId` arg do retain — doc no SKILL.md recomenda passar o mesmo id).
- Expiração/GC de sessões abertas antigas (jobs futuros).

## Acceptance Criteria

- [ ] `session_begin` retorna `sessionId`; segundo begin com mesmo id → `alreadyOpen:true` sem criar row nova.
- [ ] `session_end` com summary → op `done` com `ResultJson`; end repetido → `alreadyClosed:true`.
- [ ] `session_list` mostra abertas e fechadas com duração implícita (started/finished).
- [ ] `memory_context` de bank com sessão aberta inclui a sessão; após `session_end` com summary, o próximo context mostra `lastSession.summary`.
- [ ] Summary com conteúdo sensível passa pelo defense (blocked → erro padrão de write defense).
- [ ] REST begin/end/list funciona igual ao MCP.
- [ ] Contrato MCP pin atualizado com as 3 tools novas.

## Arquivos

- `src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs` (3 tools + schemas + handlers)
- `src/KnowledgeHub.Server/Memory/MemorySessionService.cs` (novo — lógica sobre MemoryOperations)
- `src/KnowledgeHub.Server/Memory/MemoryContextService.cs` (seção Sessions no digest)
- `src/KnowledgeHub.Server/Memory/MemoryModels.cs` (DTOs)
- `src/KnowledgeHub.Server/Api/MemoryEndpoints.cs` (3 rotas)
- `src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs` (DI)
- `tests/KnowledgeHub.Tests.Unit/Memory/MemorySessionTests.cs` (novo)
- `tests/KnowledgeHub.Tests.Integration/McpContractTests.cs` (pin)
- `tests/KnowledgeHub.Tests.Bench/Journeys/MemoryJourneys.cs` (journey `session-lifecycle` — SPEC do bench v2)
- `skills/knowledge-hub/SKILL.md` (session lifecycle doc)

## Riscos

- Ops "session" poluindo `operations_list` → filtro `kind` já existe; session kind documentado.
- Summary defense-block → erro explícito, caller re-escreve (igual retain).

## Task Plan

- [ ] T1 — `MemorySessionService` (begin/end/list sobre MemoryOperations) + unit tests.
- [ ] T2 — MCP tools + schemas + contract pin.
- [ ] T3 — `memory_context` seção Sessions.
- [ ] T4 — REST + SKILL.md + gates.
