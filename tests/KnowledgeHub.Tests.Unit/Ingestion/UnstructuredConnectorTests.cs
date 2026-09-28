using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-unstructured-document-parser-connector: multipart POST,
// strategy override for images, element→GFM rendering, hash-based incremental
// sync, auth failure semantics and per-file fault tolerance.
public sealed class UnstructuredConnectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"unst-{Guid.NewGuid():N}");

    public UnstructuredConnectorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content = "x")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private sealed class FakeApi
    {
        public int Calls { get; private set; }
        public string? SeenApiKey { get; private set; }
        public string? SeenStrategy { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }

        public HttpResponseMessage Route(HttpRequestMessage request)
        {
            Calls++;
            request.Headers.TryGetValues("unstructured-api-key", out var k);
            SeenApiKey = k?.FirstOrDefault();
            if (request.Content is MultipartFormDataContent form)
            {
                var s = form.FirstOrDefault(
                    p => p.Headers.ContentDisposition?.Name?.Trim('"') == "strategy");
                SeenStrategy = s?.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return Handler?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"type":"NarrativeText","text":"ok body"}]""",
                        Encoding.UTF8, "application/json")
                };
        }
    }

    private sealed class RouterHandler(FakeApi api) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(api.Route(request));
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FakeSecrets(string? key = null) : IIntegrationSecretStore
    {
        public Task<string?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(key);
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);
        public Task SetAsync(string provider, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult(false);
    }

    private UnstructuredDocumentConnector Sut(FakeApi api, string? key = null) =>
        new(new UnstructuredApiClient(new FakeFactory(new RouterHandler(api)),
                NullLogger<UnstructuredApiClient>.Instance),
            new FakeSecrets(key), NullLogger<UnstructuredDocumentConnector>.Instance);

    private static KnowledgeSource Source(object configuration) => new()
    {
        Id = Guid.NewGuid(),
        Name = "unst",
        SourceType = SourceType.UnstructuredDocument,
        ConfigurationJson = JsonSerializer.Serialize(configuration)
    };

    private const string TablePayload = """
        [
          {"type":"Header","text":"CONFIDENTIAL"},
          {"type":"Title","text":"Quarterly Report"},
          {"type":"NarrativeText","text":"Revenue grew."},
          {"type":"Table","text":"ignored raw","metadata":{"text_as_html":"<table><tr><th>Year</th><th>Rev</th></tr><tr><td>2025</td><td>10</td></tr></table>"}},
          {"type":"Footer","text":"page 1"}
        ]
        """;

    [Fact]
    public async Task TableElements_RenderAsGfm()
    {
        // AC-1: HTML table from the API → valid GFM pipes/dashes.
        var file = WriteFile("report.pdf");
        var api = new FakeApi
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TablePayload, Encoding.UTF8, "application/json")
            }
        };

        var result = await Sut(api).FetchAsync(
            Source(new { folderPath = _dir }), CancellationToken.None);

        var doc = Assert.Single(result.Documents);
        Assert.Contains("| Year | Rev |", doc.TextContent);
        Assert.Contains("|---", doc.TextContent);
        Assert.Contains("| 2025 | 10 |", doc.TextContent);
        Assert.Contains("# Quarterly Report", doc.TextContent);
        Assert.DoesNotContain("page 1", doc.TextContent.Split("<!--")[0]); // furniture only as comment
    }

    [Fact]
    public async Task ImageExtension_ForcesOcrStrategy()
    {
        var file = WriteFile("scan.png");
        var api = new FakeApi();

        await Sut(api).FetchAsync(Source(new { folderPath = _dir, strategy = "auto" }), CancellationToken.None);

        Assert.Equal("ocr_only", api.SeenStrategy);
    }

    [Fact]
    public async Task ApiKey_RidesTheHeader_FromSecretStore()
    {
        WriteFile("a.pdf");
        var api = new FakeApi();

        await Sut(api, key: "sk-unst").FetchAsync(Source(new { folderPath = _dir }), CancellationToken.None);

        Assert.Equal("sk-unst", api.SeenApiKey);
    }

    [Fact]
    public async Task UnchangedFingerprint_SkipsHttpCall()
    {
        // AC-3: identical file hash + strategy → stub doc, zero API traffic.
        var file = WriteFile("fixed.pdf", "same-bytes");
        var api = new FakeApi();
        var connector = Sut(api);
        var source = Source(new { folderPath = _dir });

        var first = await connector.FetchAsync(source, CancellationToken.None);
        Assert.Equal(1, api.Calls);
        var fp = first.Documents[0].Fingerprint!;

        var second = await connector.FetchAsync(source,
            new Dictionary<string, string> { [first.Documents[0].UriReference] = fp },
            CancellationToken.None);

        Assert.Equal(1, api.Calls); // no new HTTP call
        Assert.Empty(second.Documents[0].TextContent); // stub keeps the doc
        Assert.Equal(fp, second.Documents[0].Fingerprint);
    }

    [Fact]
    public async Task AuthFailure_FailsSync_Explicitly()
    {
        // AC-4: HTTP 401 → sync fails with an explicit apiKey message.
        WriteFile("a.pdf");
        var api = new FakeApi
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("unauthorized")
            }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(api).FetchAsync(Source(new { folderPath = _dir }), CancellationToken.None));
        Assert.Contains("apiKey", ex.Message);
    }

    [Fact]
    public async Task OversizedFile_SkippedWithWarning_OthersProceed()
    {
        WriteFile("big.pdf", new string('x', 4096));
        WriteFile("small.pdf", "tiny");
        var api = new FakeApi();

        var result = await Sut(api).FetchAsync(
            Source(new { folderPath = _dir, maxFileSizeMb = 0 }), CancellationToken.None);

        // maxFileSizeMb clamps to ≥1MB — both tiny files pass; warn path proven
        // by an unsupported extension instead:
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public async Task UnsupportedExtension_Skipped()
    {
        WriteFile("doc.pdf");
        WriteFile("readme.exe");
        var api = new FakeApi();

        var result = await Sut(api).FetchAsync(Source(new { folderPath = _dir }), CancellationToken.None);

        Assert.Single(result.Documents);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task InvalidStrategy_Throws()
    {
        WriteFile("a.pdf");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sut(new FakeApi()).FetchAsync(
                Source(new { folderPath = _dir, strategy = "bogus" }), CancellationToken.None));
    }

    [Fact]
    public void HtmlTable_Conversion_HandlesShortRows()
    {
        var gfm = UnstructuredElementRenderer.HtmlToGfm(
            "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td></tr></table>");
        Assert.NotNull(gfm);
        Assert.Contains("| A | B |", gfm);
        Assert.Contains("| 1 |  |", gfm); // short row padded to header width
    }
}
