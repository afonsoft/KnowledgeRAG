using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Blackbox;

/// <summary>
/// SPEC-20261111-agent-rules-hardening RF-002: blackbox smoke — the app under
/// test is exercised through its public MCP surface only (JSON-RPC over
/// Streamable HTTP): auth → initialize → notifications/initialized →
/// tools/list → tools/call. No internal services, no DbContext, no reflection
/// into Server internals. Bugs caught here live "in the seams between steps"
/// (wiring, serialization, auth, transport) that unit/integration tests miss.
/// </summary>
public class McpSmokeTests : IClassFixture<McpSmokeTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-blackbox-{Guid.NewGuid():N}.db")
                }));
        }
    }

    private readonly Factory _factory;
    public McpSmokeTests(Factory factory) => _factory = factory;

    // Same flow a real client would drive: admin login (seeded admin/123qwe),
    // clear the first-access password gate, mint an aft_* key through the
    // public API, then talk MCP with Bearer auth.
    private async Task<HttpClient> AuthedMcpClientAsync()
    {
        var http = _factory.CreateClient();
        var login = await http.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "123qwe" });
        login.EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "123qwe", newPassword = "blackbox-123" })).EnsureSuccessStatusCode();

        var keyRes = await http.PostAsJsonAsync("/api/apikeys", new { name = "blackbox" });
        keyRes.EnsureSuccessStatusCode();
        using var keyDoc = JsonDocument.Parse(await keyRes.Content.ReadAsStringAsync());
        var key = keyDoc.RootElement.GetProperty("key").GetString()!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return http;
    }

    [Fact]
    public async Task Mcp_Initialize_ListTools_CallTool_Succeeds()
    {
        var http = await AuthedMcpClientAsync();

        // initialize (2025-11-25 → stateful session on the hybrid transport)
        var init = await PostAsync(http, """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"blackbox","version":"1.0"}}}
            """);
        var (initResult, initHeaders) = init;
        Assert.True(initResult.TryGetProperty("serverInfo", out var server));
        Assert.Equal("knowledge", server.GetProperty("name").GetString());

        // Stateful clients must echo the session id + negotiated version.
        if (initHeaders.TryGetValues("Mcp-Session-Id", out var sessionIds))
            http.DefaultRequestHeaders.Add("Mcp-Session-Id", sessionIds.First());
        http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");

        // notifications/initialized
        using var notify = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                Encoding.UTF8, "application/json")
        };
        var notifyRes = await http.SendAsync(notify);
        Assert.True(notifyRes.IsSuccessStatusCode, $"notifications/initialized: {(int)notifyRes.StatusCode}");

        // tools/list — catalog must expose the core knowledge tools
        var (listResult, _) = await PostAsync(http, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = listResult.GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("search_knowledge", names);
        Assert.Contains("ask_knowledge", names);

        // tools/call — real call through the dispatcher; isError must be false
        var (callResult, _) = await PostAsync(http, """
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_knowledge","arguments":{"query":"smoke","topK":1}}}
            """);
        var isError = callResult.TryGetProperty("isError", out var e) && e.GetBoolean();
        Assert.False(isError, $"tools/call search_knowledge returned isError=true: {callResult.GetRawText()}");
    }

    private static async Task<(JsonElement Result, HttpResponseHeaders Headers)> PostAsync(
        HttpClient http, string body)
    {
        var res = await http.PostAsync("/mcp", new StringContent(body, Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        return (ExtractResult(await res.Content.ReadAsStringAsync()), res.Headers);
    }

    private static JsonElement ExtractResult(string payload)
    {
        // Streamable HTTP may answer with an SSE body — take the last data: line.
        var data = payload.Contains("data:")
            ? payload.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()).Last()
            : payload;
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"JSON-RPC error: {err.GetRawText()}");
        return doc.RootElement.GetProperty("result").Clone();
    }
}
