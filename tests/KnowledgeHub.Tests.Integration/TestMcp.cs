using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KnowledgeHub.Tests.Integration;

/// <summary>Shared JSON-RPC/Streamable-HTTP plumbing for the MCP test clients.</summary>
internal static class McpJsonRpc
{
    public static string RequestBody(int id, string method, object? parameters) =>
        parameters is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{JsonSerializer.Serialize(parameters)}}}""";

    public static HttpRequestMessage BuildPost(string body, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        configure?.Invoke(request);
        return request;
    }

    /// <summary>Extracts the JSON-RPC result from a plain or SSE payload;
    /// throws on a JSON-RPC error frame.</summary>
    public static JsonElement ExtractResult(string payload)
    {
        var data = ExtractLastMessage(payload);
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"JSON-RPC error: {err.GetRawText()}");
        return doc.RootElement.GetProperty("result").Clone();
    }

    public static string ExtractLastMessage(string payload)
    {
        if (!payload.Contains("data:"))
            return payload;
        string? last = null;
        foreach (var line in payload.Split('\n'))
            if (line.StartsWith("data:"))
                last = line[5..].Trim();
        return last ?? payload;
    }
}

/// <summary>Minimal MCP client over Streamable HTTP (/mcp) shared by integration tests.</summary>
public sealed class TestMcp : IAsyncDisposable
{
    private readonly HttpClient _http;
    private string? _sessionId;
    private int _nextId = 1;

    public TestMcp(HttpClient http) => _http = http;

    public static Task<TestMcp> ConnectAsync(WebApplicationFactory<Program> factory) =>
        ConnectAsync(factory, capabilities: new { });

    /// <summary>ConnectAsync with explicit client capabilities — elicitation/tasks
    /// declarations drive the MRTR + Tasks code paths (SPEC-20260926-mcp-sdk-alignment).</summary>
    public static async Task<TestMcp> ConnectAsync(
        WebApplicationFactory<Program> factory, object capabilities)
    {
        // SPEC-20260914-auth-login: /mcp requires auth — default to the seeded
        // admin cookie session; callers needing a specific principal pass a client.
        var client = new TestMcp(await TestAuth.LoginAsync(factory));
        var init = await client.SendAsync("initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities,
            clientInfo = new { name = "test", version = "1.0" }
        });
        Assert.Equal("knowledge", init.GetProperty("serverInfo").GetProperty("name").GetString());
        await client.NotifyAsync("notifications/initialized");
        return client;
    }

    public static HashSet<string> ToolNames(JsonElement toolsList) =>
        toolsList.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToHashSet();

    public async Task<JsonElement> SendAsync(string method, object? parameters = null)
    {
        var id = _nextId++;
        using var request = McpJsonRpc.BuildPost(McpJsonRpc.RequestBody(id, method, parameters), WithSessionHeaders);

        using var response = await _http.SendAsync(request);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids))
            _sessionId = ids.First();
        response.EnsureSuccessStatusCode();

        return McpJsonRpc.ExtractResult(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Sends a request and answers server→client requests (e.g.
    /// <c>elicitation/create</c> — the stateful in-band MRTR fulfillment) until
    /// the final response arrives. <paramref name="answer"/> gets (method,
    /// params) and returns the result object to send back.
    /// </summary>
    public async Task<JsonElement> SendInteractiveAsync(
        string method, object? parameters,
        Func<string, JsonElement, object?> answer)
    {
        var id = _nextId++;
        using var request = McpJsonRpc.BuildPost(McpJsonRpc.RequestBody(id, method, parameters), WithSessionHeaders);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data:"))
                continue;
            using var doc = JsonDocument.Parse(line[5..].Trim());
            var root = doc.RootElement;

            // Server→client request (has method + id): answer it and keep reading.
            if (root.TryGetProperty("method", out var srvMethod) && root.TryGetProperty("id", out var srvId))
            {
                var reply = answer(srvMethod.GetString()!, root.GetProperty("params"));
                if (reply is not null)
                    await RespondAsync(srvId.GetRawText(), reply);
                continue;
            }

            if (root.TryGetProperty("id", out var myId) && myId.GetRawText() == id.ToString())
            {
                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException($"JSON-RPC error: {err.GetRawText()}");
                return root.GetProperty("result").Clone();
            }
        }
        throw new InvalidOperationException("stream ended without a result");
    }

    /// <summary>Posts a JSON-RPC response to a server→client request on this session.</summary>
    private async Task RespondAsync(string rawId, object result)
    {
        var body = $$"""{"jsonrpc":"2.0","id":{{rawId}},"result":{{JsonSerializer.Serialize(result)}}}""";
        using var request = McpJsonRpc.BuildPost(body, WithSessionHeaders);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task NotifyAsync(string method)
    {
        using var request = McpJsonRpc.BuildPost(
            $$"""{"jsonrpc":"2.0","method":"{{method}}"}""", WithSessionHeaders);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private void WithSessionHeaders(HttpRequestMessage request)
    {
        if (_sessionId is null)
            return;
        request.Headers.Add("Mcp-Session-Id", _sessionId);
        request.Headers.Add("MCP-Protocol-Version", "2025-03-26");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Minimal MCP client for the 2026-07-28 stateless era (SPEC-20260926-mcp-sdk-alignment):
/// no initialize/session — every request carries MCP-Protocol-Version + Mcp-Method
/// headers and the client's capabilities inside params <c>_meta</c>.
/// </summary>
public sealed class TestMcp2026 : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly JsonNode _caps;
    private int _nextId = 1;

    private TestMcp2026(HttpClient http, JsonNode caps)
    {
        _http = http;
        _caps = caps;
    }

    public static async Task<TestMcp2026> CreateAsync(
        WebApplicationFactory<Program> factory, object clientCapabilities)
    {
        var http = await TestAuth.LoginAsync(factory);
        return new TestMcp2026(http, JsonSerializer.SerializeToNode(clientCapabilities)!);
    }

    /// <summary>Sends one JSON-RPC request over the 2026-07-28 Streamable-HTTP
    /// session and returns the result payload.</summary>
    /// <param name="name">Value for the required <c>Mcp-Name</c> header — tool
    /// name for tools/call, taskId for tasks/*.</param>
    public async Task<JsonElement> SendAsync(string method, string name, object? parameters = null)
    {
        var id = _nextId++;
        var p = parameters is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(parameters)!.AsObject();
        p["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientCapabilities"] = _caps.DeepClone()
        };

        using var request = McpJsonRpc.BuildPost(
            $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{p.ToJsonString()}}}""",
            r =>
            {
                r.Headers.Add("MCP-Protocol-Version", "2026-07-28");
                r.Headers.Add("Mcp-Method", method);
                r.Headers.Add("Mcp-Name", name);
            });

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return McpJsonRpc.ExtractResult(await response.Content.ReadAsStringAsync());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
