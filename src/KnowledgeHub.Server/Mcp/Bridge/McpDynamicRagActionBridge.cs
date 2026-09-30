using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Shared.Contracts;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp.Bridge;

/// <summary>
/// Action-Augmented RAG orchestrator
/// (SPEC-20260927-mcp-dynamic-rag-action-bridge RF-001/RF-003): runs the
/// live-tool nominations detected in retrieved context inside the SAME
/// security scope as the caller — the tool list must already be
/// caller-scope filtered (see <see cref="IDynamicToolCatalog.GetToolsAsync"/>),
/// and only read-only tools are eligible.
/// </summary>
public static class McpDynamicRagActionBridge
{
    private const int OutputPreviewChars = 4000;

    /// <summary>
    /// Executes up to <paramref name="maxCalls"/> nominations. Marker args are
    /// used verbatim; arg-less nominations are dispatched only when the tool
    /// schema has a single obvious text parameter (<c>query</c>/<c>question</c>)
    /// which is filled with the user's question — anything else is skipped so
    /// no argument is ever invented.
    /// </summary>
    public static async Task<IReadOnlyList<LiveToolExecution>> ExecuteAsync(
        string question,
        IReadOnlyList<SearchResultItem> results,
        IReadOnlyList<CatalogTool> visibleTools,
        ToolCallContext ctx,
        int maxCalls,
        bool allowDocumentMarkers,
        CancellationToken ct)
    {
        var nominations = ToolActionAnnotationDetector.Detect(
            question, results, visibleTools, maxCalls, allowDocumentMarkers);
        if (nominations.Count == 0)
            return [];

        // SPEC-20260928-observability-followups RF-002/RF-003: one span per
        // bridge run + a counter per execution (tool, outcome).
        using var span = Telemetry.KnowledgeHubActivity.Start("search.live_actions");
        span?.SetTag("nominations", nominations.Count);
        var byName = visibleTools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var executions = new List<LiveToolExecution>();

        // RF-003: hard ceiling — the 3rd execution finalizes the chain.
        foreach (var nomination in nominations.Take(Math.Max(0, maxCalls)))
        {
            ct.ThrowIfCancellationRequested();
            if (!byName.TryGetValue(nomination.ToolName, out var tool))
                continue;

            var args = ResolveArgs(nomination, tool, question);
            if (args is null)
                continue; // required params we cannot fill — never invent data

            var timestamp = DateTimeOffset.UtcNow;
            CallToolResult result;
            try
            {
                result = await tool.Handler(new ToolCallContext
                {
                    Services = ctx.Services,
                    Arguments = args,
                    ConversationContext = ctx.ConversationContext
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Telemetry.KnowledgeHubActivity.Fail(span, ex);
                result = new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"ERROR: {ex.Message}" }],
                    IsError = true
                };
            }

            // SPEC-20260929-observability-and-tests-residual RF-002: a failed
            // live call returns IsError in-band (no exception) — the span must
            // still read as an error or failures look like successes.
            if (result.IsError == true)
                span?.SetStatus(ActivityStatusCode.Error, $"live tool {tool.Name} failed");

            Telemetry.KnowledgeHubMetrics.LiveToolExecutions.Add(1,
                new KeyValuePair<string, object?>("tool", tool.Name),
                new KeyValuePair<string, object?>("outcome",
                    result.IsError == true ? "error" : "success"));

            var text = string.Join("\n",
                result.Content.Select(SummarizeBlock));
            executions.Add(new LiveToolExecution
            {
                ToolName = tool.Name,
                TimestampUtc = timestamp,
                ArgsSummary = SummarizeArgs(args),
                IsError = result.IsError == true,
                OutputPreview = text.Length <= OutputPreviewChars
                    ? text : text[..OutputPreviewChars] + "…"
            });
        }
        return executions;
    }

    /// <summary>
    /// Textual preview of a result block — non-text payloads (images, embedded
    /// resources, resource links) surface as placeholders so they are visible
    /// in the answer instead of silently dropped
    /// (SPEC-20260929-live-actions-bridge-hardening RF-006).
    /// </summary>
    private static string SummarizeBlock(ContentBlock block) => block switch
    {
        TextContentBlock t => t.Text,
        ImageContentBlock i => $"[image: {i.MimeType ?? "unknown"}]",
        AudioContentBlock a => $"[audio: {a.MimeType ?? "unknown"}]",
        EmbeddedResourceBlock e => $"[embedded resource: {e.Resource.Uri}]",
        ResourceLinkBlock r => $"[resource: {r.Name} ({r.Uri})]",
        _ => $"[{block.GetType().Name}]"
    };

    /// <summary>Args for a nomination. Marker-supplied args are used verbatim
    /// but every required parameter must be present and non-empty — a partial
    /// marker like <c>query=""</c> never dispatches a meaningless call
    /// (SPEC-20260929-live-actions-bridge-hardening RF-006). For an arg-less
    /// nomination: the tool's single required string param must be a
    /// query-shaped name — otherwise skip.</summary>
    private static IDictionary<string, JsonElement>? ResolveArgs(
        ToolActionAnnotation nomination, CatalogTool tool, string question) =>
        nomination.Args.Count > 0
            ? ValidateMarkerArgs(nomination.Args, tool)
            : ArglessNominationArgs(tool, question);

    /// <summary>Marker-supplied args are valid only when every schema-required
    /// param is present with a non-blank value (RF-006).</summary>
    private static IDictionary<string, JsonElement>? ValidateMarkerArgs(
        IReadOnlyDictionary<string, JsonElement> supplied, CatalogTool tool)
    {
        var markerArgs = new Dictionary<string, JsonElement>(supplied);
        if (tool.InputSchema.TryGetPropertyValue("required", out var markerReq)
            && markerReq is JsonArray requiredParams)
        {
            foreach (var param in requiredParams)
            {
                if (param?.GetValue<string>() is not { } name)
                    continue;
                if (!markerArgs.TryGetValue(name, out var value)
                    || (value.ValueKind == JsonValueKind.String
                        && string.IsNullOrWhiteSpace(value.GetString())))
                    return null;
            }
        }
        return markerArgs;
    }

    /// <summary>Arg-less nomination: the tool's single required string param
    /// must be a query-shaped name — otherwise skip.</summary>
    private static IDictionary<string, JsonElement>? ArglessNominationArgs(
        CatalogTool tool, string question)
    {
        if (tool.InputSchema.TryGetPropertyValue("required", out var req)
            && req is JsonArray { Count: 1 } arr
            && arr[0]?.GetValue<string>() is { } single
            && single is "query" or "question" or "input" or "prompt")
        {
            return new Dictionary<string, JsonElement>
            {
                [single] = JsonSerializer.SerializeToElement(question)
            };
        }
        return null;
    }

    private static string SummarizeArgs(IDictionary<string, JsonElement> args)
    {
        var json = JsonSerializer.Serialize(
            args.ToDictionary(kv => kv.Key, kv => kv.Value), JsonSerializerOptions.Web);
        return json.Length <= 200 ? json : json[..200] + "…";
    }
}
