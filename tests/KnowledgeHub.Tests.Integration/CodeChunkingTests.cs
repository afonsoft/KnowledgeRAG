using System.Net.Http.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260923-code-aware-chunking RF-004/RF-005: a .cs file synced through
/// DocumentFile ingestion produces code-kind chunks with SymbolPath metadata.
/// </summary>
public class CodeChunkingTests : IClassFixture<CodeChunkingTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-chunk-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    private readonly Fixture _factory;
    private readonly HttpClient _client;
    private readonly string _dir;

    public CodeChunkingTests(Fixture factory)
    {
        _factory = factory;
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"chunks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task CsFile_ChunksWithCodeKindAndSymbolPath()
    {
        var cs = """
            namespace Demo;

            public class Svc
            {
                public void Handle()
                {
                    System.Console.WriteLine("hi");
                }
            }
            """;
        await File.WriteAllTextAsync(Path.Join(_dir, "svc.cs"), cs);
        var src = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"cs-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = _dir, glob = "**/*.cs" },
            isActive = true
        });
        src.EnsureSuccessStatusCode();
        var source = (await src.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var chunks = await db.Chunks
            .Where(c => c.Document!.KnowledgeSourceId == source.Id)
            .ToListAsync();

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal("code", c.ChunkKind));
        Assert.Contains(chunks, c => c.SymbolPath != null && c.SymbolPath.Contains("Svc.Handle"));
    }
}
