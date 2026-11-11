using System.Text.Json.Nodes;
using KnowledgeHub.Server.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20261111-mcp-ergonomics-wave2 RF-006: catalog lint — every native tool
/// (providers under <c>Mcp.ToolProviders</c>; upstream proxies are excluded —
/// their schemas come from external servers) must carry a non-empty
/// description and at least one JSON <c>examples</c> entry in its input
/// schema, so the catalog is self-explanatory to agents.
/// </summary>
public class ToolCatalogLintTests : IClassFixture<ToolCatalogLintTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Join(Path.GetTempPath(), $"kh-lint-{Guid.NewGuid():N}.db")
                }));
        }
    }

    private readonly Factory _factory;
    public ToolCatalogLintTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task NativeTools_HaveDescriptionAndExamples()
    {
        using var scope = _factory.Services.CreateScope();
        var providers = scope.ServiceProvider.GetServices<IToolProvider>()
            .Where(p => p.GetType().Namespace == "KnowledgeHub.Server.Mcp.ToolProviders");

        var failures = new List<string>();
        var seen = 0;
        foreach (var provider in providers)
        {
            var tools = await provider.GetToolsAsync(scope.ServiceProvider, CancellationToken.None);
            seen += tools.Count;
            foreach (var tool in tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Description))
                    failures.Add($"{tool.Name}: missing description");
                if (tool.InputSchema["examples"] is not JsonArray { Count: > 0 })
                    failures.Add($"{tool.Name}: missing examples[]");
            }
        }

        // Sanity: the lint must actually enumerate the static native tools.
        Assert.True(seen >= 10, $"expected ≥10 native tools enumerated, got {seen}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
