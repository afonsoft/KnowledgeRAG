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
    private const string FlowNotFound = "flow not found";

    public static RouteGroupBuilder MapFlowsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/flows");

        MapFlowCrudEndpoints(group);
        MapTriggerEndpoints(group);
        MapRunSurfaceEndpoints(app);

        return group;
    }

    /// <summary>CRUD + validate + run endpoints under /api/flows.</summary>
    private static void MapFlowCrudEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/", async (bool? enabledOnly, FlowService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(enabledOnly == true, ct)))
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapGet("/{id:guid}", async (Guid id, FlowService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } detail
                ? Results.Ok(detail)
                : Results.NotFound(new { error = FlowNotFound }))
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
                    : Results.NotFound(new { error = FlowNotFound });
            }
            catch (FlowStepException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization(AuthPolicies.CookieSession);

        group.MapDelete("/{id:guid}", async (Guid id, FlowService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = FlowNotFound }))
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapPost("/validate", (ValidateFlowRequest request, FlowService svc) =>
            FlowService.ValidateDefinition(request.Definition) is { } error
                ? Results.BadRequest(new { error })
                : Results.Ok(new { valid = true }))
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapPost("/{id:guid}/run", RunAsync)
            .RequireAuthorization(AuthPolicies.Operational).RequireRateLimiting("llm");

        group.MapGet("/{id:guid}/runs", async (Guid id, int? take, FlowService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListRunsAsync(id, take ?? 50, ct)))
            .RequireAuthorization(AuthPolicies.CookieSession);
    }

    /// <summary>Run inspection/resume + the anonymous inbound webhook.</summary>
    private static void MapRunSurfaceEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/flowruns/{runId:guid}", async (Guid runId, FlowService svc, CancellationToken ct) =>
            await svc.GetRunAsync(runId, ct) is { } run
                ? Results.Ok(run)
                : Results.NotFound(new { error = "run not found" }))
            .RequireAuthorization(AuthPolicies.CookieSession);

        // Resume a run suspended on an approval step (admin).
        app.MapPost("/api/flowruns/{runId:guid}/resume", async (Guid runId, HttpContext http, FlowService svc, CancellationToken ct) =>
        {
            try
            {
                var db = http.RequestServices.GetRequiredService<Data.KnowledgeHubDbContext>();
                var run = await db.FlowRuns.FindAsync([runId], ct);
                if (run?.PendingApprovalId is not { } approvalId)
                    return Results.NotFound(new { error = "run not found or not waiting" });
                var result = await svc.ResumeByApprovalAsync(approvalId, http.RequestServices, ct);
                return Results.Ok(result);
            }
            catch (ConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).RequireAuthorization(AuthPolicies.CookieSession);

        // Inbound webhook: unauthenticated — the fwt_ path token IS the
        // credential (rate-limited like the operational run surface).
        app.MapPost("/api/flowtriggers/{token}", async (string token, HttpContext http, FlowService svc, CancellationToken ct) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<JsonObject>(http.Request.Body, cancellationToken: ct);
                var result = await svc.InvokeWebhookAsync(token, body, http.RequestServices, ct);
                if (result is null)
                    return Results.NotFound(new { error = "trigger not found or disabled" });
                if (result.Status == "waiting_approval")
                    return Results.Accepted($"/api/flowruns/{result.RunId}", result);
                return result.Status == "done"
                    ? Results.Ok(result)
                    : Results.UnprocessableEntity(result);
            }
            catch (FlowStepException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (FlowAbortException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).AllowAnonymous().RequireRateLimiting("llm");
    }

    /// <summary>Trigger CRUD under /api/flows (CookieSession admin).</summary>
    private static void MapTriggerEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/triggers", async (Guid id, HttpRequest http, FlowService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListTriggersAsync(id, http, ct)))
            .RequireAuthorization(AuthPolicies.CookieSession);

        group.MapPost("/{id:guid}/triggers", async (Guid id, CreateFlowTriggerRequest request, HttpRequest http, FlowService svc, CancellationToken ct) =>
        {
            try
            {
                return await svc.CreateTriggerAsync(id, request, http, ct) is { } dto
                    ? Results.Created($"/api/flowtriggers/{dto.Id}", dto)
                    : Results.NotFound(new { error = FlowNotFound });
            }
            catch (FlowStepException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization(AuthPolicies.CookieSession);

        group.MapPut("/triggers/{triggerId:guid}", async (Guid triggerId, UpdateFlowTriggerRequest request, HttpRequest http, FlowService svc, CancellationToken ct) =>
        {
            try
            {
                return await svc.UpdateTriggerAsync(triggerId, request, http, ct) is { } dto
                    ? Results.Ok(dto)
                    : Results.NotFound(new { error = "trigger not found" });
            }
            catch (FlowStepException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization(AuthPolicies.CookieSession);

        group.MapDelete("/triggers/{triggerId:guid}", async (Guid triggerId, FlowService svc, CancellationToken ct) =>
            await svc.DeleteTriggerAsync(triggerId, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "trigger not found" }))
            .RequireAuthorization(AuthPolicies.CookieSession);
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
            // waiting_approval → 202: the run is parked on an approval,
            // not failed — the caller resolves it and calls /resume.
            http.Response.StatusCode = result.Status switch
            {
                "done" => 200,
                "waiting_approval" => 202,
                _ => 422,
            };
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
