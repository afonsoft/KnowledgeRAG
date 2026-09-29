using System.Security.Claims;
using System.Text.Json;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Mcp.ToolProviders;
using KnowledgeHub.Server.Mcp.Upstream;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260922-per-key-integration-secrets RF-001/RF-002/RF-003/RF-004:
// the caller's apikey-{provider}-{keyId} secret reaches the upstream transport
// as the effective credential; the transport factory probe throws after
// capturing options so no real upstream session is needed.
public class PerKeyIntegrationSecretTests
{
    private static readonly IServiceProvider BareServices =
        new ServiceCollection().BuildServiceProvider();

    private static (Context7ToolsProvider provider, Context7UpstreamClient client,
        List<HttpClientTransportOptions> transports) BuildContext7(
            string? storedKey = null, string? envKey = null)
    {
        var options = new Context7Options { Enabled = true, ApiKey = envKey ?? "" };
        var client = new Context7UpstreamClient(
            Options.Create(options),
            new FakeSecretStore(context7Key: storedKey),
            NullLoggerFactory.Instance,
            NullLogger<Context7UpstreamClient>.Instance);
        var captured = new List<HttpClientTransportOptions>();
        client.TransportFactory = opts =>
        {
            captured.Add(opts);
            throw new InvalidOperationException("probe");
        };
        var provider = new Context7ToolsProvider(
            client, Options.Create(options), NullLogger<Context7ToolsProvider>.Instance);
        return (provider, client, captured);
    }

    private static (DeepWikiToolsProvider provider, DeepWikiUpstreamClient client,
        List<HttpClientTransportOptions> transports) BuildDeepWiki(
            string? storedKey = null, string? envKey = null)
    {
        var options = new DeepWikiOptions { Enabled = true, ApiKey = envKey ?? "" };
        var client = new DeepWikiUpstreamClient(
            Options.Create(options),
            new FakeSecretStore(deepwikiKey: storedKey),
            NullLoggerFactory.Instance,
            NullLogger<DeepWikiUpstreamClient>.Instance);
        var captured = new List<HttpClientTransportOptions>();
        client.TransportFactory = opts =>
        {
            captured.Add(opts);
            throw new InvalidOperationException("probe");
        };
        var provider = new DeepWikiToolsProvider(
            client, Options.Create(options), NullLogger<DeepWikiToolsProvider>.Instance);
        return (provider, client, captured);
    }

