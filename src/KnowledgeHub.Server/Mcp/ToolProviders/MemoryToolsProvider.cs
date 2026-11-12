using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Memory;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-002/RF-003/RF-004: the agent-memory
/// MCP surface — retain/recall plus unit & bank CRUD, all scoped to banks
/// owned by the calling API key.
/// </summary>
public sealed class MemoryToolsProvider : IToolProvider
{
    private const string ArgBankKey = "bankKey";
    private const string ArgConfirm = "confirm";
    private const string ArgContent = "content";

    private static readonly JsonObject RetainSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"],"description":"Target bank (default 'default'); auto-created on first use"},
          "content":{"type":"string","description":"Text to remember — observations, decisions, facts, transcripts"},
          "context":{"type":["string","null"],"description":"Why this is being remembered (helps extraction)"},
          "timestamp":{"type":["string","null"],"description":"ISO-8601 moment the content happened (default: now)"},
          "tags":{"type":["array","null"],"items":{"type":"string"},"description":"Free-form tags"},
          "metadata":{"type":["object","null"],"description":"Flat string key/value payload"},
          "mode":{"type":["string","null"],"enum":["sync","async",null],"description":"sync returns unit ids; async returns operationId (poll operations_get)"},
          "strategy":{"type":["string","null"],"enum":["extract","verbatim",null],"description":"extract = LLM fact extraction (default); verbatim = store content as-is, no LLM"},
          "documentId":{"type":["string","null"],"description":"Logical document id — enables delta-retain (unchanged chunks keep their ids)"},
          "documentTitle":{"type":["string","null"],"description":"Title for the logical document"},
          "sourcePrompt":{"type":["string","null"],"description":"Originating prompt/turn — defense-screened, capped at 2000 chars, deduped per (bank, sessionId)"},
          "sessionId":{"type":["string","null"],"description":"Caller session/thread id — map your agent_chat threadId here"},
          "origin":{"type":["string","null"],"enum":["explicit","passive","import","system",null],"description":"Who produced the content (default explicit)"}
        },"required":["content"],
        "examples":[{"content":"Deploy da API falhou por falta de memória no nó 3","strategy":"verbatim"},
                    {"bankKey":"projeto-x","content":"O time decidiu migrar para Postgres em março","mode":"async"},
                    {"content":"Usuário prefere respostas em pt-BR","sourcePrompt":"prefiro respostas curtas em pt-BR","sessionId":"thread-42"}]}
        """)!.AsObject();

    private static readonly JsonObject RecallSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"],"description":"Bank to search (default 'default')"},
          "query":{"type":"string","description":"Natural-language recall query"},
          "topK":{"type":"integer","description":"Max results (default 8, cap 100)"},
          "minScore":{"type":["number","null"],"description":"Drop results with normalized score below this (0..1)"},
          "window":{"type":["string","null"],"description":"Temporal window — whitelist: 1h, 6h, 24h, 7d — enables the temporal arm"},
          "budget":{"type":["integer","null"],"description":"Token budget for returned texts"},
          "factType":{"type":["string","null"],"enum":["world","experience","observation",null],"description":"Restrict to one fact type"},
          "sessionId":{"type":["string","null"],"description":"Restrict to units retained under this session id"}
        },"required":["query"],
        "examples":[{"query":"quando foi o deploy que falhou?","window":"7d"},
                    {"query":"decisões sobre Postgres","topK":5,"factType":"experience"}]}
        """)!.AsObject();

    private static readonly JsonObject ListSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "factType":{"type":["string","null"],"enum":["world","experience","observation",null]},
          "tag":{"type":["string","null"]},
          "sessionId":{"type":["string","null"],"description":"Restrict to units retained under this session id"},
          "limit":{"type":"integer","description":"Default 20, cap 200"},
          "offset":{"type":"integer"}
        },
        "examples":[{"bankKey":"default","factType":"world","limit":10}]}
        """)!.AsObject();

    private static readonly JsonObject IdSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string","description":"Memory unit id (GUID)"}
        },"required":["id"],
        "examples":[{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}]}
        """)!.AsObject();

    private static readonly JsonObject UpdateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string","description":"Memory unit id (GUID)"},
          "text":{"type":["string","null"],"description":"New fact text (rehashes + re-embeds)"},
          "context":{"type":["string","null"]},
          "tags":{"type":["array","null"],"items":{"type":"string"}},
          "metadata":{"type":["object","null"]}
        },"required":["id"],
        "examples":[{"id":"3fa85f64-…","tags":["decisão","postgres"]}]}
        """)!.AsObject();

    private static readonly JsonObject DeleteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string","description":"Memory unit id (GUID)"},
          "confirm":{"type":"boolean","description":"Must be true"},
          "hard":{"type":["boolean","null"],"description":"true = permanent delete; default soft-delete"}
        },"required":["id","confirm"],
        "examples":[{"id":"3fa85f64-…","confirm":true}]}
        """)!.AsObject();

    private static readonly JsonObject BankCreateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":"string","description":"Slug (unique per API key)"},
          "name":{"type":["string","null"],"description":"Display name (default = bankKey)"},
          "mission":{"type":["string","null"],"description":"Bank purpose — guides future reflect"},
          "disposition":{"type":["object","null"],"description":"{\"skepticism\":1-5,\"literalism\":1-5,\"empathy\":1-5}"},
          "config":{"type":["object","null"],"description":"Per-bank config overrides (maxUnitsPerRetain, defaultStrategy, …)"}
        },"required":["bankKey"],
        "examples":[{"bankKey":"projeto-x","name":"Projeto X","mission":"memória do agente do projeto X"}]}
        """)!.AsObject();

    private static readonly JsonObject BankUpdateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":"string"},
          "name":{"type":["string","null"]},
          "mission":{"type":["string","null"]},
          "disposition":{"type":["object","null"]},
          "config":{"type":["object","null"]}
        },"required":["bankKey"],
        "examples":[{"bankKey":"projeto-x","mission":"memória do agente + decisões arquiteturais"}]}
        """)!.AsObject();

    private static readonly JsonObject ConsolidateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "wait":{"type":["boolean","null"],"description":"true (default) runs inline and returns the summary; false queues a consolidate operation (poll operations_get)"}
        },
        "examples":[{"bankKey":"projeto-x"},{"bankKey":"default","wait":false}]}
        """)!.AsObject();

    private static readonly JsonObject ObsHistorySchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "observationId":{"type":"string","description":"Observation unit id (GUID)"}
        },"required":["observationId"],
        "examples":[{"observationId":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}]}
        """)!.AsObject();

    private static readonly JsonObject ContextSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"],"description":"Bank to summarize (default 'default')"},
          "topK":{"type":"integer","description":"Items per section (default 10, cap 50)"},
          "since":{"type":["string","null"],"description":"ISO-8601 — only items at/after this moment count as 'recent'"},
          "synthesize":{"type":["boolean","null"],"description":"true asks the chat provider for a prose summary (opt-in LLM; ignored when unconfigured)"}
        },
        "examples":[{},{"topK":5,"since":"2026-11-01T00:00:00Z"},{"synthesize":true}]}
        """)!.AsObject();

    private static readonly JsonObject TimelineSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "from":{"type":["string","null"],"description":"ISO-8601 lower bound (inclusive) on occurred/mentioned time"},
          "to":{"type":["string","null"],"description":"ISO-8601 upper bound (inclusive)"},
          "factType":{"type":["string","null"],"enum":["world","experience","observation",null]},
          "groupBy":{"type":["string","null"],"enum":["day","hour","week",null],"description":"Bucket granularity (default day)"},
          "limit":{"type":"integer","description":"Max units scanned (default 50, cap 200)"}
        },
        "examples":[{"groupBy":"day"},{"factType":"observation","from":"2026-11-01T00:00:00Z","groupBy":"week"}]}
        """)!.AsObject();

    private static readonly JsonObject StatsSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"],"description":"Bank to measure — omitted aggregates every bank in the caller scope"}
        },
        "examples":[{"bankKey":"default"},{}]}
        """)!.AsObject();

    private static readonly JsonObject ReflectSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "query":{"type":"string","description":"Question to reflect over the bank's memory"},
          "context":{"type":["string","null"],"description":"Extra context injected into the reflect prompt"},
          "budget":{"type":["string","null"],"enum":["low","mid","high",null],"description":"Reflect depth — iterations: low=3, mid=6, high=Memory:Reflect:MaxIterations (default 10)"},
          "maxTokens":{"type":["integer","null"],"description":"Max output tokens per model call"},
          "responseSchema":{"type":["object","null"],"description":"JSON Schema — triggers a structured-output extraction pass"},
          "tags":{"type":["array","null"],"items":{"type":"string"},"description":"Scope directives by tag"},
          "tagsMatch":{"type":["string","null"],"enum":["any","all",null]},
          "applyAllDirectives":{"type":["boolean","null"],"description":"Ignore tag scoping — apply every active directive"},
          "includeBasedOn":{"type":["boolean","null"],"description":"Include validated citations (default true)"},
          "includeTrace":{"type":["boolean","null"],"description":"Include the tool-call trace + dropped citations"}
        },"required":["query"],
        "examples":[{"query":"o que sabemos sobre a falha de deploy?"},
                    {"query":"resuma decisões de arquitetura","budget":"high","includeTrace":true}]}
        """)!.AsObject();

    private static readonly JsonObject DirectivesListSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "includeInactive":{"type":["boolean","null"]}
        },"examples":[{}]}
        """)!.AsObject();

    private static readonly JsonObject DirectiveCreateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "name":{"type":["string","null"],"description":"Short label"},
          "content":{"type":"string","description":"The standing instruction (max 2000 chars)"},
          "priority":{"type":["integer","null"],"description":"Higher applies first (default 0)"},
          "isActive":{"type":["boolean","null"]},
          "tags":{"type":["array","null"],"items":{"type":"string"},"description":"Scope tags — empty = bank-global"}
        },"required":["content"],
        "examples":[{"content":"Sempre responda em pt-BR","priority":10},
                    {"content":"Nunca exponha dados de billing","tags":["public"]}]}
        """)!.AsObject();

    private static readonly JsonObject DirectiveUpdateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string"},
          "isActive":{"type":["boolean","null"]},
          "priority":{"type":["integer","null"]},
          "content":{"type":["string","null"]},
          "tags":{"type":["array","null"],"items":{"type":"string"}}
        },"required":["id"],
        "examples":[{"id":"3fa85f64-…","isActive":false}]}
        """)!.AsObject();

    private static readonly JsonObject DirectiveDeleteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string"},
          "confirm":{"type":"boolean","description":"Must be true"}
        },"required":["id","confirm"],
        "examples":[{"id":"3fa85f64-…","confirm":true}]}
        """)!.AsObject();

    private static readonly JsonObject MentalModelListSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "subtype":{"type":["string","null"],"enum":["mental_model","page",null]}
        },"examples":[{}]}
        """)!.AsObject();

    private static readonly JsonObject MentalModelGetSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":["string","null"]},
          "path":{"type":["string","null"],"description":"'folder/name' or 'name'"}
        },"examples":[{"id":"3fa85f64-…"},{"path":"projeto/convencoes/api"}]}
        """)!.AsObject();

    private static readonly JsonObject MentalModelCreateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "subtype":{"type":["string","null"],"enum":["mental_model","page",null]},
          "name":{"type":"string"},
          "path":{"type":["string","null"],"description":"folder path e.g. 'projeto/convencoes'"},
          "sourceQuery":{"type":"string","description":"the standing question this model answers"},
          "trigger":{"type":["object","null"],"description":"refresh trigger: {mode,refreshAfterConsolidation,refreshIntervalMinutes,fullRefreshIntervalMinutes,minRefreshIntervalSeconds,factTypes,tagsMatch,tags}"},
          "tags":{"type":["array","null"],"items":{"type":"string"}},
          "wait":{"type":["boolean","null"],"description":"run the initial refresh synchronously (default false — signals the worker)"}
        },"required":["name","sourceQuery"],
        "examples":[{"name":"Convenções do projeto","path":"projeto","sourceQuery":"Quais as convenções de código do projeto?","subtype":"page","wait":true}]}
        """)!.AsObject();

    private static readonly JsonObject MentalModelUpdateSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string"},
          "name":{"type":["string","null"]},
          "path":{"type":["string","null"]},
          "sourceQuery":{"type":["string","null"]},
          "trigger":{"type":["object","null"]},
          "tags":{"type":["array","null"],"items":{"type":"string"}},
          "content":{"type":["string","null"],"description":"manual edit — recorded as manual_edit in history"}
        },"required":["id"],
        "examples":[{"id":"3fa85f64-…","path":"projeto/api"}]}
        """)!.AsObject();

    private static readonly JsonObject MentalModelDeleteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string"},
          "confirm":{"type":"boolean","description":"Must be true"}
        },"required":["id","confirm"],
        "examples":[{"id":"3fa85f64-…","confirm":true}]}
        """)!.AsObject();

    private static readonly JsonObject MentalModelRefreshSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "id":{"type":"string"},
          "wait":{"type":["boolean","null"],"description":"true = run now synchronously (ignores min-interval); false = signal the worker"}
        },"required":["id"],
        "examples":[{"id":"3fa85f64-…","wait":true}]}
        """)!.AsObject();

    private static readonly JsonObject PageSearchSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":["string","null"]},
          "query":{"type":"string"},
          "limit":{"type":["integer","null"]}
        },"required":["query"],
        "examples":[{"query":"convenções de código","limit":5}]}
        """)!.AsObject();

    private static readonly JsonObject BankDeleteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "bankKey":{"type":"string"},
          "confirm":{"type":"boolean","description":"Must be true — deletes the bank and ALL its memories"}
        },"required":["bankKey","confirm"],
        "examples":[{"bankKey":"projeto-x","confirm":true}]}
        """)!.AsObject();

    public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "memory_retain",
                Title = "Retain memory",
                Description = "Store facts into an agent memory bank. strategy=extract (default) pulls typed facts/entities/dates via LLM; strategy=verbatim stores as-is. mode=async returns an operationId for long contents. Repeat calls with the same documentId delta-update: unchanged chunks keep their unit ids.",
                InputSchema = RetainSchema,
                Handler = RetainAsync
            },
            new CatalogTool
            {
                Name = "memory_recall",
                Title = "Recall memories",
                Description = "Search a memory bank across four arms — semantic, lexical, entity-graph and temporal (temporal only when window is set) — fused with reciprocal rank. Returns scored facts with entities and dates. Call BEFORE starting tasks to recover context.",
                InputSchema = RecallSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = RecallAsync
            },
            new CatalogTool
            {
                Name = "memory_list",
                Title = "List memories",
                Description = "List memory units in a bank (newest first), filterable by fact type and tag.",
                InputSchema = ListSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = ListAsync
            },
            new CatalogTool
            {
                Name = "memory_get",
                Title = "Get memory",
                Description = "Fetch one memory unit by id.",
                InputSchema = IdSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = GetAsync
            },
            new CatalogTool
            {
                Name = "memory_update",
                Title = "Update memory",
                Description = "Patch a memory unit's text/context/tags/metadata. Text changes rehash and re-embed the unit.",
                InputSchema = UpdateSchema,
                IdempotentHint = true,
                Handler = UpdateAsync
            },
            new CatalogTool
            {
                Name = "memory_delete",
                Title = "Delete memory",
                Description = "Delete a memory unit (soft-delete default; hard=true removes permanently). Requires confirm=true.",
                InputSchema = DeleteSchema,
                DestructiveHint = true,
                Handler = DeleteAsync
            },
            new CatalogTool
            {
                Name = "memory_bank_list",
                Title = "List memory banks",
                Description = "List the caller's memory banks with unit counts.",
                InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"examples":[{}]}""")!.AsObject(),
                ReadOnly = true,
                IdempotentHint = true,
                Handler = BankListAsync
            },
            new CatalogTool
            {
                Name = "memory_bank_create",
                Title = "Create memory bank",
                Description = "Create an isolated memory bank (one per user/agent/project). bankKey is unique per API key.",
                InputSchema = BankCreateSchema,
                Handler = BankCreateAsync
            },
            new CatalogTool
            {
                Name = "memory_bank_update",
                Title = "Update memory bank",
                Description = "Update a bank's name/mission/disposition/config.",
                InputSchema = BankUpdateSchema,
                IdempotentHint = true,
                Handler = BankUpdateAsync
            },
            new CatalogTool
            {
                Name = "memory_reflect",
                Title = "Reflect over memory",
                Description = "Agentic, read-only reasoning over a bank — the model gathers evidence from observations first, then raw facts, and answers with validated citations (based_on). Bank mission/disposition/directives shape the response. Use for synthesized answers, not raw lookups.",
                InputSchema = ReflectSchema,
                ReadOnly = true,
                Handler = ReflectAsync
            },
            new CatalogTool
            {
                Name = "memory_directives_list",
                Title = "List directives",
                Description = "List a bank's standing instructions (priority-ordered; empty tags = bank-global).",
                InputSchema = DirectivesListSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = DirectivesListAsync
            },
            new CatalogTool
            {
                Name = "memory_directives_create",
                Title = "Create directive",
                Description = "Add a standing instruction to a bank (e.g. 'always answer in pt-BR'). Injected into every reflect prompt.",
                InputSchema = DirectiveCreateSchema,
                Handler = DirectiveCreateAsync
            },
            new CatalogTool
            {
                Name = "memory_directives_update",
                Title = "Update directive",
                Description = "Patch a directive's active flag, priority, content or tags.",
                InputSchema = DirectiveUpdateSchema,
                IdempotentHint = true,
                Handler = DirectiveUpdateAsync
            },
            new CatalogTool
            {
                Name = "memory_directives_delete",
                Title = "Delete directive",
                Description = "Remove a directive. Requires confirm=true.",
                InputSchema = DirectiveDeleteSchema,
                DestructiveHint = true,
                Handler = DirectiveDeleteAsync
            },
            // SPEC-20261111-mental-models-pages RF-004.
            new CatalogTool
            {
                Name = "memory_mental_models_list",
                Title = "List mental models",
                Description = "List a bank's mental models and pages (standing synthesized answers refreshed in background). Includes staleness flag.",
                InputSchema = MentalModelListSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = MentalModelsListAsync
            },
            new CatalogTool
            {
                Name = "memory_mental_models_get",
                Title = "Get mental model",
                Description = "Get one mental model/page by id or path ('folder/name'). Cheap DB read — no LLM.",
                InputSchema = MentalModelGetSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = MentalModelsGetAsync
            },
            new CatalogTool
            {
                Name = "memory_mental_models_create",
                Title = "Create mental model",
                Description = "Create a standing question whose answer is synthesized and refreshed in background. subtype 'page' gets wiki defaults (observations-only, delta refresh, optional file projection). wait=true runs the initial refresh now.",
                InputSchema = MentalModelCreateSchema,
                Handler = MentalModelsCreateAsync
            },
            new CatalogTool
            {
                Name = "memory_mental_models_update",
                Title = "Update mental model",
                Description = "Rename/move, change sourceQuery/trigger/tags, or manually edit content (recorded in history).",
                InputSchema = MentalModelUpdateSchema,
                IdempotentHint = true,
                Handler = MentalModelsUpdateAsync
            },
            new CatalogTool
            {
                Name = "memory_mental_models_delete",
                Title = "Delete mental model",
                Description = "Delete a mental model/page and its history. Requires confirm=true.",
                InputSchema = MentalModelDeleteSchema,
                DestructiveHint = true,
                Handler = MentalModelsDeleteAsync
            },
            new CatalogTool
            {
                Name = "memory_mental_model_refresh",
                Title = "Refresh mental model",
                Description = "Trigger a refresh: re-synthesizes the answer over in-scope units (delta = new memories only, full = everything). wait=true runs synchronously.",
                InputSchema = MentalModelRefreshSchema,
                IdempotentHint = true,
                Handler = MentalModelsRefreshAsync
            },
            new CatalogTool
            {
                Name = "memory_pages_tree",
                Title = "Pages tree",
                Description = "Folder tree of the bank's pages — the recommended session-start read (cheap, no LLM).",
                InputSchema = MentalModelListSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = PagesTreeAsync
            },
            new CatalogTool
            {
                Name = "memory_pages_search",
                Title = "Search pages",
                Description = "Token-overlap search over page names and content; returns hits with snippets.",
                InputSchema = PageSearchSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = PagesSearchAsync
            },
            new CatalogTool
            {
                Name = "memory_page_get",
                Title = "Get page",
                Description = "Read a page's current content by id or path — pure DB read (~0 cost), the recommended session-start call.",
                InputSchema = MentalModelGetSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = PageGetAsync
            },
            new CatalogTool
            {
                Name = "memory_page_create",
                Title = "Create page",
                Description = "Create a wiki page (mental model with page defaults: observations-only scope, delta refresh, optional file projection).",
                InputSchema = MentalModelCreateSchema,
                Handler = PageCreateAsync
            },
            new CatalogTool
            {
                Name = "memory_page_update",
                Title = "Update page",
                Description = "Rename/move a page or manually edit its content (marked manual_edit in history).",
                InputSchema = MentalModelUpdateSchema,
                IdempotentHint = true,
                Handler = PageUpdateAsync
            },
            new CatalogTool
            {
                Name = "memory_page_delete",
                Title = "Delete page",
                Description = "Delete a page (and its projected file). Requires confirm=true.",
                InputSchema = MentalModelDeleteSchema,
                DestructiveHint = true,
                Handler = PageDeleteAsync
            },
            new CatalogTool
            {
                Name = "memory_consolidate",
                Title = "Consolidate memories",
                Description = "Run observation consolidation on a bank: unconsolidated facts are merged into durable observations (evidence-backed, refine-not-overwrite) by an LLM pass. wait=false queues the run as an operation.",
                InputSchema = ConsolidateSchema,
                IdempotentHint = true,
                Handler = ConsolidateAsync
            },
            new CatalogTool
            {
                Name = "memory_observation_history",
                Title = "Observation history",
                Description = "Audit trail of an observation — every create/update version with its added source ids and proof count.",
                InputSchema = ObsHistorySchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = ObsHistoryAsync
            },
            new CatalogTool
            {
                Name = "memory_context",
                Title = "Session-start digest",
                Description = "Deterministic digest to open a session with: top directives, recent facts, top observations, stale models/pages, recent operation failures and mini-stats — no LLM cost. Call this first when starting work on a bank.",
                InputSchema = ContextSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = ContextAsync
            },
            new CatalogTool
            {
                Name = "memory_timeline",
                Title = "Memory timeline",
                Description = "Chronological view of a bank's units grouped in day/hour/week buckets, ordered by when the fact happened (OccurredStart) falling back to when it was retained.",
                InputSchema = TimelineSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = TimelineAsync
            },
            new CatalogTool
            {
                Name = "memory_stats",
                Title = "Memory stats",
                Description = "Per-bank health metrics — units by fact type, embedding coverage, links, observations proof counts, operation status, consolidation backlog. Use it to decide when to consolidate or audit a bank.",
                InputSchema = StatsSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = StatsAsync
            },
            new CatalogTool
            {
                Name = "memory_bank_delete",
                Title = "Delete memory bank",
                Description = "Delete a bank and ALL its memories. Requires confirm=true.",
                InputSchema = BankDeleteSchema,
                DestructiveHint = true,
                Handler = BankDeleteAsync
            }
        ];
        foreach (var t in tools)
            SchemaExamples.Ensure(t.InputSchema);
        return Task.FromResult(tools);
    }

    // ---------- retain / recall ----------

    private static async ValueTask<CallToolResult> RetainAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var content = ToolArgs.RequiredString(ctx, ArgContent);
        var mode = ToolArgs.OptionalString(ctx, "mode") ?? "sync";
        var req = new MemoryRetainRequest(
            content,
            ToolArgs.OptionalString(ctx, "context"),
            ParseDate(ToolArgs.OptionalString(ctx, "timestamp")),
            OptionalStringList(ctx, "tags"),
            OptionalStringMap(ctx, "metadata"),
            ToolArgs.OptionalString(ctx, "strategy") ?? "extract",
            ToolArgs.OptionalString(ctx, "documentId"),
            ToolArgs.OptionalString(ctx, "documentTitle"),
            ToolArgs.OptionalString(ctx, "sourcePrompt"),
            ToolArgs.OptionalString(ctx, "sessionId"),
            ToolArgs.OptionalString(ctx, "origin"));
        var bankKey = MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey));
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();

        if (mode == "async")
        {
            var queue = ctx.Services.GetRequiredService<IMemoryRetainQueue>();
            var bank = await banks.ResolveAsync(bankKey, ct);
            var scope = await banks.ScopeKeyAsync(ct);
            var op = await queue.EnqueueAsync(bank, scope, req, ct);
            return await ToolResults.Structured("retain queued", new
            { bankId = bank.Id, operationId = op.Id, status = op.Status, async = true });
        }

        var retain = ctx.Services.GetRequiredService<MemoryRetainService>();
        try
        {
            var result = await retain.RetainAsync(bankKey, req, ct);
            return await ToolResults.Structured(
                $"retained {result.UnitsCreated} unit(s), {result.UnitsPreserved} preserved", result);
        }
        catch (MemoryDefenseException ex)
        {
            return await ToolResults.Error(JsonSerializer.Serialize(new
            {
                blocked = true,
                findings = ex.Findings.Select(f => new { pattern = f.Pattern, count = f.Count })
            }));
        }
        catch (MemoryServiceUnavailableException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> RecallAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var req = new MemoryRecallRequest(
            ToolArgs.RequiredString(ctx, "query"),
            ToolArgs.OptionalInt(ctx, "topK", 8, 100),
            ToolArgs.OptionalDouble(ctx, "minScore"),
            ToolArgs.OptionalString(ctx, "window"),
            ToolArgs.OptionalIntOrNull(ctx, "budget"),
            ToolArgs.OptionalString(ctx, "factType"),
            ToolArgs.OptionalString(ctx, "sessionId"));
        var recall = ctx.Services.GetRequiredService<MemoryRecallService>();
        var result = await recall.RecallAsync(MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)), req, ct);
        return await ToolResults.Structured($"{result.Results.Count} memor(ies)", result);
    }

    // ---------- unit CRUD ----------

    private static async ValueTask<CallToolResult> ListAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryUnitService>();
        var items = await svc.ListAsync(
            MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
            ToolArgs.OptionalString(ctx, "factType"),
            ToolArgs.OptionalString(ctx, "tag"),
            ToolArgs.OptionalInt(ctx, "limit", 20, 200),
            ToolArgs.OptionalInt(ctx, "offset", 0, 1_000_000),
            ToolArgs.OptionalString(ctx, "sessionId"), ct);
        return await ToolResults.Structured($"{items.Count} unit(s)", new { count = items.Count, units = items });
    }

    private static async ValueTask<CallToolResult> GetAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryUnitService>();
        try
        {
            var unit = await svc.GetAsync(MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")), ct);
            return await ToolResults.Structured("memory unit", unit);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> UpdateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryUnitService>();
        var embeddings = ctx.Services.GetRequiredService<IEmbeddingProvider>();
        try
        {
            var unit = await svc.UpdateAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")),
                new MemoryUnitService.MemoryUnitPatch(
                    ToolArgs.OptionalString(ctx, "text"),
                    ToolArgs.OptionalString(ctx, "context"),
                    OptionalStringList(ctx, "tags"),
                    OptionalStringMap(ctx, "metadata")),
                embeddings, ct);
            return await ToolResults.Structured("memory updated", unit);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> DeleteAsync(ToolCallContext ctx, CancellationToken ct)
    {
        if (ToolArgs.OptionalBool(ctx, ArgConfirm) != true)
            return await ToolResults.Error("memory_delete requires confirm:true");
        var svc = ctx.Services.GetRequiredService<MemoryUnitService>();
        try
        {
            await svc.DeleteAsync(MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")),
                ToolArgs.OptionalBool(ctx, "hard") == true, ct);
            return await ToolResults.Structured("memory deleted", new { deleted = true });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    // ---------- reflect + directives (SPEC-20261111-memory-reflect-directives) ----------

    private static async ValueTask<CallToolResult> ReflectAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var req = new MemoryReflectRequest(
            ToolArgs.RequiredString(ctx, "query"),
            ToolArgs.OptionalString(ctx, "context"),
            ToolArgs.OptionalString(ctx, "budget") ?? "mid",
            ToolArgs.OptionalIntOrNull(ctx, "maxTokens"),
            ctx.Arguments?.TryGetValue("responseSchema", out var rs) == true
                && rs is { ValueKind: JsonValueKind.Object } rsel ? rsel : null,
            OptionalStringList(ctx, "tags"),
            ToolArgs.OptionalString(ctx, "tagsMatch") ?? "any",
            ToolArgs.OptionalBool(ctx, "applyAllDirectives") == true,
            ToolArgs.OptionalBool(ctx, "includeBasedOn") != false,
            ToolArgs.OptionalBool(ctx, "includeTrace") == true);
        var svc = ctx.Services.GetRequiredService<MemoryReflectService>();
        try
        {
            var result = await svc.ReflectAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)), req, ct);
            return await ToolResults.Structured("reflect answer", result);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (MemoryServiceUnavailableException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> DirectivesListAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryDirectiveService>();
        try
        {
            var items = await svc.ListAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ToolArgs.OptionalBool(ctx, "includeInactive") == true, ct);
            return await ToolResults.Structured($"{items.Count} directive(s)",
                new { count = items.Count, directives = items });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> DirectiveCreateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryDirectiveService>();
        try
        {
            var d = await svc.CreateAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ToolArgs.OptionalString(ctx, "name"),
                ToolArgs.RequiredString(ctx, ArgContent),
                ToolArgs.OptionalInt(ctx, "priority", 0, int.MaxValue),
                ToolArgs.OptionalBool(ctx, "isActive"),
                OptionalStringList(ctx, "tags"), ct);
            return await ToolResults.Structured("directive created", d);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> DirectiveUpdateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryDirectiveService>();
        try
        {
            var d = await svc.UpdateAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")),
                ToolArgs.OptionalBool(ctx, "isActive"),
                ctx.Arguments?.TryGetValue("priority", out _) == true ? ToolArgs.OptionalInt(ctx, "priority", 0, int.MaxValue) : null,
                ToolArgs.OptionalString(ctx, ArgContent),
                OptionalStringList(ctx, "tags"), ct);
            return await ToolResults.Structured("directive updated", d);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> DirectiveDeleteAsync(ToolCallContext ctx, CancellationToken ct)
    {
        if (ToolArgs.OptionalBool(ctx, ArgConfirm) != true)
            return await ToolResults.Error("memory_directives_delete requires confirm:true");
        var svc = ctx.Services.GetRequiredService<MemoryDirectiveService>();
        try
        {
            await svc.DeleteAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")), ct);
            return await ToolResults.Structured("directive deleted", new { deleted = true });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    // ---------- mental models + pages (SPEC-20261111-mental-models-pages) ----------

    private static string? EffKey(ToolCallContext ctx) =>
        MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey));

    private static async ValueTask<CallToolResult> MentalModelsListAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            var items = await svc.ListAsync(EffKey(ctx), ToolArgs.OptionalString(ctx, "subtype"), ct);
            return await ToolResults.Structured($"{items.Count} model(s)",
                new { count = items.Count, models = items });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> MentalModelsGetAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            var dto = await svc.GetAsync(EffKey(ctx), OptionalGuid(ctx, "id"),
                ToolArgs.OptionalString(ctx, "path"), ct);
            return await ToolResults.Structured("mental model", dto);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> MentalModelsCreateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            var dto = await svc.CreateAsync(EffKey(ctx),
                new Memory.MentalModels.MentalModelCreate(
                    ToolArgs.OptionalString(ctx, "subtype") ?? "mental_model",
                    ToolArgs.RequiredString(ctx, "name"),
                    ToolArgs.OptionalString(ctx, "path"),
                    ToolArgs.RequiredString(ctx, "sourceQuery"),
                    OptionalJson(ctx, "trigger"),
                    OptionalStringList(ctx, "tags")), ct);
            if (ToolArgs.OptionalBool(ctx, "wait") == true)
            {
                var refresh = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelRefreshService>();
                var r = await refresh.RefreshAsync(dto.Id, ignoreMinInterval: true, ct);
                return await ToolResults.Structured("model created + refreshed", new { model = dto, refresh = r });
            }
            ctx.Services.GetRequiredService<Memory.MentalModels.IMentalModelRefreshTrigger>().Signal(dto.Id);
            return await ToolResults.Structured("model created (initial refresh signaled)", dto);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (MemoryConflictException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> MentalModelsUpdateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            var dto = await svc.UpdateAsync(EffKey(ctx),
                ParseGuid(ToolArgs.RequiredString(ctx, "id")),
                new Memory.MentalModels.MentalModelUpdate(
                    ToolArgs.OptionalString(ctx, "name"),
                    ToolArgs.OptionalString(ctx, "path"),
                    ToolArgs.OptionalString(ctx, "sourceQuery"),
                    OptionalJson(ctx, "trigger"),
                    OptionalStringList(ctx, "tags"),
                    ToolArgs.OptionalString(ctx, ArgContent),
                    ManualEdit: true), ct);
            return await ToolResults.Structured("model updated", dto);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (MemoryConflictException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> MentalModelsDeleteAsync(ToolCallContext ctx, CancellationToken ct)
    {
        if (ToolArgs.OptionalBool(ctx, ArgConfirm) != true)
            return await ToolResults.Error("memory_mental_models_delete requires confirm:true");
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            await svc.DeleteAsync(EffKey(ctx), ParseGuid(ToolArgs.RequiredString(ctx, "id")), ct);
            return await ToolResults.Structured("model deleted", new { deleted = true });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> MentalModelsRefreshAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var id = ParseGuid(ToolArgs.RequiredString(ctx, "id"));
        if (ToolArgs.OptionalBool(ctx, "wait") == true)
        {
            var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelRefreshService>();
            try
            {
                var r = await svc.RefreshAsync(id, ignoreMinInterval: true, ct);
                return await ToolResults.Structured(
                    r.Refreshed ? "refreshed" : "refresh skipped", r);
            }
            catch (MemoryBadRequestException ex)
            {
                return await ToolResults.Error(ex.Message);
            }
        }
        ctx.Services.GetRequiredService<Memory.MentalModels.IMentalModelRefreshTrigger>().Signal(id);
        return await ToolResults.Structured("refresh signaled", new { signaled = true, modelId = id });
    }

    private static async ValueTask<CallToolResult> PagesTreeAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            var tree = await svc.PageTreeAsync(EffKey(ctx), ct);
            return await ToolResults.Structured("pages tree", new { tree });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> PagesSearchAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            var hits = await svc.SearchPagesAsync(EffKey(ctx),
                ToolArgs.RequiredString(ctx, "query"),
                ToolArgs.OptionalInt(ctx, "limit", 10, 50), ct);
            return await ToolResults.Structured($"{hits.Count} hit(s)", new { count = hits.Count, hits });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> PageGetAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            var dto = await svc.GetPageAsync(EffKey(ctx), OptionalGuid(ctx, "id"),
                ToolArgs.OptionalString(ctx, "path"), ct);
            return await ToolResults.Structured("page", dto);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> PageCreateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        // Subtype fixed to 'page'.
        var svc = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelService>();
        try
        {
            var dto = await svc.CreateAsync(EffKey(ctx),
                new Memory.MentalModels.MentalModelCreate("page",
                    ToolArgs.RequiredString(ctx, "name"),
                    ToolArgs.OptionalString(ctx, "path"),
                    ToolArgs.RequiredString(ctx, "sourceQuery"),
                    OptionalJson(ctx, "trigger"),
                    OptionalStringList(ctx, "tags")), ct);
            if (ToolArgs.OptionalBool(ctx, "wait") == true)
            {
                var refresh = ctx.Services.GetRequiredService<Memory.MentalModels.MentalModelRefreshService>();
                var r = await refresh.RefreshAsync(dto.Id, ignoreMinInterval: true, ct);
                return await ToolResults.Structured("page created + refreshed", new { page = dto, refresh = r });
            }
            ctx.Services.GetRequiredService<Memory.MentalModels.IMentalModelRefreshTrigger>().Signal(dto.Id);
            return await ToolResults.Structured("page created (initial refresh signaled)", dto);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (MemoryConflictException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> PageUpdateAsync(ToolCallContext ctx, CancellationToken ct)
        => await MentalModelsUpdateAsync(ctx, ct);

    private static async ValueTask<CallToolResult> PageDeleteAsync(ToolCallContext ctx, CancellationToken ct)
        => await MentalModelsDeleteAsync(ctx, ct);

    private static Guid? OptionalGuid(ToolCallContext ctx, string name) =>
        ToolArgs.OptionalString(ctx, name) is { } s ? ParseGuid(s) : null;

    // ---------- consolidation (SPEC-20261111-memory-observations-consolidation) ----------

    private static async ValueTask<CallToolResult> ConsolidateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var bankKey = MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey));
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            var bank = await banks.ResolveAsync(bankKey, ct, create: false);
            if (ToolArgs.OptionalBool(ctx, "wait") == false)
            {
                var queue = ctx.Services.GetRequiredService<IMemoryRetainQueue>();
                var scope = await banks.ScopeKeyAsync(ct);
                var op = await queue.EnqueueAsync(bank, scope, "consolidate",
                    new { bankKey = bank.BankKey }, ct);
                return await ToolResults.Structured("consolidation queued",
                    new MemoryConsolidateResult(bank.Id, bank.BankKey, 0, 0, 0, 0, 0, 0, true, op.Id));
            }
            var svc = ctx.Services.GetRequiredService<MemoryConsolidationService>();
            var result = await svc.RunAsync(bank, ct);
            return await ToolResults.Structured(
                $"consolidated {result.Processed} fact(s): {result.Created} created, " +
                $"{result.Updated} updated, {result.Skipped} skipped, {result.Failed} failed",
                result);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (MemoryConflictException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
        catch (MemoryServiceUnavailableException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> ObsHistoryAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryConsolidationService>();
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            var history = await svc.HistoryAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ParseGuid(ToolArgs.RequiredString(ctx, "observationId")), banks, ct);
            return await ToolResults.Structured($"{history.Count} version(s)",
                new { count = history.Count, versions = history });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    // ---------- context / timeline / stats (SPEC-20261112-memory-context-timeline-stats) ----------

    private static async ValueTask<CallToolResult> ContextAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryContextService>();
        try
        {
            var digest = await svc.ContextAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ToolArgs.OptionalInt(ctx, "topK", 10, 50),
                ParseDate(ToolArgs.OptionalString(ctx, "since"))?.UtcDateTime,
                ToolArgs.OptionalBool(ctx, "synthesize") == true, ct);
            return await ToolResults.Structured(MemoryContextService.BuildDigest(digest), digest);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> TimelineAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryContextService>();
        try
        {
            var result = await svc.TimelineAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)),
                ParseDate(ToolArgs.OptionalString(ctx, "from"))?.UtcDateTime,
                ParseDate(ToolArgs.OptionalString(ctx, "to"))?.UtcDateTime,
                ToolArgs.OptionalString(ctx, "factType") is { } ft ? [ft] : null,
                ToolArgs.OptionalString(ctx, "groupBy") ?? "day",
                ToolArgs.OptionalInt(ctx, "limit", 50, 200), ct);
            return await ToolResults.Structured($"{result.Buckets.Count} bucket(s)", result);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> StatsAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var svc = ctx.Services.GetRequiredService<MemoryContextService>();
        try
        {
            var stats = await svc.StatsAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.OptionalString(ctx, ArgBankKey)), ct);
            return await ToolResults.Structured("memory stats", stats);
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    // ---------- bank CRUD ----------

    private static async ValueTask<CallToolResult> BankListAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        var items = await banks.ListAsync(ct);
        return await ToolResults.Structured($"{items.Count} bank(s)", new { count = items.Count, banks = items });
    }

    private static async ValueTask<CallToolResult> BankCreateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            var bank = await banks.CreateAsync(
                ToolArgs.RequiredString(ctx, ArgBankKey),
                ToolArgs.OptionalString(ctx, "name") ?? "",
                ToolArgs.OptionalString(ctx, "mission"),
                OptionalJson(ctx, "disposition"),
                OptionalJson(ctx, "config"), ct);
            return await ToolResults.Structured("bank created", new { bank.Id, bank.BankKey, bank.Name });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> BankUpdateAsync(ToolCallContext ctx, CancellationToken ct)
    {
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            var bank = await banks.UpdateAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.RequiredString(ctx, ArgBankKey))!,
                ToolArgs.OptionalString(ctx, "name"),
                ToolArgs.OptionalString(ctx, "mission"),
                OptionalJson(ctx, "disposition"),
                OptionalJson(ctx, "config"), ct);
            return await ToolResults.Structured("bank updated", new { bank.Id, bank.BankKey, bank.Name });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    private static async ValueTask<CallToolResult> BankDeleteAsync(ToolCallContext ctx, CancellationToken ct)
    {
        if (ToolArgs.OptionalBool(ctx, ArgConfirm) != true)
            return await ToolResults.Error("memory_bank_delete requires confirm:true");
        var banks = ctx.Services.GetRequiredService<MemoryBankService>();
        try
        {
            await banks.DeleteAsync(
                MemoryBankContext.EffectiveKey(ctx.Services, ToolArgs.RequiredString(ctx, ArgBankKey))!, ct);
            return await ToolResults.Structured("bank deleted", new { deleted = true });
        }
        catch (MemoryBadRequestException ex)
        {
            return await ToolResults.Error(ex.Message);
        }
    }

    // ---------- helpers ----------

    private static Guid ParseGuid(string raw) =>
        Guid.TryParse(raw, out var g)
            ? g
            : throw new McpProtocolException("argument 'id' must be a GUID", McpErrorCode.InvalidParams);

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (raw is null)
            return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        throw new McpProtocolException("argument 'timestamp' must be ISO-8601", McpErrorCode.InvalidParams);
    }

    private static IReadOnlyList<string>? OptionalStringList(ToolCallContext ctx, string name) =>
        ToolArgs.OptionalStringArray(ctx, name);

    private static IReadOnlyDictionary<string, string>? OptionalStringMap(ToolCallContext ctx, string name)
    {
        if (ToolArgs.OptionalElement(ctx, name) is not { } el || el.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string>();
        foreach (var p in el.EnumerateObject())
            map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.ToString();
        return map;
    }

    private static string? OptionalJson(ToolCallContext ctx, string name) =>
        ToolArgs.OptionalElement(ctx, name) is { } el && el.ValueKind == JsonValueKind.Object
            ? el.GetRawText() : null;
}
