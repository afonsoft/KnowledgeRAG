using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260914-webpage-docfile-connectors ACs: DocumentFile sync,
// per-item warnings, removal propagation, WebPage SSRF guard + opt-in fetch.
public class ConnectorSyncTests : IClassFixture<ConnectorSyncTests.Fixture>, IDisposable
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-conn-{Guid.NewGuid():N}.db");

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
    private readonly string _dir;

    public ConnectorSyncTests(Fixture factory)
    {
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) { _ = ex.Message; }
    }

    private async Task<KnowledgeSourceDto> CreateSource(object configuration, string type = "DocumentFile")
    {
        var response = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"src-{Guid.NewGuid():N}",
            type,
            configuration,
            isActive = true
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KnowledgeSourceDto>())!;
    }

    private async Task<SyncResultDto> Sync(Guid id) =>
        (await (await _client.PostAsync($"/api/sources/{id}/sync?wait=true", null))
            .Content.ReadFromJsonAsync<SyncResultDto>())!;

    [Fact]
    public async Task DocumentFile_DirSync_IndexesSupported_AndWarnsOnUnsupported()
    {
        await File.WriteAllTextAsync(Path.Join(_dir, "notes.md"), "# Notes\n\nunique marker DIRTOKEN77");
        await File.WriteAllTextAsync(Path.Join(_dir, "readme.txt"), "plain text file body");
        await File.WriteAllTextAsync(Path.Join(_dir, "data.bin"), "\x00\x01 binary");

        var source = await CreateSource(new { path = _dir });
        var result = await Sync(source.Id);

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.DocumentsProcessed);
        Assert.NotNull(result.Reason); // skipped item warning surfaced

        var docs = await _client.GetFromJsonAsync<List<KnowledgeDocumentDto>>(
            $"/api/sources/{source.Id}/documents");
        Assert.Equal(2, docs!.Count);

        var search = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=DIRTOKEN77&mode=lexical");
        Assert.NotEmpty(search!.Results);
    }

    [Fact]
    public async Task DocumentFile_ChangeAndDelete_PropagateOnResync()
    {
        var file = Path.Join(_dir, "mutable.txt");
        await File.WriteAllTextAsync(file, "version one V1TOKEN");
        var source = await CreateSource(new { path = _dir });
        await Sync(source.Id);

        await File.WriteAllTextAsync(file, "version two V2TOKEN");
        // The file watcher races this test (debounced incremental sync) — the
        // manual sync may return "skipped" while it holds the per-source gate.
        // Propagation is what matters: poll the index until it reflects v2.
        Assert.True(await EventuallyLexical(source.Id, "V2TOKEN", expectHits: true));
        Assert.True(await EventuallyLexical(source.Id, "V1TOKEN", expectHits: false));

        File.Delete(file);
        Assert.True(await EventuallyEmptyDocuments(source.Id));
    }

    private async Task<bool> EventuallyLexical(Guid sourceId, string query, bool expectHits)
    {
        for (var i = 0; i < 30; i++)
        {
            await Sync(sourceId);
            var r = await _client.GetFromJsonAsync<SearchResponse>(
                $"/api/search?query={query}&mode=lexical");
            if ((r!.Results.Count > 0) == expectHits)
                return true;
            await Task.Delay(300);
        }
        return false;
    }

    private async Task<bool> EventuallyEmptyDocuments(Guid sourceId)
    {
        for (var i = 0; i < 30; i++)
        {
            await Sync(sourceId);
            var docs = await _client.GetFromJsonAsync<List<KnowledgeDocumentDto>>(
                $"/api/sources/{sourceId}/documents");
            if (docs!.Count == 0)
                return true;
            await Task.Delay(300);
        }
        return false;
    }

    [Fact]
    public async Task DocumentFile_MissingPath_Fails()
    {
        var source = await CreateSource(new { path = "/nonexistent/dir-xyz" });
        var result = await Sync(source.Id);
        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task DocumentFile_Pdf_IsIndexed()
    {
        // Minimal PDF built with PdfPig's writer.
        var pdfPath = Path.Join(_dir, "doc.pdf");
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        page.AddText("PDFTOKEN42 extractable pdf text", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        await File.WriteAllBytesAsync(pdfPath, builder.Build());

        var source = await CreateSource(new { path = _dir });
        var result = await Sync(source.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.DocumentsProcessed);

        var search = await _client.GetFromJsonAsync<SearchResponse>(
            "/api/search?query=PDFTOKEN42&mode=lexical");
        Assert.NotEmpty(search!.Results);
    }

    [Fact]
    public async Task WebPage_PrivateHost_RejectedWithoutOptIn()
    {
        var source = await CreateSource(
            new { url = "http://127.0.0.1:9/" }, type: "WebPage");
        var result = await Sync(source.Id);
        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task WebPage_WithOptIn_FetchesLocalPage()
    {
        using var listener = new HttpListener();
        var port = 18700 + Random.Shared.Next(500);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                var bytes = ctx.Request.Url!.AbsolutePath == "/robots.txt"
                    ? []
                    : System.Text.Encoding.UTF8.GetBytes(
                        "<html><head><title>Local Doc</title></head><body><article><h1>Local Doc</h1><p>WEBTOKEN99 reachable content</p></article></body></html>");
                ctx.Response.StatusCode = ctx.Request.Url!.AbsolutePath == "/robots.txt" ? 404 : 200;
                ctx.Response.ContentType = "text/html";
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { break; }
            }
        });

        try
        {
            var source = await CreateSource(
                new { url = $"http://127.0.0.1:{port}/page", allowPrivateHosts = true },
                type: "WebPage");
            var result = await Sync(source.Id);

            Assert.Equal("completed", result.Status);
            Assert.Equal(1, result.DocumentsProcessed);

            var search = await _client.GetFromJsonAsync<SearchResponse>(
                "/api/search?query=WEBTOKEN99&mode=lexical");
            Assert.NotEmpty(search!.Results);
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }
}
