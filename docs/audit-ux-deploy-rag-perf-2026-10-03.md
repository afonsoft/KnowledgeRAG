# Auditoria — UX/Mobile, Deploy, RAG/MCP e Performance

Data: 2026-10-03 · Base: `main` pós-merge #533 (`6d24a19`)
Escopo: todas as telas do `KnowledgeHub.Client`, artefatos de implantação (Dockerfile, compose, install/backup, workflows), pipeline RAG + engine MCP e análise de performance residual (pós #532).

Método: leitura de código fonte com evidência `arquivo:linha`, publish real do client para medir payload, verificação de gates de CI existentes. Nada foi alterado — este doc é só análise e sugestão.

---

## 1. Telas — mobile e acessibilidade

### O que já está bom (não mexer)

- Sidebar mobile com backdrop + `aria-expanded` no toggler, alvo de toque 44px (`MainLayout.razor:28`, `MainLayout.razor.css:37`)
- Tokens CSS com contraste documentado (`app.css:167-174`), tema único claro coerente
- `.table-responsive-wrapper` em Sources/ApiKeys/McpMonitor/Settings; `.d-none-mobile` para colunas
- Botões com ícone colapsam rótulo em `<576px` mantendo texto no DOM para leitores de tela (`app.css:380-404`)
- Ações destrutivas majoritariamente com `PopConfirmButton` (confirmação)
- iOS safe-area (`app.css:319`), `-webkit-overflow-scrolling`, PWA manifest + SW

### Achados (ordenados por impacto)

| # | Severidade | Achado | Evidência | Sugestão |
|---|-----------|--------|-----------|----------|
| A1 | Alta (a11y) | `<html lang="en">` numa UI 100% pt-BR — leitores de tela pronunciam com fonética inglesa (WCAG 3.1.1) | `wwwroot/index.html:2` | `lang="pt-BR"` |
| A2 | Alta (a11y) | `role="button"`/`tabindex="0"` sem handler de teclado — focável mas não ativável por Enter/Espaço (WCAG 2.1.1) | `Pages/Sources.razor:57,64,71` (badges de status); `Pages/Settings.razor:1009,1012` (`<th role="button">` de sort) | Trocar `<span>`/`<th>` por `<button>` real, ou adicionar `@onkeydown` (Enter/Espaço). Sort de tabela idealmente `aria-sort` no `<th>` + `<button>` interno |
| A3 | Alta (a11y) | Labels visuais sem associação programática (`<label>` sem `for`, `<input>` sem `id`) | `Pages/Login.razor:27-33`, `Pages/ChangePassword.razor:22-31`, `Pages/Graph.razor:17-36` | `for=`/`id=` explícito nos inputs nativos |
| A4 | Alta (a11y) | Erros e estados dinâmicos não anunciados: `alert-danger` sem `role="alert"`; chat sem região live | `Login.razor:23`, `ChangePassword.razor:18`, `RagQualityDashboard.razor:16`, `Chat.razor:66-83` | `role="alert"` nos alerts de erro; `role="log" aria-live="polite"` no container de mensagens do Chat |
| A5 | Média (mobile) | Card de troca de senha com `width: 400px` fixo — transborda em viewports <400px (iPhone SE = 375px) | `Pages/ChangePassword.razor:11` | `style="width: 100%; max-width: 400px;"` |
| A6 | Média (mobile) | Tabelas fora de `.table-responsive-wrapper` — overflow horizontal em mobile | `Pages/Graph.razor:63` (6 colunas + nested), `Pages/RagQualityDashboard.razor:58` | Envolver em `.table-responsive-wrapper` + `.d-none-mobile` em colunas secundárias (Vigência/Episódio) |
| A7 | Média (UX) | Chat: Enter não envia (sem `@onkeydown`/form); "Excluir thread" deleta sem confirmação; input só com placeholder | `Pages/Chat.razor:59-61,85-89,124-130` | `@onkeydown` Enter→SendAsync; `PopConfirmButton` no delete; `DisplayText`/`ShowLabel` ou `aria-label` no input |
| A8 | Média (a11y) | Aprovações: "Negar" sem confirmação; input de args-override sem label | `Pages/Approvals.razor:40-41,47-48` | `PopConfirmButton` em Negar; label no `BootstrapInput` |
| A9 | Baixa (a11y/i18n) | Strings em inglês: `blazor-error-ui` ("An unhandled error has occurred"), `loading-progress-text` ("Loading") | `wwwroot/index.html:31-34`, `app.css:101` | Traduzir para pt-BR |
| A10 | Baixa (a11y) | Hierarquia de headings: toda página começa em `<h3>` sem `<h1>` | todas as `Pages/*.razor` | Um `<h1 class="visually-hidden">@PageTitle</h1>` por página ou começar em `<h1>` |
| A11 | Baixa (UX) | `.sidebar-collapse-btn` = 36px (abaixo dos 44px de touch target), ok pois é `d-none d-md-inline-flex` (só desktop) — mas o toggler mobile já cobre | `NavMenu.razor.css:22-23` | Opcional |

**Quick wins (1 PR, ~1h):** A1, A3, A5, A6, A9 — mudanças cirúrgicas sem risco.
**Médio:** A2, A4, A7, A8 — toca markup/comportamento, precisa de revisão visual.
**Estrutural:** A10 — convenção de headings em todas as páginas.

---

## 2. Implantação

### O que já está bom

- Dockerfile multi-stage self-contained, não-root (`USER app`), RID por arch, restore sem RID comentado, `apt upgrade` no runtime
- Health checks `/health/live` + `/health/ready` completos (db, embeddings, ingestion, vectorstore, redis condicional)
- `install.sh --host --systemd` e `backup.sh`/`restore.sh` com VACUUM INTO + vaults + dataprotection keys
- CI com gates reais: format, cobertura, unit+integration, SonarCloud, CodeQL, Trivy, Snyk
- release.yml com versionamento por tag, GHCR + Docker Hub (secrets configurados nesta sessão)

### Gaps

| # | Severidade | Gap | Evidência | Sugestão |
|---|-----------|-----|-----------|----------|
| D1 | Alta | `HEALTHCHECK` bate `/` (SPA estático) — container reporta healthy com DB quebrado/migrations falhando | `Dockerfile:59-60` | `curl -fsS http://localhost:8080/health/ready` |
| D2 | Alta | `backup.sh` só cobre SQLite — com `DATABASE_PROVIDER=postgres` (unified provider desde 2026-09-26) o backup silenciosamente não protege o catálogo | `backup.sh` inteiro (só `knowledgehub.db`) | Documentar/adicionar ramo Postgres (`pg_dump`) ou validar provider no início do script |
| D3 | Média | Publish do client dentro do Dockerfile pode rodar **sem `wasm-tools`** — medido na VM: publish imprimiu "Publishing without optimizations … install wasm-tools" (sem IL-trimming). Se o `sdk:10.0` também não trouxer o workload, a imagem de produção carrega WASM não-trimado | `Dockerfile:36-37`; publish local observado | `RUN dotnet workload install wasm-tools` no stage build (idempotente) — corta wasm não-referenciado |
| D4 | Média | Payload de boot medido: **~6,2 MB** brotli (94 .wasm + 3 icudt ~600KB br). pt-BR não precisa de CJK | medição real em `/tmp/kh-client-pub` | `<BlazorIcuDataFileName>icudt_EFIGS.dat</BlazorIcuDataFileName>` ou `InvariantGlobalization` no client; avaliar `WasmStripILForNSObjects`/`RunAOTCompilation` (já no roadmap do plano de perf) |
| D5 | Média | `docker-compose.yml` sempre builda local — sem caminho documentado para subir a imagem publicada (GHCR/Docker Hub) | `docker-compose.yml:5-6` | Bloco comentado `image: ghcr.io/afonsoft/knowledgerag:X.Y.Z` no override example |
| D6 | Baixa | Sem `logging:` no compose — json-file do docker cresce sem bound (o file sink tem rotação, stdout não) | `docker-compose.yml` | `logging: {driver: json-file, options: {max-size: 10m, max-file: "3"}}` |
| D7 | Info | `.env.example` existe — verificar se cobre `ALLOWED_HOSTS`/`DATABASE_PROVIDER`/`POSTGRES_*` na mesma medida do compose | — | Sincronizar exemplo |

---

## 3. RAG + MCP

### O que já está bom

- Pipeline híbrido maduro: rewrite history-aware → expansão (multi/HyDE) → braços vetor+lexical+grafo em `Task.WhenAll` por braço → RRF → floors → MMR → relaxation → context-expand — com fail-soft por braço e cache de resultado
- sqlite-vec KNN + pgvector com fallback, FTS5 bm25, `SqliteConnectionLease` para leitores concorrentes dedicados
- Ingestão assíncrona com jobs persistidos, chunking semântico opt-in, conectores amplos (Obsidian/WebDAV, Notion, REST, SQL guard, Git, RSS, YouTube, S3/Azure/OCI, Unstructured, áudio)
- MCP sobre o SDK oficial + extensão Tasks (MRTR) para tools longas; A2A v1.0; aprovações HITL; rate limiting por key/user/IP; eval com baselines e latências p50/p95/p99; evidência HMAC append-only; monitor SignalR

### Gaps e melhorias

| # | Severidade | Gap | Evidência | Sugestão |
|---|-----------|-----|-----------|----------|
| R1 | Alta (perf) | Braços **vetor e lexical rodam sequenciais** (`await RunVectorArmsAsync` depois `await RunLexicalArmsAsync`) — latência híbrida = soma. Ambos já usam conexões dedicadas (`SqliteConnectionLease`), ou seja, paralelismo entre eles é seguro | `Services/SearchService.cs:911-917` | `Task.WhenAll(vectorTask, lexicalTask)` — latência vira ~max(braços). Cuidado: `degraded.Any` compartilhado (escrita de bool é benigna); medir com `search.rrf` spans |
| R2 | Média (RAG) | Chat UI refaz `GET /threads/{id}` completo após cada envio (recarrega todas as mensagens) — O(thread) por mensagem | `Pages/Chat.razor:138-140`; existe `MapStreamingApi` no server | Usar o endpoint SSE de streaming (token incremental + melhor UX) |
| R3 | Média (RAG) | `db.Threads.Include(t => t.Messages)` carrega a thread inteira por request do agente (transcript sem bound no load — o cap de 100 msgs do #532 cobre só a *sumarização*) | `Services/AgentService.cs:137` | Load bounded (últimas N + summary) — já listado no roadmap do plano de perf |
| R4 | Média (processo) | Sync-over-async em paths quentes/frios: lazy init de settings com `GetAwaiter().GetResult()` (bloqueia thread sob lock); chunker vision e `AssistantChatClientProvider` | `Settings/SingleRowSettingsStore.cs:81`, `Settings/ApiKeyChatSettingsService.cs:222`, `Assistant/AssistantChatClientProvider.cs:90,128`, `Ingestion/Chunking/VisionLayoutTextChunker.cs:28` | Onde for cold-path (init único) ok; os providers deveriam subir o async até o caller ou double-check com Lazy<Task<T>> |
| R5 | Baixa (MCP) | Tools longas não-em-task (upstream crawls sem extensão tasks declarada) seguram a chamada — mitigado pelo timeout por provider | `Program.cs:77-81` | Ampliar `ExecutionModeSelector` ou timeout advisory por tool |
| R6 | Baixa (RAG) | Eval cobre a tríade + latências; falta recall@k/MRR no corpus de eval para regredir retrieval (não só síntese) | `Eval/EvalRunner.cs` | Métrica de retrieval no eval gate |
| R7 | Info | `approve → resume` é manual (POST /api/agent/resume) — UX do fluxo HITL pode resumir sozinho | `Pages/Approvals.razor:92` | Auto-resume opcional após aprovar |

---

## 4. Performance residual (além do #532)

Medido/verificado:

| # | Achado | Evidência |
|---|--------|-----------|
| P1 | Braços híbridos sequenciais (R1) — maior alavanca restante no path de busca | `SearchService.cs:911-917` |
| P2 | Payload WASM ~6,2MB wire, publish possivelmente sem trimming (D3/D4) | publish real |
| P3 | `degraded.Any` escrito por braços concorrentes — benigno (bool), mas `DegradationState` merece `volatile`/Interlocked se R1 paralelizar | `SearchService.cs` |
| P4 | Response compression JSON de API — documentado no roadmap, segue não implementado | `Program.cs` (sem `UseResponseCompression`) |
| P5 | STJ source-gen p/ contratos MCP — documentado no roadmap | `KnowledgeHub.Shared` |
| P6 | Sync-over-async pontual (R4) | vários arquivos |

### Sugestão de ordenação

**Sprint 1 (quick wins, 1 PR):** A1, A3, A5, A6, A9, D1, D6
**Sprint 2:** R1 (paralelizar braços) + P3; A2, A4, A7, A8; D3+D4 (wasm-tools + ICU)
**Sprint 3:** R2 (Chat via SSE streaming), D2 (backup Postgres), D5
**Roadmap:** R3 (bounded thread load), R6 (retrieval metrics no eval), P4/P5 (compression + source-gen), A10

---

## Apêndice — comandos de verificação

```bash
dotnet build KnowledgeHub.slnx
env -u Database__Provider dotnet test
dotnet format KnowledgeHub.slnx --verify-no-changes
dotnet publish src/KnowledgeHub.Client -c Release -o /tmp/kh-pub   # medir wwwroot/*.br
```
