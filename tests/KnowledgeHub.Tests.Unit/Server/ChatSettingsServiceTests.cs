using System.Net;
using System.Text;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260916-settings-chat-config RF-002/RF-003/RF-005: store-over-env
// precedence, masked key reporting, snapshot caching/invalidation, and the
// connection probe (test seam via Func<HttpClient>).
public sealed class ChatSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _services;
    private readonly FakeSecretStore _secrets = new();

    /// <summary>Sobe um SQLite em memória com o schema criado e o container de DI dos testes.</summary>
    public ChatSettingsServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();

        var sc = new ServiceCollection();
        sc.AddDbContext<KnowledgeHubDbContext>(o => o.UseSqlite(_conn));
        sc.AddHttpClient();
        _services = sc.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>().Database.EnsureCreated();
    }

    /// <summary>Instancia o serviço sob teste com o env e o probe informados.</summary>
    private ChatSettingsService Sut(ChatProviderOptions? env = null, Func<HttpClient>? probe = null) =>
        new(Options.Create(env ?? new ChatProviderOptions()),
            _secrets,
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ChatSettingsService>.Instance,
            probe);

    /// <summary>Monta options de ambiente com provider openai para os cenários de teste.</summary>
    private static ChatProviderOptions EnvOpenAi(
        string endpoint = "https://env.test",
        string model = "env-model",
        string? apiKey = null) =>
        new() { Provider = "openai", Endpoint = endpoint, Model = model, ApiKey = apiKey };

    /// <summary>Sem config nenhuma, o estado efetivo reporta provider/source "none".</summary>
    [Fact]
    public async Task Describe_NoConfig_SourceNone()
    {
        var dto = await Sut().DescribeAsync();

        Assert.Equal("none", dto.Provider);
        Assert.Equal("none", dto.Source);
        Assert.Equal("none", dto.ApiKeySource);
        Assert.False(dto.HasApiKey);
        Assert.False(dto.EnvConfigured);
        Assert.Null(dto.UpdatedAt);
    }

    /// <summary>Com provider no env e sem linha salva, o estado efetivo vem do ambiente.</summary>
    [Fact]
    public async Task Describe_EnvProvider_SourceEnv()
    {
        var dto = await Sut(EnvOpenAi(apiKey: "sk-env-1234")).DescribeAsync();

        Assert.Equal("openai", dto.Provider);
        Assert.Equal("env", dto.Source);
        Assert.Equal("https://env.test", dto.Endpoint);
        Assert.Equal("env-model", dto.Model);
        Assert.True(dto.HasApiKey);
        Assert.Equal("env", dto.ApiKeySource);
        Assert.Equal("••••1234", dto.ApiKeyHint);
        Assert.True(dto.EnvConfigured);
    }

    /// <summary>Depois de salvar, endpoint/model do store sobrepõem os do env.</summary>
    [Fact]
    public async Task Save_ThenDescribe_StoreBeatsEnv()
    {
        var sut = Sut(EnvOpenAi(endpoint: "https://env.test", model: "env-model"));

        await sut.SaveAsync("https://store.test", "store-model", apiKey: null);
        var dto = await sut.DescribeAsync();

        Assert.Equal("openai", dto.Provider);
        Assert.Equal("store", dto.Source);
        Assert.Equal("https://store.test", dto.Endpoint);
        Assert.Equal("store-model", dto.Model);
        Assert.True(dto.EnvConfigured);
        Assert.NotNull(dto.UpdatedAt);
    }

    /// <summary>Options efetivas usam endpoint/model do store e a key do store vence a do env.</summary>
    [Fact]
    public async Task EffectiveOptions_StoreEndpointModel_StoreKeyBeatsEnvKey()
    {
        var sut = Sut(EnvOpenAi(apiKey: "sk-env-aaaa"));

        await sut.SaveAsync("https://store.test", "store-model", "sk-store-bbbb");
        var options = sut.GetEffectiveOptions();

        Assert.Equal("openai", options.Provider);
        Assert.Equal("https://store.test", options.Endpoint);
        Assert.Equal("store-model", options.Model);
        Assert.Equal("sk-store-bbbb", options.ApiKey);
        Assert.Equal("chat", _secrets.LastSetProvider);
    }

    /// <summary>Salvar com key em branco preserva a key já armazenada.</summary>
    [Fact]
    public async Task Save_BlankApiKey_KeepsStoredKey()
    {
        var sut = Sut();

        await sut.SaveAsync("https://a.test", "m1", "sk-first-0001");
        await sut.SaveAsync("https://b.test", "m2", apiKey: "   ");

        var dto = await sut.DescribeAsync();
        Assert.True(dto.HasApiKey);
        Assert.Equal("store", dto.ApiKeySource);
        Assert.Equal("sk-first-0001", sut.GetEffectiveOptions().ApiKey);
    }

    /// <summary>Remover a key do store faz a key do env voltar a valer.</summary>
    [Fact]
    public async Task RemoveKey_EnvKeyFallsBack()
    {
        var sut = Sut(EnvOpenAi(apiKey: "sk-env-cccc"));

        await sut.SaveAsync("https://store.test", "m", "sk-store-dddd");
        await sut.RemoveKeyAsync();

        Assert.Equal("sk-env-cccc", sut.GetEffectiveOptions().ApiKey);
        var dto = await sut.DescribeAsync();
        Assert.True(dto.HasApiKey);
        Assert.Equal("env", dto.ApiKeySource);
    }

    /// <summary>Limpar remove a linha e a key do store, voltando a configuração ao env.</summary>
    [Fact]
    public async Task Clear_RemovesRowAndKey_BackToEnv()
    {
        var sut = Sut(EnvOpenAi(apiKey: "sk-env-eeee"));

        await sut.SaveAsync("https://store.test", "m", "sk-store-ffff");
        await sut.ClearAsync();

        var dto = await sut.DescribeAsync();
        Assert.Equal("env", dto.Source);
        Assert.Equal("https://env.test", dto.Endpoint);
        Assert.Equal("env", dto.ApiKeySource);
        Assert.True(_secrets.RemovedChat);
    }

    /// <summary>Com provider "none", o client efetivo é null.</summary>
    [Fact]
    public void GetClient_NullWhenProviderNone() =>
        Assert.Null(Sut().GetClient());

    /// <summary>O client é cacheado em snapshot e reconstruído só após Invalidate().</summary>
    [Fact]
    public void GetClient_CachesSnapshot_UntilInvalidate()
    {
        var sut = Sut(EnvOpenAi());

        var first = sut.GetClient();
        var second = sut.GetClient();
        Assert.NotNull(first);
        Assert.Same(first, second);

        sut.Invalidate();
        var third = sut.GetClient();
        Assert.NotNull(third);
        Assert.NotSame(first, third);
    }

    /// <summary>Salvar invalida o snapshot — as options efetivas mudam sem restart.</summary>
    [Fact]
    public async Task GetClient_RebuildsAfterSave_WithoutRestart()
    {
        var sut = Sut(EnvOpenAi(endpoint: "https://env.test", model: "env-model"));
        Assert.Equal("env-model", sut.GetEffectiveOptions().Model);

        await sut.SaveAsync("https://store.test", "store-model", apiKey: null); // saves → Invalidate()

        Assert.Equal("store-model", sut.GetEffectiveOptions().Model);
        Assert.Equal("https://store.test", sut.GetEffectiveOptions().Endpoint);
    }

    /// <summary>Sem endpoint no request nem na config efetiva, o teste falha com "endpoint is required".</summary>
    [Fact]
    public async Task Test_BlankEndpoint_RequiresEndpoint()
    {
        var result = await Sut().TestAsync(new TestChatConnectionRequest());

        Assert.False(result.Ok);
        Assert.Equal("endpoint is required", result.Detail);
    }

    /// <summary>O probe envia Bearer da key e sinaliza se o model consta na lista do provider.</summary>
    [Fact]
    public async Task Test_Probe_ModelListed_TrueAndFalse()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"m1"},{"id":"m2"}]}""");
        var sut = Sut(probe: () => new HttpClient(handler));

        var listed = await sut.TestAsync(new TestChatConnectionRequest
        {
            Endpoint = "https://stub.local",
            Model = "m2",
            ApiKey = "sk-probe-9999"
        });
        Assert.True(listed.Ok);
        Assert.True(listed.ModelListed);
        Assert.Equal("Bearer sk-probe-9999", handler.LastAuth);

        var absent = await sut.TestAsync(new TestChatConnectionRequest
        {
            Endpoint = "https://stub.local",
            Model = "nope"
        });
        Assert.True(absent.Ok);
        Assert.False(absent.ModelListed);
    }

    /// <summary>Resposta não-2xx do provider vira falha com detalhe sanitizado ("HTTP 401").</summary>
    [Fact]
    public async Task Test_Probe_NonSuccess_SanitizedDetail()
    {
        var sut = Sut(probe: () => new HttpClient(
            new StubHandler(HttpStatusCode.Unauthorized, """{"error":"bad key"}""")));

        var result = await sut.TestAsync(new TestChatConnectionRequest
        {
            Endpoint = "https://stub.local",
            ApiKey = "sk-wrong"
        });

        Assert.False(result.Ok);
        Assert.Equal("HTTP 401", result.Detail);
    }

    /// <summary>Sem endpoint no override nem na config efetiva, a listagem falha rápido.</summary>
    [Fact]
    public async Task ListModels_BlankEndpoint_RequiresEndpoint()
    {
        var result = await Sut().ListModelsAsync(null);

        Assert.False(result.Ok);
        Assert.Equal("endpoint is required", result.Detail);
        Assert.Empty(result.Models);
    }

    /// <summary>O probe de modelos devolve os ids do formato OpenAI (data[].id)
    /// com o Bearer da key efetiva; o override do form vence a config.</summary>
    [Fact]
    public async Task ListModels_Probe_ReturnsIds_WithEffectiveKey()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"data":[{"id":"m1"},{"id":"m2"},{"id":"m1"}]}""");
        var sut = Sut(EnvOpenAi(apiKey: "sk-env-1234"), probe: () => new HttpClient(handler));

        var res = await sut.ListModelsAsync("https://stub.local");

        Assert.True(res.Ok);
        Assert.Equal(["m1", "m2"], res.Models);
        Assert.Equal("Bearer sk-env-1234", handler.LastAuth);
    }

    /// <summary>Formato nativo do Ollama (models[].name) também popula o combo.</summary>
    [Fact]
    public async Task ListModels_OllamaNativeShape_ReturnsNames()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"models":[{"name":"llama3.1:8b"},{"model":"qwen3:4b"}]}""");
        var sut = Sut(probe: () => new HttpClient(handler));

        var res = await sut.ListModelsAsync("http://ollama.local");

        Assert.True(res.Ok);
        Assert.Equal(["llama3.1:8b", "qwen3:4b"], res.Models);
    }

    /// <summary>Sem override, a lista usa o endpoint da config efetiva (env).</summary>
    [Fact]
    public async Task ListModels_NoOverride_UsesEffectiveEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"env-m"}]}""");
        var sut = Sut(EnvOpenAi(endpoint: "https://env.test"), probe: () => new HttpClient(handler));

        var res = await sut.ListModelsAsync(null);

        Assert.True(res.Ok);
        Assert.Equal(["env-m"], res.Models);
    }

    /// <summary>Resposta não-2xx do provider vira lista vazia com detalhe sanitizado.</summary>
    [Fact]
    public async Task ListModels_NonSuccess_SanitizedDetail()
    {
        var sut = Sut(probe: () => new HttpClient(
            new StubHandler(HttpStatusCode.Unauthorized, """{"error":"bad key"}""")));

        var res = await sut.ListModelsAsync("https://stub.local");

        Assert.False(res.Ok);
        Assert.Equal("HTTP 401", res.Detail);
        Assert.Empty(res.Models);
    }

    /// <summary>O DTO serializado nunca contém a key — só o hint mascarado dos 4 últimos caracteres.</summary>
    [Fact]
    public async Task Describe_NeverEchoesApiKey()
    {
        var sut = Sut();
        const string secret = "sk-supersecret-315f";
        await sut.SaveAsync("https://store.test", "m", secret);

        var dto = await sut.DescribeAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(dto);

        Assert.DoesNotContain(secret, json);
        Assert.Equal("••••315f", dto.ApiKeyHint);
    }

    /// <summary>Handler fake que responde o probe com status/corpo fixos e captura o Authorization.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        /// <summary>Último header Authorization recebido pelo probe.</summary>
        public string? LastAuth { get; private set; }

        /// <summary>Responde a requisição com o status e o corpo configurados.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuth = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Store de segredos em memória para isolar o serviço sob teste.</summary>
    private sealed class FakeSecretStore : IIntegrationSecretStore
    {
        private readonly Dictionary<string, string> _secrets = new();

        /// <summary>Último provider que teve key gravada.</summary>
        public string? LastSetProvider { get; private set; }

        /// <summary>True quando a key do slug "chat" foi removida.</summary>
        public bool RemovedChat { get; private set; }

        /// <summary>Retorna o segredo armazenado do provider, ou null.</summary>
        public Task<string?> GetAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(provider, out var s) ? s : (string?)null);

        /// <summary>Retorna os metadados mascarados do segredo armazenado, ou null.</summary>
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(provider, out var s)
                ? new IntegrationSecretInfo(provider, s.Length >= 4 ? s[^4..] : s, DateTimeOffset.UtcNow)
                : (IntegrationSecretInfo?)null);

        /// <summary>Grava o segredo do provider em memória.</summary>
        public Task SetAsync(string provider, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[provider] = secret;
            LastSetProvider = provider;
            return Task.CompletedTask;
        }

        /// <summary>Remove o segredo do provider, marcando quando for o slug "chat".</summary>
        public Task<bool> RemoveAsync(string provider, CancellationToken cancellationToken = default)
        {
            if (provider == IntegrationProviders.Chat)
                RemovedChat = true;
            return Task.FromResult(_secrets.Remove(provider));
        }
    }

    /// <summary>Libera o container de DI e a conexão SQLite dos testes.</summary>
    public void Dispose()
    {
        _services.Dispose();
        _conn.Dispose();
    }
}
