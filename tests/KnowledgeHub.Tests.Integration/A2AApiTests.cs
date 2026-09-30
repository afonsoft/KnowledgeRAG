using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260929-a2a-server-interop: Agent Card discovery, JSON-RPC +
// HTTP+JSON bindings, aft_* auth, and scope enforcement on delegated calls.
public class A2AApiTests
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-a2a-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { File.Delete(DbPath); } catch (IOException) { /* best effort */ }
        }
    }

    private static async Task<string> NewKeyAsync(Fixture factory, string name)
    {
        var admin = await TestAuth.LoginAsync(factory);
        return await TestAuth.CreateApiKeyAsync(admin, name);
    }

    private static HttpRequestMessage JsonRpc(object id, string method, object? @params = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/a2a");
        req.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        });
        return req;
    }

    private static object SendMessageParams(string text, string? skill = null) => new
    {
        message = new
        {
            role = "ROLE_USER",
            messageId = Guid.NewGuid().ToString("N"),
            parts = new[] { new { text } },
            metadata = skill is null ? null : new Dictionary<string, object> { ["skill"] = skill }
        }
    };

    [Fact]
    public async Task AgentCard_IsAnonymous_AndDeclaresSkills()
    {
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/.well-known/agent-card.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var card = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("KnowledgeHub", card.GetProperty("name").GetString());
        Assert.Equal("bearer",
            card.GetProperty("securitySchemes").GetProperty("bearer")
                .GetProperty("httpAuthSecurityScheme").GetProperty("scheme").GetString());
        var skills = card.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("ask_knowledge", skills);
        Assert.Contains("search_knowledge", skills);
        Assert.Contains("agent_chat", skills);
        Assert.Contains("read_document", skills);
        var bindings = card.GetProperty("supportedInterfaces").EnumerateArray()
            .Select(i => i.GetProperty("protocolBinding").GetString()).ToList();
        Assert.Contains("JSONRPC", bindings);
        Assert.Contains("HTTP+JSON", bindings);
    }

    [Fact]
    public async Task JsonRpc_WithoutKey_Rejected()
    {
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var res = await client.SendAsync(JsonRpc(1, "SendMessage", SendMessageParams("hi")));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task JsonRpc_SendMessage_ExecutesSearchSkill()
    {
        await using var factory = new Fixture();
        var secret = await NewKeyAsync(factory, "a2a-key");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var res = await client.SendAsync(JsonRpc(7, "SendMessage", SendMessageParams("anything", "search_knowledge")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // JSON-RPC success — result is a Task or Message; either way no error member.
        Assert.False(body.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object,
            $"JSON-RPC error: {body.GetRawText()}");
        Assert.True(body.TryGetProperty("result", out var result));
    }

    [Fact]
    public async Task JsonRpc_UndelegableSkill_Fails()
    {
        await using var factory = new Fixture();
        var secret = await NewKeyAsync(factory, "a2a-key2");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var res = await client.SendAsync(JsonRpc(8, "SendMessage", SendMessageParams("x", "delete_everything")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var raw = body.GetRawText();
        // Task reaches Failed state or error message text is present.
        Assert.True(raw.Contains("not delegable") || raw.Contains("failed", StringComparison.OrdinalIgnoreCase),
            raw);
    }

    [Fact]
    public async Task Rest_MessageSend_Works()
    {
        await using var factory = new Fixture();
        var secret = await NewKeyAsync(factory, "a2a-rest");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var res = await client.PostAsJsonAsync("/a2a/message:send", new
        {
            message = new
            {
                role = "ROLE_USER",
                messageId = Guid.NewGuid().ToString("N"),
                parts = new[] { new { text = "ping" } },
                metadata = new Dictionary<string, object> { ["skill"] = "search_knowledge" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.ValueKind is JsonValueKind.Object);
    }
}
