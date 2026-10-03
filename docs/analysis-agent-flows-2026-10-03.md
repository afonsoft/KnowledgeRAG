# Análise: Agent Flows (UI-defined) para o KnowledgeHub

Data: 2026-10-03 · Contexto: comparativo README — linha "Agent loop" — incluir
capacidade de **fluxos de agente definidos pela UI** como AnythingLLM e Dify.

## 1. O que "Agent flows" significa nos dois produtos

### AnythingLLM — Agent Flows

Editor visual (React Flow) de **sequência linear** de blocos. Vocabulário real
(`frontend/src/pages/Admin/AgentBuilder/`):

| Bloco | Função |
|---|---|
| `flowInfo` | nome + descrição do fluxo — **dobra como tool schema**: a descrição é o que o LLM usa para decidir quando invocar o fluxo |
| `start` (variables) | declara variáveis de entrada/saída do fluxo |
| `apiCall` | HTTP arbitrário (method/headers/body) com interpolação `${var}`; resposta parseada e traversável (`resp.data.users[0].name`) |
| `llmInstruction` | chamada LLM com prompt interpolado |
| `webScraping` | captura de página |
| `finish` | retorno do fluxo |

Detalhe arquitetural-chave: **um flow publicado vira uma tool chamável pelo
agent** (`flow_<name>` no catálogo). O agente ReAct continua livre — o flow é o
braço *determinístico* que o modelo invoca quando precisa de uma pipeline
multi-step garantida.

### Dify — Workflows / Chatflows

**DAG completo**, não só linear. Nós: `start`, `llm`, `code` (Py/JS),
`http-request`, `if-else`, `question-classifier`, `iteration`, `loop`,
`knowledge-retrieval`, `agent`, `answer`/`end`. Características:

- Templating `{{var}}` + deep access `{{node.body.items[0].id}}`.
- Escopo de variáveis por branch/loop; variável de saída padronizada por branch.
- Estratégia de falha por nó (fail / retry / default-value / continue).
- Triggers: manual, API, webhook, schedule.
- `answer` node para streaming de saída parcial.

### Onde o KnowledgeHub está hoje

`AgentService` (`IAgentService`) implementa só o polo ReAct: model→tools→model
com HITL (`ToolApproval` + `ResumeAsync`), Chain AST para tool calls órfãs,
compactor de histórico, SSE `token`/`tool_*`/`awaiting_approval`/`done`.
Tudo que é "pipeline" hoje tem que ser escrito em código ou induzido via prompt.

**O gap exato**: nenhum caminho para o usuário compor uma sequência
determinística `search → llm → http → tool → output` e expô-la como tool/skill.

## 2. Design proposto para o KnowledgeHub

Princípio: **flows são orquestração determinística que se expõe como tool** —
o agent loop existente continua o polo livre; flows viram novos itens do
catálogo dinâmico, herdam escopo por API key e HITL.

### 2.1 Modelo de dados (EF Core, SQLite/Postgres)

- `AgentFlow`: `Id, Name, Slug, Description, Enabled, Version, DefinitionJson, CreatedAt, UpdatedAt` — definição JSON = `{start:{inputs:[{name,type,required,default}]}, steps:[...]}`.
- `FlowRun`: `Id, FlowId, FlowVersion, Status(running|done|failed|awaiting), InputsJson, StepResultsJson, Error, DurationMs, ApiKeyId, CreatedAt` — auditoria no mesmo padrão de `EvalRun`/`ApiKeyUsageEvent`.

### 2.2 Engine (`FlowEngine` em `src/KnowledgeHub.Server/Flows/`)

Executa um DAG pequeno em topological order; contexto de variáveis
`Dictionary<string, JsonNode>` (`vars` + `steps.<id>.output`); templating
`{{steps.a.output.total}}`/`{{vars.q}}` resolvido por caminho dot/index
(igual Dify; `${x}` do AnythingLLM também aceito por compat).

```csharp
public interface IFlowStepHandler {
    string Type { get; }                       // "llm", "tool", "http", ...
    Task<JsonNode?> ExecuteAsync(FlowStep step, FlowContext ctx, CancellationToken ct);
}
```

Handlers MVP:

| Tipo | Implementa | Reuso |
|---|---|---|
| `start`/`output` | declara inputs / materializa resposta | — |
| `tool` | chama qualquer tool do catálogo (`agent_chat` incl.) | dispatcher MCP existente — breadth de graça |
| `llm` | prompt template → completion | `ResilientChatClient`/`IChatClient` + per-key chat settings |
| `http` | HTTP call c/ auth (none/basic/bearer/custom) + result var | `IHttpClientFactory` + SSRF allowlist + `IIntegrationSecretStore` (`secret:flow:{id}:name` — nunca literal na definição) |
| `knowledge` | açúcar p/ `search_knowledge`/`ask_knowledge` | `ISearchService` |
| `condition` | if/elif/else sobre comparações (`eq/contains/gt/...`) | — |
| `foreach` | itera array, sub-steps por item (cap `Flow:MaxIterations`) | — |
| `transform` | monta JsonNode de template (juntar outputs) | — |

