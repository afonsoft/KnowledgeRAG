# Análise — RAG / loop de agente, MCP, A2A e CI/CD (testes)

Data: 2026-10-03 · Base: `main` pós-merge #538
Escopo: pipeline RAG e loop de agente ("LangChain" — o repo não usa LangChain; o equivalente é o loop de agente sobre `IChatClient` + tool-calling), engine MCP, endpoints A2A e pipeline de testes/cobertura do GitHub Actions.

Método: leitura de código com evidência `arquivo:linha` + reprodução real de bugs no app rodando (Blazor WASM em `localhost:5009`, depuração via CDP). ` .github/workflows/` é protegido — os achados de CI ficam documentados aqui, não implementados.

---

## 1. Estado atual (o que já está maduro)

- **RAG**: retrieval híbrido FTS5+vetorial com RRF, braços paralelos (#535), MMR, multi-query/HyDE, rewrite history-aware, corrective loop (grading→retry→abstenção), contextual enrichment, graph arm (`useGraph`), window retrieval + autocut, relaxação de filtros.
- **Agente**: loop ReAct com tool-calling, Chain AST + repair de tool calls órfãos, compactor de histórico, sumarização com cap (`Agent:SummarizationMaxMessages`), HITL com auto-resume (#537), SSE token-a-token no chat (#536).
- **MCP**: transporte híbrido Stateful/Stateless por protocolo do client, task-eligible tools 9 (#538), elicitation/MRTR, structuredContent, proxies upstream com cache lock-free (#533).
- **A2A v1.0**: Agent Card anônimo, JSON-RPC + HTTP+JSON, streaming, push notifications, task store EF com purge (#538), 4 skills com escopo da `aft_*`.
- **Eval**: recall/precision/MRR + hit_rate/nDCG (#538), gates, baselines nomeados, latências p50/p95/p99, faithfulness.

## 2. Pontos de melhoria — RAG e loop de agente

| # | Achado | Evidência | Sugestão |
|---|--------|-----------|----------|
| R1 | Sem reranker cross-encoder — precisão depende só de RRF+MMR | `SearchService.cs` | Opt-in `Rerank:` via ONNX (mesmo runtime do MiniLM já presente): reranquear top-N (ex.: 20→k) com bge-reranker-mini. Ganho típico em precision@k; custo só quando ligado |
| R2 | Tríade RAG (relevance/groundedness) medida mas **não gateia resposta** | `RagEvaluations`, `/rag-quality` | Modo opt-in `RagEval:EnforceGroundedness` — abster/flaggar resposta quando groundedness < limiar, reaproveitando o caminho de abstenção do corrective loop |
| R3 | Citations não são verificadas pós-síntese | `AskService`/`AgentService` | Verificação barata: overlap léxico entre claims citadas e chunk citado; marcar `citationVerified` na resposta |
| R4 | Budget de tokens do agente sem telemetria por step | `AgentService` | Log/span por iteração: tokens in/out, tool, duração — alimenta o dashboard e a triade |
| R5 | `agent_chat` expõe pouco contexto de retrieval na resposta | `AgentResponse` | Retornar `sourcesUsed`/`appliedFilters` no step para clients (o A2A card já delega as mesmas skills) |

## 3. MCP

| # | Achado | Sugestão |
|---|--------|----------|
| M1 | Sem `notifications/progress` nas tools longas (firecrawl_agent, sync) | Emitir progress tokens quando o client declarar `progressToken` no request |
| M2 | Anotações de tools (`readOnlyHint`/`destructiveHint`/`openWorldHint`) só parciais | Auditoria do catálogo — clients usam hints pra auto-permissionamento; `delete_*`/write tools sem `destructiveHint` são os piores casos |
| M3 | Resultados de tasks grandes sem cap/TTL próprio | `Mcp:TaskResultMaxBytes` + expiração por TTL no `EfMcpTaskStore` (purge por idade já existe) |
| M4 | `resources/` e `prompts/` do MCP não expostos (só tools) | Útil: expor fontes como `resource` (subscription-less read) — baixo esforço, alto valor p/ agents que navegam por resources |

## 4. A2A

| # | Achado | Sugestão |
|---|--------|----------|
| A1 | Skills fixas (4) — write tools não expostas | Expor `write_note`/`write_knowledge` condicionado a scope `write` da chave (o catalog já centraliza a lista — `A2aSkillCatalog.cs`) |
| A2 | Sem `tasks/resubscribe` após desconexão SSE | Gap pequeno do v1.0 — considerar se clients pedirem retomada de stream |
| A3 | Agent Card sem `skills[].examples`/tags ricas | Melhora discoverability no registro público do card |

## 5. CI/CD e cobertura de testes (`.github/workflows` — protegido, documentar)

Pipeline atual: `ci-build-test.yml` (build, unit+gate de coverage, integração SQLite, validação WASM, docker build, ratchet de baseline), `code-quality.yml` (Qodana + SonarQube), `security-scan.yml` (CodeQL), `release.yml` (tag → GHCR + Docker Hub).

**Gaps reais, ordenados por valor:**

1. **Sem teste de componente Blazor (bUnit).** O bug desta sessão — `PlaceHolder="[{""metric""...}]"` produzindo atributo inválido e exceção unhandled no WASM — seria capturado por um smoke test bUnit que renderiza `Eval.razor`. Recomendo `bunit` + um teste por página que renderiza sem exception com services mockados (`tests/KnowledgeHub.Tests.Unit` já tem o harness). **E2E Playwright** de smoke (login → /sources → /eval) capturaria a classe inteira de bugs de serialização WASM (`NoMetadataForType` — ver seção 6).
2. **Integração só roda SQLite.** `pgvector`/Npgsql, `ExecuteUpdateAsync` específico de provider e a stack Postgres não têm cobertura. Um job com service container `postgres:16-alpine` + `pgvector/pgvector:pg16` rodando os mesmos testes com `Database__Provider=PostgreSQL` dobra o valor da suíte por ~30 linhas de YAML.
3. **Coverage só de unit tests.** O gate mede só `Tests.Unit`; relatório combinado (unit+integração) via `reportgenerator` daria o número real e habilitaria comentário de diff de cobertura no PR (hoje só artefato + gate numérico).
4. **Dois builds redundantes.** Jobs `unit`/`integration` rebuildam o que o job `build` já fez — `--no-build` com artefato ou `dotnet test` único economiza ~2min/PR.
5. **O ratchet de coverage abre issue mas não falha quando `GITHUB_TOKEN` não pode escrever a variável.** OK como está (avisa), só documentar que o fluxo é manual.
6. **Sem quarentena de flaky tests.** Sem sinal de flakiness recente — marcar como observação, não ação.

## 6. Bugs encontrados e corrigidos nesta sessão (registro)

Durante a captura dos prints o app WASM estava quebrando em múltiplas telas. Root cause: `JsonSerializerIsReflectionEnabledByDefault=false` (default do SDK WASM) exige **toda** serialização via `SharedJson.Options` com tipo registrado em `SharedJsonContext` — tipos anônimos, DTOs locais do client e coleções novas (`List<T>` precisa de registro próprio) lançam `NoMetadataForType` até em Debug. Corrigido:

- Todas as serializações do client migradas para contratos tipados em `Shared/Contracts/` (Eval, Settings log-level, Sources ingestion jobs, RagQuality stats, Threads, Approvals, A2A via `JsonObject`, Streaming).
- Payloads do SignalR hub passam por `.AddJsonProtocol(o => o.PayloadSerializerOptions = SharedJson.Options)`.
- **Bug de Razor**: `PlaceHolder="[{""metric"":""recall_at_k""...}]"` em `Eval.razor` — `""` dentro de atributo não é escape válido; o restante virava **nome de atributo** → `InvalidCharacterError` → banner de erro permanente. Agora com aspas simples.
- Bug pré-existente revelado pelo source-gen: propriedades `init` com default (`IsActive = true`) desserializavam como `false` quando ausentes no JSON — corrigido no #537 migrando para `set`.

**Regra durável**: toda (de)serialização JSON no client WASM precisa de tipo registrado em `SharedJsonContext` — sem anônimos, sem DTOs locais ao client, coleções registram `List<T>`/`IReadOnlyList<T>` explicitamente.

## 7. Recomendação de execução

- **Imediato (este PR)**: README com screenshots, doc de análise, fixes de serialização WASM + razor.
- **Próximo PR de testes**: bUnit smoke por página + cobertura combinada no CI (precisa de acesso ao workflow — pedir ao owner).
- **Roadmap RAG**: R1 (reranker ONNX opt-in) e R2 (groundedness gate) são os de maior razão valor/esforço.
- **Roadmap MCP**: M2 (auditoria de annotations) é meio dia; M4 (resources) é o diferencial para agents resource-first.
