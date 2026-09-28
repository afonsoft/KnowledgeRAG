# SPEC-20260927-cryptographic-evidence-provenance-chain

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `cryptographic-evidence-provenance-chain` |
| Type | `Feature` |
| Stack | `.NET 10 / System.Security.Cryptography (SHA-256, Ed25519/HMAC) / System.Text.Json / EF Core SQLite` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `#271` |
| Status | `Done` |

## 1. User Story

**As a** auditor, oficial de conformidade ou integrador corporativo do Knowledge MCP Hub
**I want** que cada resposta gerada pelo RAG, execução de ferramenta e decisão de agente emita um recibo criptográfico encadeado à prova de adulteração (Evidence Chain)
**So that** eu possa verificar matematicamente a proveniência exata de qualquer informação gerada, comprovando quais chunks foram recuperados, quais ferramentas foram executadas e se os registros foram modificados após a geração.

**Problem context:**
Em ambientes corporativos e regulados, respostas sintetizadas por agentes de IA ("alucinações", decisões automatizadas ou consultas sobre dados confidenciais) sofrem com a falta de auditabilidade verificável externamente. Logs comuns em texto ou bancos de dados relacionais padrão podem ser alterados, deletados ou falsificados post-hoc por administradores ou invasores sem deixar vestígios matemáticos.
Inspirado na proposta de RFC do **PentAGI** (`examples/proposals/evidence_chain.md`), esta SPEC estabelece uma cadeia criptográfica de recibos encadeados (DAG hash-linked estilo Merkle) para todas as operações do Knowledge MCP Hub. Cada resposta do RAG, tool call e consulta amarra o hash da pergunta, os hashes dos chunks recuperados, o hash do prompt de sistema e a assinatura digital da instância, permitindo exportar pacotes de verificação autônomos (`evidence-bundle.json`) verificáveis offline por ferramentas independentes.

## 2. Scope

**In scope:**
- **Modelo de Recibo Criptográfico (`EvidenceReceipt`):**
  - Campos do recibo:
    - `ReceiptId`: UUID estável formato ULID / GUID ordenável por tempo.
    - `ParentReceiptIds`: lista de identificadores dos recibos imediatamente anteriores no mesmo fluxo/sessão.
    - `SessionId` / `ThreadId` / `ApiKeyId`: identificadores contextuais.
    - `EventType`: `QuerySubmitted`, `ChunksRetrieved`, `ToolExecuted`, `AnswerSynthesized`.
    - `ActorType`: `User`, `Agent`, `System`, `McpTool`.
    - `InputHash`: SHA-256 canonical do payload de entrada (pergunta ou argumentos da tool).
    - `OutputHash`: SHA-256 canonical da saída gerada.
    - `ArtifactHashes`: lista de hashes SHA-256 de chunks e documentos citados.
    - `Timestamp`: UTC ISO-8601 server-side.
    - `ParentDigest`: SHA-256 combinado dos hashes dos pais.
    - `ReceiptDigest`: SHA-256 canonical do corpo do recibo.
    - `Signature`: Assinatura criptográfica (HMAC-SHA256 ou Ed25519) da chave da instância.
- **Canalização e Persistência de Recibos:**
  - Tabela `EvidenceReceipts` no banco SQLite/Postgres com índices por `SessionId`, `Timestamp` e `ReceiptDigest`.
  - Inserção append-only intransponível (regras de integridade impedem UPDATE/DELETE).
- **Emissão Automática no Ciclo de RAG e MCP:**
  - `AskKnowledge`: emite recibo `ChunksRetrieved` contendo os hashes dos chunks do banco + recibo `AnswerSynthesized` amarrado ao resultado final.
  - `AgentChat`: emite recibos sequenciais para cada `tool_call` e resposta.
- **Exportação e Verificação Offline:**
  - Endpoint REST `GET /api/v1/evidence/{sessionId}/export`: gera pacote JSON assinado com todos os recibos e metadados de chave pública.
  - Ferramenta / classe utilitária de verificação `EvidenceChainVerifier`: recalcula os digests, valida a árvore genealógica de hashes e atesta a validade ou aponta exatamente o recibo violado.

