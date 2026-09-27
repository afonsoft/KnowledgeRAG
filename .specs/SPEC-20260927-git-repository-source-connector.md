# SPEC-20260927-git-repository-source-connector

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `git-repository-source-connector` |
| Type | `Feature` |
| Stack | `.NET 10 / HttpClient / System.Text.Json / GitHub & GitLab REST APIs` |
| Repository | `afonsoft/LangGraph-UI` |
| Branch | `feature/OpenCode-20260927-pentagi-rag-enhancements` |
| Ticket | `[A DEFINIR]` |
| Status | `Draft` |

## 1. User Story

**As a** desenvolvedor ou arquiteto utilizando o Knowledge MCP Hub
**I want** cadastrar repositórios remotos do GitHub, GitLab ou Gitea como fontes de conhecimento (`SourceType = GitRepository`)
**So that** documentações em Markdown, especificações técnicas e arquivos de código-fonte de repositórios remotos sejam automaticamente ingeridos e indexados para consultas de RAG e MCP sem necessidade de clone local em disco.

**Problem context:**
Grande parte do conhecimento técnico de engenharia de software reside em repositórios Git (arquivos `README.md`, pastas `docs/`, `ADR/`, `.specs/` e código-fonte em várias linguagens). Hoje, para indexar esse conteúdo no Knowledge MCP Hub, o usuário precisa clonar manualmente o repositório no host e apontar como `DocumentFile` ou `ObsidianVault`.
Inspirado na implementação do **Weaviate Verba** (`goldenverba/components/reader/GitReader.py`), esta SPEC implementa o conector `GitRepositoryConnector` nativo no Knowledge MCP Hub. O conector utiliza as APIs REST públicas/autenticadas do GitHub, GitLab ou Gitea para ler a árvore de arquivos recursiva (`git tree`), filtrar caminhos e extensões de arquivo com padrões glob (ex.: `**/*.md`, `docs/**`, `*.cs`, `*.py`), extrair o conteúdo via blob API e realizar sincronizações incrementais eficientes comparando o `commit_sha` da branch configurada (`LastCommitSha` fingerprint).

## 2. Scope

**In scope:**
- Novo membro no enum `SourceType.GitRepository = 14` em `src/KnowledgeHub.Shared/Contracts/SourceType.cs`.
- `GitRepositoryConnector` (`IIncrementalSourceConnector` + `IItemFetchConnector`) que aceita:
  - `provider`: `github` | `gitlab` | `gitea` (default: `github`).
  - `repoUrl` ou `owner` / `name` (ex.: `dotnet/runtime`, `https://github.com/weaviate/Verba`).
  - `branch`: nome da branch (default: `main` ou `master`).
  - `path`: subdiretório raiz opcional (ex.: `docs/` ou vazio para todo o repositório).
  - `includePatterns`: glob patterns (default: `["**/*.md", "**/README*", "**/*.txt"]`).
  - `excludePatterns`: glob patterns (default: `[".git/**", "**/node_modules/**", "**/bin/**", "**/obj/**", "**/*.min.js"]`).
  - `maxFiles`: limite de segurança (default: 200, clamp: 1–1000).
  - `maxFileSizeBytes`: limite de tamanho por arquivo (default: 500KB, clamp: 10KB–5MB).
- Suporte a credenciais privadas (Personal Access Token / PAT):
  - Armazenado via `IIntegrationSecretStore` com chave `git:{sourceId}` (nunca exposto no `ConfigurationJson`).
  - Suporte a repositórios públicos sem necessidade de token (respeitando rate limits anônimos).
- Sincronização Incremental por Commit SHA:
  - `Fingerprint = "git:{provider}:{owner}/{name}:{branch}:{commitSha}"`.
  - Se o `commitSha` da branch remota for idêntico ao da última sincronização bem-sucedida, o conector retorna status `NoChanges` sem re-baixar blobs.
- Validação e segurança de paths e SSRF:
  - Sanitização de URLs de instâncias auto-hospedadas (GitLab/Gitea) impedindo loopback/endereços privados não autorizados.
- UI `SourceEditDialog.razor`:
  - Seção dedicada para `GitRepository` com seleção de plataforma, campos de repositório, branch, padrões de filtro e campo de token mascarado.
- Inclusão no whitelist de sincronização automática do `ScheduledSyncBackgroundService`.

**Out of scope:**
- Operações de escrita, commit ou push no repositório remoto (o conector é estritamente read-only).
- Execução de comandos CLI `git` locais (a integração é 100% baseada em HTTP REST/API).
- Arquivos binários compilados (PDFs complexos dentro do Git, `.exe`, `.dll`, imagens — apenas texto e markdown).

