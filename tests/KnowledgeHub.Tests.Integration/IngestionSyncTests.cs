using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-03 ACs: vault sync indexes docs/chunks, hash short-circuit, deletion propagates.
public class IngestionSyncTests : IClassFixture<IngestionSyncTests.Fixture>, IDisposable
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-ing-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["Ingestion:MaxTokens"] = "500",
                    ["Ingestion:OverlapTokens"] = "50"
                }));
        }
    }

    private readonly HttpClient _client;
    private readonly string _vault;

    public IngestionSyncTests(Fixture factory)
    {
        _client = TestAuth.Login(factory);
        _vault = Path.Join(Path.GetTempPath(), $"vault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_vault);
    }

    public void Dispose()
    {
        try { Directory.Delete(_vault, recursive: true); } catch { }
    }

    private async Task<KnowledgeSourceDto> CreateVaultSource()
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"vault-{Guid.NewGuid():N}",
            type = "ObsidianVault",
            configuration = new { path = _vault },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
    }

    [Fact]
    public async Task Sync_IndexesMarkdownFiles_WithChunks()
    {
        await File.WriteAllTextAsync(Path.Join(_vault, "a.md"), "# Nota A\n\nconteúdo sobre arquitetura limpa");
        await File.WriteAllTextAsync(Path.Join(_vault, "b.md"), "---\ntags: [x]\n---\n# Nota B\n\noutro conteúdo");
        await File.WriteAllTextAsync(Path.Join(_vault, "c.md"), "sem header");

        var source = await CreateVaultSource();
        var response = await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<SyncResultDto>();
        Assert.Equal("completed", result!.Status);
        Assert.Equal(3, result.DocumentsProcessed);
        Assert.True(result.ChunksCreated >= 3);

        var docs = await _client.GetFromJsonAsync<List<KnowledgeDocumentDto>>($"/api/sources/{source.Id}/documents");
        Assert.Equal(3, docs!.Count);
    }

    [Fact]
    public async Task Sync_UnchangedFile_IsSkipped_AndDeletedFile_IsRemoved()
    {
        var file = Path.Join(_vault, "keep.md");
        await File.WriteAllTextAsync(file, "# Keep\n\nestável");
        var source = await CreateVaultSource();

        await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);
        var second = await (await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null))
            .Content.ReadFromJsonAsync<SyncResultDto>();
        Assert.Equal(1, second!.DocumentsSkipped);
        Assert.Equal(0, second.DocumentsProcessed);

        File.Delete(file);
        var third = await (await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null))
            .Content.ReadFromJsonAsync<SyncResultDto>();
        Assert.Equal(1, third!.DocumentsRemoved);

        var docs = await _client.GetFromJsonAsync<List<KnowledgeDocumentDto>>($"/api/sources/{source.Id}/documents");
        Assert.Empty(docs!);
    }

    [Fact]
    public async Task Sync_ObsidianDirAndHiddenDirs_AreExcluded()
    {
        Directory.CreateDirectory(Path.Join(_vault, ".obsidian"));
        Directory.CreateDirectory(Path.Join(_vault, ".hidden"));
        await File.WriteAllTextAsync(Path.Join(_vault, ".obsidian", "config.md"), "secret");
        await File.WriteAllTextAsync(Path.Join(_vault, ".hidden", "x.md"), "hidden");
        await File.WriteAllTextAsync(Path.Join(_vault, "visible.md"), "# V\n\nok");

        var source = await CreateVaultSource();
        var result = await (await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null))
            .Content.ReadFromJsonAsync<SyncResultDto>();

        Assert.Equal(1, result!.DocumentsProcessed);
    }

    [Fact]
    public async Task Sync_MissingPath_ReturnsFailed_AndRecordsStatusOnSource()
    {
        var missing = Path.Join(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}");
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"gone-{Guid.NewGuid():N}",
            type = "ObsidianVault",
            configuration = new { path = missing }
        });
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;

        var result = await (await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null))
            .Content.ReadFromJsonAsync<SyncResultDto>();
        Assert.Equal("failed", result!.Status);

        // RF-002: failure is recorded on the source (mount-unavailable hint).
        var fetched = await _client.GetFromJsonAsync<KnowledgeSourceDto>($"/api/sources/{source.Id}");
        Assert.Equal("failed", fetched!.LastSyncStatus);
        Assert.Contains("mount unavailable", fetched.LastError);

        // Mount "comes back": recreate the path → next sync succeeds and clears the error.
        Directory.CreateDirectory(missing);
        try
        {
            await File.WriteAllTextAsync(Path.Join(missing, "back.md"), "# Back\n\nmount recovered");
            var result2 = await (await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null))
                .Content.ReadFromJsonAsync<SyncResultDto>();
            Assert.Equal("completed", result2!.Status);

            var fetched2 = await _client.GetFromJsonAsync<KnowledgeSourceDto>($"/api/sources/{source.Id}");
            Assert.Equal("completed", fetched2!.LastSyncStatus);
            Assert.Null(fetched2.LastError);
        }
        finally
        {
            Directory.Delete(missing, recursive: true);
        }
    }

    [Fact]
    public async Task Sync_VaultFile_WithNulBytes_IsSanitizedAndIndexed()
    {
        // Postgres `text` rejects U+0000 ("invalid byte sequence 0x00") —
        // vault files with embedded NULs must be stripped before persistence.
        await File.WriteAllTextAsync(Path.Join(_vault, "withnul.md"),
            "# Nota com Nul\n\nantes \0 depois do byte nulo");

        var source = await CreateVaultSource();
        var result = await (await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null))
            .Content.ReadFromJsonAsync<SyncResultDto>();

        Assert.Equal("completed", result!.Status);
        Assert.Equal(1, result.DocumentsProcessed);
        Assert.Equal(0, result.DocumentsFailed);

        var search = await _client.GetFromJsonAsync<SearchResponse>("/api/search?query=depois byte nulo&topK=5");
        var hit = Assert.Single(search!.Results);
        Assert.DoesNotContain('\0', hit.ChunkText);
    }

    [Fact]
    public async Task Search_ReturnsRankedResults_AfterSync()
    {
        await File.WriteAllTextAsync(Path.Join(_vault, "vectors.md"),
            "# Busca Vetorial\n\nembeddings vetores similaridade cosseno busca semântica");
        await File.WriteAllTextAsync(Path.Join(_vault, "cake.md"),
            "# Bolo\n\nreceita de bolo de chocolate com farinha");

        var source = await CreateVaultSource();
        await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);

        var search = await _client.GetFromJsonAsync<SearchResponse>("/api/search?query=embeddings vetores&topK=5");
        Assert.NotEmpty(search!.Results);
        Assert.Equal("Busca Vetorial", search.Results[0].DocumentTitle);
        Assert.True(search.Results[0].Score >= search.Results[^1].Score);
    }
}
