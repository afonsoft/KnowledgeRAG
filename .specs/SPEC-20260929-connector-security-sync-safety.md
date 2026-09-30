# SPEC-20260929-connector-security-sync-safety

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `connector-security-sync-safety` |
| Type | `Fix` (bugs de sync + segurança SSRF/segredos) |
| Stack | `.NET 10 / C#` — conectores, `IIntegrationSecretStore`, `HttpClient` egress |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/Devin-20260929-connector-security-sync-safety` |
| Status | `Done` |
| Source | Comentários `devin-ai-integration` em PRs #371–#374, #381 |

## 1. User Story

**As a** mantenedor do KnowledgeHub
**I want** conectores endurecidos contra apagão de índice, vazamento de credenciais e SSRF
**So that** uma pasta indisponível não apague o índice, PATs não vazem por redirect e endpoints configuráveis não virem proxy para a rede interna.

## 2. Findings

1. 🔴 Nova API key/connection-string é recusada ao criar fonte — validação acontece antes do secret estar registrado (RestApi, SqlDatabase, Git, Audio — padrão compartilhado).
2. 🔴 Arquivos homônimos em pastas distintas compartilham o mesmo documento/sobrescrevem (Unstructured, DocumentFile).
3. 🔴 Pasta/árvore indisponível ou truncada → sync deleta documentos já indexados (Obsidian/Audio/Git tree truncation — deleção em massa sem safety gate).
4. 🔴 Mudança de opções (filtros Git, extração Unstructured, opções de áudio, config de tabelas) não invalida fingerprint → documentos ficam com extração antiga.
5. 🟥 **SSRF** — URLs de figuras na legendagem de visão; endpoint Whisper; endpoint Unstructured pode receber `file://`/path local; **redirects do Git enviam PAT para outro host** e alcançam rede interna; DNS pinning não fixa o IP resolvido.
6. 🟨 Erros da API externa logam payload/segredo potencial (Unstructured, Whisper).
7. 🔍 GitLab: `maxFileSize` não aplicado; URLs com subgrupos reduzidas incorretamente; fallback `main→master` mantém fingerprint da branch errada; alterar URL mantém repo anterior; metadados prometidos ausentes.

## 3. Requirements

- RF-001: `RequiredKeys` podem ser salvos junto à criação da fonte (write-through ao secret store) ou validação diferida até primeiro sync — nunca rejeitar a criação por chave ainda não persistida.
- RF-002: Identidade de documento inclui path completo (não só basename).
- RF-003: **Safety gate de deleção** — se a enumeração retornar <50% do índice atual (ou erro de acesso), abortar deleções e reportar `failed` em vez de apagar; deleção em massa só quando a enumeração foi completa e bem-sucedida.
- RF-004: Fingerprint incorpora as opções relevantes por conector (include/exclude, strategy, OCR/language, table-mode, branch).
- RF-005: **Egress policy** — `HttpClient` de conectores com handler que: bloqueia RFC1918/loopback/link-local por default (opt-in `allowPrivateNetworks`), não envia `Authorization` a hosts fora do origin original (redirect-safe), fixa IP pós-DNS.
- RF-006: Sanitização de erro — mensagens de exceção HTTP nunca incluem body de request/authorization; testar com secret canário.
- RF-007: GitLab: honrar `maxFileSize`, subgrupos (`group/sub/repo`), fingerprint por branch resolvida.

## 4. Acceptance Criteria

- AC-1: criação de fonte com chave nova persiste secret + fonte sem erro — teste de integração por conector afetado.
- AC-2: sync com diretório indisponível → job `failed`, 0 documentos deletados — teste.
- AC-3: redirect Git para outro host → request sem header `Authorization` — teste com servidor fake.
- AC-4: endpoint `http://169.254.169.254` rejeitado (SSRF block) — teste.
- AC-5: suite verde, 0 warnings.
