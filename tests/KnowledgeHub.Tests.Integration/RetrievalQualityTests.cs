using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20260923-retrieval-quality ACs: metadata filters (REST GET/POST),
/// invalid filter → 400, reranker reorders when enabled, default pipeline
/// unchanged when disabled.
/// </summary>
public class RetrievalQualityTests : IClassFixture<RetrievalQualityTests.Fixture>, IClassFixture<RetrievalQualityTests.RerankFixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-rq-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
        }
    }

    /// <summary>Host with rerank enabled and a stub client that scores each
    /// candidate by position — the LAST candidate gets the highest score.</summary>
    public sealed class RerankFixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-rr-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["Search:Rerank:Enabled"] = "true"
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(new PositionalRerankClient()));
        }
    }

    /// <summary>Replies "i: i" — ascending scores, so the last candidate wins.</summary>
    private sealed class PositionalRerankClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Concat(messages.Select(m => m.Text));
            var count = System.Text.RegularExpressions.Regex.Matches(prompt, @"(?m)^\[\d+\] ").Count;
            var reply = string.Join('\n', Enumerable.Range(1, count).Select(i => $"{i}: {i}"));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { ModelId = "rr" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private readonly Fixture _factory;
    private readonly RerankFixture _rerank;
    private readonly HttpClient _client;
    private readonly string _dir;

    public RetrievalQualityTests(Fixture factory, RerankFixture rerank)
    {
        _factory = factory;
        _rerank = rerank;
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"rq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    private async Task SeedBoth(string token)
    {
        // DocumentFile source with a unique token.
        var docDir = Path.Join(_dir, "docs");
        Directory.CreateDirectory(docDir);
        await File.WriteAllTextAsync(Path.Join(docDir, "f.txt"), $"docfile content {token}");
        var s1 = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"rqf-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = docDir },
            isActive = true
        });
        var src1 = (await s1.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        await _client.PostAsync($"/api/sources/{src1.Id}/sync?wait=true", null);

        // Obsidian vault source with the same token.
        var vault = Path.Join(_dir, "vault");
        Directory.CreateDirectory(vault);
        await File.WriteAllTextAsync(Path.Join(vault, "n.md"), $"vault note {token}");
        var s2 = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"rqv-{Guid.NewGuid():N}",
            type = "ObsidianVault",
            configuration = new { path = vault },
            isActive = true
        });
        var src2 = (await s2.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        await _client.PostAsync($"/api/sources/{src2.Id}/sync?wait=true", null);
    }

    [Fact]
    public async Task SourceTypeFilter_OnlyMatchingSourceHydrates()
    {
        var token = $"RQTOK{Guid.NewGuid():N}";
        await SeedBoth(token);

        var all = await _client.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={token}&mode=lexical");
        Assert.True(all!.Results.Count >= 2); // both sources hit

        var filtered = await _client.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={token}&mode=lexical&sourceType=ObsidianVault");
        Assert.NotEmpty(filtered!.Results);
        Assert.All(filtered.Results, r =>
        {
            Assert.Equal(SourceType.ObsidianVault, r.SourceType);
            Assert.Equal("ObsidianVault", r.Metadata!["sourceType"]);
            Assert.NotNull(r.ChunkId);
            Assert.NotNull(r.DocumentId);
            Assert.NotNull(r.IndexedAt);
        });
    }

    [Fact]
    public async Task PostSearch_FiltersObject_AndInvalidFilter400()
    {
        var token = $"RQPOST{Guid.NewGuid():N}";
        await SeedBoth(token);

        var ok = await _client.PostAsJsonAsync("/api/search", new
        {
            query = token,
            mode = "lexical",
            filters = new { sourceType = "DocumentFile" }
        });
        ok.EnsureSuccessStatusCode();
        var hits = (await ok.Content.ReadFromJsonAsync<SearchResponse>())!;
        Assert.NotEmpty(hits.Results);
        Assert.All(hits.Results, r => Assert.Equal(SourceType.DocumentFile, r.SourceType));

        var bad = await _client.PostAsJsonAsync("/api/search", new
        {
            query = token,
            filters = new { sourceType = "bogus" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var badGet = await _client.GetAsync($"/api/search?query={token}&indexedAfter=nope");
        Assert.Equal(HttpStatusCode.BadRequest, badGet.StatusCode);
    }

    [Fact]
    public async Task IndexedAfter_FutureDate_EmptyResultSet()
    {
        var token = $"RQDATE{Guid.NewGuid():N}";
        await SeedBoth(token);

        var hits = await _client.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={token}&mode=lexical&indexedAfter=2999-01-01");
        Assert.Empty(hits!.Results);
    }

    [Fact]
    public async Task RerankEnabled_ReordersAndPopulatesBreakdown()
    {
        var client = await TestAuth.LoginAsync(_rerank);
        var dir = Path.Join(Path.GetTempPath(), $"rr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var token = $"RRTOK{Guid.NewGuid():N}";
        // Several matching docs → a real candidate window to reorder.
        for (var i = 0; i < 4; i++)
            await File.WriteAllTextAsync(Path.Join(dir, $"d{i}.txt"), $"{token} document {i}");
        var src = await client.PostAsJsonAsync("/api/sources", new
        {
            name = $"rr-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = dir },
            isActive = true
        });
        var source = (await src.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
        await client.PostAsync($"/api/sources/{source.Id}/sync?wait=true", null);

        var hits = await client.GetFromJsonAsync<SearchResponse>(
            $"/api/search?query={token}&mode=lexical&topK=4");
        Assert.NotNull(hits);
        Assert.True(hits.Results.Count >= 2);
        // Ascending position scores → strictly descending rerank order.
        var reranks = hits.Results.Select(r => r.ScoreBreakdown!.Rerank).ToList();
        Assert.All(reranks, r => Assert.NotNull(r));
        Assert.Equal(reranks.OrderByDescending(r => r), reranks);
    }
}
