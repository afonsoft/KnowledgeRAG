using System.Net;
using System.Text;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Ingestion.Conformance;

// SPEC-20261021-connector-conformance-suite: concrete fixtures, one per
// connector that can run without real network. Connectors that demand real
// cloud credentials (AwsS3/AzureFiles/OciStorage/GoogleDrive/ObsidianVault
// local-vault) stay on the documented skip list at the bottom of this file.

public sealed class DocumentFileConformance : SourceConnectorConformance, IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), $"conf-docfile-{Guid.NewGuid():N}");

    public DocumentFileConformance()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Join(_dir, "doc1.md"), "# Hello\n\nconformance body");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    protected override SourceType ExpectedType => SourceType.DocumentFile;
    protected override ISourceConnector CreateConnector() =>
        new DocumentFileConnector(NullLogger<DocumentFileConnector>.Instance);
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.DocumentFile, new { path = _dir });
}

public sealed class RssFeedConformance : IncrementalSourceConnectorConformance
{
    private const string FeedUrl = "https://feed.test/blog";
    private readonly RouteTable _routes = new RouteTable().Map(FeedUrl, """
        <?xml version="1.0"?>
        <rss version="2.0"><channel>
          <item><title>Alpha</title><link>https://blog.test/1</link>
            <guid>post-1</guid><description>alpha body</description></item>
        </channel></rss>
        """, contentType: "text/xml");

    protected override SourceType ExpectedType => SourceType.RssFeed;
    protected override ISourceConnector CreateConnector() => CreateIncrementalConnector();
    protected override IIncrementalSourceConnector CreateIncrementalConnector() =>
        new RssFeedConnector(new FakeHttpFactory(new RouterHandler(_routes)),
            NullLogger<RssFeedConnector>.Instance);
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.RssFeed, new { feedUrl = FeedUrl });
}

public sealed class RestApiConformance : SourceConnectorConformance
{
    private const string Base = "https://api.test/data";
    private readonly RouteTable _routes = new RouteTable().Map(Base, """
        [{"id":"1","title":"Alpha","body":"alpha body"}]
        """);

    // restapi:{id} stores a JSON object of headers — sentinel rides inside a value.
    private sealed class HeaderSecrets() : SentinelSecrets($$"""{"X-Api-Key":"{{SecretSentinel}}"}""");

    protected override SourceType ExpectedType => SourceType.RestApi;
    protected override bool UsesSecrets => true;
    protected override ISourceConnector CreateConnector() =>
        new RestApiConnector(new FakeHttpFactory(new RouterHandler(_routes)),
            new HeaderSecrets(), NullLogger<RestApiConnector>.Instance);
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.RestApi, new { endpoint = Base, idField = "id", hasKey = true });
}

public sealed class SqlDatabaseConformance : SourceConnectorConformance, IDisposable
{
    private readonly string _dbPath = Path.Join(Path.GetTempPath(), $"conf-sql-{Guid.NewGuid():N}.db");

    public SqlDatabaseConformance()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE notes(id INTEGER PRIMARY KEY, title TEXT, body TEXT);" +
            "INSERT INTO notes(title, body) VALUES('Alpha','alpha body');";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { if (File.Exists(_dbPath)) File.Delete(_dbPath); }

    protected override SourceType ExpectedType => SourceType.SqlDatabase;
    protected override ISourceConnector CreateConnector() =>
        new SqlDatabaseConnector(new SentinelSecrets($"Data Source={_dbPath}"),
            NullLogger<SqlDatabaseConnector>.Instance);
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.SqlDatabase, new
        {
            provider = "sqlite",
            query = "SELECT id, title, body FROM notes",
            idColumn = "id",
            titleColumn = "title"
        });
}

public sealed class WebPageConformance : SourceConnectorConformance
{
    private const string PageUrl = "https://page.test/index";
    private readonly RouteTable _routes = new RouteTable()
        .Map(PageUrl, "<html><body><h1>Alpha</h1><p>alpha body</p></body></html>",
            contentType: "text/html")
        .Map("https://page.test/robots.txt", "User-agent: *\nAllow: /",
            contentType: "text/plain");

    protected override SourceType ExpectedType => SourceType.WebPage;
    protected override ISourceConnector CreateConnector() =>
        new WebPageConnector(new FakeHttpFactory(new RouterHandler(_routes)),
            NullLogger<WebPageConnector>.Instance);
    // allowPrivateHosts skips the DNS SSRF guard — the fake host won't resolve.
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.WebPage, new { url = PageUrl, allowPrivateHosts = true });
}

