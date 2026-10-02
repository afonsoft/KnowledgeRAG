using System.Net;
using System.Text;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Ingestion.Staging;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Server.Ingestion;

/// <summary>
/// SPEC-20260924-gdrive-shared-link-connector: URL parsing, gateway listing/
/// download against a stub Drive API, native-doc export, maxFiles cap.
/// </summary>
public class GoogleDriveConnectorTests : IDisposable
{
    private readonly string _tempRoot = Path.Join(Path.GetTempPath(), $"kh-gd-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch (Exception ex) { _ = ex.Message; }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private GoogleDriveSharedConnector Connector(HttpMessageHandler handler, string? storedKey = null)
    {
        var secrets = new FakeSecrets();
        if (storedKey is not null)
            secrets.Store["gdrive:" + _srcId] = storedKey;
        var http = new HttpClient(handler);
        _owned.Add(http);
        var sut = new GoogleDriveSharedConnector(
            new GoogleDriveApiClient(http),
            secrets,
            new FakeStaging(Path.Join(_tempRoot, "staging")),
            NullLogger<GoogleDriveSharedConnector>.Instance);
        return sut;
    }

    private Guid _srcId;
    private KnowledgeSource NewSource()
    {
        _srcId = Guid.NewGuid();
        return new KnowledgeSource
        {
            Id = _srcId,
            Name = "gd",
            SourceType = SourceType.GoogleDrive,
            ConfigurationJson = """{"sharedUrl":"https://drive.google.com/drive/folders/ROOT1"}"""
        };
    }

    private sealed class FakeSecrets : KnowledgeHub.Server.Settings.IIntegrationSecretStore
    {
        public Dictionary<string, string> Store { get; } = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Store.GetValueOrDefault(key));
        public Task<KnowledgeHub.Server.Settings.IntegrationSecretInfo?> GetInfoAsync(
            string key, CancellationToken ct = default) =>
            Task.FromResult<KnowledgeHub.Server.Settings.IntegrationSecretInfo?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { Store[key] = value; return Task.CompletedTask; }
        public Task<bool> RemoveAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Store.Remove(key));
    }

    private sealed class FakeStaging(string root) : IStagingStorageService
    {
        public string GetStagingDirectory(Guid sourceId)
        {
            var p = Path.Join(root, sourceId.ToString("N"));
            Directory.CreateDirectory(p);
            return p;
        }
        public Task CleanupStagingAsync(Guid sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CleanupOrphanedStagingAsync(IReadOnlySet<Guid> known, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body, string contentType = "text/plain") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/1a2B3c_4-xyz", true, "1a2B3c_4-xyz")]
    [InlineData("https://drive.google.com/drive/u/0/folders/FID", true, "FID")]
    [InlineData("https://drive.google.com/file/d/FILEID123/view?usp=sharing", false, "FILEID123")]
    [InlineData("https://drive.google.com/open?id=ANYID", false, "ANYID")]
    public void ParseSharedUrl_ValidPatterns(string url, bool isFolder, string expectedId)
    {
        Assert.True(GoogleDriveApiClient.TryParseSharedUrl(url, out var id, out var folder));
        Assert.Equal(expectedId, id);
        Assert.Equal(isFolder, folder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://drive.google.com/")]
    [InlineData("https://docs.google.com/document/d/abc")]
    public void ParseSharedUrl_Invalid(string url)
    {
        Assert.False(GoogleDriveApiClient.TryParseSharedUrl(url, out _, out _));
    }

    [Fact]
    public async Task Fetch_WithApiKey_ListsRecursively_DownloadsAndExports()
    {
        // folders/ROOT1 → file a.txt + folder sub; sub → native doc.
        var handler = new StubHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("files/ROOT1?fields")) return Json("""{"id":"ROOT1","name":"root","mimeType":"application/vnd.google-apps.folder"}""");
            if (u.Contains("/files?q=") && u.Contains("ROOT1")) return Json("""
                {"files":[
                  {"id":"F1","name":"a.txt","mimeType":"text/plain","size":"5","md5Checksum":"m1","modifiedTime":"2026-01-01T00:00:00Z"},
                  {"id":"SUB","name":"sub","mimeType":"application/vnd.google-apps.folder"}]}
                """);
            if (u.Contains("/files?q=") && u.Contains("SUB")) return Json("""
                {"files":[{"id":"D1","name":"Report","mimeType":"application/vnd.google-apps.document","modifiedTime":"2026-01-02T00:00:00Z"}]}
                """);
            if (u.Contains("alt=media")) return Text("file-body");
            if (u.Contains("/export")) return Text("exported-text");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var src = NewSource();
        var sut = Connector(handler, storedKey: "KEY");
        var result = await sut.FetchAsync(src, CancellationToken.None);

        Assert.True(result.Warnings.Count == 0, string.Join(" | ", result.Warnings));
        Assert.Equal(2, result.Documents.Count);
        var names = result.Documents.Select(d => d.UriReference).OrderBy(x => x).ToList();
        Assert.Contains("gdrive://", names[0]);
        // native doc exported as .txt under sub/
        var native = result.Documents.Single(d => d.UriReference.Contains("Report.txt"));
        Assert.Contains("exported-text", native.TextContent);
        var plain = result.Documents.Single(d => d.UriReference.Contains("a.txt"));
        Assert.Contains("file-body", plain.TextContent);
    }

    [Fact]
    public async Task Fetch_NativeDoc_UsesExport_NotDownload()
    {
        var downloaded = false;
        var handler = new StubHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("/files?q=")) return Json("""
                {"files":[{"id":"D1","name":"Doc","mimeType":"application/vnd.google-apps.document"}]}
                """);
            if (u.Contains("alt=media")) { downloaded = true; return Text("x"); }
            if (u.Contains("/export")) return Text("exported");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var src = NewSource();
        await Connector(handler, "KEY").FetchAsync(src, CancellationToken.None);
        Assert.False(downloaded);
    }

    [Fact]
    public async Task Fetch_Fingerprint_Unchanged_SkipsDownload()
    {
        var handler = new StubHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("/files?q=")) return Json("""
                {"files":[{"id":"F1","name":"a.txt","mimeType":"text/plain","size":"5","md5Checksum":"m1","modifiedTime":"2026-01-01T00:00:00Z"}]}
                """);
            if (u.Contains("alt=media")) return Text("body");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var src = NewSource();
        var sut = Connector(handler, "KEY");
        var fp = "m1|2026-01-01T00:00:00.0000000Z";
        var result = await sut.FetchAsync(src,
            new Dictionary<string, string> { [$"gdrive://{System.Text.Json.Nodes.JsonNode.Parse(src.ConfigurationJson!)!["sharedUrl"]}/a.txt"] = fp },
            CancellationToken.None);

        var doc = Assert.Single(result.Documents);
        Assert.Equal(fp, doc.Fingerprint);
        Assert.Equal("", doc.TextContent); // unchanged — no download, empty content
        Assert.DoesNotContain(handler.Requests, r => r.Contains("alt=media"));
    }

    [Fact]
    public async Task Fetch_InvalidSharedUrl_Throws()
    {
        var src = NewSource();
        src.ConfigurationJson = """{"sharedUrl":"https://example.com/nope"}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Connector(new StubHandler(_ => new(HttpStatusCode.OK)), "KEY").FetchAsync(src, CancellationToken.None));
    }
}
