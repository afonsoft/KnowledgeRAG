# SPEC-20261114-path-denylist

## Metadata

| Campo | Valor |
|---|---|
| Status | `Approved` |
| Ticket | #631 — Epic #630 |
| Epic | E32 — agent-hardening wave 4 (gentle-ai parity) |
| Date | 2026-11-14 |

## Contexto

gentle-ai bloqueia paths sensíveis antes de qualquer leitura/escrita. KnowledgeHub hoje tem duas proteções **parciais**: `ObsidianNoteWriter.SafePath` impede traversal fora do vault root, e `IngestionService.IsExcluded` pula segmentos iniciados por `.` na varredura de `*.md`. O que falta:

1. `read_document` lê **qualquer** arquivo dentro do vault — `.env`, `id_rsa`, `*.pem`, `credentials.md` — traversal guard não cobre nomes sensíveis dentro do root.
2. `write_note` escreve qualquer nome `.md` — pode criar/sobrescrever `secrets.md` (o conteúdo é secret-scanned, mas o *path* não é denied).
3. `IsExcluded` só exclui dot-segments — `credentials.md`, `secrets.md`, `api-keys.md` no vault são ingeridos para a base de conhecimento (e depois citados pelo RAG).

O secret-scanner (E29) cobre **conteúdo**; esta SPEC cobre **nomes de path** — camada complementar.

## Requisitos

### RF-001 — `PathDenyList` compartilhado
`src/KnowledgeHub.Server/Security/PathDenyList.cs` — serviço que decide se um path relativo (ou nome de arquivo) é denied. Padrões default (case-insensitive, match por segmento ou glob no nome do arquivo):

```
.env, .env.*, *.pem, *.key, *.pfx, *.p12, *.keystore, *.kubeconfig,
id_rsa*, id_ed25519*, id_dsa*, *.ppk,
.ssh, .aws, .azure, .gcp, .gnupg, .docker (segmentos)
credentials*, secrets*, secret-*, *-secrets, api-keys*, tokens*
```

Config: `Security:PathDenyList` (string[], opt-in extra patterns) + `Security:PathAllowList` (string[], wins over deny — escape hatch documentado). `IsDenied(string relativePath)` + `IsAllowed` precedence.

### RF-002 — enforcement em `SafePath`
`ObsidianNoteWriter.SafePath` chama `PathDenyList` após o traversal guard: denied → `McpProtocolException` com mensagem `"path '{name}' is denied by policy"` — cobre `read_document`, `write_note`, `KnowledgeResourceProvider` e qualquer caller futuro (ponto único de enforcement para acesso a arquivo do vault).

### RF-003 — enforcement na ingestão
`IngestionService.IsExcluded` ganha a mesma checagem por segmento/nome: `credentials.md` no vault é pulada na sync (e conta como `DocsSkipped` no resumo do sync — visível, não silencioso).

### RF-004 — superfície de erros
Denied read/write retorna o erro MCP padrão (`isError`/`InvalidParams`) com a mensagem da policy — **não** vaza qual pattern casou (evita oracle de enumeração). Mensagem: `path '{fileName}' is denied by policy` — só o nome do arquivo pedido, nunca o pattern.

## Fora de escopo

- Deny-list de conteúdo além do secret-scanner existente.
- Deny-list para conectores não-filesystem (SQL, RestApi, S3).
- UI de administração da lista (config-only em v1).

## Acceptance Criteria

- [ ] `read_document {path: ".env"}` num vault contendo `.env` → erro `denied by policy` (não "not found" — mas sem revelar o pattern).
- [ ] `read_document {path: "credentials.md"}` → denied; `write_note {path: "secrets/api-keys.md"}` → denied.
- [ ] `read_document {path: "journal/nota.md"}` → funciona normal.
- [ ] Vault com `credentials.md` + `nota.md` → sync indexa `nota.md`, pula `credentials.md` (contado em skipped).
- [ ] `Security:PathAllowList` com `credentials.md` → o mesmo arquivo passa a ser legível/ingerido.
- [ ] Pattern extra via `Security:PathDenyList` (ex.: `*.draft`) é honrado sem recompilar lógica.

## Arquivos

- `src/KnowledgeHub.Server/Security/PathDenyList.cs` (novo)
- `src/KnowledgeHub.Server/Mcp/ObsidianNoteWriter.cs` (SafePath)
- `src/KnowledgeHub.Server/Ingestion/IngestionService.cs` (IsExcluded)
- `src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs` (DI)
- `appsettings.json` (defaults documentados)
- `tests/KnowledgeHub.Tests.Unit/Security/PathDenyListTests.cs` (novo)
- `tests/KnowledgeHub.Tests.Integration/` — read/write/sync denied (estender suíte existente ou novo arquivo)

## Riscos

- **Falso positivo** (nome legítimo casando `secrets*`/`credentials*`) → mitigado por `PathAllowList` + match por nome de arquivo, não substring de path.
- Bypass via case/Unicode → match `OrdinalIgnoreCase`; normalização igual à do SafePath (`\\`→`/`).

## Task Plan

- [ ] T1 — `PathDenyList` + testes unitários (default patterns, allow-wins, segment vs glob).
- [ ] T2 — wire `SafePath` + testes read/write denied.
- [ ] T3 — wire `IsExcluded` + teste de sync skip.
- [ ] T4 — config docs + gates.