**Out of scope:**
- Certificados PKI X.509 corporativos em hardware HSM (v1 utiliza par de chaves gerenciado pelo Kestrel Data Protection / SecretStore).
- Gravação de recibos em blockchains externas públicas.

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Server/Audit/Evidence/`:
  - `EvidenceReceipt.cs`: entidade e contrato do recibo.
  - `IEvidenceChainService.cs`: interface para gravação e verificação de recibos.
  - `EvidenceChainService.cs`: orquestrador de hashing canônico e assinatura.
  - `EvidenceChainVerifier.cs`: motor de validação offline de integridade.
  - `CanonicalJsonSerializer.cs`: normalizador JSON para garantir hashes determinísticos.
- `src/KnowledgeHub.Server/Data/`:
  - Entidade `EvidenceReceiptEntity` no `KnowledgeHubDbContext` e migração correspondente.
- `src/KnowledgeHub.Server/Endpoints/EvidenceEndpoints.cs`: endpoints para consulta e download do bundle de auditoria.
- `tests/KnowledgeHub.Tests.Unit/Audit/`: testes unitários cobrindo integridade da cadeia, detecção de adulteração e assinaturas.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Auth/ApiKeyUsageMiddleware.cs`
- `src/KnowledgeHub.Server/Services/LlmAnswerSynthesisService.cs`
- `src/KnowledgeHub.Server/Data/KnowledgeHubDbContext.cs`
- `src/KnowledgeHub.McpEngine/Tools/AskKnowledgeTool.cs`

**Files to create or modify:**
```text
src/KnowledgeHub.Server/Audit/Evidence/EvidenceReceipt.cs             (create)
src/KnowledgeHub.Server/Audit/Evidence/IEvidenceChainService.cs       (create)
src/KnowledgeHub.Server/Audit/Evidence/EvidenceChainService.cs       (create)
src/KnowledgeHub.Server/Audit/Evidence/EvidenceChainVerifier.cs      (create)
src/KnowledgeHub.Server/Audit/Evidence/CanonicalJsonSerializer.cs    (create)
src/KnowledgeHub.Server/Data/Entities/EvidenceReceiptEntity.cs       (create)
src/KnowledgeHub.Server/Endpoints/EvidenceEndpoints.cs               (create)
src/KnowledgeHub.Server/Data/KnowledgeHubDbContext.cs                (modify)
src/KnowledgeHub.Server/Services/LlmAnswerSynthesisService.cs        (modify)
src/KnowledgeHub.McpEngine/Tools/AskKnowledgeTool.cs                 (modify)
tests/KnowledgeHub.Tests.Unit/Audit/EvidenceChainTests.cs            (create)
```

## 4. Requirements

### RF-001: Estrutura Canônica de Recibos Criptográficos
- **Description:** O sistema deve estruturar todo evento significativo de RAG e tool call em um recibo com serialização canônica determinística.
- **Rules:**
  - As chaves do JSON para cômputo de hash devem ser estritamente ordenadas alfabeticamente sem espaços em branco (`CanonicalJsonSerializer`).
  - O `ReceiptDigest` é o SHA-256 em hexadecimal da string canônica do recibo (excluindo o campo de assinatura).
  - O `ParentDigest` vincula os recibos imediatamente anteriores, formando uma cadeia encadeada e imutável.
- **Input → Output:** Dados do evento de RAG → `EvidenceReceipt` assinado e indexado.

### RF-002: Encadeamento de Proveniência de Resposta RAG
- **Description:** Durante a execução de `ask_knowledge`, a síntese da resposta deve ligar diretamente a pergunta feita, a lista de chunks recuperados e o texto final sintetizado.
- **Rules:**
  - O recibo registra o SHA-256 de cada chunk de conhecimento consumido no campo `ArtifactHashes`.
  - Se a resposta sintetizada for alterada após o fato no banco de dados, o `OutputHash` do recibo discordará do conteúdo, disparando violação na verificação.
- **Input → Output:** Pergunta + 3 chunks recuperados + Resposta gerada → Recibo imutável atestando o vínculo causal.

### RF-003: Verificação de Integridade e Detecção de Adulteração
- **Description:** O motor `EvidenceChainVerifier` deve inspecionar uma sequência de recibos e comprovar se há quebra na cadeia, ordenação alterada ou digest inválido.
- **Rules:**
  - O algoritmo reprocessa cada recibo na ordem temporal:
    1. Recalcula o `ReceiptDigest` canônico.
    2. Compara com a assinatura digital.
    3. Confirma que os `ParentReceiptIds` apontam para recibos existentes válidos.
  - Qualquer discrepância em um único byte resulta em `VerificationResult.Failed` detalhando o ID do nó comprometido.
- **Input → Output:** Coleção de recibos → Relatório de integridade `Valid = true/false` com rastro dos nós.

### RF-004: Endpoint de Exportação de Pacote de Evidências
- **Description:** Expor rota segura para download de bundles de auditoria em formato JSON para arquivamento ou perícia externa.
- **Rules:**
  - Endpoint `GET /api/v1/evidence/sessions/{sessionId}/bundle`.
  - Requer política de autorização `Admin` ou posse da API Key da sessão.
  - Inclui todos os recibos da sessão e as chaves de verificação públicas necessárias.

