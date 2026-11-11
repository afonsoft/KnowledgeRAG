using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Memory;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Api;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-002/RF-003: REST mirror of the
/// memory_* MCP tools — same handlers via <see cref="IDynamicToolCatalog"/>,
/// same ProblemDetails contract as ContextEndpoints.
/// </summary>
public static class MemoryEndpoints
{
    public static RouteGroupBuilder MapMemoryApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memory");

        group.MapPost("/retain", async (HttpContext http, CancellationToken ct) =>
                await Invoke(http, "memory_retain", await BodyAsync(http, ct), ct))
            .RequireRateLimiting("llm");
        group.MapPost("/recall", async (HttpContext http, CancellationToken ct) =>
            await Invoke(http, "memory_recall", await BodyAsync(http, ct), ct));

        group.MapGet("/units", (HttpContext http, CancellationToken ct) =>
            Invoke(http, "memory_list", Query(http), ct));
        group.MapGet("/units/{id:guid}", (HttpContext http, Guid id, CancellationToken ct) =>
            Invoke(http, "memory_get", Merge(Query(http), "id", id.ToString()), ct));
        group.MapPatch("/units/{id:guid}", async (HttpContext http, Guid id, CancellationToken ct) =>
            await Invoke(http, "memory_update", Merge(await BodyAsync(http, ct) ?? new JsonObject(), "id", id.ToString()), ct));
        group.MapDelete("/units/{id:guid}", (HttpContext http, Guid id, CancellationToken ct) =>
            Invoke(http, "memory_delete", Merge(Query(http), "id", id.ToString(), ("confirm", true)), ct));

