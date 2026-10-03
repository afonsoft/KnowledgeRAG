using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Shared;

namespace KnowledgeHub.Client.Services;

/// <summary>
/// Typed client for the hub's own A2A surface (SPEC-20261001-a2a-task-durability):
/// the anonymous Agent Card at /.well-known/agent-card.json and the authenticated
/// JSON-RPC binding at /a2a (SendMessage / GetTask). The shared HttpClient flows
/// the session cookie, which satisfies the Operational policy the same way an
/// aft_* Bearer key does.
/// </summary>
public sealed class A2aApiClient(HttpClient http)
{
    /// <summary>Fetches the Agent Card — anonymous endpoint, safe to call pre-login.</summary>
    public async Task<JsonElement> GetAgentCardAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<JsonElement>(".well-known/agent-card.json", SharedJson.Options, ct);

    /// <summary>Delegates a text message to a skill; returns the created/updated Task JSON.</summary>
    public async Task<JsonElement> SendMessageAsync(
        string skill, string text, string? contextId = null, CancellationToken ct = default)
    {
        // JsonObject instead of anonymous types: trimmed WASM has no reflection-based
        // serializer, and JsonNode is handled natively by STJ.
        var result = await RpcAsync("SendMessage", new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "ROLE_USER",
                ["messageId"] = Guid.NewGuid().ToString("N"),
                ["contextId"] = contextId,
                ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
                ["metadata"] = new JsonObject { ["skill"] = skill }
            }
        }, ct);
        // Send* returns the union {task: {...}} — normalize to the task itself.
        return result.TryGetProperty("task", out var task) ? task : result;
    }

    /// <summary>Reads a task by id (durable — survives hub restarts).</summary>
    public Task<JsonElement> GetTaskAsync(string taskId, CancellationToken ct = default)
        => RpcAsync("GetTask", new JsonObject { ["id"] = taskId }, ct);

    /// <summary>Cancels a task by id; returns the updated Task JSON.</summary>
    public Task<JsonElement> CancelTaskAsync(string taskId, CancellationToken ct = default)
        => RpcAsync("CancelTask", new JsonObject { ["id"] = taskId }, ct);

    /// <summary>Extracts the task's current state string (TASK_STATE_*), or null.</summary>
    public static string? TaskState(JsonElement task) =>
        task.ValueKind == JsonValueKind.Object &&
        task.TryGetProperty("status", out var s) && s.TryGetProperty("state", out var st)
            ? st.GetString()
            : null;

    /// <summary>Terminal states after which polling can stop.</summary>
    public static bool IsTerminal(string? state) =>
        state is "TASK_STATE_COMPLETED" or "TASK_STATE_FAILED" or "TASK_STATE_CANCELED" or "TASK_STATE_REJECTED";

    private int _nextId;

    private async Task<JsonElement> RpcAsync(string method, JsonObject @params, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("a2a", new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _nextId),
            ["method"] = method,
            ["params"] = @params
        }, SharedJson.Options, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(SharedJson.Options, ct);
        if (body.TryGetProperty("error", out var error))
            throw new InvalidOperationException(
                $"A2A {method} falhou: {error.GetRawText()}");
        return body.GetProperty("result");
    }
}
