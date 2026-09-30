# SPEC-20260929-evidence-chain-integrity

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `evidence-chain-integrity` |
| Type | `Fix` (integridade + authz) |
| Stack | `.NET 10 / C#` — EvidenceChainService/Verifier/Emission, HMAC |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-evidence-chain-integrity` |
| Status | `Done` |
| Source | Comentários `devin-ai-integration` no PR #377 |

## 1. User Story

**As a** auditor do KnowledgeHub
**I want** a cadeia de evidências HMAC íntegra sob concorrência e a exportação restrita ao dono da sessão
**So that** assinaturas não colidam em appends paralelos, o bundle não vaze sessões alheias e nenhuma verificação diga "íntegro" sem checar a assinatura.

## 2. Findings

1. 🔴 Appends simultâneos assinam digests inválidos — `AppendAsync` lê os parents e assina sem serialização; duas requests concorrentes produzem receipts com `ParentDigest`/encadeamento inconsistentes.
2. 🟥 **SECURITY** — `GET /api/v1/evidence/sessions/{sessionId}/bundle` não valida que o caller é dono da sessão — qualquer `aft_*` válida exporta evidências de qualquer sessão.
3. 🟥 **SECURITY** — Bundle adulterado pode ser declarado íntegro sem verificar a assinatura — caminho de exportação confia no digest sem recomputar a cadeia HMAC.
4. 🟨 Argumentos longos de tools são truncados antes de entrar no payload assinado — parte do conteúdo fica fora da autenticação.
5. 🟨 Prefixo de assinatura alterado (`hmac-sha256:`) pode pular a verificação em vez de falhar.
6. 🟨 Emissão omite o contexto expandido efetivamente consumido (ExpandedChunkIndices); SSE de respostas não emite evidência; `sessionId` agrupa perguntas distintas (chat vs ask compartilham).

## 3. Requirements

- RF-001: `AppendAsync` serializa appends por sessão (lock por `sessionId` ou constraint única de ordem) — digest do parent é sempre do último receipt persistido.
- RF-002: Exportação valida `SessionId` vs o principal autenticado (cookie session ↔ dono; `aft_*` ↔ `ApiKeyId` do receipt) — 403 para sessões alheias.
- RF-003: Exportação/verify re-verifica a cadeia completa (`EvidenceChainVerifier` com key) antes de marcar `intact: true` — resultado inclui `verified` separado de `complete`.
- RF-004: Payload assinado cobre os args completos (hash do payload integral, não da preview truncada).
- RF-005: Prefixo de assinatura irreconhecível → violação `UnknownSignatureScheme`, não skip.
- RF-006: Emissão inclui `ExpandedChunkIndices`/texto expandido no payload de `ChunksRetrieved`; path SSE (`/api/chat/stream`) passa por `EvidenceEmission`; `ThreadId`/`sessionId` separados por canal.

## 4. Acceptance Criteria

- AC-1: 20 appends paralelos na mesma sessão → cadeia verifica 100% — teste de concorrência.
- AC-2: `aft_*` de sessão A exportando sessão B → 403 — teste de integração.
- AC-3: tamper num receipt do bundle → `intact:false` + violação `TamperingDetected` — teste.
- AC-4: suite verde.