## 5. API Contract (if applicable)

**Endpoint:** `GET /api/v1/evidence/sessions/{sessionId}/bundle`
**Auth:** `Bearer aft_...` (Policy: `AuditRead` ou `Admin`)

**Response (success 200):**
```json
{
  "sessionId": "ses_01j8xyz987654321",
  "generatedAt": "2026-09-27T14:30:00Z",
  "instanceKeyId": "khub-master-2026",
  "totalReceipts": 4,
  "chainIntegrity": "Valid",
  "receipts": [
    {
      "receiptId": "rec_01j8xyz_01",
      "parentReceiptIds": [],
      "eventType": "QuerySubmitted",
      "actorType": "User",
      "inputHash": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "outputHash": "",
      "artifactHashes": [],
      "timestamp": "2026-09-27T14:29:55Z",
      "receiptDigest": "a1b2c3d4e5...",
      "signature": "sig_ed25519_..."
    },
    {
      "receiptId": "rec_01j8xyz_02",
      "parentReceiptIds": ["rec_01j8xyz_01"],
      "eventType": "ChunksRetrieved",
      "actorType": "System",
      "inputHash": "a1b2c3...",
      "outputHash": "f6e5d4...",
      "artifactHashes": [
        "c8d9e0... (chunk 1)",
        "b1a2c3... (chunk 2)"
      ],
      "timestamp": "2026-09-27T14:29:57Z",
      "receiptDigest": "b2c3d4e5f6...",
      "signature": "sig_ed25519_..."
    }
  ]
}
```

## 6. Acceptance Criteria

- [x] **Given** uma execução de `ask_knowledge` **when** a síntese é concluída **then** um recibo com `EventType = AnswerSynthesized` é persistido com os hashes exatos dos chunks retornados e amarrado ao recibo pai da busca.
- [x] **Given** um pacote de recibos íntegros **when** `EvidenceChainVerifier.VerifyChain(bundle)` é executado **then** o resultado é `IsValid = true` e zero erros são reportados.
- [x] **Given** um recibo cujo campo `OutputHash` ou `Timestamp` foi alterado manualmente no banco **when** o verificador roda **then** o resultado é `IsValid = false`, indicando `TamperingDetected` no nó específico.
- [x] **Given** a remoção de um recibo intermediário da cadeia **when** a verificação roda **then** o validador falha apontando `BrokenParentLink` na referência pai inexistente.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Sessão sem chamadas de tools | Sessão recém criada | Retorna bundle vazio ou apenas recibo de inicialização sem falhas |
| Chunks sem conteúdo textual válido | Chunk com string vazia | Hash SHA-256 do vazio (`e3b0c44...`) gerado normalmente |
| Chave da instância rotacionada | Recibos antigos assinados com `key-2025`, novos com `key-2026` | Verificador aceita bundle utilizando histórico de chaves públicas por KeyId |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** Revisar pipeline de síntese em `LlmAnswerSynthesisService.cs` e entidades EF Core.
- [x] **T2 — Canonical Serializer:** Implementar `CanonicalJsonSerializer.cs` garantindo ordenação estável de propriedades e ausência de formatação inconsistente.
- [x] **T3 — Receipt & Storage:** Criar `EvidenceReceiptEntity.cs` e registrá-la no `KnowledgeHubDbContext`.
- [x] **T4 — Chain Service:** Implementar `EvidenceChainService.cs` com cômputo de hashes SHA-256 e geração de assinaturas.
- [x] **T5 — Pipeline Hook:** Injetar a emissão de recibos nos fluxos de `AskKnowledgeTool` e `AgentChatService`.
- [x] **T6 — Export & Verifier:** Implementar `EvidenceChainVerifier.cs` e o endpoint `GET /api/v1/evidence/sessions/{sessionId}/bundle`.
- [x] **T7 — Unit & Tamper Tests:** Validar cenários de sucesso, adulteração de conteúdo, quebra de link e rotação de chaves.

## 8. Organization Guardrails

- **Imutabilidade:** Nunca permitir operações de `UPDATE` ou `DELETE` na tabela de recibos; registros de evidência são exclusivamente append-only.
- **Segurança de Chaves:** A chave privada de assinatura de evidências deve ser mantida protegida via `IDataProtectionProvider` ou secret store do ambiente.

## 9. Definition of Done

- [x] Todos os requisitos (RF-001 a RF-004) implementados.
- [x] Testes unitários comprovando detecção de adulteração de hash com 100% de precisão.
- [x] Endpoint de exportação funcional e protegido por política de autenticação.
- [x] Zero impacto perceptível de latência na resposta de chat (> 95% do overhead de hashing < 2ms).
