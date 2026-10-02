using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-01 acceptance criteria: legacy SSE handshake, Streamable HTTP init, expired session.
public class McpTransportTests : IClassFixture<McpTransportTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-mcp-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = DbPath }));
        }
    }

    private readonly Fixture _factory;

    public McpTransportTests(Fixture factory) => _factory = factory;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static StringContent InitializeRequest() => new(
        """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """,
        Encoding.UTF8, "application/json");

    [Fact]
    public async Task Get_McpSse_EmitsEndpointEvent_WithSessionId()
    {
        // AC: GET /mcp/sse → first frame is `event: endpoint` carrying sessionId
        var client = await TestAuth.LoginAsync(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await client.GetAsync("/mcp/sse", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer, cts.Token);
        var frame = Encoding.UTF8.GetString(buffer, 0, read);

        Assert.Contains("event: endpoint", frame);
        Assert.Contains("sessionId=", frame);
    }

    [Fact]
    public async Task Post_Mcp_StreamableHttp_Initialize_ReturnsServerInfo_AndSessionHeader()
    {
        // AC: POST initialize → /mcp returns valid result + Mcp-Session-Id header (stateful)
        var client = await TestAuth.LoginAsync(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = InitializeRequest() };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.Contains("Mcp-Session-Id"));

        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("knowledge", body);
        Assert.Contains("protocolVersion", body);
        Assert.Contains("tools", body);
    }

    [Fact]
    public async Task Post_McpMessage_WithUnknownSession_Fails()
    {
        // AC: expired/unknown sessionId → HTTP error (not a silent accept)
        var client = await TestAuth.LoginAsync(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await client.PostAsync("/mcp/message?sessionId=definitely-not-a-session", InitializeRequest(), cts.Token);
        Assert.True((int)response.StatusCode >= 400, $"expected error, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Post_Mcp_Native2026_ToolsList_ReturnsResult_WithoutSessionHeader()
    {
        // SPEC-20260918-mcp-v2-hybrid-transport AC: hybrid mode — a client posting
        // tools/list with MCP-Protocol-Version: 2026-07-28 gets 200 + full tool
        // list + NO Mcp-Session-Id (served statelessly, no initialize downgrade).
        var client = await TestAuth.LoginAsync(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            // 2026-07-28 (SEP-2567/SEP-2575): no initialize handshake — the
            // revision is declared by MCP-Protocol-Version header + params._meta.
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/list");

        using var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"expected success, got {(int)response.StatusCode}: {body}");
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));

        Assert.Contains("\"tools\"", body);
        Assert.Contains("search_knowledge", body);
        Assert.DoesNotContain("-32022", body); // no UnsupportedProtocolVersion downgrade
    }

    [Fact]
    public async Task Post_Mcp_Native2026_WithStraySessionHeader_IsIgnored()
    {
        // Edge case: a 2026-07-28 request carrying a stray Mcp-Session-Id must be
        // served statelessly — the header is ignored, none is minted or echoed.
        var client = await TestAuth.LoginAsync(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/list");
        request.Headers.Add("Mcp-Session-Id", "stray-session-id");

        using var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"expected success, got {(int)response.StatusCode}: {body}");
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));

        Assert.DoesNotContain("-32022", body);
    }

    [Fact]
    public async Task Post_Mcp_Native2026_ToolsCall_ExecutesSessionless()
    {
        // SPEC AC + RF-004: a sessionless tools/call flows through the shared
        // concurrency bucket and returns a normal result (no session minted).
        var client = await TestAuth.LoginAsync(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_knowledge","arguments":{"query":"test"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", "search_knowledge");

        using var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.True(response.IsSuccessStatusCode, $"expected success, got {(int)response.StatusCode}: {body}");
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        Assert.DoesNotContain("-32022", body);
    }

    [Fact]
    public async Task Get_Mcp_WithoutSession_ReturnsMethodNotAllowed()
    {
        // SPEC AC: GET /mcp with no session → 405 (stateless requests can't open
        // the long-lived stream; GET remains available only to session clients).
        var client = await TestAuth.LoginAsync(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");

        using var response = await client.SendAsync(request, cts.Token);
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Post_Mcp_WithMalformedJson_ReturnsJsonRpcError()
    {
        // Edge case: malformed JSON → -32700 parse error from the SDK
        var client = await TestAuth.LoginAsync(_factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{invalid", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        // SDK 2.x rejects unparseable bodies with -32600 (Bad Request) rather than -32700.
        Assert.Contains("\"error\"", body);
        Assert.Matches(@"""code"":\s*-32(600|700)", body);
    }
}
