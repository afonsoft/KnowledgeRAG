using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Flows;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// /api/flows — UI-defined agent flow management (CookieSession admin) +
/// run surface (Operational, parity with /api/agent). POST /run accepts
/// <c>?stream=1</c> for SSE step events. /api/flowruns/{id} fetches a run.
/// </summary>
public static class FlowEndpoints
{
    public static RouteGroupBuilder MapFlowsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/flows");

        group.MapGet("/", async (bool? enabledOnly, FlowService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(enabledOnly == true, ct)))
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapGet("/{id:guid}", async (Guid id, FlowService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } detail
                ? Results.Ok(detail)
                : Results.NotFound(new { error = "flow not found" }))
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapPost("/", async (CreateFlowRequest request, FlowService svc, CancellationToken ct) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                    return Results.BadRequest(new { error = "name is required" });
                var detail = await svc.CreateAsync(request, ct);
                return Results.Created($"/api/flows/{detail!.Flow.Id}", detail);
            }
            catch (FlowStepException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization(AuthPolicies.CookieSession);

        group.MapPut("/{id:guid}", async (Guid id, UpdateFlowRequest request, FlowService svc, CancellationToken ct) =>
        {
            try
            {
                return await svc.UpdateAsync(id, request, ct) is { } detail
                    ? Results.Ok(detail)
                    : Results.NotFound(new { error = "flow not found" });
            }
            catch (FlowStepException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization(AuthPolicies.CookieSession);

        group.MapDelete("/{id:guid}", async (Guid id, FlowService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "flow not found" }))
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapPost("/validate", (ValidateFlowRequest request, FlowService svc) =>
            svc.ValidateDefinition(request.Definition) is { } error
                ? Results.BadRequest(new { error })
                : Results.Ok(new { valid = true }))
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapPost("/{id:guid}/run", RunAsync)
            .RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting("llm");

        group.MapGet("/{id:guid}/runs", async (Guid id, int? take, FlowService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListRunsAsync(id, take ?? 50, ct)))
            .RequireAuthorization(AuthPolicies.CookieSession);

        app.MapGet("/api/flowruns/{runId:guid}", async (Guid runId, FlowService svc, CancellationToken ct) =>
            await svc.GetRunAsync(runId, ct) is { } run
                ? Results.Ok(run)
                : Results.NotFound(new { error = "run not found" }))
            .RequireAuthorization(AuthPolicies.CookieSession);

        return group;
    }

    private static async Task RunAsync(
        HttpContext http, Guid id, FlowRunRequest request, FlowService svc,
        CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<Data.KnowledgeHubDbContext>();
        var flow = await db.AgentFlows.FindAsync([id], ct);
        if (flow is null)
        {
            http.Response.StatusCode = 404;
            await http.Response.WriteAsJsonAsync(new { error = "flow not found" }, ct);
            return;
        }
        if (!flow.Enabled)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "flow is disabled" }, ct);
            return;
        }

        var apiKeyId = GetApiKeyId(http);

        if (http.Request.Query["stream"] == "1")
        {
            await RunStreamingAsync(http, svc, flow, request, apiKeyId, ct);
            return;
        }

        try
        {
            var result = await svc.RunAsync(flow, request.Inputs, http.RequestServices, apiKeyId, null, ct);
            http.Response.StatusCode = result.Status == "done" ? 200 : 422;
            await http.Response.WriteAsJsonAsync(result, SharedJson.Options, ct);
        }
        catch (FlowAbortException ex)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
        }
    }

    private static async Task RunStreamingAsync(
        HttpContext http, FlowService svc, Domain.Entities.AgentFlow flow,
        FlowRunRequest request, Guid? apiKeyId, CancellationToken ct)
    {
        http.Response.StatusCode = 200;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        async ValueTask WriteAsync(string type, JsonNode? data, CancellationToken wct)
        {
            var payload = JsonSerializer.Serialize(data, SharedJson.Options);
            await http.Response.WriteAsync($"event: {type}\ndata: {payload}\n\n", wct);
            await http.Response.Body.FlushAsync(wct);
        }

        try
        {
            var result = await svc.RunAsync(
                flow, request.Inputs, http.RequestServices, apiKeyId,
                sink: (e, wct) => WriteAsync(e.Type, e.Data, wct), ct);
            await WriteAsync(
                result.Status == "done" ? FlowStreamEvent.Done : FlowStreamEvent.Error,
                JsonSerializer.SerializeToNode(result, SharedJson.Options),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteAsync(FlowStreamEvent.Error, JsonValue.Create(new { error = ex.Message }), CancellationToken.None);
        }
    }

    private static Guid? GetApiKeyId(HttpContext http) =>
        http.User.FindFirst(ApiKeyAuthenticationHandler.AuthMethodClaim)?.Value == "apikey"
        && Guid.TryParse(http.User.FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value, out var id)
            ? id
            : null;
}