Governança: `Flow:MaxSteps` (64), `Flow:MaxIterations` (500), timeout por step
(120s default), output cap por step (256KB), `CancellationToken` end-to-end.

### 2.3 Superfícies

- **REST**: `GET/POST/PUT/DELETE /api/flows`, `POST /api/flows/{id}/run`
  (`{inputs}` → `FlowRunResult`), `?stream=1` SSE com eventos
  `flow_step_start`/`flow_step_end`/`done`, `GET /api/flows/{id}/runs` (audit),
  `POST /api/flows/validate` (dry-run estrutural).
- **MCP**: `list_flows`, `run_flow(flow, inputs)` + **cada flow habilitado vira
  `flow_<slug>` no catálogo** com `inputSchema` gerado do bloco `start` — o
  agente (`agent_chat`, A2A, SDKs) invoca flows como tools nativas. Escopo por
  key: flow só aparece se a key tem `sources`/`tools` compatíveis.
- **HITL**: steps `tool` que resolvem para write-capable tools suspendem o run
  com `FlowRun.Status=awaiting` + `ToolApproval` existente → `resume` reaproveita
  `POST /api/agent/resume` estendido p/ flows (F2 se complexo; MVP pode exigir
  que a key já tenha `allowWrite` e logar warning).
- **UI** (`/flows`): MVP = **editor de steps ordenados** (accordion por step,
  tipo+config por tipo, reorder, test-run inline com trace de steps) — não canvas.
  Canvas drag-and-drop (F2) precisa de lib JS via interop (drawflow/litegraph)
  — custo real de uma fase inteira.
- **A2A** (F3): `metadata {"skill":"flow_<slug>"}` reusa `KnowledgeHubA2AAgent`.

### 2.4 Segurança (o ponto que Dify/AnythingLLM tratam superficialmente)

- SSRF: `http` steps passam pelo mesmo guard do conector GitRepository
  (`allowPrivateHosts` opt-in por flow, default deny RFC1918/loopback).
- Secrets: referência por nome (`secret:flow:{id}:token`), nunca valor inline;
  resolução via `IIntegrationSecretStore`.
- Escopo por API key + auditoria de run (FlowRun) + rate limit herdado.
- Flow definitions validadas no save: refs de step existem, sem ciclos em MVP,
  inputs obrigatórios têm default ou são exigidos na chamada.

## 3. Escopo em fases

| Fase | Conteúdo | Tamanho |
|---|---|---|
| **F1** (MVP) | Entities+migration, engine linear+condition+foreach, handlers `tool`/`llm`/`http`/`knowledge`/`transform`/`output`, REST CRUD+run+runs, `flow_<slug>` + `list_flows`/`run_flow` no catálogo, SSE de run, UI editor por lista, FlowRun audit, testes | 1 sessão grande |
| F2 | Canvas visual (JS interop), HITL mid-flow (suspend/resume), retry/failure strategy por step, sub-flows (`flow` chama flow) | 1 sessão |
| F3 | Triggers (webhook entrante, schedule), A2A skills, versionamento/diff de definição, integração eval (flow como sujeito de baseline), import Dify DSL | 1 sessão |

## 4. Riscos e decisões

- **Canvas vs lista**: canvas é 60%+ do esforço total por causa de JS interop em
  Blazor; lista ordenada cobre 100% dos fluxos lineares e branching por
  condition. Recomendo lista no MVP.
- **DAG vs sequência**: condition+foreach dentro de sequência ordenada já cobre
  branching sem engine de grafo genérico. DAG livre só se F2 pedir.
- **HITL em flows**: suspender um run no meio de um step write é o mesmo
  mecanismo do agent loop, mas o estado a persistir é o FlowContext — incluído
  no design, MVP pode exigir key com write scope.
- **Segurança do http block** é o maior vetor (SSRF/exfil) — guard obrigatório
  no F1, não opcional.
- Nome da feature em português na UI: "Fluxos de agente" (manter "Agent flows"
  nos READMEs).

## 5. Impacto no comparativo (README)

Após F1, a linha "Agent loop" vira:

```
| Agent loop | Tool-calling + HITL + auto-resume + task tools + **agent flows (UI-defined)** | LangGraph (code) | Function calling | Agent flows (UI-defined) |
```

Fechando a única lacuna estrutural restante vs Dify/AnythingLLM.