        group.MapGet("/banks", (HttpContext http, CancellationToken ct) =>
            Invoke(http, "memory_bank_list", new JsonObject(), ct));
        group.MapPost("/banks", async (HttpContext http, CancellationToken ct) =>
            await Invoke(http, "memory_bank_create", await BodyAsync(http, ct), ct));
        group.MapPatch("/banks/{key}", async (HttpContext http, string key, CancellationToken ct) =>
            await Invoke(http, "memory_bank_update", Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key), ct));
        group.MapDelete("/banks/{key}", (HttpContext http, string key, CancellationToken ct) =>
            Invoke(http, "memory_bank_delete", Merge(new JsonObject(), "bankKey", key, ("confirm", true)), ct));
        group.MapPost("/banks/{key}/consolidate", async (HttpContext http, string key, CancellationToken ct) =>
            await Invoke(http, "memory_consolidate",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key), ct))
            .RequireRateLimiting("llm");
        group.MapGet("/observations/{id:guid}/history", (HttpContext http, Guid id, CancellationToken ct) =>
            Invoke(http, "memory_observation_history",
                Merge(Query(http), "observationId", id.ToString()), ct));

        // SPEC-20261111-memory-reflect-directives RF-005.
        group.MapPost("/banks/{key}/reflect", async (HttpContext http, string key, CancellationToken ct) =>
            await Invoke(http, "memory_reflect",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key), ct))
            .RequireRateLimiting("llm");
        group.MapGet("/banks/{key}/directives", (HttpContext http, string key, CancellationToken ct) =>
            Invoke(http, "memory_directives_list",
                Merge(Query(http), "bankKey", key), ct));
        group.MapPost("/banks/{key}/directives", async (HttpContext http, string key, CancellationToken ct) =>
            await Invoke(http, "memory_directives_create",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key), ct));
        group.MapPatch("/banks/{key}/directives/{id:guid}", async (HttpContext http, string key, Guid id, CancellationToken ct) =>
            await Invoke(http, "memory_directives_update",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key, ("id", id.ToString())), ct));
        group.MapDelete("/banks/{key}/directives/{id:guid}", (HttpContext http, string key, Guid id, CancellationToken ct) =>
            Invoke(http, "memory_directives_delete",
                Merge(new JsonObject(), "bankKey", key, ("id", id.ToString()), ("confirm", true)), ct));

        // SPEC-20261111-mental-models-pages RF-004.
        group.MapGet("/banks/{key}/mental-models", (HttpContext http, string key, CancellationToken ct) =>
            Invoke(http, "memory_mental_models_list", Merge(Query(http), "bankKey", key), ct));
        group.MapPost("/banks/{key}/mental-models", async (HttpContext http, string key, CancellationToken ct) =>
            await Invoke(http, "memory_mental_models_create",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key), ct))
            .RequireRateLimiting("llm");
        group.MapGet("/banks/{key}/mental-models/{id:guid}", (HttpContext http, string key, Guid id, CancellationToken ct) =>
            Invoke(http, "memory_mental_models_get",
                Merge(Query(http), "bankKey", key, ("id", id.ToString())), ct));
        group.MapPatch("/banks/{key}/mental-models/{id:guid}", async (HttpContext http, string key, Guid id, CancellationToken ct) =>
            await Invoke(http, "memory_mental_models_update",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key, ("id", id.ToString())), ct));
        group.MapDelete("/banks/{key}/mental-models/{id:guid}", (HttpContext http, string key, Guid id, CancellationToken ct) =>
            Invoke(http, "memory_mental_models_delete",
                Merge(new JsonObject(), "bankKey", key, ("id", id.ToString()), ("confirm", true)), ct));
        group.MapPost("/banks/{key}/mental-models/{id:guid}/refresh", async (HttpContext http, string key, Guid id, CancellationToken ct) =>
            await Invoke(http, "memory_mental_model_refresh",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key, ("id", id.ToString())), ct))
            .RequireRateLimiting("llm");
        group.MapGet("/banks/{key}/pages/tree", (HttpContext http, string key, CancellationToken ct) =>
            Invoke(http, "memory_pages_tree", Merge(Query(http), "bankKey", key), ct));
        group.MapGet("/banks/{key}/pages/search", (HttpContext http, string key, CancellationToken ct) =>
            Invoke(http, "memory_pages_search", Merge(Query(http), "bankKey", key), ct));
        group.MapGet("/banks/{key}/pages/{id:guid}", (HttpContext http, string key, Guid id, CancellationToken ct) =>
            Invoke(http, "memory_page_get",
                Merge(Query(http), "bankKey", key, ("id", id.ToString())), ct));
        group.MapPost("/banks/{key}/pages", async (HttpContext http, string key, CancellationToken ct) =>
            await Invoke(http, "memory_page_create",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key), ct))
            .RequireRateLimiting("llm");
        group.MapPatch("/banks/{key}/pages/{id:guid}", async (HttpContext http, string key, Guid id, CancellationToken ct) =>
            await Invoke(http, "memory_page_update",
                Merge(await BodyAsync(http, ct) ?? new JsonObject(), "bankKey", key, ("id", id.ToString())), ct));
        group.MapDelete("/banks/{key}/pages/{id:guid}", (HttpContext http, string key, Guid id, CancellationToken ct) =>
            Invoke(http, "memory_page_delete",
                Merge(new JsonObject(), "bankKey", key, ("id", id.ToString()), ("confirm", true)), ct));

        return group;
    }

    private static async Task<IResult> Invoke(HttpContext http, string tool,
        JsonObject? body, CancellationToken ct)
    {
        var catalog = http.RequestServices.GetRequiredService<IDynamicToolCatalog>();
        var found = (await catalog.GetToolsAsync(http.RequestServices, ct))
            .FirstOrDefault(t => t.Name == tool);
        if (found is null)
            return Results.Problem(statusCode: 500, title: "tool_unavailable",
                detail: $"tool '{tool}' is not registered");
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (body is not null)
            foreach (var prop in body)
                if (prop.Value is not null)
                    args[prop.Key] = JsonSerializer.SerializeToElement(prop.Value);
        var ctx = new ToolCallContext
        {
            Services = http.RequestServices,
            Arguments = args,
            Transport = "rest"
        };
        try
        {
            var result = await found.Handler(ctx, ct);
            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
            if (result.IsError == true)
                return Results.Problem(statusCode: 400, title: "memory_error", detail: text);
            var payload = result.StructuredContent is { } el
                ? el.Deserialize<JsonObject>() ?? new JsonObject()
                : new JsonObject();
            return Results.Ok(payload);
        }
        catch (McpProtocolException ex)
        {
            return Results.Problem(statusCode: 400, title: "invalid_argument", detail: ex.Message);
        }
        catch (MemoryNotFoundException ex)
        {
            return Results.Problem(statusCode: 404, title: "memory_not_found", detail: ex.Message);
        }
        catch (MemoryConflictException ex)
        {
            return Results.Problem(statusCode: 409, title: "memory_conflict", detail: ex.Message);
        }
        catch (Memory.MemoryDefenseException ex)
        {
            // SPEC-20261111-memory-defense RF-003: blocked item → 422 with
            // pattern/count findings only (never spans or values).
            return Results.Json(new
            {
                blocked = true,
                findings = ex.Findings.Select(f => new { pattern = f.Pattern, count = f.Count })
            }, statusCode: 422);
        }
        catch (MemoryBadRequestException ex)
        {
            return Results.Problem(statusCode: 400, title: "memory_bad_request", detail: ex.Message);
        }
        catch (MemoryServiceUnavailableException ex)
        {
            return Results.Problem(statusCode: 503, title: "memory_llm_unavailable", detail: ex.Message);
        }
    }

    private static async Task<JsonObject?> BodyAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is null or 0)
            return null;
        return await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject;
    }

    private static JsonObject Query(HttpContext http)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in http.Request.Query)
            if (value.ToString() is { Length: > 0 } v)
                obj[key] = JsonValue.Create(v);
        return obj;
    }

    private static JsonObject Merge(JsonObject obj, string key, string value, params (string K, object V)[] extras)
    {
        obj[key] = value;
        foreach (var (k, v) in extras)
            obj[k] = v switch
            {
                bool b => JsonValue.Create(b),
                string s => JsonValue.Create(s),
                _ => JsonValue.Create(v.ToString())
            };
        return obj;
    }
}
