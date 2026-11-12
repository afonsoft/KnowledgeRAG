# SPEC-20261111-memory-defense

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `memory-defense` |
| Type | `Feature` |
| Stack | `.NET 10`, segurança no write-path |
| Repository | `afonsoft/KnowledgeRAG` |
| Branch | `feature/Devin-20261111-memory-defense` |
| Status | `Done` |
| Ticket | [#615](https://github.com/afonsoft/KnowledgeRAG/issues/615) — Epic E29 #609 |
| Source | Benchmark analysis: `vectorize-io/hindsight` (`extensions/memory_defense.py`) |
| Depends on | Independente; integra com `SPEC-20261111-agent-memory-retain` via hook `IMemoryDefenseScreen` |

## 1. User Story

**As a** operador do KnowledgeHub
**I want** que todo write-path (retain, notes, ingestão, context_save) passe por um scan de secrets/PII com política configurável
**So that** tokens, chaves e dados pessoais não entrem na memória/corpus — são redigidos ou bloqueados antes de persistir, com auditoria sem vazar o valor.

## 2. Contexto técnico

- **Referência hindsight**: `memory_defense.screen()` roda **antes de qualquer persistência** no retain. `DefensePolicy` mapeia padrão→`ALLOW|REDACT|BLOCK` sobre ~45 padrões (tokens de GitHub/AWS/OpenAI, JWT, chaves privadas, cartões, CPF/PII etc.). REDACT reescreve com `[REDACTED:<type>]`; BLOCK descarta o item (lote todo bloqueado → 422). Ocorrências logadas por **fingerprint** (hash), nunca o valor bruto; webhook `memory_defense.triggered`.
- **O que já temos**: scan de secrets embutido em `ContextPayload`/`context_save` (`src/KnowledgeHub.Server/Context/`), redaction de logs Serilog (`***REDACTED***` enricher), `SecurityEvent` entity, `IIntegrationSecretStore`. Ou seja: a peça existe mas está acoplada ao context-snapshot — falta generalizar em serviço compartilhado + políticas + cobertura de todos os write-paths.

## 3. Requisitos

### RF-001 — `ISecretScanner` compartilhado

- **Description:** Extrair o scan atual de `ContextPayload` para `Security/SecretScanner.cs` — catálogo de padrões nomeados (≥40: `github_token`, `aws_access_key`, `openai_api_key`, `jwt`, `private_key`, `slack_token`, `generic_api_key`, `credit_card`, `cpf`, `email_pii`, `password_assignment`, `connstring`, `bearer_token` etc.) retornando matches `{pattern, span, fingerprint}` (fingerprint = SHA-256 do match + salt do server).
- **Rules:** regexes com timeout (≥100ms por padrão, ReDoS-safe); valores **nunca** em logs/exceptions (só fingerprint + pattern + offset); catálogo extensível via config `Security:SecretScan:ExtraPatterns` (regex validados no boot).
- **Input → Output:** serviço único usado por todos os write-paths.

### RF-002 — Política `DefensePolicy`

- **Description:** Config `Security:SecretScan` = `{ defaultAction: 'redact'|'block'|'allow', patterns: { <name>: 'allow|redact|block' }, enabled: true }` global; override por bank (`MemoryBank.ConfigJson["defense"]`) e por source (`KnowledgeSource` metadata). Resolução: bank/source > global.
- **Rules:** `allow` registra finding mas deixa passar (audit mode); `redact` substitui por `[REDACTED:<pattern>]`; `block` rejeita o item; default global = `redact` para secrets, `allow` para PII fraca (email) — decisão documentada.
- **Input → Output:** política efetiva resolvida por escopo.

### RF-003 — Cobertura dos write-paths

- **Description:** Scan obrigatório antes de persistir em: `memory_retain` (via `IMemoryDefenseScreen`), `write_note`, `write_knowledge`, ingestão de chunks (`IngestionService`), `context_save` (migrar do scan próprio para o compartilhado — comportamento preservado). Item todo bloqueado → `422` com `{ blocked: true, findings: [{pattern, count}] }` (sem spans/valores); parcial → prossegue com matches redigidos e `redactions:[{pattern,count}]` no output.
- **Rules:** scan roda no texto final a persistir (após chunking = por chunk, evitando bypass por split); findings nunca incluem o valor.
- **Input → Output:** nenhum caminho de escrita persiste secret sem política aplicada.

### RF-004 — Auditoria e telemetria

- **Description:** `SecurityEvent` novo tipo `secret_scan` por decisão `{path, pattern, action, fingerprint, bankKey?/sourceId?, callerKeyPrefix}`. Métricas no activity feed; contadores por pattern (Serilog/health).
- **Rules:** sem valor bruto em nenhuma superfície (evento, log, resposta de API); retenção segue a política de SecurityEvent existente.
- **Input → Output:** trilha de auditoria completa e segura.

### RF-005 — Bypass documentado (opt-out consciente)

- **Description:** `Security:SecretScan:Enabled=false` global ou por-escopo desliga — exige flag explícita; boot loga `WARN` quando desligado; endpoint `/api/security/secret-scan/status` reporta estado efetivo.
- **Rules:** nunca desligar por default; `allow` em padrão específico != desligar o scanner.
- **Input → Output:** operação consciente do risco.

## 4. Fora de escopo

- DLP completo (classificação semântica de PII por LLM) — futuro; aqui é pattern-based.
- Scan em read-path (saídas) — redaction de saída fica para o enricher de logs já existente + futura SPEC se necessário.
- Webhooks externos (temos activity feed; webhook outbound pode vir com infra de webhooks própria).

## 5. Critérios de aceite

- [ ] **Dado** `write_note` com `ghp_...` token no conteúdo e política `redact` **então** nota persiste com `[REDACTED:github_token]` e resposta traz `redactions:[{pattern:"github_token",count:1}]`.
- [ ] **Dado** política `block` em `private_key` e retain contendo `-----BEGIN RSA PRIVATE KEY-----` **então** 422 `blocked:true` sem nenhuma unidade persistida.
- [ ] **Dado** secret quebrado em 2 chunks na ingestão **então** cada chunk é scaneado e a parte contendo o match é redigida (não há bypass por split).
- [ ] **Dado** `context_save` após migração **então** comportamento idêntico ao atual (mesmos padrões, mesma UX) usando o serviço compartilhado — teste de paridade.
- [ ] **Dado** override por bank `{defense:{patterns:{email_pii:'block'}}}` **então** só naquele bank e-mails bloqueiam.
- [ ] **Edge:** ReDoS — pattern malicioso em `ExtraPatterns` é rejeitado no boot; nenhum valor de match aparece em log/evento/resposta (assert greppable).

## 6. Arquivos

**Ler antes:**
- `src/KnowledgeHub.Server/Context/{ContextPayload,ContextSnapshotService}.cs`
- `src/KnowledgeHub.Server/Security/`, `Domain/Entities/SecurityEvent.cs`
- `src/KnowledgeHub.Server/Ingestion/IngestionService.cs`, `Mcp/ToolProviders/{KnowledgeToolsProvider,ObsidianToolsProvider,ContextToolsProvider}.cs`
- `/tmp/hindsight/hindsight-api-slim/hindsight_api/extensions/builtin/` (memory_defense)

**Criar/modificar:**
```text
src/KnowledgeHub.Server/Security/{SecretScanner,DefensePolicyResolver,ISecretScanner}.cs
src/KnowledgeHub.Server/Memory/IMemoryDefenseScreen.cs (hook; impl delega ao SecretScanner)
src/KnowledgeHub.Server/Context/ContextPayload.cs (migrar para serviço compartilhado)
src/KnowledgeHub.Server/Api/SecurityEndpoints.cs (status)
tests/KnowledgeHub.Tests.Unit/Security/SecretScannerTests.cs
tests/KnowledgeHub.Tests.Integration/Security/*
```

## 7. Riscos

- Falsos positivos em código-fonte (hashes, exemplos) — mitigado por `allow` por padrão + override por escopo + modo audit.
- Regex perf em docs grandes — timeout por pattern + scan por chunk.
- Padrões desatualizados — catálogo versionado + `ExtraPatterns` + revisão periódica (documentar em CLAUDE.md).
- Vazamento via fingerprint reversível — salt server-side no hash.

## 8. Task Plan + DoD

- [ ] T1: `SecretScanner` + catálogo de padrões + fingerprint + `ExtraPatterns` validado no boot.
- [ ] T2: `DefensePolicyResolver` (global→bank/source) + ações allow/redact/block.
- [ ] T3: wiring nos 5 write-paths + `IMemoryDefenseScreen` + respostas 422/redactions.
- [ ] T4: auditoria `SecurityEvent` + status endpoint + métricas.
- [ ] T5: testes (unit: patterns/false-positive/paridade context_save; integração: write-paths E2E; greppable no-value-in-logs) + gates.
- [ ] DoD: RF implementados, AC cobertos, zero valor bruto em qualquer superfície (teste dedicado), ≥80% coverage.