## 3. Technical Context

**Where the change happens:**
- `src/KnowledgeHub.Shared/Contracts/SourceType.cs`: adição de `GitRepository = 14`.
- `src/KnowledgeHub.Server/Ingestion/Connectors/`:
  - `GitRepositoryConnector.cs`: implementação do conector.
  - `GitProviders/IGitApiClient.cs`: abstração de API para provedores Git.
  - `GitProviders/GitHubApiClient.cs`: cliente REST GitHub (Trees & Blobs).
  - `GitProviders/GitLabApiClient.cs`: cliente REST GitLab (Repository Tree & Files).
  - `GitProviders/GiteaApiClient.cs`: cliente REST Gitea.
  - `GitPathMatcher.cs`: motor de casamento de globs.
- `src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs`: registro de `RequiredKeys[GitRepository] = ["repoUrl"]`.
- `src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs`: adição ao whitelist de auto-sync.
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor`: formulário Blazor WASM.
- `tests/KnowledgeHub.Tests.Unit/Connectors/`: testes unitários com handlers HTTP mockados.

**Files to read before implementing:**
- `src/KnowledgeHub.Server/Ingestion/Connectors/{ISourceConnector,ConnectorConfig,WebPageConnector,NotionConnector}.cs`
- `src/KnowledgeHub.Server/Ingestion/IngestionService.cs`
- `src/KnowledgeHub.Server/Settings/{IIntegrationSecretStore,IntegrationSecretStore}.cs`
- `src/KnowledgeHub.Client/Pages/SourceEditDialog.razor`

**Files to create or modify:**
```text
src/KnowledgeHub.Shared/Contracts/SourceType.cs                          (modify — GitRepository = 14)
src/KnowledgeHub.Server/Ingestion/Connectors/GitRepositoryConnector.cs   (create)
src/KnowledgeHub.Server/Ingestion/Connectors/GitProviders/IGitApiClient.cs (create)
src/KnowledgeHub.Server/Ingestion/Connectors/GitProviders/GitHubApiClient.cs (create)
src/KnowledgeHub.Server/Ingestion/Connectors/GitProviders/GitLabApiClient.cs (create)
src/KnowledgeHub.Server/Ingestion/Connectors/GitPathMatcher.cs          (create)
src/KnowledgeHub.Server/Services/KnowledgeSourceService.cs               (modify)
src/KnowledgeHub.Server/BackgroundServices/ScheduledSyncBackgroundService.cs (modify)
src/KnowledgeHub.Server/KnowledgeHubServiceCollectionExtensions.cs       (modify)
src/KnowledgeHub.Client/Pages/SourceEditDialog.razor                     (modify)
tests/KnowledgeHub.Tests.Unit/Connectors/GitRepositoryConnectorTests.cs  (create)
tests/KnowledgeHub.Tests.Unit/Connectors/GitPathMatcherTests.cs         (create)
```

## 4. Requirements

### RF-001: Mapeamento de Configuração e Identificação de Repositório
- **Description:** O conector deve aceitar URLs completas (`https://github.com/owner/repo`) ou a tupla `owner` e `name`, normalizando para a estrutura padrão.
- **Rules:**
  - `repoUrl` válido deve resolver para plataforma conhecida (`github`, `gitlab` ou `gitea`).
  - Se `token` for fornecido na criação/edição, deve ser persistido em `IIntegrationSecretStore` sob a chave `git:{sourceId}` e removido do payload público (`hasKey = true`).
- **Input → Output:** URL `https://github.com/dotnet/aspnetcore` → Config estruturada com provider=github, owner=dotnet, repo=aspnetcore.

### RF-002: Descoberta de Arquivos via Árvore Git (Tree API)
- **Description:** O conector deve consultar o endpoint de árvore recursiva (`/repos/{owner}/{repo}/git/trees/{branch}?recursive=1`) para mapear os arquivos do repositório em uma única chamada HTTP.
- **Rules:**
  - O retorno deve ser filtrado via `GitPathMatcher` aplicando os `includePatterns` e `excludePatterns`.
  - Arquivos que ultrapassarem `maxFileSizeBytes` são desconsiderados com aviso nos logs.
  - A contagem total de arquivos elegíveis é truncada em `maxFiles` para proteção de recursos.
- **Input → Output:** Árvore com 1.000 arquivos → Seleciona 45 arquivos Markdown relevantes dentro das regras de inclusão/exclusão.