    private static IServiceProvider ApiKeyServices(Guid keyId, IApiKeyChatSettingsService settings,
        string? keyIdClaim = null)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ApiKeyAuthenticationHandler.AuthMethodClaim, "apikey"),
                new Claim(ApiKeyAuthenticationHandler.KeyIdClaim, keyIdClaim ?? keyId.ToString())
            ], "apikey"))
        };
        return new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = http })
            .AddSingleton<IApiKeyChatSettingsService>(settings)
            .BuildServiceProvider();
    }

    private static ToolCallContext Call(IServiceProvider services) =>
        new() { Services = services, Arguments = new Dictionary<string, JsonElement>() };

    private static async Task<CallToolResult> CallToolAsync(
        Context7ToolsProvider provider, string name, ToolCallContext ctx)
    {
        var tools = await provider.GetToolsAsync(BareServices, CancellationToken.None);
        return await tools.Single(t => t.Name == name).Handler(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task Context7_PerKeySecret_UsedAsBearer()
    {
        var (provider, _, captured) = BuildContext7(storedKey: "ctx7sk-global");
        var keyId = Guid.NewGuid();
        var settings = new FakeApiKeySettings("ctx7sk-perkey");
        var ctx = Call(ApiKeyServices(keyId, settings));

        var result = await CallToolAsync(provider, "query-docs", ctx);

        Assert.True(result.IsError); // probe throws — result irrelevant
        Assert.Equal(1, settings.IntegrationLookups);
        Assert.Equal("Bearer ctx7sk-perkey", captured.Last().AdditionalHeaders?["Authorization"]);
    }

    [Fact]
    public async Task Context7_NoPerKey_FallsBackToGlobalStore()
    {
        var (provider, _, captured) = BuildContext7(storedKey: "ctx7sk-global");
        var keyId = Guid.NewGuid();
        var settings = new FakeApiKeySettings(secret: null);
        var ctx = Call(ApiKeyServices(keyId, settings));

        await CallToolAsync(provider, "query-docs", ctx);

        Assert.Equal(1, settings.IntegrationLookups);
        Assert.Equal("Bearer ctx7sk-global", captured.Last().AdditionalHeaders?["Authorization"]);
    }

    [Fact]
    public async Task Context7_NoPerKeyNoStore_UsesEnv()
    {
        var (provider, _, captured) = BuildContext7(envKey: "ctx7sk-env");
        var keyId = Guid.NewGuid();
        var ctx = Call(ApiKeyServices(keyId, new FakeApiKeySettings(secret: null)));

        await CallToolAsync(provider, "query-docs", ctx);

        Assert.Equal("Bearer ctx7sk-env", captured.Last().AdditionalHeaders?["Authorization"]);
    }

    [Fact]
    public async Task Context7_NoKeyAnywhere_FriendlyIsError()
    {
        var (provider, _, captured) = BuildContext7();
        var ctx = Call(ApiKeyServices(Guid.NewGuid(), new FakeApiKeySettings(secret: null)));

        var result = await CallToolAsync(provider, "query-docs", ctx);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("Context7 API key", text);
        Assert.Empty(captured);
    }

    [Fact]
    public async Task Context7_NonApiKeyCaller_SkipsPerKeyLookup()
    {
        var (provider, _, captured) = BuildContext7(storedKey: "ctx7sk-global");
        // No IHttpContextAccessor in services → treated as non-apikey caller.
        var result = await CallToolAsync(provider, "query-docs", Call(BareServices));

        Assert.True(result.IsError);
        Assert.Equal("Bearer ctx7sk-global", captured.Last().AdditionalHeaders?["Authorization"]);
    }

    [Fact]
    public async Task Context7_InvalidKeyIdClaim_TreatedAsGlobalCaller()
    {
        var (provider, _, captured) = BuildContext7(storedKey: "ctx7sk-global");
        var settings = new FakeApiKeySettings(secret: null);
        var ctx = Call(ApiKeyServices(Guid.NewGuid(), settings, keyIdClaim: "not-a-guid"));

        await CallToolAsync(provider, "query-docs", ctx);

        Assert.Equal(0, settings.IntegrationLookups); // invalid claim → no lookup
        Assert.Equal("Bearer ctx7sk-global", captured.Last().AdditionalHeaders?["Authorization"]);
    }

    [Fact]
    public async Task Context7_KeyChangeBetweenCalls_Reconnects()
    {
        var options = new Context7Options { Enabled = true };
        var client = new Context7UpstreamClient(
            Options.Create(options), new FakeSecretStore(),
            NullLoggerFactory.Instance, NullLogger<Context7UpstreamClient>.Instance);
        var captured = new List<HttpClientTransportOptions>();
        client.TransportFactory = opts =>
        {
            captured.Add(opts);
            throw new InvalidOperationException("probe");
        };

        await client.CallAsync("query-docs", null, CancellationToken.None, apiKeyOverride: "ctx7sk-a");
        await client.CallAsync("query-docs", null, CancellationToken.None, apiKeyOverride: "ctx7sk-b");

        Assert.Equal("Bearer ctx7sk-a", captured[0].AdditionalHeaders?["Authorization"]);
        // _connectedKey mismatch → new transport with the new key.
        Assert.Contains(captured, o => o.AdditionalHeaders?["Authorization"] == "Bearer ctx7sk-b");
    }

    [Fact]
    public async Task DeepWiki_PerKeySecret_PrivateEndpointPlusBearer()
    {
        var (provider, _, captured) = BuildDeepWiki();
        var keyId = Guid.NewGuid();
        var ctx = Call(ApiKeyServices(keyId, new FakeApiKeySettings("dw-perkey")));

        var tools = await provider.GetToolsAsync(BareServices, CancellationToken.None);
        var result = await tools.Single(t => t.Name == "ask_question")
            .Handler(new ToolCallContext
            {
                Services = ctx.Services,
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["question"] = JsonDocument.Parse("\"q\"").RootElement,
                    ["repoName"] = JsonDocument.Parse("\"o/r\"").RootElement
                }
            }, CancellationToken.None);

        Assert.True(result.IsError);
        var last = captured.Last();
        Assert.Equal(new Uri("https://mcp.devin.ai/mcp"), last.Endpoint);
        Assert.Equal("Bearer dw-perkey", last.AdditionalHeaders?["Authorization"]);
    }

    [Fact]
    public async Task DeepWiki_NoKey_PublicEndpoint()
    {
        var (provider, _, captured) = BuildDeepWiki();
        var tools = await provider.GetToolsAsync(BareServices, CancellationToken.None);
        await tools.Single(t => t.Name == "read_wiki_structure")
            .Handler(new ToolCallContext
            {
                Services = BareServices,
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["repoName"] = JsonDocument.Parse("\"o/r\"").RootElement
                }
            }, CancellationToken.None);

        Assert.Equal(new Uri("https://mcp.deepwiki.com/mcp"), captured.Last().Endpoint);
        Assert.False(captured.Last().AdditionalHeaders?.ContainsKey("Authorization") ?? false);
    }

    [Fact]
    public async Task SetApiKeySettings_DeepWiki_SavesPerKeySecret()
    {
        var provider = new SettingsToolsProvider();
        var tools = await provider.GetToolsAsync(BareServices, CancellationToken.None);
        var tool = tools.Single(t => t.Name == "set_api_key_settings");

        var settings = new FakeApiKeySettings(secret: null);
        var keyId = Guid.NewGuid();
        var ctx = new ToolCallContext
        {
            Services = ApiKeyServices(keyId, settings),
            Arguments = new Dictionary<string, JsonElement>
            {
                ["provider"] = JsonDocument.Parse("\"deepwiki\"").RootElement,
                ["apiKey"] = JsonDocument.Parse("\"dw-key-1\"").RootElement
            }
        };

        var result = await tool.Handler(ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains((IntegrationProviders.DeepWiki, "dw-key-1"), settings.Saved);
    }

    [Fact]
    public async Task SetApiKeySettings_DeepWiki_NullKey_RemovesPerKeySecret()
    {
        var provider = new SettingsToolsProvider();
        var tools = await provider.GetToolsAsync(BareServices, CancellationToken.None);
        var tool = tools.Single(t => t.Name == "set_api_key_settings");

        var settings = new FakeApiKeySettings(secret: null);
        var ctx = new ToolCallContext
        {
            Services = ApiKeyServices(Guid.NewGuid(), settings),
            Arguments = new Dictionary<string, JsonElement>
            {
                ["provider"] = JsonDocument.Parse("\"deepwiki\"").RootElement
            }
        };

        var result = await tool.Handler(ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal([IntegrationProviders.DeepWiki], settings.Removed);
    }

    // Covers SPEC-20260926-split-settings-tools RF-001: set_api_key_settings
    // only configures integrations, rejects provider "chat".
    [Fact]
    public async Task SetApiKeySettings_ChatProvider_ThrowsInvalidParams()
    {
        var provider = new SettingsToolsProvider();
        var tools = await provider.GetToolsAsync(BareServices, CancellationToken.None);
        var tool = tools.Single(t => t.Name == "set_api_key_settings");

        var settings = new FakeApiKeySettings(secret: null);
        var ctx = new ToolCallContext
        {
            Services = ApiKeyServices(Guid.NewGuid(), settings),
            Arguments = new Dictionary<string, JsonElement>
            {
                ["provider"] = JsonDocument.Parse("\"chat\"").RootElement
            }
        };

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    // Covers SPEC-20260926-split-settings-tools RF-002: set_chat_settings
    // configures chat LLM settings for the current API key.
    [Fact]
    public async Task SetChatSettings_SavesChatSettings()
    {
        var provider = new SettingsToolsProvider();
        var tools = await provider.GetToolsAsync(BareServices, CancellationToken.None);
        var tool = tools.Single(t => t.Name == "set_chat_settings");

        var settings = new FakeApiKeySettings(secret: null);
        var keyId = Guid.NewGuid();
        var ctx = new ToolCallContext
        {
            Services = ApiKeyServices(keyId, settings),
            Arguments = new Dictionary<string, JsonElement>
            {
                ["endpoint"] = JsonDocument.Parse("\"http://localhost:11434\"").RootElement,
                ["model"] = JsonDocument.Parse("\"llama3\"").RootElement,
                ["apiKey"] = JsonDocument.Parse("\"sk-test\"").RootElement
            }
        };

        var result = await tool.Handler(ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains(("http://localhost:11434", "llama3", "sk-test"), settings.SavedChat);
    }

    // Covers SPEC-20260926-split-settings-tools RF-002: set_chat_settings
    // requires an API-key authenticated session.
    [Fact]
    public async Task SetChatSettings_AnonymousCaller_ThrowsInvalidParams()
    {
        var provider = new SettingsToolsProvider();
        var tools = await provider.GetToolsAsync(BareServices, CancellationToken.None);
        var tool = tools.Single(t => t.Name == "set_chat_settings");

        var settings = new FakeApiKeySettings(secret: null);
        var http = new DefaultHttpContext();
        var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = http })
            .AddSingleton<IApiKeyChatSettingsService>(settings)
            .BuildServiceProvider();
        var ctx = new ToolCallContext
        {
            Services = services,
            Arguments = new Dictionary<string, JsonElement>()
        };

        var ex = await Assert.ThrowsAsync<McpProtocolException>(async () => await tool.Handler(ctx, CancellationToken.None));
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    private sealed class FakeApiKeySettings(string? secret) : IApiKeyChatSettingsService
    {
        public int IntegrationLookups;
        public readonly List<(string Provider, string Key)> Saved = [];
        public readonly List<string> Removed = [];
        public readonly List<(string? Endpoint, string? Model, string? ApiKey)> SavedChat = [];

        public ChatProviderOptions GetEffectiveOptions(Guid apiKeyId) => new();
        public IChatClient? GetClient(Guid apiKeyId) => null;
        public void Invalidate(Guid apiKeyId) { }

        public Task<ApiKeyChatSettingsDto> DescribeAsync(Guid apiKeyId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApiKeyChatSettingsDto
            {
                Provider = "none",
                HasApiKey = false,
                ApiKeySource = "none",
                Source = "none",
                EnvConfigured = false,
                HasOverride = false,
                OverrideFields = []
            });

        public Task SaveAsync(Guid apiKeyId, string? endpoint, string? model, string? apiKey,
            CancellationToken cancellationToken = default)
        {
            SavedChat.Add((endpoint, model, apiKey));
            return Task.CompletedTask;
        }

        public Task SaveIntegrationKeyAsync(Guid apiKeyId, string provider, string apiKey,
            CancellationToken cancellationToken = default)
        {
            Saved.Add((provider, apiKey));
            return Task.CompletedTask;
        }

        public Task RemoveIntegrationKeyAsync(Guid apiKeyId, string provider,
            CancellationToken cancellationToken = default)
        {
            Removed.Add(provider);
            return Task.CompletedTask;
        }

        public Task<string?> GetIntegrationSecretAsync(Guid apiKeyId, string provider,
            CancellationToken cancellationToken = default)
        {
            IntegrationLookups++;
            return Task.FromResult(secret);
        }

        public Task RemoveAsync(Guid apiKeyId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeSecretStore(string? context7Key = null, string? deepwikiKey = null)
        : IIntegrationSecretStore
    {
        public Task<string?> GetAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(provider == IntegrationProviders.Context7 ? context7Key
                : provider == IntegrationProviders.DeepWiki ? deepwikiKey
                : (string?)null);

        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);

        public Task SetAsync(string provider, string secret, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RemoveAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
