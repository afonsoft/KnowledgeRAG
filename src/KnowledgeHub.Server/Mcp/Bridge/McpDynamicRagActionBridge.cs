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
        CancellationToken ct)
    {
        var nominations = ToolActionAnnotationDetector.Detect(
            question, results, visibleTools, maxCalls);
        if (nominations.Count == 0)
            return [];

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
                result = new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"ERROR: {ex.Message}" }],
                    IsError = true
                };
            }

            var text = string.Join("\n",
                result.Content.OfType<TextContentBlock>().Select(b => b.Text));
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

    /// <summary>Args for an arg-less nomination: the tool's single required
    /// string param must be a query-shaped name — otherwise skip.</summary>
    private static IDictionary<string, JsonElement>? ResolveArgs(
        ToolActionAnnotation nomination, CatalogTool tool, string question)
    {
        if (nomination.Args.Count > 0)
            return new Dictionary<string, JsonElement>(nomination.Args);

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
