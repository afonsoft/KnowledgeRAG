namespace KnowledgeHub.Server.Api;

/// <summary>
/// SPEC-20260926-ops-and-ui-polish: anonymous MCP capabilities probe — the
/// login page advertises the legacy SSE transport only when it is actually
/// served (disabled under <c>Mcp:SessionMode=Stateless</c>).
/// </summary>
public static class McpInfoEndpoints
{
    public static RouteGroupBuilder MapMcpInfoApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mcp").WithTags("MCP");

        group.MapGet("/capabilities", async (IConfiguration cfg,
            Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            // Global config-derived payload — L1 hit is a live object.
            var payload = await Caching.EndpointCache.GetJsonAsync(cache,
                "mcp:capabilities", c =>
                {
                    var mode = cfg["Mcp:SessionMode"] ?? "StatefulForInitializeClients";
                    var legacySse = !mode.Equals("Stateless", StringComparison.OrdinalIgnoreCase);
                    return Task.FromResult<McpCapabilities?>(new(mode, legacySse));
                }, lf, ct);
            return Results.Ok(payload);
        }).AllowAnonymous();

        return group;
    }

    private sealed record McpCapabilities(string SessionMode, bool LegacySse);
}
