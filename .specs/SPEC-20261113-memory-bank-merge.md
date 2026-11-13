# SPEC-20261113-memory-bank-merge

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `memory-bank-merge` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/KnowledgeRAG` (`/home/ubuntu/repos/KnowledgeRAG`) |
| Branch | `feature/devin-20261113-memory-bank-merge` |
| Ticket | #625 — Epic #624 |
| Status | `Done` |

## 1. User Story

**As a** KnowledgeHub operator running multiple agents against the same project
**I want** to merge two memory banks and have the old bank key keep resolving to the merged bank
**So that** agents that named the same project differently (`myrepo` vs `org-myrepo` vs `my-repo`) converge on one shared memory instead of fragmenting into orphan banks.

**Problem context:**
gentle-ai's Engram solves this with `mem_merge_projects` + auto-detection via normalized git remote. KnowledgeRAG (post-E29/E30) has richer memory primitives but no drift remedy: `bankKey` is caller-chosen, two keys for the same project create divergent banks with no way to unify. Bank-scoped rows span 7 tables (`MemoryUnits`, `MemoryDocuments`, `MemoryDirectives`, `ObservationHistories`, `MemoryMentalModels`, `MemoryOperations`, `MemoryUnitEntities`), so merging must re-parent all of them consistently.

## 2. Scope

**In scope:**
- `memory_bank_merge` MCP tool + REST mirror: re-parents every bank-scoped row from source bank to target bank, resolves conflicts deterministically, marks the source bank as merged.
- `MemoryBank.MergedIntoBankId` (nullable FK) — merged banks become redirect tombstones; `ResolveAsync` follows the pointer so old keys keep working for retain/recall/list.
- Audit: a `MemoryOperation` row `kind:"bank_merge"` on the target bank recording moved/skipped counts.
- Dual migrations (SQLite + Postgres) for the new column, plus parity test coverage.
- Optional `repo` argument on memory tools: when `bankKey` is absent in path/header/arg but `repo` is supplied, the effective bank key derives from the normalized repo slug — reuses the repo normalization from `context_save` (`owner/name`, lowercase).

**Out of scope:**
- Cross-`ApiKeyId` merges (banks are scoped to the caller key — merge is within the same scope).
- Content-level dedup/adjudication of conflicting facts (collision policy is "target wins", no LLM merge).
- Merging bank configuration semantics beyond fill-gaps (target `Mission`/`ConfigJson`/`DispositionJson` win; source fills only keys absent in target).
- Undo/split operation.

## 3. Technical Context

**Where the change happens:**
`src/KnowledgeHub.Server/Memory/` — `MemoryBankService` (resolution + merge service `MemoryBankMergeService`), `MemoryBankContext.EffectiveKey` (repo fallback), entity `MemoryBank` (+`MergedIntoBankId`), EF config in `KnowledgeHubDbContext`, migrations in `Migrations/` + `Migrations/Postgres/`, MCP surface in `Mcp/ToolProviders/MemoryToolsProvider.cs`, REST mirror in `Api/MemoryEndpoints.cs` (proxies through `Invoke`, no handler duplication).

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Memory/MemoryBankService.cs`
- `src/KnowledgeHub.Server/Memory/MemoryBankContext.cs`
- `src/KnowledgeHub.Server/Domain/Entities/MemoryBank.cs`
- `src/KnowledgeHub.Server/Data/KnowledgeHubDbContext.cs` (MemoryBank config + unique index on `(ApiKeyId, BankKey)`)
- `src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs` (bank CRUD handlers ~1193-1300)
- `tests/KnowledgeHub.Tests.Unit/Memory/` (fixture pattern — SQLite file + `EnsureCreated`)
- `tests/KnowledgeHub.Tests.Integration/Conventions/MigrationParityTests.cs`
- repo normalizer used by `context_save` (search `Normalize`/`owner/name` in `Context*` services)

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Domain/Entities/MemoryBank.cs              (modify)
src/KnowledgeHub.Server/Data/KnowledgeHubDbContext.cs              (modify)
src/KnowledgeHub.Server/Memory/MemoryBankService.cs                (modify — Resolve follows MergedInto)
src/KnowledgeHub.Server/Memory/MemoryBankContext.cs                (modify — repo fallback)
src/KnowledgeHub.Server/Memory/MemoryBankMergeService.cs           (new)
src/KnowledgeHub.Server/Mcp/ToolProviders/MemoryToolsProvider.cs   (modify — tool + handler + schema)
src/KnowledgeHub.Server/Migrations/*_MemoryBankMerge.cs            (new — SQLite)
src/KnowledgeHub.Server/Migrations/Postgres/*_MemoryBankMerge.cs   (new — Postgres)
tests/KnowledgeHub.Tests.Unit/Memory/MemoryBankMergeTests.cs       (new)
tests/KnowledgeHub.Tests.Unit/Mcp/McpContractTests.cs              (modify — tool pin)
```

## 4. Requirements

### RF-001: `memory_bank_merge` tool
- **Description:** Merges `sourceKey` bank into `targetKey` bank inside the caller's `ApiKeyId` scope.
- **Rules:** Re-parents `BankId` on all 7 bank-scoped tables in one transaction; on unique-key collision (`MemoryUnits`/`MemoryDocuments` content-hash/externalId, `MemoryDirectives` key, `MemoryMentalModels` slug) the target row wins and the source row is soft-deleted (units) or skipped (counted); records `MemoryOperation kind:"bank_merge"` on the target with per-table moved/skipped counts; sets `source.MergedIntoBankId = target.Id`; returns `{ merged:true, targetBankId, moved:{units,documents,directives,mentalModels,operations,unitEntities,observationHistories}, skipped:{...} }`.
- **Input → Output:** `sourceKey`, `targetKey` → structured merge report.

### RF-002: `MergedIntoBankId` redirect
- **Description:** `MemoryBankService.ResolveAsync` (and list/get paths) follow the merge pointer.
- **Rules:** `MergedIntoBankId` nullable FK, self-referencing, no cascade; resolution follows up to 8 hops then throws `MemoryBadRequestException("merge chain cycle")`; `memory_bank_list` excludes merged tombstones by default (`includeMerged:true` shows them flagged); a merged bank rejects **direct** writes only when addressed by its own id — but writes addressed via its `BankKey` transparently land on the merge target (that is the point of the redirect).
- **Input → Output:** resolution `merged-from-key` → target `MemoryBank`.

### RF-003: MCP + REST surface
- **Description:** Tool `memory_bank_merge` (write, `Idempotent` per re-invocation contract) + REST `POST /api/memory/banks/{targetKey}/merge` body `{"sourceKey":"..."}`.
- **Rules:** 404 when either key is unknown in scope; 400 when `sourceKey == targetKey` or source is already merged (`alreadyMerged:true` result is fine — idempotent re-run returns the previous outcome shape, `merged:false, alreadyMerged:true`); both args validated against `MemoryBank` scope; rate-limit profile same as bank CRUD.

### RF-004: `repo` fallback on effective bank resolution
- **Description:** When no `bankKey` is supplied via path (`/mcp/banks/{key}`), header (`X-Memory-Bank`) or arg, and the caller passes `repo`, the effective key is `repo:` + normalized slug.
- **Rules:** Normalization identical to `context_save` (lowercase `owner/name`, `.git` stripped, URL → slug); `bankKey` always wins over `repo`; `repo` is accepted on all memory tools sharing `MemoryBankContext` (schema addition optional per tool — at minimum retain/recall/context/stats); bank auto-created on first write, `ResolveAsync` read paths return 404 when missing.

### RF-005: Migrations + audit
- **Description:** Dual migrations add `MergedIntoBankId` (+ index) and stay snapshot-consistent.
- **Rules:** SQLite and Postgres generated via `dotnet ef`; `MigrationParityTests` green; merge operation row is queryable via `operations_list` for audit.

## 5. API Contract

**Endpoint:** `POST /api/memory/banks/{targetKey}/merge`
**Auth:** `Bearer aft_*` (existing memory scope)

**Request:**
```json
{ "sourceKey": "myrepo" }
```

**Response (success):**
```json
{
  "merged": true,
  "alreadyMerged": false,
  "targetBankId": "guid",
  "moved": { "units": 12, "documents": 3, "directives": 2, "mentalModels": 1, "operations": 4, "unitEntities": 9, "observationHistories": 0 },
  "skipped": { "units": 1, "documents": 0, "directives": 1, "mentalModels": 0, "operations": 0, "unitEntities": 0, "observationHistories": 0 }
}
```

**Expected errors:** `400 source==target | 400 merge chain cycle | 404 bank not found` — generic ProblemDetails, no PII.

## 6. Acceptance Criteria

- [ ] **Given** banks A (3 units, 1 doc, 1 directive) and B (2 units) **when** `memory_bank_merge(sourceKey:A, targetKey:B)` **then** all A rows carry `BankId=B`, `A.MergedIntoBankId==B.Id`, merge op row exists with correct counts.
- [ ] **Given** A merged into B **when** retain/recall/list are called with `bankKey:A` **then** they operate on B transparently.
- [ ] **Given** A and B each retain the same content hash **when** merged **then** target's unit survives; source unit is soft-deleted and counted in `skipped`.
- [ ] **Given** A→B merge done **when** re-running `memory_bank_merge(A,B)` **then** `alreadyMerged:true`, zero rows touched.
- [ ] **Given** A→B and B→C **when** resolving key A **then** lands on C (≤8 hops); a cycle A→B→A throws 400.
- [ ] **Given** no `bankKey` in path/header/arg **when** `memory_recall` is called with `repo:"github.com/Org/MyRepo.git"` **then** resolves bank `repo:org/myrepo` (404 if absent, auto-create on retain).
- [ ] **Given** `bankKey` AND `repo` both supplied **when** any memory call **then** `bankKey` wins.
- [ ] **Given** merged tombstone A **when** `memory_bank_list` **then** A hidden by default, visible flagged with `includeMerged:true`.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `sourceKey == targetKey` | `A → A` | 400 |
| Unknown source | `ghost → B` | 404 |
| Source already merged to other target | `A(→B) → C` | 400 `alreadyMerged` naming the real target |
| Merge chain >8 | A→B→…→J | 400 cycle guard |
| Empty source bank | `empty → B` | `merged:true`, all counts 0, tombstone set |
| `repo` malformed | `repo:"not a repo"` | falls back to `default` bank (no error) |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** read files in section 3; confirm bank-scoped table list and unique indexes that define collisions; find the repo normalizer in context services.
- [ ] **T2 — Entity + migrations:** `MergedIntoBankId` on `MemoryBank`, EF config, dual `dotnet ef` migrations.
- [ ] **T3 — Redirect:** `ResolveAsync` follows `MergedInto` with cycle guard; list filter + `includeMerged`.
- [ ] **T4 — Merge service:** `MemoryBankMergeService` transactional re-parent + collision policy + op audit + result record.
- [ ] **T5 — Surface:** `memory_bank_merge` tool (schema+handler, contract pin) + REST route; `repo` arg in `EffectiveKey` + schema on retain/recall/context/stats.
- [ ] **T6 — Tests:** `MemoryBankMergeTests` covering every AC incl. dedupe, chains, idempotent re-run, repo fallback.
- [ ] **T7 — Gates:** full unit + integration + format + parity; update `skills/knowledge-hub` merge guidance; commit.

## 8. Organization Guardrails

- Branches: `feature/devin-20261113-memory-bank-merge` off `main`; never commit to `main`.
- `.github/workflows/` protected.
- Secrets: none new; merge writes no credentials anywhere.
- Dual-provider migrations mandatory (SQLite + Postgres) — `MigrationParityTests` is the gate.
- `MemoryBank` writes remain per-caller-scope (`ApiKeyId`) — no cross-key leakage.
- TDD per `execute-specs`: RED→GREEN→REFACTOR per slice.

## 9. Definition of Done

- [ ] RF-001..RF-005 implemented.
- [ ] Every AC covered by a passing test; edge cases handled.
- [ ] Unit + integration suites green; `dotnet format --verify-no-changes` clean; migration parity green.
- [ ] MCP contract pin updated; REST route mirrors tool.
- [ ] `memory_bank_merge` documented in `skills/knowledge-hub/SKILL.md` + regenerated docs skill.
- [ ] No PII/secrets in logs or audit payloads.

## Open Questions / Pending Ambiguity

- None — collision policy "target wins" was chosen over interactive adjudication deliberately (deterministic, matches Engram behavior).
