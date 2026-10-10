using System.Text.Json;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Shared.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// REST façade over the live MCP tool catalog (SPEC-20260914-playground-tools
/// RF-002/RF-003): same IDynamicToolCatalog + handlers as tools/list/tools/call.
/// </summary>
public static class ToolsEndpoints
{
    public static RouteGroupBuilder MapToolsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tools");

        group.MapGet("/", ListToolsAsync);
        group.MapPost("/{name}", InvokeToolAsync);

        return group;
    }

    private static async Task<IResult> ListToolsAsync(
        IDynamicToolCatalog catalog, HttpContext http,
        Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
        ILoggerFactory lf, CancellationToken ct)
    {
        // Same per-request DTO projection as MCP tools/list — cache it per
        // (catalog version, caller scope); bumps/scope changes re-key.
        var scope = http.RequestServices.GetService<ICallerScopeProvider>() is { } sp
            ? await sp.GetAsync(ct)
            : CallerScope.Unrestricted;
        var version = http.RequestServices.GetRequiredService<IToolCatalogChangeNotifier>().Version;
        var tools = await Caching.EndpointCache.GetJsonAsync(cache,
            $"mcp:toolslist:rest:v{version}:{scope.Fingerprint}",
            async c => BuildToolList(await catalog.GetToolsAsync(http.RequestServices, c)),
            lf, ct);
        return Results.Ok(new ToolListResponse { Tools = tools ?? [] });
    }

    private static List<ToolDescriptorDto> BuildToolList(IReadOnlyList<CatalogTool> tools) =>
        tools.Select(t => new ToolDescriptorDto
        {
            Name = t.Name,
            Title = t.Title,
            Description = t.Description,
            InputSchema = JsonSerializer.SerializeToElement(t.InputSchema),
            OutputSchema = t.OutputSchema is { } os
                ? JsonSerializer.SerializeToElement(os) : null,
            ReadOnly = t.ReadOnly,
            DestructiveHint = t.DestructiveHint,
            IdempotentHint = t.IdempotentHint,
            OpenWorldHint = t.OpenWorldHint
        }).ToList();

    private static async Task<IResult> InvokeToolAsync(
        IDynamicToolCatalog catalog, HttpContext http, string name, CancellationToken ct)
    {
        var tool = (await catalog.GetToolsAsync(http.RequestServices, ct))
            .FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return await HandleUnavailableToolAsync(catalog, http, name, ct);

        // Per-key write gate: read-only credentials get an informative
        // isError instead of the write executing — the tool stays visible
        // in the list so clients can discover it.
        var callScope = http.RequestServices.GetService<ICallerScopeProvider>() is { } scopeProvider
            ? await scopeProvider.GetAsync(ct)
            : CallerScope.Unrestricted;
        if (!tool.ReadOnly && !callScope.AllowWrite)
        {
            await ScopeAudit.RecordToolDeniedAsync(http.RequestServices, name, ct);
            return ToolError($"tool '{name}' requires write access — this credential is read-only");
        }

        var arguments = await ReadArgumentsAsync(http, ct);
        var context = new ToolCallContext
        {
            Services = http.RequestServices,
            Arguments = arguments
        };

        var toolCache = http.RequestServices.GetService<Caching.IToolCacheService>();
        if (await TryGetCachedAsync(toolCache, name, tool.ReadOnly, arguments, ct) is { } cached)
            return Results.Ok(cached);

        var toolSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await tool.Handler(context, ct);
            if (toolCache is not null && toolCache.IsCacheable(name, tool.ReadOnly))
            {
                await toolCache.SetCachedResultAsync(name, arguments, result, ct);
            }
            return Results.Ok(result);
        }
        catch (McpProtocolException ex)
        {
            // MCP semantics: argument/validation errors are isError results,
            // not HTTP 500 — the Playground renders them in the result pane.
            return ToolError(ex.Message);
        }
        finally
        {
            Telemetry.KnowledgeHubMetrics.ToolDuration.Record(toolSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("tool", name));
        }
    }

    /// <summary>
    /// SPEC-20260923-source-authorization RF-004: scope-hidden tools
    /// get the same friendly isError as MCP tools/call; unknown
    /// names keep their 404.
    /// </summary>
    private static async Task<IResult> HandleUnavailableToolAsync(
        IDynamicToolCatalog catalog, HttpContext http, string name, CancellationToken ct)
    {
        var exists = (await catalog.GetUnfilteredToolsAsync(http.RequestServices, ct))
            .Any(t => t.Name == name);
        if (!exists)
            return Results.NotFound(new { error = $"unknown tool '{name}'" });

        await ScopeAudit.RecordToolDeniedAsync(http.RequestServices, name, ct);
        return ToolError($"tool '{name}' is not available for this credential");
    }

    private static async Task<CallToolResult?> TryGetCachedAsync(
        Caching.IToolCacheService? toolCache, string name, bool readOnly,
        Dictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (toolCache is null || !toolCache.IsCacheable(name, readOnly))
            return null;
        return await toolCache.GetCachedResultAsync(name, arguments, ct);
    }

    private static IResult ToolError(string message) =>
        Results.Ok(new CallToolResult
        {
            Content = [new TextContentBlock { Text = message }],
            IsError = true
        });

    private static async Task<Dictionary<string, JsonElement>?> ReadArgumentsAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is null or 0)
            return null;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: ct);
        if (body.ValueKind is not JsonValueKind.Object)
            return null;
        return body.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