public sealed class NotionConformance : IncrementalSourceConnectorConformance
{
    private readonly RouteTable _routes = new RouteTable()
        .MapFunc("https://api.notion.test/v1/users/me", _ => Json("""{"object":"user"}"""))
        .MapFunc("https://api.notion.test/v1/search", _ => Json("""
            {"results":[{"object":"page","id":"p1","last_edited_time":"2026-01-01T00:00:00Z",
             "properties":{"title":{"title":[{"plain_text":"Alpha"}]}}}],
             "has_more":false,"next_cursor":null}
            """))
        .MapFunc("https://api.notion.test/v1/blocks/p1/children", _ => Json("""
            {"results":[{"object":"block","id":"b1","type":"paragraph",
             "paragraph":{"rich_text":[{"plain_text":"alpha body"}]}}],
             "has_more":false,"next_cursor":null}
            """));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class NotionFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("https://api.notion.test") };
    }

    protected override SourceType ExpectedType => SourceType.Notion;
    protected override bool UsesSecrets => true;
    protected override ISourceConnector CreateConnector() => CreateIncrementalConnector();
    protected override IIncrementalSourceConnector CreateIncrementalConnector() =>
        new NotionConnector(new NotionFactory(new RouterHandler(_routes)),
            new SentinelSecrets(), NullLogger<NotionConnector>.Instance);
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.Notion, new { hasKey = true, apiBaseUrl = "https://api.notion.test" });
}

public sealed class YouTubeConformance : IncrementalSourceConnectorConformance
{
    private sealed class FakeYouTubeClient : IYouTubeClient
    {
        public Task<VideoInfo> GetVideoAsync(string videoId, CancellationToken ct) =>
            Task.FromResult(new VideoInfo(videoId, "Alpha", "TestChannel", TimeSpan.FromSeconds(60)));

        public async IAsyncEnumerable<VideoInfo> GetPlaylistVideosAsync(
            string playlistId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }

        public async IAsyncEnumerable<VideoInfo> GetChannelUploadsAsync(
            string channelIdOrHandle, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }

        public Task<IReadOnlyList<CaptionTrackInfo>> GetCaptionManifestAsync(string videoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CaptionTrackInfo>>([new CaptionTrackInfo("en", false)]);

        public Task<IReadOnlyList<Caption>> GetCaptionTrackAsync(CaptionTrackInfo track, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Caption>>([new Caption("alpha body", TimeSpan.Zero)]);
    }

    protected override SourceType ExpectedType => SourceType.YouTube;
    protected override ISourceConnector CreateConnector() => CreateIncrementalConnector();
    protected override IIncrementalSourceConnector CreateIncrementalConnector() =>
        new YouTubeConnector(new FakeYouTubeClient(), NullLogger<YouTubeConnector>.Instance);
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.YouTube, new { urls = new[] { "vid00000001" } });
}

public sealed class GitRepositoryConformance : IncrementalSourceConnectorConformance
{
    private const string TreeJson = """
        {"tree":[
          {"path":"README.md","type":"blob","size":12,"sha":"b1"}
        ],"truncated":false}
        """;

    private static HttpResponseMessage Route(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/branches/main", StringComparison.Ordinal))
            return Json("""{"commit":{"sha":"abc123"}}""");
        if (path.Contains("/git/trees/"))
            return Json(TreeJson);
        if (path.Contains("/contents/"))
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("# readme body", Encoding.UTF8)
            };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private readonly RouteTable _routes = new RouteTable().MapFunc("https://api.github.com", Route);

    protected override SourceType ExpectedType => SourceType.GitRepository;
    protected override bool UsesSecrets => true;
    protected override ISourceConnector CreateConnector() => CreateIncrementalConnector();
    protected override IIncrementalSourceConnector CreateIncrementalConnector() =>
        new GitRepositoryConnector(new FakeHttpFactory(new RouterHandler(_routes)),
            new SentinelSecrets(), NullLogger<GitRepositoryConnector>.Instance);
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.GitRepository, new
        {
            provider = "github",
            owner = "weaviate",
            name = "Verba",
            branch = "main",
            hasKey = true
        });
}

public sealed class UnstructuredConformance : SourceConnectorConformance, IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), $"conf-unst-{Guid.NewGuid():N}");

    public UnstructuredConformance()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Join(_dir, "report.pdf"), "fake-pdf-bytes");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private readonly RouteTable _routes = new RouteTable().MapFunc("https://unst.test", _ =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"type":"NarrativeText","text":"alpha body"}]""",
                Encoding.UTF8, "application/json")
        });

    protected override SourceType ExpectedType => SourceType.UnstructuredDocument;
    protected override bool UsesSecrets => true;
    protected override ISourceConnector CreateConnector() =>
        new UnstructuredDocumentConnector(
            new UnstructuredApiClient(new FakeHttpFactory(new RouterHandler(_routes)),
                NullLogger<UnstructuredApiClient>.Instance),
            new SentinelSecrets(), NullLogger<UnstructuredDocumentConnector>.Instance);
    protected override KnowledgeSource CreateValidSource() =>
        Source(SourceType.UnstructuredDocument, new { folderPath = _dir, apiUrl = "https://unst.test" });
}

// Documented skip list (SPEC RF-005): connectors requiring real cloud/local
// infra not fakeable in unit tests — AudioTranscriptionConnector (needs real
// media bytes through a render pipeline — covered by its own test file),
// AwsS3Connector/AzureFilesConnector/OciStorageConnector (real cloud staging
// via CloudConnectorBase gateways), GoogleDriveSharedConnector (OAuth),
// ObsidianVault sources (local vault path handled by a different sync path).
