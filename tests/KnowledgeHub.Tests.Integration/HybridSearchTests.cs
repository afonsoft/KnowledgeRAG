using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260914-hybrid-retrieval ACs: lexical wins on literals,
// semantic mode preserves pre-hybrid results, source filter, FTS safety,
// index maintenance across sync/delete.
public class HybridSearchTests : IClassFixture<HybridSearchTests.Fixture>, IDisposable
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-hybrid-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    private readonly HttpClient _client;
    private readonly string _vault;

    public HybridSearchTests(Fixture factory)
    {
        _client = TestAuth.Login(factory);
        _vault = Path.Join(Path.GetTempPath(), $"vault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_vault);
    }

    public void Dispose()
    {
        try { Directory.Delete(_vault, recursive: true); } catch (Exception ex) { _ = ex.Message; }
    }

    private async Task<KnowledgeSourceDto> CreateAndSyncVault(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name,
            type = "ObsidianVault",
            configuration = new { path = _vault },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        var source = (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        var sync = await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);
        sync.EnsureSuccessStatusCode();
        return source;
    }

    [Fact]
    public async Task Hybrid_ExactLiteral_RanksFirst_ViaLexicalRank()
    {
        // Out-of-vocabulary token: the vector ranker cannot score it
        // meaningfully, but FTS must rank the containing doc #1.
        await File.WriteAllTextAsync(Path.Join(_vault, "runbook.md"),
            "# Runbook\n\nRestart procedure uses token ZXQW77ALPHA for the freeze window.");
        await File.WriteAllTextAsync(Path.Join(_vault, "unrelated.md"),
            "# Notes\n\ncompletely unrelated content about gardening and soil");

        await CreateAndSyncVault($"lit-{Guid.NewGuid():N}");

        var hybrid = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=ZXQW77ALPHA&topK=5&mode=hybrid");

        Assert.NotEmpty(hybrid!.Results);
        Assert.Equal("Runbook", hybrid.Results[0].DocumentTitle);
        Assert.NotNull(hybrid.Results[0].ScoreBreakdown);
        Assert.Equal(1, hybrid.Results[0].ScoreBreakdown!.LexicalRank);
    }

    [Fact]
    public async Task Lexical_OnlyMode_FindsExactTerm()
    {
        await File.WriteAllTextAsync(Path.Join(_vault, "spec.md"),
            "# Spec\n\nidentifier KLMN99BETA appears in the spec table");
        await CreateAndSyncVault($"lex-{Guid.NewGuid():N}");

        var lexical = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=KLMN99BETA&topK=5&mode=lexical");

        Assert.NotEmpty(lexical!.Results);
        Assert.Equal("Spec", lexical.Results[0].DocumentTitle);
    }

    [Fact]
    public async Task Semantic_Mode_PreservesVectorResults()
    {
        await File.WriteAllTextAsync(Path.Join(_vault, "vectors.md"),
            "# Busca Vetorial\n\nembeddings vetores similaridade cosseno busca semântica");
        await File.WriteAllTextAsync(Path.Join(_vault, "cake.md"),
            "# Bolo\n\nreceita de bolo de chocolate com farinha");
        await CreateAndSyncVault($"sem-{Guid.NewGuid():N}");

        var semantic = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=embeddings vetores&topK=5&mode=semantic");

        Assert.NotEmpty(semantic!.Results);
        Assert.Equal("Busca Vetorial", semantic.Results[0].DocumentTitle);
        Assert.Null(semantic.Results[0].ScoreBreakdown);
    }

    [Fact]
    public async Task SourceFilter_RestrictsLexicalResults()
    {
        var shared = "UNIQTOKEN42";
        await File.WriteAllTextAsync(Path.Join(_vault, "only.md"),
            $"# Only\n\ncontains {shared}");
        var source = await CreateAndSyncVault($"filt-{Guid.NewGuid():N}");

        var other = Guid.NewGuid();
        // SPEC-20260929 RF-005: an unmatched strict scope now flows into the
        // relaxation cascade — pin allowRelaxation=false to assert the strict
        // contract, and verify relaxed hits are flagged when allowed.
        var filtered = await _client.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={shared}&topK=5&mode=lexical&sourceId={other}&allowRelaxation=false");
        Assert.Empty(filtered!.Results);

        var relaxed = await _client.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={shared}&topK=5&mode=lexical&sourceId={other}&allowRelaxation=true");
        Assert.All(relaxed!.Results, r => Assert.True(r.IsRelaxed));

        var matched = await _client.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={shared}&topK=5&mode=lexical&sourceId={source.Id}");
        Assert.Single(matched!.Results);
        Assert.Equal(source.Id, matched.Results[0].SourceId);
    }

    [Fact]
    public async Task FtsSyntaxGarbage_Returns200_NotException()
    {
        await File.WriteAllTextAsync(Path.Join(_vault, "x.md"), "# X\n\nplain text");
        await CreateAndSyncVault($"syn-{Guid.NewGuid():N}");

        var response = await _client.GetAsync(
            "/api/search?query=a%22%20NEAR%2F2%20*(unbalanced&topK=5&mode=hybrid");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeletedFile_LeavesLexicalIndex()
    {
        var file = Path.Join(_vault, "ephemeral.md");
        await File.WriteAllTextAsync(file, "# Ephemeral\n\nunique marker DELTATOKEN55 here");
        var source = await CreateAndSyncVault($"del-{Guid.NewGuid():N}");

        var before = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=DELTATOKEN55&topK=5&mode=lexical");
        Assert.NotEmpty(before!.Results);

        File.Delete(file);
        var resync = await _client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);
        resync.EnsureSuccessStatusCode();

        var after = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=DELTATOKEN55&topK=5&mode=lexical");
        Assert.Empty(after!.Results);
    }

    [Fact]
    public async Task InvalidMode_Returns400()
    {
        var response = await _client.GetAsync("/api/search?query=x&mode=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
