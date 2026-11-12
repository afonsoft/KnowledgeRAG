using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Memory;

/// <summary>SPEC-20261111-memory-reflect-directives RF-002.</summary>
public sealed record MemoryReflectRequest(
    string Query,
    string? Context = null,
    /// <summary>low | mid | high — controls max iterations (3|6|configured).</summary>
    string Budget = "mid",
    int? MaxTokens = null,
    /// <summary>JSON Schema object — triggers a structured-output pass (RF-004).</summary>
    JsonElement? ResponseSchema = null,
    /// <summary>Scope directives by tag; empty-tag directives always apply.</summary>
    IReadOnlyList<string>? Tags = null,
    /// <summary>any (default) | all — tag matching mode for directives.</summary>
    string TagsMatch = "any",
    bool ApplyAllDirectives = false,
    bool IncludeBasedOn = true,
    bool IncludeTrace = false);

public sealed record ReflectCitation(string Type, Guid Id, string Text);
public sealed record ReflectUsage(long? InputTokens, long? OutputTokens, int ToolCalls, int Iterations);
public sealed record ReflectTraceCall(string Tool, string? Query, int Results, double Ms);
public sealed record ReflectTrace(
    IReadOnlyList<ReflectTraceCall> Calls,
    IReadOnlyList<string> DroppedCitations,
    bool Uncited);

public sealed record MemoryReflectResult(
    Guid BankId,
    string Text,
    IReadOnlyList<ReflectCitation>? BasedOn,
    JsonElement? StructuredOutput,
    string? StructuredOutputError,
    ReflectTrace? Trace,
    ReflectUsage Usage);

