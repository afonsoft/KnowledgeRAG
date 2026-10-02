using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260914-auth-login RF-006..RF-010 + acceptance criteria:
// anonymous 401, login + forced password change, lockout, aft_* keys on /mcp.
// Each test gets its own host+DB — auth state (password, lockout, keys) is
// mutable and must not leak between tests.
public class AuthFlowTests
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-auth-{Guid.NewGuid():N}.db");

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
            try { File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Anonymous_OperationalEndpoints_Return401()
    {
        // Covers AC: anonymous /api/sources → 401 (and /mcp).
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_HealthEndpoints_StayPublic()
    {
        await using var factory = new Fixture();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Login_SeededAdmin_ReturnsMustChangePassword_ThenGateBlocks()
    {
        // Covers AC: admin/123qwe → cookie + mustChangePassword:true; while
        // flagged every operational endpoint → 403 password_change_required.
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(TestAuth.AdminUsername, TestAuth.AdminInitialPassword));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal(TestAuth.AdminUsername, body!.Username);
        Assert.True(body.MustChangePassword);
        Assert.True(login.Headers.Contains("Set-Cookie"));

        var gated = await client.GetAsync("/api/sources");
        Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);
        Assert.Contains("password_change_required", await gated.Content.ReadAsStringAsync());

        // me/logout/change-password stay reachable during the gate.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ClearsGate_EndpointsPass()
    {
        await using var factory = new Fixture();
        using var client = await TestAuth.LoginAsync(factory);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.False(me!.MustChangePassword);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sources")).StatusCode);
    }

    [Fact]
    public async Task Login_WrongPassword_401_Generic_AndLocksAfterFive()
    {
        // Covers AC: 5 failures → 423 lockout; generic 401 (no user leak).
        await using var factory = new Fixture();
        using var client = factory.CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(TestAuth.AdminUsername, "wrong-pass"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("credenciais inválidas", await wrong.Content.ReadAsStringAsync());

        // Unknown user gets the same generic 401.
        var unknown = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("nobody", "wrong-pass"));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        // 4 more wrong attempts (5 total for admin) → lockout; even the
        // correct password is rejected while locked.
        for (var i = 0; i < 4; i++)
            await client.PostAsJsonAsync("/api/auth/login",
                new LoginRequest(TestAuth.AdminUsername, "wrong-pass"));

        var locked = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(TestAuth.AdminUsername, TestAuth.AdminInitialPassword));
        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        Assert.Contains("conta bloqueada", await locked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiKey_OnMcp_Authenticates_RevokedFails()
    {
        // Covers AC: Bearer aft_* on /mcp initialize → 200; revoked → 401;
        // GET /api/apikeys never returns the secret.
        await using var factory = new Fixture();
        using var admin = await TestAuth.LoginAsync(factory);
        var secret = await TestAuth.CreateApiKeyAsync(admin, "mcp-test");
        Assert.Matches("^aft_[0-9a-f]{32}$", secret);

        var list = await admin.GetFromJsonAsync<List<ApiKeyDto>>("/api/apikeys");
        var key = Assert.Single(list!, k => k.Name == "mcp-test");
        Assert.Equal(secret[..12], key.Prefix);
        // ApiKeyDto has no Key/KeyHash member — the secret structurally
        // cannot leak through GET /api/apikeys.

        // Bearer on /mcp — MCP initialize over Streamable HTTP requires the
        // SSE accept header alongside application/json.
        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await McpInitializeAsync(bearer)).StatusCode);

        // Anonymous /mcp → 401.
        using var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpInitializeAsync(anon)).StatusCode);

        // Revoke → the same key now fails.
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/apikeys/{key.Id}")).StatusCode);
        using var revoked = factory.CreateClient();
        revoked.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpInitializeAsync(revoked)).StatusCode);
    }

    [Fact]
    public async Task ApiKey_CannotManageApiKeys()
    {
        // Covers RF-010: keys cannot manage keys (no bootstrap from a leaked key).
        await using var factory = new Fixture();
        using var admin = await TestAuth.LoginAsync(factory);
        var secret = await TestAuth.CreateApiKeyAsync(admin, "self-manage");

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await bearer.GetAsync("/api/apikeys")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await bearer.PostAsJsonAsync("/api/apikeys", new CreateApiKeyRequest("x"))).StatusCode);
    }

    [Fact]
    public async Task ApiKey_AccessToken_OnMcpSse_Authenticates()
    {
        // Covers SPEC-20260917-sse-e2e-prod RF-003: legacy SSE clients
        // (EventSource) cannot send Authorization headers — the key must be
        // accepted via ?access_token= on /mcp/sse, and stay rejected elsewhere.
        await using var factory = new Fixture();
        using var admin = await TestAuth.LoginAsync(factory);
        var secret = await TestAuth.CreateApiKeyAsync(admin, "sse-test");

        // Without credentials → 401.
        using var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/mcp/sse")).StatusCode);

        // ?access_token= on /mcp/sse → stream opens (endpoint event).
        using var sse = factory.CreateClient();
        sse.Timeout = TimeSpan.FromSeconds(15);
        using var response = await sse.GetAsync(
            $"/mcp/sse?access_token={secret}", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer);
        var firstChunk = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        Assert.Contains("event:", firstChunk);

        // Same query param on a normal endpoint stays rejected — the fallback
        // is scoped to header-less transports only.
        using var scoped = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await scoped.GetAsync($"/api/sources?access_token={secret}")).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_PolicyViolations_Return400()
    {
        await using var factory = new Fixture();
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(TestAuth.AdminUsername, TestAuth.AdminInitialPassword));

        var tooShort = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest(TestAuth.AdminInitialPassword, "short"));
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var wrongCurrent = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest("nope-nope", "valid-new-1"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
    }

    [Fact]
    public async Task ApiKey_RevealSecret_Covers_RF001_RF002()
    {
        // Covers SPEC-20260924-api-key-reveal-and-copy RF-001 & RF-002:
        // Owner can retrieve the full secret; anonymous gets 401; non-owner/missing gets 404.
        await using var factory = new Fixture();
        using var admin = await TestAuth.LoginAsync(factory);

        var createRes = await admin.PostAsJsonAsync("/api/apikeys", new CreateApiKeyRequest("reveal-test"));
        createRes.EnsureSuccessStatusCode();
        var created = await createRes.Content.ReadFromJsonAsync<ApiKeyCreatedDto>();
        Assert.NotNull(created);
        Assert.Matches("^aft_[0-9a-f]{32}$", created.Key);

        // Anonymous → 401
        using var anon = factory.CreateClient();
        var anonRes = await anon.GetAsync($"/api/apikeys/{created.Id}/secret");
        Assert.Equal(HttpStatusCode.Unauthorized, anonRes.StatusCode);

        // Non-existent key → 404
        var notFoundRes = await admin.GetAsync($"/api/apikeys/{Guid.NewGuid()}/secret");
        Assert.Equal(HttpStatusCode.NotFound, notFoundRes.StatusCode);

        // Owner gets the decrypted secret
        var secretRes = await admin.GetAsync($"/api/apikeys/{created.Id}/secret");
        Assert.Equal(HttpStatusCode.OK, secretRes.StatusCode);
        var secretDto = await secretRes.Content.ReadFromJsonAsync<ApiKeySecretDto>();
        Assert.NotNull(secretDto);
        Assert.Equal(created.Id, secretDto.Id);
        Assert.True(secretDto.IsAvailable);
        Assert.Equal(created.Key, secretDto.Secret);
    }

    [Fact]
    public async Task ApiKey_RevealSecret_LegacyKey_ReturnsUnavailable()
    {
        // Covers SPEC-20260924-api-key-reveal-and-copy RF-002:
        // Legacy keys created without ProtectedKey return IsAvailable: false and Secret: null.
        await using var factory = new Fixture();
        using var admin = await TestAuth.LoginAsync(factory);

        var createRes = await admin.PostAsJsonAsync("/api/apikeys", new CreateApiKeyRequest("legacy-test"));
        createRes.EnsureSuccessStatusCode();
        var created = await createRes.Content.ReadFromJsonAsync<ApiKeyCreatedDto>();
        Assert.NotNull(created);

        // Simulate legacy key by setting ProtectedKey = null in the database
        using (var scope = factory.Services.CreateScope())
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<KnowledgeHub.Server.Data.KnowledgeHubDbContext>(scope.ServiceProvider);
            var key = await db.ApiKeys.FindAsync(created.Id);
            Assert.NotNull(key);
            key.ProtectedKey = null;
            await db.SaveChangesAsync();
        }

        var secretRes = await admin.GetAsync($"/api/apikeys/{created.Id}/secret");
        Assert.Equal(HttpStatusCode.OK, secretRes.StatusCode);
        var secretDto = await secretRes.Content.ReadFromJsonAsync<ApiKeySecretDto>();
        Assert.NotNull(secretDto);
        Assert.Equal(created.Id, secretDto.Id);
        Assert.False(secretDto.IsAvailable);
        Assert.Null(secretDto.Secret);
    }


    private static async Task<HttpResponseMessage> McpInitializeAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new { },
                    clientInfo = new { name = "test", version = "1.0" }
                }
            })
        };
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.Accept.Add(new("text/event-stream"));
        return await client.SendAsync(request);
    }
}
