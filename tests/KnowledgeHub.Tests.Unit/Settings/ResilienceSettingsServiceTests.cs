using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Resilience;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using KnowledgeHub.Tests.Unit.Server;

namespace KnowledgeHub.Tests.Unit.Settings;

/// <summary>
/// SPEC-20260928-resilience-tool-fallback-wiring RF-004: snapshot resolution
/// (store row → Resilience:Fallback config → defaults), masked apiKey
/// round-trip, and invalidation on save/clear.
/// </summary>
public sealed class ResilienceSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;

    public ResilienceSettingsServiceTests()
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

    private readonly McpProxySourceServiceTests.FakeSecretStore _secrets = new();

    private ResilienceSettingsService Sut(FallbackOptions? env = null) => new(
        Options.Create(env ?? new FallbackOptions()),
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _secrets,
        new FakeNotifier(),
        NullLogger<ResilienceSettingsService>.Instance);

    private sealed class FakeNotifier : KnowledgeHub.Server.Mcp.IToolCatalogChangeNotifier
    {
        public long Version { get; private set; }
        public Task NotifyToolsChangedAsync(CancellationToken ct = default)
        { Version++; return Task.CompletedTask; }
    }

    private static readonly SaveResilienceSettingsRequest StoreRequest = new()
    {
        Mode = "enforce",
        MaxFallbackAttempts = 3,
        ChatFallbacks =
        [
            new ChatFallbackOptionDto
            {
                Provider = "ollama", Endpoint = "http://h:11434",
                Model = "llama3", ApiKey = "sekret"
            }
        ],
        ToolCapabilities = new Dictionary<string, List<string>>
        {
            ["WebSearch"] = ["tavily", "firecrawl"]
        }
    };

    [Fact]
    public async Task NoRow_ReturnsConfiguredOptions_SourceEnv()
    {
        var sut = Sut(new FallbackOptions { Mode = "observe", MaxFallbackAttempts = 4 });
        var dto = await sut.DescribeAsync();
        Assert.Equal("observe", dto.Mode);
        Assert.Equal(4, dto.MaxFallbackAttempts);
        Assert.Equal("env", dto.Source);
        Assert.Null(dto.UpdatedAt);
    }

    [Fact]
    public async Task SaveThenDescribe_StoreWinsOverEnv_ApiKeyMasked()
    {
        var sut = Sut(new FallbackOptions { Mode = "disabled" });
        await sut.SaveAsync(StoreRequest);

        var dto = await sut.DescribeAsync();
        Assert.Equal("enforce", dto.Mode);
        Assert.Equal(3, dto.MaxFallbackAttempts);
        Assert.Equal("store", dto.Source);
        Assert.Equal("***", dto.ChatFallbacks[0].ApiKey);
        Assert.Equal("http://h:11434", dto.ChatFallbacks[0].Endpoint);
        Assert.NotNull(dto.UpdatedAt);

        var effective = sut.GetEffective();
        Assert.Equal("enforce", effective.Mode);
        Assert.Equal("sekret", effective.ChatFallbacks[0].ApiKey); // real key inside
        Assert.Equal(["tavily", "firecrawl"], effective.ToolCapabilities["WebSearch"]);
    }

    [Fact]
    public async Task Clear_RevertsToEnvSnapshot()
    {
        var sut = Sut(new FallbackOptions { Mode = "observe" });
        await sut.SaveAsync(StoreRequest);
        Assert.Equal("enforce", sut.GetEffective().Mode);

        await sut.ClearAsync();
        var dto = await sut.DescribeAsync();
        Assert.Equal("env", dto.Source);
        Assert.Equal("observe", sut.GetEffective().Mode);
    }

    [Fact]
    public async Task MaskedApiKey_KeepsStoredValue_OnReSave()
    {
        var sut = Sut();
        await sut.SaveAsync(StoreRequest);
        await sut.SaveAsync(new SaveResilienceSettingsRequest
        {
            Mode = "observe",
            MaxFallbackAttempts = 1,
            ChatFallbacks =
            [
                new ChatFallbackOptionDto
                {
                    Provider = "ollama", Endpoint = "http://h:11434",
                    Model = "llama3", ApiKey = "***" // masked round-trip
                }
            ]
        });
        Assert.Equal("sekret", sut.GetEffective().ChatFallbacks[0].ApiKey);
        Assert.Equal("observe", sut.GetEffective().Mode);
    }

    [Fact]
    public async Task Save_NeverPersistsPlaintextKey()
    {
        // SPEC-20260929 RF-001: apiKey moves to the secret store — the row
        // JSON carries only hasKey.
        var sut = Sut();
        await sut.SaveAsync(StoreRequest);

        using var scope = _provider.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>()
            .ResilienceSettings.SingleAsync();
        Assert.DoesNotContain("sekret", row.ChatFallbacksJson!);
        Assert.Contains("\"hasKey\":true", row.ChatFallbacksJson);
        var slot = Assert.Single(_secrets.Store, kv => kv.Key.StartsWith("resilience:fallback:"));
        Assert.Equal("sekret", slot.Value);
    }

    [Fact]
    public async Task Save_OmittedProvider_PreservesPrevious()
    {
        // SPEC-20260929 RF-002: UI rows without provider keep the stored
        // provider — an Ollama alternate must not silently become OpenAI.
        var sut = Sut();
        await sut.SaveAsync(StoreRequest); // provider: ollama
        await sut.SaveAsync(new SaveResilienceSettingsRequest
        {
            Mode = "observe",
            MaxFallbackAttempts = 1,
            ChatFallbacks =
            [
                new ChatFallbackOptionDto { Endpoint = "http://h:11434", Model = "llama3" }
            ]
        });

        Assert.Equal("ollama", sut.GetEffective().ChatFallbacks[0].Provider);
    }

    [Fact]
    public async Task LegacyPlaintextRow_MigratesToSecretStore()
    {
        // SPEC-20260929 RF-001: rows written by the old format are migrated on
        // first load — key lands in the store, JSON stripped.
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            db.ResilienceSettings.Add(new ResilienceSettings
            {
                Id = 1,
                Mode = "enforce",
                MaxFallbackAttempts = 2,
                ChatFallbacksJson = """[{"provider":"openai","endpoint":"https://api.openai.com","model":"gpt-4o","apiKey":"sk-legacy"}]""",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var sut = Sut();
        Assert.Equal("sk-legacy", sut.GetEffective().ChatFallbacks[0].ApiKey);

        using var verify = _provider.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>()
            .ResilienceSettings.SingleAsync();
        Assert.DoesNotContain("sk-legacy", row.ChatFallbacksJson);
        var slot = Assert.Single(_secrets.Store, kv => kv.Key.StartsWith("resilience:fallback:"));
        Assert.Equal("sk-legacy", slot.Value);
    }

    [Fact]
    public async Task MaskedSave_BeforeFirstRead_KeepsLegacyPlaintextKey()
    {
        // Devin-review #398: PUT landing before any snapshot load must migrate
        // the legacy plaintext key first — the masked "***" save would
        // otherwise overwrite the only copy while the vault is empty.
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            db.ResilienceSettings.Add(new ResilienceSettings
            {
                Id = 1,
                Mode = "enforce",
                MaxFallbackAttempts = 2,
                ChatFallbacksJson = """[{"provider":"openai","endpoint":"https://api.openai.com","model":"gpt-4o","apiKey":"sk-legacy"}]""",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var sut = Sut();
        await sut.SaveAsync(new SaveResilienceSettingsRequest
        {
            Mode = "enforce",
            MaxFallbackAttempts = 2,
            ChatFallbacks =
            [
                new ChatFallbackOptionDto
                {
                    Provider = "openai", Endpoint = "https://api.openai.com",
                    Model = "gpt-4o", ApiKey = "***"
                }
            ]
        });

        Assert.Equal("sk-legacy", sut.GetEffective().ChatFallbacks[0].ApiKey);
    }

    [Fact]
    public async Task ReorderedAlternates_KeepTheirOwnKeys()
    {
        // Devin-review #398: secrets bind to provider|endpoint|model — a
        // reorder must never hand provider A's key to provider B's endpoint.
        var sut = Sut();
        await sut.SaveAsync(new SaveResilienceSettingsRequest
        {
            Mode = "enforce",
            MaxFallbackAttempts = 2,
            ChatFallbacks =
            [
                new ChatFallbackOptionDto
                {
                    Provider = "openai", Endpoint = "https://api.openai.com",
                    Model = "gpt-4o", ApiKey = "sk-openai"
                },
                new ChatFallbackOptionDto
                {
                    Provider = "ollama", Endpoint = "http://h:11434",
                    Model = "llama3", ApiKey = "sk-ollama"
                }
            ]
        });

        // Same entries, reversed order, masked keys.
        await sut.SaveAsync(new SaveResilienceSettingsRequest
        {
            Mode = "enforce",
            MaxFallbackAttempts = 2,
            ChatFallbacks =
            [
                new ChatFallbackOptionDto
                {
                    Provider = "ollama", Endpoint = "http://h:11434",
                    Model = "llama3", ApiKey = "***"
                },
                new ChatFallbackOptionDto
                {
                    Provider = "openai", Endpoint = "https://api.openai.com",
                    Model = "gpt-4o", ApiKey = "***"
                }
            ]
        });

        var effective = sut.GetEffective();
        Assert.Equal("sk-ollama", effective.ChatFallbacks[0].ApiKey);
        Assert.Equal("sk-openai", effective.ChatFallbacks[1].ApiKey);
    }

    [Fact]
    public async Task Invalidate_ReloadsFromStore()
    {
        var sut = Sut();
        Assert.Equal("disabled", sut.GetEffective().Mode);

        // External writer changes the row behind the service's back.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        db.ResilienceSettings.Add(new ResilienceSettings
        {
            Mode = "enforce",
            MaxFallbackAttempts = 5
        });
        await db.SaveChangesAsync();

        Assert.Equal("disabled", sut.GetEffective().Mode); // snapshot cached
        sut.Invalidate();
        Assert.Equal("enforce", sut.GetEffective().Mode);
        Assert.Equal(5, sut.GetEffective().MaxFallbackAttempts);
    }

    [Fact]
    public async Task EmptyStoredCapabilities_InheritConfiguredMap()
    {
        var env = new FallbackOptions
        {
            ToolCapabilities = new Dictionary<string, List<string>>
            {
                ["WebSearch"] = ["tavily"]
            }
        };
        var sut = Sut(env);
        await sut.SaveAsync(new SaveResilienceSettingsRequest
        {
            Mode = "observe",
            MaxFallbackAttempts = 1,
            ToolCapabilities = null // not provided → null JSON → inherit
        });
        Assert.Equal(["tavily"], sut.GetEffective().ToolCapabilities["WebSearch"]);
    }
}