/// <summary>
/// SPEC-20261111-memory-reflect-directives RF-002/RF-003/RF-004: agentic
/// reflect — a read-only LLM loop over hierarchical memory (observations
/// first, then raw facts). The bank's mission/disposition/directives shape
/// the system prompt; every cited id is validated against the units actually
/// retrieved this call (fabricated citations are dropped into the trace).
/// </summary>
public sealed class MemoryReflectService(
    KnowledgeHubDbContext db,
    MemoryBankService banks,
    MemoryRecallService recall,
    MemoryConfigResolver config,
    IServiceProvider sp)
{
    public async Task<MemoryReflectResult> ReflectAsync(
        string? bankKey, MemoryReflectRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Query))
            throw new MemoryBadRequestException("query is required");
        var chat = sp.GetService<IChatClient>()
            ?? throw new MemoryServiceUnavailableException(
                "memory reflect requires a configured chat provider (Chat:*)");
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);

        var directives = await LoadDirectivesAsync(bank, req, ct);
        var maxIter = Math.Clamp(BudgetIterations(req.Budget), 1, config.ReflectMaxIterations(bank));

        // Retrieved units this call — the citation universe (RF-003).
        var retrieved = new Dictionary<Guid, (string Type, string Text)>();
        var traceCalls = new List<ReflectTraceCall>();
        string? doneAnswer = null;
        var doneCited = new List<Guid>();
        var toolCalls = 0;
        long? input = 0, output = 0;

        var functions = BuildFunctions();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt(bank, directives, req.Context)),
            new(ChatRole.User, req.Query)
        };
        var options = new ChatOptions { Tools = functions, MaxOutputTokens = req.MaxTokens };

        string? finalText = null;
        var iterations = 0;
        for (var i = 0; i < maxIter && doneAnswer is null; i++)
        {
            iterations = i + 1;
            var response = await chat.GetResponseAsync(messages, options, ct);
            input += response.Usage?.InputTokenCount;
            output += response.Usage?.OutputTokenCount;
            messages.AddRange(response.Messages);
            var calls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
            if (calls.Count == 0)
            {
                finalText = response.Text;
                break;
            }
            foreach (var call in calls)
            {
                var result = await DispatchAsync(call, bank, retrieved, traceCalls,
                    done => { doneAnswer = done.Answer; doneCited = done.Cited; }, ct);
                toolCalls++;
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, result)]));
            }
        }

        var text = doneAnswer ?? finalText
            ?? "(reflect produced no answer within the iteration budget)";
        // Natural finish may embed a {"based_on":[...]} block — extract ids.
        List<Guid> cited;
        if (doneAnswer is not null)
        {
            cited = doneCited;
        }
        else
        {
            cited = ParseBasedOn(text, out var cleaned);
            if (cleaned is not null)
                text = cleaned;
        }
        var (basedOn, dropped) = ValidateCitations(cited, retrieved);

        JsonElement? structured = null;
        string? structuredError = null;
        if (req.ResponseSchema is { } schema && schema.ValueKind == JsonValueKind.Object)
            (structured, structuredError) = await ExtractStructuredAsync(chat, text, schema, ct);

        var trace = req.IncludeTrace ? new ReflectTrace(
            traceCalls, dropped.Select(d => d.ToString()).ToList(),
            basedOn.Count == 0) : null;
        return new MemoryReflectResult(bank.Id, text,
            req.IncludeBasedOn ? basedOn : null,
            structured, structuredError, trace,
            new ReflectUsage(input, output, toolCalls, iterations));
    }

    // ---------------- internal read-only tools ----------------

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Schema exposure for the model — dispatch runs in <see cref="DispatchAsync"/>.</summary>
    private static AIFunction[] BuildFunctions() =>
    [
        new DelegateAIFunction("search_observations",
            "Search consolidated observations (durable, evidence-backed conclusions). ALWAYS try this layer first.",
            Schema("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
            (_, _) => new ValueTask<object?>("")),
        new DelegateAIFunction("recall_facts",
            "Recall raw retained facts (world + experience). Use when observations don't cover the question.",
            Schema("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
            (_, _) => new ValueTask<object?>("")),
        new DelegateAIFunction("done",
            "Submit the final answer. based_on = array of unit ids the answer relies on (must come from retrieved results).",
            Schema("""{"type":"object","properties":{"answer":{"type":"string"},"based_on":{"type":"array","items":{"type":"string"}}},"required":["answer"]}"""),
            (_, _) => new ValueTask<object?>("submitted"))
    ];

    private async ValueTask<object?> DispatchAsync(
        FunctionCallContent call, MemoryBank bank,
        Dictionary<Guid, (string Type, string Text)> retrieved,
        List<ReflectTraceCall> traceCalls,
        Action<(string Answer, List<Guid> Cited)> captureDone, CancellationToken ct)
    {
        var args = new AIFunctionArguments(call.Arguments);
        return call.Name switch
        {
            "search_observations" or "recall_facts" =>
                await SearchDispatch(call.Name, args, bank, retrieved, traceCalls, ct),
            "done" => DoneDispatch(args, captureDone, traceCalls),
            _ => $"unknown tool '{call.Name}'"
        };
    }

    private async ValueTask<object?> SearchDispatch(
        string name, AIFunctionArguments args, MemoryBank bank,
        Dictionary<Guid, (string Type, string Text)> retrieved,
        List<ReflectTraceCall> traceCalls, CancellationToken ct)
    {
        var q = ArgString(args, "query") ?? "";
        var factType = name == "search_observations" ? "observation" : "world,experience";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await recall.RecallAsync(bank.BankKey,
                new MemoryRecallRequest(q, TopK: 6, FactType: factType), ct);
            foreach (var r in result.Results)
                retrieved[r.UnitId] = (r.FactType, r.Text);
            traceCalls.Add(new ReflectTraceCall(name, q, result.Results.Count,
                sw.Elapsed.TotalMilliseconds));
            return JsonSerializer.Serialize(new
            {
                results = result.Results.Select(r => new { r.UnitId, r.FactType, r.Text, r.Score })
            });
        }
        catch (Exception ex)
        {
            traceCalls.Add(new ReflectTraceCall(name, q, 0, sw.Elapsed.TotalMilliseconds));
            return $"search failed: {ex.Message}";
        }
    }

    private static object DoneDispatch(AIFunctionArguments args,
        Action<(string Answer, List<Guid> Cited)> captureDone,
        List<ReflectTraceCall> traceCalls)
    {
        var answer = ArgString(args, "answer") ?? "";
        var cited = new List<Guid>();
        if (args.TryGetValue("based_on", out var raw) && raw is not null)
            foreach (var s in ArgStrings(raw))
                if (Guid.TryParse(s, out var id))
                    cited.Add(id);
        captureDone((answer, cited));
        traceCalls.Add(new ReflectTraceCall("done", null, cited.Count, 0));
        return "submitted";
    }

    // ---------------- prompt ----------------

    internal static string BuildSystemPrompt(MemoryBank bank, List<MemoryDirective> directives, string? context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are reflecting over an agent memory bank to answer a question grounded in evidence.");
        if (bank.Mission is { Length: > 0 } m)
            sb.AppendLine($"BANK MISSION: {m}");
        var persona = DispositionLine(bank.DispositionJson);
        if (persona is not null)
            sb.AppendLine(persona);
        if (directives.Count > 0)
        {
            sb.AppendLine("\nDIRECTIVES (always apply):");
            foreach (var d in directives)
                sb.AppendLine($"- {d.Content}");
        }
        sb.AppendLine("""
            TOOLS — read-only; gather evidence BEFORE answering:
            - search_observations(query): consolidated conclusions — try this layer FIRST.
            - recall_facts(query): raw retained facts — use when observations don't cover it.
            - done(answer, based_on): submit the answer citing the unit ids it relies on.
            """);
        sb.AppendLine("""
            RULES:
            - Ground every claim in retrieved units and cite their ids via done(based_on) — never invent ids.
            - When memory is insufficient, say what is missing instead of fabricating.
            - Keep the answer in the language of the question.
            """);
        if (context is { Length: > 0 } c)
            sb.AppendLine($"CONTEXT: {c}");
        return sb.ToString();
    }

    internal static string? DispositionLine(string? json)
    {
        if (json is not { Length: > 0 }) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var parts = new List<string>();
            if (Score(doc.RootElement, "skepticism") >= 4)
                parts.Add("skeptical — demand evidence before accepting claims");
            if (Score(doc.RootElement, "literalism") >= 4)
                parts.Add("literal — report exactly what memory says, no extrapolation");
            if (Score(doc.RootElement, "empathy") >= 4)
                parts.Add("empathetic — weigh the human context behind facts");
            return parts.Count > 0 ? $"PERSONA: You are {string.Join(", ", parts)}." : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Score(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : 3;

    private async Task<List<MemoryDirective>> LoadDirectivesAsync(
        MemoryBank bank, MemoryReflectRequest req, CancellationToken ct)
    {
        var all = await db.MemoryDirectives.AsNoTracking()
            .Where(d => d.BankId == bank.Id && d.IsActive)
            .OrderByDescending(d => d.Priority)
            .ToListAsync(ct);
        if (req.ApplyAllDirectives)
            return all;
        var reqTags = (req.Tags ?? []).Select(t => t.ToLowerInvariant()).ToHashSet();
        return all.Where(d =>
            d.Tags.Count == 0 ||
            (reqTags.Count > 0 && (req.TagsMatch == "all"
                ? d.Tags.All(t => reqTags.Contains(t.ToLowerInvariant()))
                : d.Tags.Any(t => reqTags.Contains(t.ToLowerInvariant())))))
            .ToList();
    }

    private int BudgetIterations(string budget) => budget switch
    {
        "low" => 3,
        "high" => config.ReflectMaxIterations(),
        _ => 6
    };

    // ---------------- citations ----------------

    /// <summary>RF-003: cited ids ∩ actually-retrieved ids; rest are dropped.</summary>
    internal static (List<ReflectCitation> Valid, List<Guid> Dropped) ValidateCitations(
        List<Guid> cited, Dictionary<Guid, (string Type, string Text)> retrieved)
    {
        var valid = new List<ReflectCitation>();
        var dropped = new List<Guid>();
        foreach (var id in cited.Distinct())
        {
            if (retrieved.TryGetValue(id, out var r))
                valid.Add(new ReflectCitation(r.Type, id, r.Text));
            else
                dropped.Add(id);
        }
        return (valid, dropped);
    }

    /// <summary>Extracts a trailing {"answer":?,"based_on":[...]} block from text.</summary>
    internal static List<Guid> ParseBasedOn(string text, out string? cleaned)
    {
        cleaned = null;
        var idx = text.LastIndexOf('{');
        if (idx < 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(text[idx..]);
            if (!doc.RootElement.TryGetProperty("based_on", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return [];
            var ids = arr.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => Guid.TryParse(s, out _))
                .Select(s => Guid.Parse(s!)).ToList();
            // An explicit "answer" field replaces the raw text as the answer.
            cleaned = doc.RootElement.TryGetProperty("answer", out var a)
                && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : text[..idx].TrimEnd();
            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ---------------- structured output (RF-004) ----------------

    private static async Task<(JsonElement?, string?)> ExtractStructuredAsync(
        IChatClient chat, string answer, JsonElement schema, CancellationToken ct)
    {
        var prompt = $$"""
            Extract a JSON object from ANSWER that conforms to SCHEMA. Reply with JSON only.

            SCHEMA:
            {{schema.GetRawText()}}

            ANSWER:
            {{answer}}
            """;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var response = await chat.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
                var raw = response.Text?.Trim();
                if (raw is { Length: > 0 } && raw.StartsWith("```"))
                    raw = raw[(raw.IndexOf('\n') + 1)..].TrimEnd('`', ' ', '\n');
                if (raw is { Length: > 0 })
                {
                    var start = raw.IndexOf('{');
                    var end = raw.LastIndexOf('}');
                    if (start >= 0 && end > start)
                    {
                        using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
                        return (doc.RootElement.Clone(), null);
                    }
                }
                prompt = $"The previous reply was not valid JSON. Reply ONLY with the JSON object matching this schema: {schema.GetRawText()}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == 1)
                    return (null, $"structured output extraction failed: {ex.Message}");
            }
        }
        return (null, "structured output extraction returned invalid JSON after retry");
    }

    // ---------------- helpers ----------------

    private static string? ArgString(AIFunctionArguments args, string name)
    {
        if (!args.TryGetValue(name, out var v) || v is null)
            return null;
        return v is JsonElement el && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : v.ToString();
    }

    private static IEnumerable<string> ArgStrings(object raw)
    {
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    yield return s;
        if (raw is IEnumerable<string> strs)
            foreach (var s in strs)
                yield return s;
    }

    /// <summary>Minimal delegate-backed <see cref="AIFunction"/> for the reflect loop.</summary>
    private sealed class DelegateAIFunction(
        string name, string description, JsonElement schema,
        Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> impl) : AIFunction
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => schema;
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            impl(arguments, cancellationToken);
    }
}
