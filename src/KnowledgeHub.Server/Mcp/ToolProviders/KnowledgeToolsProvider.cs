using System.Text;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.ToolProviders;

/// <summary>
/// Always-on tools (SPEC-04 RF-001/RF-002b/RF-002c):
/// search_knowledge, ask_knowledge, write_knowledge.
/// </summary>
public sealed class KnowledgeToolsProvider : IToolProvider
{
    private static readonly JsonObject SearchSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Text or question to search for","examples":["what is RAG?"]},
          "topK":{"type":"integer","description":"Max results (default 5, max 50)"},
          "source":{"type":"string","description":"Source slug (default: all active sources)"},
          "mode":{"type":"string","enum":["hybrid","semantic","lexical"],"description":"Search mode (default: hybrid)"},
          "sourceType":{"type":"string","description":"Filter by connector type (e.g. DocumentFile, WebPage)"},
          "pathPrefix":{"type":"string","description":"Filter by document path/URI prefix"},
          "indexedAfter":{"type":"string","description":"ISO-8601 date — only documents indexed at/after it"},
          "language":{"type":"string","description":"BCP-47 language tag filter (matches document metadata when present)"},
          "expand":{"type":"string","enum":["off","multi","hyde","both"],"description":"Query expansion override (default: server config). multi rewrites N variants + fuses; hyde embeds a hypothetical doc on the vector arm; both combines them"},
          "contextExpand":{"type":"string","enum":["none","window","section"],"description":"Attach surrounding context to each hit: window=neighbouring chunks, section=whole parent section"},
          "useGraph":{"type":"boolean","description":"Enable the knowledge-graph retrieval arm: entity linking + 1-hop evidence chunks (default: server config)"},
          "windowSize":{"type":"integer","description":"Neighbour window breadth for context expansion, 0-3 (default: server config). >0 implies contextExpand=window; 0 disables expansion"},
          "limitMode":{"type":"string","enum":["fixed","autocut"],"description":"Result limit: fixed=topK, autocut=prunes the long tail at the score elbow (default: server config)"},
          "autocutSensitivity":{"type":"integer","description":"Autocut sensitivity 1-3 — cut at the N-th abrupt score drop (default 1)"},
          "subQueries":{"type":"array","items":{"type":"string"},"description":"Extra query variants searched in parallel and fused via RRF (max 4) — for multi-faceted questions"},
          "allowRelaxation":{"type":"boolean","description":"When a strict source/tag filter yields too few results, fall back to broader scopes (source→type→global) with relaxed hits flagged (default: server config)"},
          "budget":{"type":"string","enum":["low","mid","high"],"description":"Recall depth/cost (default high). low = half candidate pool per arm, no query expansion, no corrective retries; mid = default pool without expansion; high = full pipeline"},
          "maxTokens":{"type":"integer","description":"Response token budget (default 4096, clamped 256-32768) — truncates the result list by ~4 chars/token after ranking; sets truncatedByTokens when applied"},
          "minScores":{"type":"object","properties":{"semantic":{"type":"number"},"lexical":{"type":"number"},"final":{"type":"number"}},"description":"Per-stage score floors 0-1: semantic = cosine floor on each vector arm, lexical = fraction of the arm's best FTS score, final = post-fusion floor"},
          "temporalWindow":{"type":"object","properties":{"start":{"type":"string","description":"ISO-8601"},"end":{"type":"string","description":"ISO-8601"}},"description":"Explicit recency window — documents indexed inside it rank higher (boost, not filter)"}
        },"required":["query"],
        "examples":[{"query":"what is RAG?","topK":5,"mode":"hybrid"}]}
        """)!.AsObject();

    private static readonly JsonObject AskSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "question":{"type":"string","description":"Natural-language question","examples":["How does synchronization work?"]},
          "topK":{"type":"integer","description":"Max passages used as context (default 5, max 50)"},
          "source":{"type":"string","description":"Source slug (default: all active sources)"},
          "mode":{"type":"string","enum":["hybrid","semantic","lexical"],"description":"Search mode (default: hybrid)"},
          "generate":{"type":"boolean","description":"Synthesize the answer via the server's configured chat provider (default: true when one is configured)"},
          "sourceType":{"type":"string","description":"Filter by connector type (e.g. DocumentFile, WebPage)"},
          "pathPrefix":{"type":"string","description":"Filter by document path/URI prefix"},
          "indexedAfter":{"type":"string","description":"ISO-8601 date — only documents indexed at/after it"},
          "language":{"type":"string","description":"BCP-47 language tag filter (matches document metadata when present)"},
          "expand":{"type":"string","enum":["off","multi","hyde","both"],"description":"Query expansion override (default: server config). multi rewrites N variants + fuses; hyde embeds a hypothetical doc on the vector arm; both combines them"},
          "contextExpand":{"type":"string","enum":["none","window","section"],"description":"Attach surrounding context to each hit: window=neighbouring chunks, section=whole parent section"},
          "useGraph":{"type":"boolean","description":"Enable the knowledge-graph retrieval arm: entity linking + 1-hop evidence chunks (default: server config)"},
          "windowSize":{"type":"integer","description":"Neighbour window breadth for context expansion, 0-3 (default: server config). >0 implies contextExpand=window; 0 disables expansion"},
          "limitMode":{"type":"string","enum":["fixed","autocut"],"description":"Result limit: fixed=topK, autocut=prunes the long tail at the score elbow (default: server config)"},
          "autocutSensitivity":{"type":"integer","description":"Autocut sensitivity 1-3 — cut at the N-th abrupt score drop (default 1)"},
          "subQueries":{"type":"array","items":{"type":"string"},"description":"Extra query variants searched in parallel and fused via RRF (max 4) — for multi-faceted questions"},
          "allowRelaxation":{"type":"boolean","description":"When a strict source/tag filter yields too few results, fall back to broader scopes (source→type→global) with relaxed hits flagged (default: server config)"},
          "budget":{"type":"string","enum":["low","mid","high"],"description":"Recall depth/cost (default high). low = half candidate pool per arm, no query expansion, no corrective retries; mid = default pool without expansion; high = full pipeline"},
          "maxTokens":{"type":"integer","description":"Context token budget (default 4096, clamped 256-32768) — truncates the passage list by ~4 chars/token after ranking; sets truncatedByTokens when applied"},
          "minScores":{"type":"object","properties":{"semantic":{"type":"number"},"lexical":{"type":"number"},"final":{"type":"number"}},"description":"Per-stage score floors 0-1: semantic = cosine floor on each vector arm, lexical = fraction of the arm's best FTS score, final = post-fusion floor (empty result → abstains instead of answering)"},
          "temporalWindow":{"type":"object","properties":{"start":{"type":"string","description":"ISO-8601"},"end":{"type":"string","description":"ISO-8601"}},"description":"Explicit recency window — documents indexed inside it rank higher (boost, not filter)"},
          "enableLiveActions":{"type":"boolean","description":"Action-Augmented RAG: execute live MCP tools nominated by the question itself — or by retrieved-chunk markers when the server opts in (Agent:AllowDocumentMarkers) — then fuse outputs with document citations (default: server config)"}
        },"required":["question"],
        "examples":[{"question":"How does synchronization work?","topK":5,"generate":true}]}
        """)!.AsObject();

    private static readonly JsonObject AgentSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "prompt":{"type":"string","description":"Natural-language question or task — the agent iterates tools until it can answer","examples":["Summarize this week's notes"]},
          "tools":{"type":"array","items":{"type":"string"},"description":"Allowlist of tools exposed to the model (default: all read-only tools)","examples":[["search_knowledge","ask_knowledge"]]},
          "maxIterations":{"type":"integer","description":"Max model→tools→model iterations (default 10)"},
          "allowWrite":{"type":"boolean","description":"Opt-in: exposes write tools (write_knowledge, write_note)"},
          "threadId":{"type":"string","description":"Existing thread GUID — continues the conversation with context"},
          "persist":{"type":"boolean","description":"Creates a new thread and persists this call's turns"}
        },"required":["prompt"],
        "examples":[{"prompt":"Summarize this week's notes","tools":["search_knowledge","ask_knowledge"],"maxIterations":10,"persist":true}]}
        """)!.AsObject();

    // RF-002 (SPEC-20260926-mcp-sdk-alignment): outputSchema for structuredContent.
    private static readonly JsonObject SearchOutputSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "results":{"type":"array","items":{"type":"object","properties":{
            "chunkText":{"type":"string"},"documentTitle":{"type":"string"},
            "sourceName":{"type":"string"},"sourceId":{"type":"string"},
            "score":{"type":"number"},"uriReference":{"type":"string"},
            "sourceType":{"type":"string"}},"required":["chunkText","documentTitle","sourceName","score","uriReference"]}},
          "grade":{"type":"string"},"retried":{"type":"boolean"},
          "totalMatches":{"type":"integer"},"limitModeApplied":{"type":"string"},
          "truncatedByTokens":{"type":"boolean"},
          "warnings":{"type":"array","items":{"type":"string"}}},
         "required":["results"]}
        """)!.AsObject();

    private static readonly JsonObject AskOutputSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "answer":{"type":"string"},
          "citations":{"type":"array","items":{"type":"object"}},
          "retrievalGrade":{"type":"string"},"retried":{"type":"boolean"},
          "truncatedByTokens":{"type":"boolean"}}}
        """)!.AsObject();

    private static readonly JsonObject WriteSchema = JsonNode.Parse("""
        {"type":"object","properties":{
          "title":{"type":"string","description":"Document title (becomes the file name in vault sources)","examples":["Example note"]},
          "content":{"type":"string","description":"Markdown or plain-text content","examples":["# Title\n\nMarkdown content."]},
          "source":{"type":"string","description":"Target source slug (default: first active source)"},
          "tags":{"type":"array","items":{"type":"string"},"description":"Tags (stored as frontmatter in vault sources)","examples":[["example"]]}
        },"required":["title","content"],
        "examples":[{"title":"Example note","content":"# Title\n\nMarkdown content.","tags":["example"]}]}
        """)!.AsObject();

    public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogTool> tools =
        [
            new CatalogTool
            {
                Name = "search_knowledge",
                Title = "Search knowledge",
                Description = "Unified semantic search across all active knowledge sources. Returns ranked passages with source name, document title, score and URI. Use for exploratory lookups; use a scoped query_* tool to search a single source.",
                InputSchema = SearchSchema,
                OutputSchema = SearchOutputSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = SearchKnowledgeAsync
            },
            new CatalogTool
            {
                Name = "ask_knowledge",
                Title = "Ask knowledge",
                Description = "Answers a natural-language question using the indexed knowledge base. When a chat provider is configured, returns a synthesized answer with [n] citations — each citation includes the document title, source name and file path, which can be passed to read_document to fetch the full document. Without a provider (or generate=false) returns the raw aggregated context.",
                InputSchema = AskSchema,
                OutputSchema = AskOutputSchema,
                ReadOnly = true,
                IdempotentHint = true,
                Handler = AskKnowledgeAsync
            },
            new CatalogTool
            {
                Name = "agent_chat",
                Title = "Agent chat",
                Description = "Multi-step agent: iterates model → tools → model over the live tool catalog until it can answer the prompt. Read-only tools are available by default; set allowWrite to expose write tools. Requires a configured chat provider.",
                InputSchema = AgentSchema,
                ReadOnly = true, // mutating tools still require allowWrite opt-in
                Handler = AgentChatAsync
            },
            new CatalogTool
            {
                Name = "write_knowledge",
                Title = "Write knowledge",
                Description = "Persists content into the knowledge base. For markdown-vault sources it creates a .md file; for other sources it stores a document that is indexed and immediately searchable.",
                InputSchema = WriteSchema,
                DestructiveHint = true,
                Handler = WriteKnowledgeAsync
            }
        ];
        return Task.FromResult(tools);
    }

    /// <summary>Grade hint appended to the text result when grading is on.</summary>
    private static async ValueTask<CallToolResult> SearchKnowledgeAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var query = ToolArgs.RequiredString(ctx, "query");
        var topK = ToolArgs.OptionalInt(ctx, "topK", 5, 50);
        var (sourceId, mode, filter) = await ResolveScopeAsync(ctx, ct);
        // SPEC-20260924-corrective-rag RF-004: the corrective wrapper
        // retries weak retrievals once and surfaces the grade so the
        // agent can decide to rephrase on its own.
        var retrieval = ctx.Services.GetRequiredService<CorrectiveRetrievalService>();
        var outcome = await retrieval.RetrieveAsync(query, topK, sourceId, mode, filter, ctx.ConversationContext, ct);
        // SPEC-20261001-mcp-recall-ergonomics RF-002: the token
        // budget truncates the ranked list post MMR/autocut/floors —
        // complements topK's count limit.
        var (bounded, truncatedByTokens) =
            ApplyTokenBudget(outcome.Results, ResolveMaxTokens(ctx));
        outcome = outcome with { Results = bounded };
        var suggested = await DetectSuggestedActionsAsync(ctx, query, outcome, ct);
        // Suggestions lead the text — CatalogToolAIFunction truncates
        // long results from the end, and appended suggestions were
        // being cut off when hits filled the budget (devin-review).
        return await ToolResults.Structured(
            BuildGradeLine(retrieval, outcome)
                + BuildSuggestedLine(suggested)
                + FormatHits(outcome.Results)
                + (truncatedByTokens ? "(truncated to fit maxTokens budget)" : ""),
            BuildSearchStructured(retrieval, outcome, sourceId, filter, suggested,
                ctx.Services.GetRequiredService<IConfiguration>(), truncatedByTokens));
    }

    private static string? BuildGradeLine(
        CorrectiveRetrievalService retrieval, CorrectiveRetrievalService.RetrievalOutcome outcome)
    {
        if (!retrieval.GradingEnabled)
            return null;
        return $"[grade: {outcome.Grading.Grade.ToString().ToLowerInvariant()}" +
               (outcome.Grading.Grade == Search.RetrievalGrade.Weak ? " — suggestion: rephrase the query" : "") +
               (outcome.Retried ? " — retried" : "") + "]\n";
    }

    /// <summary>SPEC-20260927-mcp-dynamic-rag-action-bridge: markers in
    /// retrieved chunks surface as suggested live actions — inside agent_chat
    /// the model can invoke them next turn. Disabled → empty list.</summary>
    private static async Task<IReadOnlyList<Bridge.ToolActionAnnotation>> DetectSuggestedActionsAsync(
        ToolCallContext ctx, string query,
        CorrectiveRetrievalService.RetrievalOutcome outcome, CancellationToken ct)
    {
        var bridgeOptions = ctx.Services.GetRequiredService<IOptions<Agent.AgentOptions>>().Value;
        if (!bridgeOptions.EnableDynamicActionBridge)
            return [];
        return Bridge.ToolActionAnnotationDetector.Detect(
            query, outcome.Results,
            await ctx.Services.GetRequiredService<IDynamicToolCatalog>().GetToolsAsync(ctx.Services, ct),
            Math.Clamp(bridgeOptions.MaxChainedDynamicCalls, 0, 3),
            bridgeOptions.AllowDocumentMarkers);
    }

    /// <summary>SPEC-20260929-live-actions-bridge-hardening RF-004: the agent
    /// loop only sees the text portion of tool results — surface nominations
    /// there so the model can invoke the suggested tool on the next iteration.</summary>
    private static string? BuildSuggestedLine(IReadOnlyList<Bridge.ToolActionAnnotation> suggested)
    {
        if (suggested.Count == 0)
            return null;
        return "\n\nSuggested live actions: "
               + string.Join(", ", suggested.Select(a => a.Args.Count == 0
                   ? a.ToolName
                   : $"{a.ToolName}({string.Join(", ", a.Args.Select(kv => kv.Key))})"))
               + " — invoke as tool calls if they help answer the request";
    }

    /// <summary>SPEC-20261001-mcp-recall-ergonomics RF-002: optional response
    /// token budget — default 4096, clamped 256–32768.</summary>
    private static int ResolveMaxTokens(ToolCallContext ctx) =>
        Math.Clamp(ToolArgs.OptionalIntOrNull(ctx, "maxTokens") ?? 4096, 256, 32768);

    /// <summary>RF-002: truncates the ranked list to a token budget (~4 chars
    /// per token over chunk + expanded-context text). The top hit is always
    /// kept — a lone over-budget hit beats silence.</summary>
    internal static (IReadOnlyList<SearchResultItem> Items, bool Truncated) ApplyTokenBudget(
        IReadOnlyList<SearchResultItem> items, int maxTokens)
    {
        var budgetChars = maxTokens * 4L;
        long spent = 0;
        var kept = new List<SearchResultItem>(items.Count);
        foreach (var item in items)
        {
            if (kept.Count > 0
                && spent + item.ChunkText.Length + (item.Context?.Length ?? 0) > budgetChars)
                return (kept, true);
            spent += item.ChunkText.Length + (item.Context?.Length ?? 0);
            kept.Add(item);
        }
        return (kept, false);
    }

    /// <summary>Structured payload for search_knowledge (RF-009 — structured
    /// callers get the warning field the text path appends inline).</summary>
    private static object BuildSearchStructured(
        CorrectiveRetrievalService retrieval,
        CorrectiveRetrievalService.RetrievalOutcome outcome,
        Guid? sourceId, Search.ResolvedSearchFilter filter,
        IReadOnlyList<Bridge.ToolActionAnnotation> suggested, IConfiguration limitCfg,
        bool truncatedByTokens)
    {
        // SPEC-20260927-multiquery RF-003: never relax silently.
        IReadOnlyList<string>? warnings = outcome.Results.All(r => r.IsRelaxed)
            && outcome.Results.Count > 0
                ? ["evidence found outside the strict requested scope"]
                : null;
        return new
        {
            results = outcome.Results,
            grade = retrieval.GradingEnabled
                ? outcome.Grading.Grade.ToString().ToLowerInvariant() : null,
            retried = outcome.Retried,
            totalMatches = outcome.Results.Count,
            limitModeApplied = filter.EffectiveLimitMode(limitCfg),
            truncatedByTokens,
            filterRelaxed = outcome.Results.Any(r => r.IsRelaxed),
            warnings,
            originalFilter = Search.ResolvedSearchFilter.DescribeScope(sourceId, filter),
            appliedFilter = outcome.Results.FirstOrDefault(r => r.IsRelaxed)?.RelaxedScope
                ?? Search.ResolvedSearchFilter.DescribeScope(sourceId, filter),
            suggestedActions = suggested.Count == 0 ? null : suggested.Select(a => new
            {
                tool = a.ToolName,
                args = a.Args.Count == 0 ? null : a.Args,
                origin = a.Origin.ToString().ToLowerInvariant()
            })
        };
    }

    /// <summary>
    /// Resolves the optional `source` slug, `mode` and metadata-filter args
    /// shared by search_knowledge/ask_knowledge (SPEC-20260914-hybrid-retrieval
    /// RF-003, SPEC-20260923-retrieval-quality RF-003).
    /// </summary>
    private static async Task<(Guid? SourceId, SearchMode Mode, Search.ResolvedSearchFilter Filter)> ResolveScopeAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var modeArg = ToolArgs.OptionalString(ctx, "mode");
        var mode = SearchMode.Hybrid;
        if (modeArg is not null)
            mode = Enum.TryParse<SearchMode>(modeArg, ignoreCase: true, out var parsed)
                ? parsed
                : throw new McpProtocolException(
                    $"invalid mode '{modeArg}' (expected: hybrid | semantic | lexical)", McpErrorCode.InvalidParams);

        var filter = ResolveFilter(ctx);
        var sourceSlug = ToolArgs.OptionalString(ctx, "source");
        if (sourceSlug is null)
            return (null, mode, filter);

        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var active = await db.Sources.Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        var slugs = ToolSlugger.Assign(active.Select(s => (s.Id, s.Name)));
        var sourceId = slugs.FirstOrDefault(kv => kv.Value == sourceSlug).Key;
        return sourceId == Guid.Empty
            ? throw new McpProtocolException($"unknown source slug '{sourceSlug}'", McpErrorCode.InvalidParams)
            : (sourceId, mode, filter);
    }

    /// <summary>Validates the optional filter args into a typed filter.</summary>
    private static Search.ResolvedSearchFilter ResolveFilter(ToolCallContext ctx)
    {
        var raw = new SearchFilter
        {
            SourceType = ToolArgs.OptionalString(ctx, "sourceType"),
            PathPrefix = ToolArgs.OptionalString(ctx, "pathPrefix"),
            IndexedAfter = ToolArgs.OptionalString(ctx, "indexedAfter"),
            Language = ToolArgs.OptionalString(ctx, "language"),
            Expand = ToolArgs.OptionalString(ctx, "expand"),
            ContextExpand = ToolArgs.OptionalString(ctx, "contextExpand"),
            UseGraph = ToolArgs.OptionalBool(ctx, "useGraph"),
            WindowSize = ToolArgs.OptionalIntOrNull(ctx, "windowSize"),
            LimitMode = ToolArgs.OptionalString(ctx, "limitMode"),
            AutocutSensitivity = ToolArgs.OptionalIntOrNull(ctx, "autocutSensitivity"),
            SubQueries = ToolArgs.OptionalStringArray(ctx, "subQueries"),
            AllowRelaxation = ToolArgs.OptionalBool(ctx, "allowRelaxation"),
            // SPEC-20261001-mcp-recall-ergonomics RF-001/RF-003/RF-004: budget,
            // per-stage floors and the explicit temporal window.
            Budget = ToolArgs.OptionalString(ctx, "budget"),
            MinScores = ToolArgs.OptionalObject(ctx, "minScores") is { } ms
                ? new SearchMinScores
                {
                    Semantic = ToolArgs.OptionalScore(ms, "minScores.semantic"),
                    Lexical = ToolArgs.OptionalScore(ms, "minScores.lexical"),
                    Final = ToolArgs.OptionalScore(ms, "minScores.final")
                }
                : null,
            TemporalStart = ToolArgs.OptionalProp(
                ToolArgs.OptionalObject(ctx, "temporalWindow"), "temporalWindow.start"),
            TemporalEnd = ToolArgs.OptionalProp(
                ToolArgs.OptionalObject(ctx, "temporalWindow"), "temporalWindow.end")
        };
        return Search.ResolvedSearchFilter.TryResolve(raw, out var filter, out var error)
            ? filter
            : throw new McpProtocolException(error!, McpErrorCode.InvalidParams);
    }

    /// <summary>
    /// SPEC-20260914-llm-answer-synthesis RF-002/RF-004: with a configured chat
    /// provider and generate!=false, synthesizes a cited answer (structuredContent).
    /// Otherwise falls back to the legacy aggregated-context payload.
    /// </summary>
    private static async ValueTask<CallToolResult> AskKnowledgeAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var question = ToolArgs.RequiredString(ctx, "question");
        var topK = ToolArgs.OptionalInt(ctx, "topK", 5, 50);
        var (sourceId, mode, filter) = await ResolveScopeAsync(ctx, ct);
        var retrieval = ctx.Services.GetRequiredService<CorrectiveRetrievalService>();
        var outcome = await retrieval.RetrieveAsync(question, topK, sourceId, mode, filter, ctx.ConversationContext, ct);
        // SPEC-20261001-mcp-recall-ergonomics RF-002: bound the evidence list —
        // and thereby the synthesis context — by the caller's token budget.
        // The outcome carries the bounded list so abstention citations and the
        // relaxed-scope warning reflect what the caller actually receives.
        var (bounded, truncatedByTokens) =
            ApplyTokenBudget(outcome.Results, ResolveMaxTokens(ctx));
        outcome = outcome with { Results = bounded };

        var live = await ExecuteLiveActionsAsync(ctx, question, bounded, ct);

        var answers = ctx.Services.GetRequiredService<IAnswerService>();
        var generate = ToolArgs.OptionalBool(ctx, "generate") ?? answers.IsConfigured;

        // SPEC-20260927-multiquery RF-003: when every piece of evidence came from
        // a relaxed scope, the response must say so — never relax silently.
        var relaxedWarning = outcome.Results.Count > 0 && outcome.Results.All(r => r.IsRelaxed)
            ? "\n\n(evidence found outside the strict requested scope)"
            : null;

        var ask = new AskContext(question, outcome, live.Results, live,
            truncatedByTokens, relaxedWarning);

        // SPEC-20260924-corrective-rag RF-003: insufficient evidence short-circuits
        // synthesis — honest abstention, no LLM call, weak citations attached.
        if (outcome.Grading.Grade == Search.RetrievalGrade.Insufficient && generate && !live.HasEvidence)
            return await BuildAbstentionResult(retrieval, ask);

        if (!generate)
            return await ToolResults.Text(
                FormatAnswerContext(question, live.Results) + relaxedWarning + live.CitationBlock);

        if (!answers.IsConfigured)
        {
            // RF risk mitigation: generate requested but no provider — raw context + warning.
            return await ToolResults.Text(
                FormatAnswerContext(question, live.Results) + relaxedWarning
                + "\n\n(warning: no chat provider configured — returning raw context)");
        }

        try
        {
            return await SynthesizeAnswerAsync(ctx, retrieval, answers, ask, ct);
        }
        catch (Chat.ChatProviderException ex)
        {
            return await ToolResults.Error($"answer generation failed: {ex.Message}");
        }
    }

    /// <summary>Everything the abstention/synthesis paths need beyond the call
    /// context — keeps their signatures under the 7-arg ceiling.</summary>
    private sealed record AskContext(
        string Question,
        CorrectiveRetrievalService.RetrievalOutcome Outcome,
        IReadOnlyList<SearchResultItem> Results,
        LiveActionsResult Live,
        bool TruncatedByTokens,
        string? RelaxedWarning);

    /// <summary>Live-action executions plus the evidence list they produced.</summary>
    private sealed record LiveActionsResult(
        IReadOnlyList<LiveToolExecution> Executions,
        IReadOnlyList<SearchResultItem> Results,
        string CitationBlock,
        bool HasEvidence);

    /// <summary>SPEC-20260927-mcp-dynamic-rag-action-bridge RF-001/RF-003: execute
    /// live MCP tools nominated by retrieved chunks (mcp-tool markers) or by
    /// the question itself — inside the caller's scope (catalog is already
    /// scope-filtered) — then fuse outputs as clearly-labelled live context.</summary>
    private static async Task<LiveActionsResult> ExecuteLiveActionsAsync(
        ToolCallContext ctx, string question,
        IReadOnlyList<SearchResultItem> results, CancellationToken ct)
    {
        var agentOptions = ctx.Services.GetRequiredService<IOptions<Agent.AgentOptions>>().Value;
        var enableLiveActions = ToolArgs.OptionalBool(ctx, "enableLiveActions")
            ?? agentOptions.EnableDynamicActionBridge;
        IReadOnlyList<LiveToolExecution> liveExecutions = [];
        if (enableLiveActions)
        {
            var catalog = ctx.Services.GetRequiredService<IDynamicToolCatalog>();
            var visible = await catalog.GetToolsAsync(ctx.Services, ct);
            liveExecutions = await Bridge.McpDynamicRagActionBridge.ExecuteAsync(
                question, results, visible, ctx,
                Math.Clamp(agentOptions.MaxChainedDynamicCalls, 0, 3),
                agentOptions.AllowDocumentMarkers, ct);
            var liveContext = Bridge.HybridCitationFormatter.AsContextItems(liveExecutions);
            if (liveContext.Count > 0)
                results = [.. results, .. liveContext];
        }
        var liveCitationBlock = Bridge.HybridCitationFormatter.FormatLiveCitations(liveExecutions);

        // SPEC-20260929-live-actions-bridge-hardening RF-005: live tool output
        // IS evidence — an "insufficient" document grade must not discard it.
        // Abstain only when there is no live context to synthesize from.
        var hasLiveEvidence = liveExecutions.Any(e =>
            !e.IsError && !string.IsNullOrWhiteSpace(e.OutputPreview));

        return new LiveActionsResult(liveExecutions, results, liveCitationBlock, hasLiveEvidence);
    }

    private static async ValueTask<CallToolResult> BuildAbstentionResult(
        CorrectiveRetrievalService retrieval, AskContext ask)
    {
        var abstention = retrieval.BuildAbstention(ask.Question, ask.Outcome) with
        {
            LiveToolExecutions = ask.Live.Executions.Count == 0 ? null : ask.Live.Executions,
            TruncatedByTokens = ask.TruncatedByTokens
        };
        return await ToolResults.Structured(
            abstention.Answer
            + (ask.Results.Count > 0 ? "\n\nClosest passages:\n" + FormatHits(ask.Results.Take(3).ToList()) : "")
            + ask.RelaxedWarning
            + ask.Live.CitationBlock,
            abstention);
    }

    private static async ValueTask<CallToolResult> SynthesizeAnswerAsync(
        ToolCallContext ctx, CorrectiveRetrievalService retrieval,
        IAnswerService answers, AskContext ask, CancellationToken ct)
    {
        var rawAnswer = await answers.AnswerAsync(ask.Question, ask.Results, ct);
        var answer = rawAnswer with
        {
            RetrievalGrade = retrieval.GradingEnabled
                ? ask.Outcome.Grading.Grade.ToString().ToLowerInvariant()
                : null,
            Retried = ask.Outcome.Retried,
            TruncatedByTokens = ask.TruncatedByTokens,
            LiveToolExecutions = ask.Live.Executions.Count == 0 ? null : ask.Live.Executions,
            // SPEC-20260929-live-actions-bridge-hardening RF-007: when the
            // answer rests on live data alone (no document citations),
            // expose the executions as pseudo-citations so consumers can
            // still see what grounded the response. Failed executions are
            // not evidence — they never become citations (devin-review
            // #402/#413).
            Citations = rawAnswer.Citations.Count == 0 && ask.Live.Executions.Any(e => !e.IsError)
                ? ask.Live.Executions.Where(e => !e.IsError).Select((e, i) => new CitationDto
                {
                    Index = i + 1,
                    Source = "live-mcp",
                    Title = $"[Live Tool: {e.ToolName}]",
                    Uri = $"live://tool/{e.ToolName}",
                    Score = 1.0
                }).ToList()
                : rawAnswer.Citations
        };
        // SPEC-20260927-cryptographic-evidence-provenance-chain RF-002:
        // QuerySubmitted → ChunksRetrieved → AnswerSynthesized receipts,
        // keyed by the caller's API key (or "mcp" for cookie sessions).
        var apiKeyId = CallerIdentity.TryGetApiKeyId(ctx)?.ToString("N");
        await Audit.Evidence.EvidenceEmission.RecordAskAsync(
            new Audit.Evidence.EvidenceEmission.EmissionContext(
                ctx.Services.GetService<Audit.Evidence.IEvidenceChainService>(),
                $"mcp:{apiKeyId ?? "session"}", apiKeyId,
                ctx.Services.GetService<ILoggerFactory>()?.CreateLogger("EvidenceEmission")),
            ask.Question, ask.Results, answer.Answer ?? "", ct);
        var text = new StringBuilder(answer.Answer + ask.RelaxedWarning);
        AppendCitations(text, answer.Citations);
        text.Append(ask.Live.CitationBlock);
        return await ToolResults.Structured(text.ToString(), answer);
    }

    private static void AppendCitations(StringBuilder text, IReadOnlyList<CitationDto> citations)
    {
        if (citations.Count == 0)
            return;
        text.Append("\n\nCitations:");
        foreach (var c in citations)
        {
            text.Append("\n[").Append(c.Index).Append("] ")
                .Append(c.Title).Append(" — ").Append(c.Source)
                .Append(c.Path is not null ? " (path: " : " (")
                .Append(c.Path ?? c.Uri).Append(')');
            if (c.SuspicionFlags is not null)
                text.Append(" [flagged: ").Append(c.SuspicionFlags).Append(']');
            if (c.Components is { Count: > 0 } comps)
                text.Append(" [components: ").Append(string.Join(", ", comps)).Append(']');
        }
    }

    /// <summary>SPEC-20260914-agent-chat-loop RF-002: agent loop as an MCP tool.</summary>
    private static async ValueTask<CallToolResult> AgentChatAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var agent = ctx.Services.GetRequiredService<IAgentService>();
        if (!agent.IsConfigured)
            return await ToolResults.Error("agent_chat requires a chat provider (Chat:Provider)");

        var request = new AgentRequest
        {
            Prompt = ToolArgs.RequiredString(ctx, "prompt"),
            Tools = ToolArgs.OptionalStringArray(ctx, "tools"),
            MaxIterations = ToolArgs.OptionalInt(ctx, "maxIterations", 10, 50),
            AllowWrite = ToolArgs.OptionalBool(ctx, "allowWrite") == true,
            ThreadId = ToolArgs.OptionalString(ctx, "threadId") is { } tid && Guid.TryParse(tid, out var g) ? g : null,
            Persist = ToolArgs.OptionalBool(ctx, "persist") == true,
            // SPEC-20261001-a2a-task-durability RF-002: A2A task calls carry a
            // progress sink — the loop reports iteration/tool progress lines.
            OnProgress = ctx.OnProgress
        };

        try
        {
            var result = await agent.RunAsync(request, ct);
            var text = new StringBuilder(result.Answer);
            if (result.Steps.Count > 0)
            {
                text.Append("\n\nSteps:");
                foreach (var s in result.Steps)
                    text.Append("\n- [").Append(s.Iteration).Append("] ")
                        .Append(s.Tool).Append(' ').Append(s.ArgsSummary)
                        .Append(s.IsError ? " (error)" : "");
            }
            return await ToolResults.Structured(text.ToString(), result);
        }
        catch (Chat.ChatProviderException ex)
        {
            return await ToolResults.Error($"agent run failed: {ex.Message}");
        }
    }

    private static async ValueTask<CallToolResult> WriteKnowledgeAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var note = new NoteWrite(
            ToolArgs.RequiredString(ctx, "title"),
            ToolArgs.RequiredString(ctx, "content"),
            ToolArgs.OptionalStringArray(ctx, "tags"),
            ObsidianNoteWriter.ResolveOrigin(ctx.Services));
        var sourceSlug = ToolArgs.OptionalString(ctx, "source");

        var db = ctx.Services.GetRequiredService<KnowledgeHubDbContext>();
        var ingestion = ctx.Services.GetRequiredService<IngestionService>();

        // Resolve target source: by slug, else first active source.
        var active = await db.Sources.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync(ct);
        if (active.Count == 0)
            return await ToolResults.Error("no active knowledge source configured");

        var slugs = ToolSlugger.Assign(active.Select(s => (s.Id, s.Name)));
        var target = sourceSlug is null
            ? active[0]
            : active.FirstOrDefault(s => slugs[s.Id] == sourceSlug)
              ?? throw new McpProtocolException($"unknown source slug '{sourceSlug}'", McpErrorCode.InvalidParams);

        if (ObsidianNoteWriter.IsReadOnly(target))
            return await ToolResults.Error($"source '{target.Name}' is read-only");

        return target.SourceType == SourceType.ObsidianVault
            ? await WriteToVaultAsync(db, ingestion, target, note, ct)
            : await WriteToDocumentAsync(ctx, db, target, slugs[target.Id], note, ct);
    }

    private sealed record NoteWrite(
        string Title, string Content, string[]? Tags, WriteOriginContext? Origin);

    private static async ValueTask<CallToolResult> WriteToVaultAsync(
        KnowledgeHubDbContext db, IngestionService ingestion,
        Domain.Entities.KnowledgeSource target, NoteWrite note, CancellationToken ct)
    {
        var root = IngestionService.ResolveVaultRoot(target.ConfigurationJson)
            ?? throw new McpProtocolException("vault source has no configured path", McpErrorCode.InvalidParams);
        var relative = ObsidianNoteWriter.TitleToFileName(note.Title);
        var full = ObsidianNoteWriter.SafePath(root, relative, forWrite: true);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, ObsidianNoteWriter.WithFrontmatter(note.Content, note.Tags, note.Origin), ct);

        var relPath = Path.GetRelativePath(root, full);
        await ingestion.SyncFileAsync(target.Id, relPath, ct);

        var doc = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.KnowledgeSourceId == target.Id && d.UriReference == relPath, ct);
        var chunkCount = doc is null ? 0
            : await db.Chunks.CountAsync(c => c.KnowledgeDocumentId == doc.Id, ct);
        return await ToolResults.Text(
            $"Wrote `{relPath}` to vault '{target.Name}'.\nDocument id: {doc?.Id}\nChunks indexed: {chunkCount}");
    }

    /// <summary>Non-vault source: persist + index an in-place KnowledgeDocument.</summary>
    private static async ValueTask<CallToolResult> WriteToDocumentAsync(
        ToolCallContext ctx, KnowledgeHubDbContext db,
        Domain.Entities.KnowledgeSource target, string targetSlug,
        NoteWrite note, CancellationToken ct)
    {
        var uriRef = $"knowledge://{targetSlug}/{ObsidianNoteWriter.TitleToFileName(note.Title)}.md";
        var doc2 = await db.Documents.Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.KnowledgeSourceId == target.Id && d.UriReference == uriRef, ct);
        if (doc2 is null)
        {
            doc2 = new Domain.Entities.KnowledgeDocument
            {
                KnowledgeSourceId = target.Id,
                Title = note.Title,
                UriReference = uriRef
            };
            db.Documents.Add(doc2);
        }
        else
        {
            doc2.Title = note.Title;
            db.Chunks.RemoveRange(doc2.Chunks);
        }

        var body = ObsidianNoteWriter.WithFrontmatter(note.Content, note.Tags, note.Origin);
        doc2.RawContent = body;
        doc2.ContentHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        doc2.IndexedAt = DateTimeOffset.UtcNow;

        var embeddings = ctx.Services.GetRequiredService<Embeddings.IEmbeddingProvider>();
        var vectors = ctx.Services.GetRequiredService<VectorStore.IVectorStore>();
        // SPEC-20260923-code-aware-chunking: kind from the document URI.
        var config = ctx.Services.GetRequiredService<IConfiguration>();
        var (kind, pieces) = await Ingestion.Chunking.ChunkerSelector.ChunkAsync(
            new Ingestion.Chunking.ChunkerSelector.ChunkRequest(doc2.UriReference, body, 500, 50,
                Ingestion.Chunking.ChunkerSelector.StrategyFor(target.ConfigurationJson)),
            embeddings, config,
            ctx.Services!.GetRequiredService<ILoggerFactory>()
                .CreateLogger("KnowledgeHub.write_knowledge"), ct, ctx.Services);
        var sanitizer = ctx.Services.GetRequiredService<Security.IContentSanitizer>();
        var flagged = 0;
        var newChunks = pieces.Select((p, i) =>
        {
            var flags = sanitizer.Scan(p.Text);
            if (flags.Count > 0)
                flagged++;
            return new Domain.Entities.DocumentChunk
            {
                KnowledgeDocumentId = doc2.Id,
                ChunkIndex = i,
                TextContent = p.Text,
                ChunkKind = kind.ToString().ToLowerInvariant(),
                SymbolPath = p.SymbolPath,
                SectionPath = p.SectionPath,
                MetadataJson = p.MetadataJson,
                EnrichedText = Ingestion.ContextEnricher.Compose(
                    target.Name, note.Title, p.SectionPath ?? p.SymbolPath, p.Text,
                    config.GetValue("Ingestion:ContextualEnrichment", "structural"),
                    config.GetValue("Ingestion:ContextualEnrichment:MinTokens", 40)),
                SuspicionFlags = flags.Count == 0 ? null : string.Join(',', flags)
            };
        }).ToList();
        db.Chunks.AddRange(newChunks);
        foreach (var c in newChunks.Where(c => c.SuspicionFlags is not null))
        {
            db.SecurityEvents.Add(new Domain.Entities.SecurityEvent
            {
                SourceId = doc2.KnowledgeSourceId,
                DocumentId = doc2.Id,
                ChunkIndex = c.ChunkIndex,
                Flags = c.SuspicionFlags!
            });
        }
        await db.SaveChangesAsync(ct);

        // SPEC-20260923-pgvector-metadata-upsert RF-003: same provenance map as
        // the ingestion path — keeps the pgvector metadata column meaningful.
        var upsertMetadata = new Dictionary<string, string>
        {
            ["sourceType"] = target.SourceType.ToString(),
            ["indexedAt"] = doc2.IndexedAt.ToString("o")
        };
        foreach (var chunk in newChunks)
        {
            var vector = await embeddings.EmbedDocumentAsync(chunk.TextContent, ct);
            await vectors.UpsertAsync(chunk.Id, doc2.Id, target.Id, vector, embeddings.ModelId,
                upsertMetadata, ct);
        }

        // RF-004: keep the FTS index consistent with Chunks.
        await ctx.Services.GetRequiredService<Search.ILexicalSearchService>().ReconcileAsync(ct);

        var flagNote = flagged == 0 ? "" : $"\nWarning: {flagged} chunk(s) flagged by the security scan (see /api/security/events).";
        return await ToolResults.Text(
            $"Stored '{note.Title}' in source '{target.Name}'.\nDocument id: {doc2.Id}\nChunks indexed: {newChunks.Count}{flagNote}");
    }

    internal static string FormatHits(IReadOnlyList<SearchResultItem> results)
    {
        if (results.Count == 0)
            return "No results found in active knowledge sources.";
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.Append("### ").Append(r.DocumentTitle).Append('\n')
              .Append("- source: ").Append(r.SourceName)
              .Append(" | score: ").Append(r.Score.ToString("F3"))
              .Append(" | uri: ").Append(r.UriReference);
            // SPEC-20260923-flagged-chunk-badge RF-002: a kept flagged chunk
            // must be distinguishable in the text surface too.
            if (r.SuspicionFlags is not null)
                sb.Append(" | flagged: ").Append(r.SuspicionFlags);
            // SPEC-20260924-graph-tool-discovery RF-003: graph entity names
            // usable as `component` args for the find_* tools.
            if (r.Components is { Count: > 0 } comps)
                sb.Append(" | components: ").Append(string.Join(", ", comps));
            sb.Append('\n')
              .Append(Security.PromptBoundary.Escape(r.ChunkText)).Append("\n\n");
        }
        return sb.ToString();
    }

    internal static string FormatAnswerContext(string question, IReadOnlyList<SearchResultItem> results)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").Append(question).Append("\n\n");
        if (results.Count == 0)
            return sb.Append("No relevant knowledge found. Answer from general knowledge and state that the knowledge base had no matches.").ToString();

        sb.Append("Retrieved context (cite sources in your answer):\n\n");
        var i = 1;
        foreach (var r in results)
        {
            var header = $"[{i}] {r.DocumentTitle} — {r.SourceName} (score {r.Score:F3}, {r.UriReference})"
                + (r.Components is { Count: > 0 } comps
                    ? $" — components: {string.Join(", ", comps)}"
                    : "");
            // SPEC-20260929 RF-008: when context expansion delivered a wider
            // window, the non-generate path must surface it — not just the hit.
            var body = r.Context is { Length: > 0 } ctx
                ? r.ChunkText + "\n\n[expanded context]\n" + ctx
                : r.ChunkText;
            sb.Append(Security.PromptBoundary.WrapChunk(
                      i++, $"{r.SourceName}/{r.UriReference}",
                      header + "\n" + body,
                      r.SuspicionFlags is not null))
              .Append("\n\n");
        }
        return sb.ToString();
    }
}
