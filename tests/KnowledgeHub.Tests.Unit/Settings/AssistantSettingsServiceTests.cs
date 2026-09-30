using KnowledgeHub.Server.Assistant;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Tests.Unit.Settings;

/// <summary>
/// SPEC-20260929-a2a-assistant-delegation RF-001/RF-002/RF-004: settings
/// round-trip (key in secret store, JSON only hasKey/hint), sub-task routing
/// (enabled+routed → decorated; disabled/unrouted → main) and main-model
/// fallback on assistant failure.
/// </summary>
public sealed class AssistantSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    private readonly FakeSecretStore _secrets = new();

    public AssistantSettingsServiceTests()
    {
        _conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<KnowledgeHubDbContext>(o => o.UseSqlite(_conn));
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>()
            .Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _conn.Dispose();
    }

    private AssistantChatClientProvider NewProvider(AssistantOptions? env = null) => new(
        Options.Create(env ?? new AssistantOptions()),
        _secrets,
        _provider.GetRequiredService<IServiceScopeFactory>(),
        new FakeHttpFactory(),
        NullLogger<AssistantChatClientProvider>.Instance);

    private AssistantSettingsService Sut(IAssistantChatClientProvider? provider = null) => new(
        Options.Create(new AssistantOptions()),
        _secrets,
        _provider.GetRequiredService<IServiceScopeFactory>(),
        new FakeHttpFactory(),
        provider ?? NewProvider(),
        NullLogger<AssistantSettingsService>.Instance);

    [Fact]
    public async Task Describe_Default_DisabledWithDefaults()
    {
        var dto = await Sut().DescribeAsync();
        Assert.False(dto.Enabled);
        Assert.Equal("local", dto.Mode);
        Assert.Equal("none", dto.Source);
        Assert.False(dto.HasApiKey);
    }

    [Fact]
    public async Task Save_PersistsRow_KeyGoesToSecretStore()
    {
        var provider = NewProvider();
        var sut = Sut(provider);
        await sut.SaveAsync(new SaveAssistantSettingsRequest
        {
            Enabled = true,
            Mode = "local",
            Endpoint = "https://llm.local",
            Model = "gpt-4o-mini",
            Route = ["rewrite", "grade"],
            TimeoutSeconds = 10,
            ApiKey = "sk-assistant-key"
        });

        var dto = await sut.DescribeAsync();
        Assert.True(dto.Enabled);
        Assert.Equal("store", dto.Source);
        Assert.Equal("https://llm.local", dto.Endpoint);
        Assert.Equal(["rewrite", "grade"], dto.Route);
        Assert.True(dto.HasApiKey);
        Assert.Equal("store", dto.ApiKeySource);
        Assert.Equal("sk-assistant-key", _secrets.Store[IntegrationProviders.Assistant]);

        using var scope = _provider.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>()
            .AssistantSettings.Single();
        Assert.DoesNotContain("sk-assistant-key", row.Endpoint + row.Model + row.RouteJson);
    }

    [Fact]
    public async Task Save_UnknownRouteEntries_AreDropped()
    {
        var sut = Sut();
        await sut.SaveAsync(new SaveAssistantSettingsRequest
        {
            Enabled = true,
            Mode = "local",
            Endpoint = "http://x",
            Route = ["rewrite", "bogus", "GRADE"]
        });
        var dto = await sut.DescribeAsync();
        Assert.Equal(["rewrite", "grade"], dto.Route);
    }

    [Fact]
    public async Task Clear_RemovesRowAndKey()
    {
        var provider = NewProvider();
        var sut = Sut(provider);
        await sut.SaveAsync(new SaveAssistantSettingsRequest
        {
            Enabled = true,
            Mode = "local",
            Endpoint = "http://x",
            ApiKey = "k"
        });
        await sut.ClearAsync();
        var dto = await sut.DescribeAsync();
        Assert.False(dto.Enabled);
        Assert.False(dto.HasApiKey);
        Assert.DoesNotContain(IntegrationProviders.Assistant, _secrets.Store.Keys);
    }

    [Fact]
    public void ForSubtask_Disabled_ReturnsMain()
    {
        var provider = NewProvider();
        var main = new StubChatClient("main");
        Assert.Same(main, provider.ForSubtask("rewrite", main));
    }

    [Fact]
    public async Task ForSubtask_Routed_WrapsInFallback_Unrouted_ReturnsMain()
    {
        var provider = NewProvider(new AssistantOptions
        {
            Enabled = true,
            Mode = "local",
            Endpoint = "http://assistant.local",
            Model = "cheap",
            Route = ["rewrite"],
            TimeoutSeconds = 5
        });
        var sut = Sut(provider);
        await sut.SaveAsync(new SaveAssistantSettingsRequest
        {
            Enabled = true,
            Mode = "local",
            Endpoint = "http://assistant.local",
            Model = "cheap",
            Route = ["rewrite"]
        });
        provider.Invalidate();

        var main = new StubChatClient("main");
        var routed = provider.ForSubtask("rewrite", main);
        Assert.IsType<AssistantFallbackChatClient>(routed);
        Assert.Same(main, provider.ForSubtask("grade", main));
    }

    [Fact]
    public async Task FallbackClient_AssistantError_FallsBackToMain()
    {
        var assistant = new StubChatClient("assistant", fails: true);
        var main = new StubChatClient("main-reply");
        var sut = new AssistantFallbackChatClient(
            assistant, main, "rewrite", TimeSpan.FromSeconds(5),
            NullLogger.Instance);
        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
        Assert.Equal("main-reply", response.Text);
    }

    [Fact]
    public async Task FallbackClient_AssistantOk_ReturnsAssistant()
    {
        var assistant = new StubChatClient("cheap-reply");
        var main = new StubChatClient("main-reply");
        var sut = new AssistantFallbackChatClient(
            assistant, main, "grade", TimeSpan.FromSeconds(5),
            NullLogger.Instance);
        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
        Assert.Equal("cheap-reply", response.Text);
    }

    [Fact]
    public async Task ForSubtask_RemoteMode_BuildsA2AAdapter()
    {
        var cardJson = AgentCardJson("http://remote/a2a");
        var provider = new AssistantChatClientProvider(
            Options.Create(new AssistantOptions
            {
                Enabled = true,
                Mode = "remote",
                Endpoint = "http://remote-agent",
                Route = ["grade"]
            }),
            _secrets,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeHttpFactory(new StubHandler(cardJson)),
            NullLogger<AssistantChatClientProvider>.Instance);

        var main = new StubChatClient("main");
        var wrapped = provider.ForSubtask("grade", main);
        var deco = Assert.IsType<AssistantFallbackChatClient>(wrapped);
        Assert.Same(main, provider.ForSubtask("rewrite", main));
    }

    [Fact]
    public async Task TestAsync_LocalOk_ReportsSuccess()
    {
        var sut = new AssistantSettingsService(
            Options.Create(new AssistantOptions()),
            _secrets,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeHttpFactory(new StubHandler("""{"data":[{"id":"m1"}]}""")),
            NewProvider(),
            NullLogger<AssistantSettingsService>.Instance);
        var res = await sut.TestAsync(new TestAssistantConnectionRequest
        {
            Mode = "local",
            Endpoint = "http://llm.test",
            Model = "m1"
        });
        Assert.True(res.Ok);
        Assert.True(res.LatencyMs >= 0);
    }

    [Fact]
    public async Task TestAsync_Remote_ResolvesAgentCard()
    {
        var cardJson = AgentCardJson("http://r/a2a");
        var sut = new AssistantSettingsService(
            Options.Create(new AssistantOptions()),
            _secrets,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeHttpFactory(new StubHandler(cardJson)),
            NewProvider(),
            NullLogger<AssistantSettingsService>.Instance);
        var res = await sut.TestAsync(new TestAssistantConnectionRequest
        {
            Mode = "remote",
            Endpoint = "http://remote-agent"
        });
        Assert.True(res.Ok);
        Assert.Contains("remote-agent", res.Detail);
    }

    [Fact]
    public async Task TestAsync_NoEndpoint_FailsFast()
    {
        var res = await Sut().TestAsync(new TestAssistantConnectionRequest());
        Assert.False(res.Ok);
        Assert.Equal("endpoint is required", res.Detail);
    }

    [Fact]
    public async Task TestAsync_Unreachable_ReportsConnectionFailed()
    {
        var sut = new AssistantSettingsService(
            Options.Create(new AssistantOptions()),
            _secrets,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeHttpFactory(new StubHandler("x", HttpStatusCode.BadGateway)),
            NewProvider(),
            NullLogger<AssistantSettingsService>.Instance);
        var res = await sut.TestAsync(new TestAssistantConnectionRequest
        {
            Mode = "local",
            Endpoint = "http://llm.test"
        });
        Assert.False(res.Ok);
        Assert.Equal("HTTP 502", res.Detail);
    }

    [Fact]
    public async Task RemoveKey_RemovesOnlyKey()
    {
        var provider = NewProvider();
        var sut = Sut(provider);
        await sut.SaveAsync(new SaveAssistantSettingsRequest
        {
            Enabled = true,
            Mode = "local",
            Endpoint = "http://x",
            ApiKey = "k1"
        });
        await sut.RemoveKeyAsync();
        var dto = await sut.DescribeAsync();
        Assert.True(dto.Enabled);          // row intact
        Assert.False(dto.HasApiKey);       // key gone
    }

    private static string AgentCardJson(string url) => JsonSerializer.Serialize(
        new A2A.AgentCard
        {
            Name = "remote-agent",
            Version = "1.0",
            Description = "d",
            SupportedInterfaces =
            [
                new A2A.AgentInterface
                {
                    Url = url, ProtocolBinding = A2A.ProtocolBindingNames.JsonRpc,
                    ProtocolVersion = "1.0"
                }
            ],
            Capabilities = new A2A.AgentCapabilities(),
            Skills = [],
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"]
        }, A2A.A2AJsonUtilities.DefaultOptions);

    private sealed class FakeHttpFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is null ? new() : new HttpClient(handler);
    }

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class StubChatClient(string reply, bool fails = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (fails)
                throw new HttpRequestException("assistant down");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeSecretStore : IIntegrationSecretStore
    {
        public readonly Dictionary<string, string> Store = new();
        public Task<string?> GetAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(provider, out var v) ? v : (string?)null);
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.TryGetValue(provider, out var s)
                ? new IntegrationSecretInfo(provider, s.Length >= 4 ? s[^4..] : s, DateTimeOffset.UtcNow)
                : (IntegrationSecretInfo?)null);
        public Task SetAsync(string provider, string secret, CancellationToken cancellationToken = default)
        { Store[provider] = secret; return Task.CompletedTask; }
        public Task<bool> RemoveAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(Store.Remove(provider));
    }
}
