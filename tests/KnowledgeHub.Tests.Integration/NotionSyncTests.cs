using System.Net;
using System.Net.Http.Json;
using System.Text;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260919-notion-connector CA-001/CA-002/CA-003/CA-004/CA-006:
// end-to-end sync against a fake Notion REST API — indexing + lexical search,
// incremental skip (no block calls when last_edited_time is unchanged),
// refetch on edit, purge on unshare, and invalid-token failure.
public class NotionSyncTests : IClassFixture<NotionSyncTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-notion-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["Security:Egress:AllowPrivateNetworks"] = "true" // local fake API targets
                }));
        }
    }

    /// <summary>Minimal fake Notion REST API over HttpListener. Routes:
    /// GET /v1/users/me, POST /v1/search, GET /v1/blocks/{id}/children.</summary>
    private sealed class FakeNotion : IDisposable
    {
        private readonly HttpListener _listener = new();

        public string BaseUrl { get; }
        public List<string> Calls { get; } = [];
        public HttpStatusCode UsersMeStatus { get; set; } = HttpStatusCode.OK;
        public string SearchBody { get; set; } =
            """{"results":[],"has_more":false,"next_cursor":null}""";
        public Dictionary<string, string> BlockBodies { get; } = new();

        public FakeNotion()
        {
            var port = 18300 + Random.Shared.Next(500);
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int BlockCallCount => Calls.Count(c => c.StartsWith("GET /v1/blocks/"));

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }

                var path = ctx.Request.Url!.AbsolutePath;
                var key = ctx.Request.HttpMethod + " " + path;
                lock (Calls) Calls.Add(key);

                var (status, body) = key switch
                {
                    "GET /v1/users/me" => (UsersMeStatus, UsersMeStatus == HttpStatusCode.OK
                        ? """{"object":"user","type":"bot"}"""
                        : """{"object":"error","code":"unauthorized","message":"bad token"}"""),
                    "POST /v1/search" => (HttpStatusCode.OK, SearchBody),
                    _ when key.StartsWith("GET /v1/blocks/") && path.EndsWith("/children")
                        => (HttpStatusCode.OK,
                            BlockBodies.TryGetValue(
                                path["/v1/blocks/".Length..^"/children".Length], out var b)
                                ? b
                                : """{"results":[],"has_more":false,"next_cursor":null}"""),
                    _ => (HttpStatusCode.NotFound,
                        """{"object":"error","code":"object_not_found","message":"not found"}""")
                };

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = (int)status;
                ctx.Response.ContentType = "application/json";
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { break; }
            }
        }

        public void Dispose() { _listener.Stop(); _listener.Close(); }
    }

    private readonly HttpClient _client;

    public NotionSyncTests(Fixture factory) => _client = TestAuth.Login(factory);

    private static string Page(string id, string title, string edited = "2026-09-19T10:00:00Z") => $$$"""
        {"object":"page","id":"{{{id}}}","last_edited_time":"{{{edited}}}",
         "properties":{"title":{"type":"title","title":[{"plain_text":"{{{title}}}"}]}} }
        """;

    private static string Search(params string[] items) =>
        $$$"""{"results":[{{{string.Join(",", items)}}}],"has_more":false,"next_cursor":null}""";

    private static string Blocks(string text) => $$$"""
        {"results":[{"id":"b1","type":"paragraph","has_children":false,
         "paragraph":{"rich_text":[{"plain_text":"{{{text}}}"}]}}],
         "has_more":false,"next_cursor":null}
        """;

    private async Task<KnowledgeSourceDto> CreateNotionSource(FakeNotion api)
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"notion-{Guid.NewGuid():N}",
            type = "Notion",
            configuration = new { token = "ntn_test_secret", apiBaseUrl = api.BaseUrl },
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
    }

    private async Task<SyncResultDto> Sync(Guid id) =>
        (await (await _client.PostAsync($"/api/sources/{id}/sync?wait=true", null))
            .Content.ReadFromJsonAsync<SyncResultDto>())!;

    private async Task<List<KnowledgeDocumentDto>> Docs(Guid id) =>
        (await _client.GetFromJsonAsync<List<KnowledgeDocumentDto>>(
            $"/api/sources/{id}/documents"))!;

    [Fact]
    public async Task NotionSync_IndexesPages_AndServesLexicalSearch()
    {
        using var api = new FakeNotion();
        api.SearchBody = Search(Page("p1", "Alpha"), Page("p2", "Beta"));
        api.BlockBodies["p1"] = Blocks("NOTIONTOKEN42 alpha body");
        api.BlockBodies["p2"] = Blocks("beta body");

        var source = await CreateNotionSource(api);
        var result = await Sync(source.Id);

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.DocumentsProcessed);

        var docs = await Docs(source.Id);
        Assert.Equal(2, docs.Count);

        var search = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=NOTIONTOKEN42&mode=lexical");
        Assert.NotEmpty(search!.Results);
    }

    [Fact]
    public async Task NotionResync_Unchanged_SkipsAll_NoBlockCalls()
    {
        using var api = new FakeNotion();
        api.SearchBody = Search(Page("p1", "Alpha"), Page("p2", "Beta"));
        api.BlockBodies["p1"] = Blocks("a");
        api.BlockBodies["p2"] = Blocks("b");

        var source = await CreateNotionSource(api);
        await Sync(source.Id);

        api.Calls.Clear();
        var result = await Sync(source.Id);

        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.DocumentsProcessed);
        Assert.Equal(2, result.DocumentsSkipped);
        Assert.Equal(0, api.BlockCallCount); // fingerprint hit — no block fetch
    }

    [Fact]
    public async Task NotionResync_EditedPage_RefetchesOnlyThat()
    {
        using var api = new FakeNotion();
        api.SearchBody = Search(Page("p1", "Alpha"), Page("p2", "Beta"));
        api.BlockBodies["p1"] = Blocks("old body");
        api.BlockBodies["p2"] = Blocks("b");

        var source = await CreateNotionSource(api);
        await Sync(source.Id);

        api.SearchBody = Search(Page("p1", "Alpha", "2026-09-19T12:00:00Z"), Page("p2", "Beta"));
        api.BlockBodies["p1"] = Blocks("EDITEDTOKEN77 new body");
        api.Calls.Clear();
        var result = await Sync(source.Id);

        Assert.Equal(1, result.DocumentsProcessed);
        Assert.Equal(1, result.DocumentsSkipped);
        Assert.Equal(1, api.BlockCallCount);

        var search = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=EDITEDTOKEN77&mode=lexical");
        Assert.NotEmpty(search!.Results);
    }

    [Fact]
    public async Task NotionResync_UnsharedPage_Purged()
    {
        using var api = new FakeNotion();
        api.SearchBody = Search(Page("p1", "Alpha"), Page("p2", "Beta"));
        api.BlockBodies["p1"] = Blocks("a");
        api.BlockBodies["p2"] = Blocks("b");

        var source = await CreateNotionSource(api);
        await Sync(source.Id);
        Assert.Equal(2, (await Docs(source.Id)).Count);

        // p2 was unshared/deleted in Notion — /search stops returning it.
        api.SearchBody = Search(Page("p1", "Alpha"));
        var result = await Sync(source.Id);

        Assert.Equal(1, result.DocumentsRemoved);
        var docs = await Docs(source.Id);
        var doc = Assert.Single(docs);
        Assert.Equal("notion://page/p1", doc.UriReference);
    }

    [Fact]
    public async Task NotionSync_InvalidToken_Fails()
    {
        using var api = new FakeNotion { UsersMeStatus = HttpStatusCode.Unauthorized };

        var source = await CreateNotionSource(api);
        var result = await Sync(source.Id);

        Assert.Equal("failed", result.Status);
        Assert.Empty(await Docs(source.Id));

        var dto = await _client.GetFromJsonAsync<KnowledgeSourceDto>($"/api/sources/{source.Id}");
        Assert.Equal("failed", dto!.LastSyncStatus);
        Assert.Contains("token", dto.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ntn_test_secret", dto.LastError);
    }
}