### RF-003: Download e Ingestão de Conteúdo de Blobs
- **Description:** Para cada arquivo elegível, o conector deve baixar o conteúdo cru (raw blob) e empacotar como `RawDocument`.
- **Rules:**
  - Metadados anexados ao documento: `source_url` (link web para o arquivo no commit exato), `path`, `commit_sha`, `extension`, `file_size`.
  - Falha transitória de download em um único arquivo deve registrar warning em `FailedUris` sem abortar a sincronização dos demais arquivos.
- **Input → Output:** Lista de caminhos de arquivos → Coleção de `RawDocument`s com conteúdo em texto claro prontos para chunking.

### RF-004: Sincronização Incremental por Commit SHA
- **Description:** Evitar downloads repetidos se o repositório não sofreu commits na branch especificada.
- **Rules:**
  - Antes de listar a árvore completa, o conector consulta o SHA do commit mais recente da branch (`GET /repos/{owner}/{repo}/commits/{branch}`).
  - Se `LastCommitSha == currentCommitSha` e `forceRefresh == false`, emite resultado imediato `NoChanges`.
  - Após sincronização bem-sucedida, atualiza o `Fingerprint` com o novo SHA.

## 5. API Contract (if applicable)

Configuração no `KnowledgeSource`:
```json
{
  "name": "Documentação Oficial Verba",
  "type": "GitRepository",
  "configuration": {
    "provider": "github",
    "owner": "weaviate",
    "name": "Verba",
    "branch": "main",
    "path": "",
    "includePatterns": ["**/*.md", "goldenverba/**/*.py"],
    "excludePatterns": [".git/**", "frontend/**"],
    "maxFiles": 100,
    "hasKey": false
  }
}
```

## 6. Acceptance Criteria

- [ ] **Given** uma URL pública de repositório GitHub **when** a sincronização roda **then** todos os arquivos Markdown que casam com o padrão `includePatterns` são baixados e convertidos em `RawDocument`.
- [ ] **Given** um repositório sincronizado com sucesso **when** uma nova sincronização é acionada sem novos commits na branch **then** o conector retorna status `NoChanges` e nenhum blob é re-baixado.
- [ ] **Given** um repositório privado onde o usuário cadastrou um Personal Access Token **when** a árvore é consultada **then** o header `Authorization: Bearer <token>` é enviado e os arquivos são recuperados.
- [ ] **Given** arquivos correspondendo a `excludePatterns` (ex.: `.git/config` ou `node_modules/index.js`) **when** a árvore é processada **then** esses arquivos são rigorosamente ignorados.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Repositório inexistente ou privado sem token | Owner/Repo inválido | Sync marca status `Failed` com mensagem HTTP 404/401 clara |
| Branch inexistente | `branch: "feature-fantasma"` | Sync falha informando que a referência remota não existe |
| Repositório vazio (zero commits) | Repositório recém inicializado | Retorna lista vazia de documentos sem falha de execução |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** Revisar contratos de conectores em `KnowledgeHub.Server/Ingestion/Connectors/`.
- [ ] **T2 — SourceType Enum:** Adicionar `GitRepository = 14` em `SourceType.cs`.
- [ ] **T3 — Git Clients:** Implementar `GitHubApiClient.cs` e `GitLabApiClient.cs` com suporte a autenticação por token.
- [ ] **T4 — Path Matcher:** Implementar `GitPathMatcher.cs` para testes de inclusão e exclusão por wildcard/glob.
- [ ] **T5 — Connector Core:** Implementar `GitRepositoryConnector.cs` integrando descoberta incremental por SHA.
- [ ] **T6 — UI & Service:** Adicionar campos na UI do Blazor `SourceEditDialog.razor` e registrar no `KnowledgeSourceService`.
- [ ] **T7 — Unit Tests:** Escrever testes cobrindo parsing de árvore, casamento de globs, tolerância a falhas e incrementalidade.

## 8. Organization Guardrails

- **Segurança:** O Personal Access Token nunca deve ser exposto no banco em texto claro nem enviado ao cliente Blazor no `ConfigurationJson`.
- **SSRF:** URLs de instâncias GitLab/Gitea devem ser restritas a esquemas HTTPS válidos e hosts que não apontem para endereços de rede local (RFC 1918) sem permissão explícita.

## 9. Definition of Done

- [ ] Todos os requisitos (RF-001 a RF-004) implementados.
- [ ] Testes unitários com clientes HTTP mockados atingindo 100% de cobertura nos cenários principais.
- [ ] UI Blazor atualizada e validada para cadastro de novas fontes Git.
- [ ] Sincronização incremental por commit SHA testada e comprovada.
