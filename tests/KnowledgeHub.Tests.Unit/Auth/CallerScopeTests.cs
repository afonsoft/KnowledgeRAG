using System.Text.Json.Nodes;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Tests.Unit.Auth;

/// <summary>
/// SPEC-20260923-source-authorization RF-002/RF-004: scope parsing semantics
/// (null = unrestricted, [] = deny-all) and per-caller catalog filtering.
/// </summary>
public class CallerScopeTests
{
    [Fact]
    public void FromJson_NullColumns_IsUnrestricted()
    {
        var scope = CallerScope.FromJson(Guid.NewGuid(), null, null);
        Assert.True(scope.IsUnrestricted);
        Assert.Equal("*", scope.SourceFingerprint);
        Assert.True(scope.AllowsSource(Guid.NewGuid()));
        Assert.True(scope.AllowsTool("anything"));
    }

    [Fact]
    public void FromJson_EmptyArray_IsDenyAll_NotUnrestricted()
    {
        var scope = CallerScope.FromJson(Guid.NewGuid(), "[]", "[]");
        Assert.False(scope.IsUnrestricted);
        Assert.NotNull(scope.AllowedSourceIds);
        Assert.NotNull(scope.AllowedTools);
        Assert.False(scope.AllowsSource(Guid.NewGuid()));
        Assert.False(scope.AllowsTool("search_knowledge"));
    }

    [Fact]
    public void FromJson_MalformedJson_FallsBackToUnrestricted()
    {
        var scope = CallerScope.FromJson(Guid.NewGuid(), "{not-json", "42");
        Assert.Null(scope.AllowedSourceIds);
        Assert.Null(scope.AllowedTools);
    }

    [Fact]
    public void SourceFingerprint_DiffersPerScope()
    {
        var a = CallerScope.FromJson(Guid.NewGuid(), $"[\"{Guid.NewGuid()}\"]", null);
        var b = CallerScope.FromJson(Guid.NewGuid(), $"[\"{Guid.NewGuid()}\"]", null);
        Assert.NotEqual(a.SourceFingerprint, b.SourceFingerprint);
        Assert.Equal("*", CallerScope.Unrestricted.SourceFingerprint);
    }

    [Fact]
    public async Task Catalog_FiltersPerCallerScope()
    {
        var sourceA = Guid.NewGuid();
        var sourceB = Guid.NewGuid();
        var catalog = new DynamicToolCatalog(
            [new StubProvider(
                Tool("search_knowledge"),
                Tool("query_a", sourceA),
                Tool("query_b", sourceB))],
            new StubNotifier(), new ConfigurationBuilder().Build());

        // Scoped key: source A + name allowlist → only search_knowledge + query_a.
        var scoped = await catalog.GetToolsAsync(
            Services(new CallerScope(Guid.NewGuid(),
                new HashSet<Guid> { sourceA },
                new HashSet<string>(["search_knowledge", "query_a"], StringComparer.OrdinalIgnoreCase))),
            CancellationToken.None);
        Assert.Equal(["query_a", "search_knowledge"], scoped.Select(t => t.Name).Order().ToArray());

        // Unrestricted caller sees the full aggregate.
        var all = await catalog.GetToolsAsync(
            Services(CallerScope.Unrestricted), CancellationToken.None);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task Catalog_ScopeWithoutToolAllowlist_StillFiltersSourceTools()
    {
        var sourceA = Guid.NewGuid();
        var catalog = new DynamicToolCatalog(
            [new StubProvider(Tool("search_knowledge"), Tool("query_a", sourceA), Tool("query_b", Guid.NewGuid()))],
            new StubNotifier(), new ConfigurationBuilder().Build());

        var scoped = await catalog.GetToolsAsync(
            Services(new CallerScope(Guid.NewGuid(), new HashSet<Guid> { sourceA }, null)),
            CancellationToken.None);
        Assert.Equal(["query_a", "search_knowledge"], scoped.Select(t => t.Name).Order().ToArray());
    }

    private static CatalogTool Tool(string name, Guid? sourceId = null) =>
        new()
        {
            Name = name,
            Description = name,
            InputSchema = new JsonObject { ["type"] = "object" },
            SourceId = sourceId,
            Handler = (_, _) => ValueTask.FromResult(new CallToolResult())
        };

    private static IServiceProvider Services(CallerScope scope)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICallerScopeProvider>(new FixedScope(scope));
        return services.BuildServiceProvider();
    }

    private sealed class FixedScope(CallerScope scope) : ICallerScopeProvider
    {
        public Task<CallerScope> GetAsync(CancellationToken ct) => Task.FromResult(scope);
    }

    private sealed class StubProvider(IReadOnlyList<CatalogTool> tools) : IToolProvider
    {
        public StubProvider(params CatalogTool[] tools) : this((IReadOnlyList<CatalogTool>)tools) { }
        public Task<IReadOnlyList<CatalogTool>> GetToolsAsync(IServiceProvider services, CancellationToken ct) =>
            Task.FromResult(tools);
    }

    private sealed class StubNotifier : IToolCatalogChangeNotifier
    {
        public long Version => 1;
        public Task NotifyToolsChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
