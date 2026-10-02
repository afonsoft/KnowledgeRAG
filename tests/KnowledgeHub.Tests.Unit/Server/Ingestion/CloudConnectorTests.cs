using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Ingestion.Connectors.Cloud;
using KnowledgeHub.Server.Ingestion.Staging;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server.Ingestion;

// Covers SPEC-20260924-cloud-storage-connectors RF-003..RF-005: gateway listing,
// glob/ext/size filters, incremental fingerprint dedup, staging download,
// text extraction, and URI schemes.
public sealed class CloudConnectorTests : IDisposable
{
    private readonly string _tempRoot = Path.Join(Path.GetTempPath(), $"kh_cloud_test_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch (Exception ex) { _ = ex.Message; }
        }
    }

    private sealed class FakeStaging(string root) : IStagingStorageService
    {
        public string GetStagingDirectory(Guid sourceId)
        {
            var dir = Path.Join(root, sourceId.ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public Task CleanupStagingAsync(Guid sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CleanupOrphanedStagingAsync(IReadOnlySet<Guid> known, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private sealed class FakeGateway(List<RemoteObject> objects) : IRemoteObjectGateway
    {
        public int Downloads { get; private set; }

        public async IAsyncEnumerable<RemoteObject> ListAsync(
            string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var o in objects.Where(o =>
                         prefix is null || o.Key.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal)
                         || o.Key.Equals(prefix, StringComparison.Ordinal)))
            {
                yield return o;
            }
            await Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
        {
            Downloads++;
            return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"content of {key}")));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static RemoteObject Obj(string key, string etag = "e1", long size = 100) =>
        new(key, etag, new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), size);

    private static KnowledgeSource Source(SourceType type, string configJson) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        SourceType = type,
        ConfigurationJson = configJson
    };

    private static AwsS3Connector S3Connector(FakeGateway gw)
    {
        var c = new AwsS3Connector(
            new McpProxySourceServiceTests.FakeSecretStore(),
            new FakeStaging(Path.Join(Path.GetTempPath(), $"kh_cloud_test_{Guid.NewGuid():N}")),
            NullLogger<AwsS3Connector>.Instance);
        c.GatewayOverride = (_, _, _) => Task.FromResult<IRemoteObjectGateway>(gw);
        return c;
    }

    private static string S3Config(string extra = "") =>
        $$"""{"bucketName":"bkt","region":"us-east-1","accessKeyId":"AK"{{extra}}}""";

    [Fact]
    public async Task S3_Fetch_NewObjects_DownloadsExtractsAndFingerprints()
    {
        var gw = new FakeGateway([Obj("docs/a.md"), Obj("docs/b.txt")]);
        var sut = S3Connector(gw);
        var result = await sut.FetchAsync(Source(SourceType.AwsS3, S3Config()), CancellationToken.None);

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal(2, gw.Downloads);
        var doc = result.Documents[0];
        Assert.Equal("s3://bkt/docs/a.md", doc.UriReference);
        Assert.Equal("content of docs/a.md", doc.TextContent);
        Assert.StartsWith("e1|", doc.Fingerprint);
    }

    [Fact]
    public async Task S3_Fetch_SameFingerprint_SkipsDownload()
    {
        var gw = new FakeGateway([Obj("a.md")]);
        var sut = S3Connector(gw);
        var src = Source(SourceType.AwsS3, S3Config());
        var first = await sut.FetchAsync(src, CancellationToken.None);
        var fp = first.Documents[0].Fingerprint!;

        var second = await sut.FetchAsync(src, new Dictionary<string, string> { ["s3://bkt/a.md"] = fp }, CancellationToken.None);

        Assert.Equal(1, gw.Downloads); // no second download
        Assert.Equal("", second.Documents[0].TextContent);
        Assert.Equal(fp, second.Documents[0].Fingerprint);
    }

    [Fact]
    public async Task S3_Fetch_Filters_GlobExtensionAndSize()
    {
        var gw = new FakeGateway([
            Obj("docs/a.md"), Obj("docs/a.exe"), Obj("docs/huge.md", size: 30L * 1024 * 1024), Obj("img/pic.md")
        ]);
        var sut = S3Connector(gw);
        var result = await sut.FetchAsync(
            Source(SourceType.AwsS3, S3Config(""","prefix":"docs/","glob":"**/*","maxFileSizeMB":20""")),
            CancellationToken.None);

        Assert.Single(result.Documents);
        Assert.Equal("s3://bkt/docs/a.md", result.Documents[0].UriReference);
        Assert.Equal(2, result.Warnings.Count); // .exe + huge
    }

    [Fact]
    public async Task Oci_Fetch_EmitsOciScheme()
    {
        var gw = new FakeGateway([Obj("v1/doc.md")]);
        var sut = new OciStorageConnector(
            new McpProxySourceServiceTests.FakeSecretStore(),
            new FakeStaging(_tempRoot),
            NullLogger<OciStorageConnector>.Instance);
        sut.GatewayOverride = (_, _, _) => Task.FromResult<IRemoteObjectGateway>(gw);

        var result = await sut.FetchAsync(
            Source(SourceType.OciStorage,
                """{"namespace":"ns","region":"sa-saopaulo-1","bucketName":"bkt","accessKeyId":"AK"}"""),
            CancellationToken.None);

        Assert.Equal("oci://bkt/v1/doc.md", result.Documents[0].UriReference);
    }

    [Fact]
    public async Task Azure_Fetch_EmitsAzureScheme()
    {
        var gw = new FakeGateway([Obj("eng/spec.md")]);
        var sut = new AzureFilesConnector(
            new McpProxySourceServiceTests.FakeSecretStore(),
            new FakeStaging(_tempRoot),
            NullLogger<AzureFilesConnector>.Instance);
        sut.GatewayOverride = (_, _, _) => Task.FromResult<IRemoteObjectGateway>(gw);

        var result = await sut.FetchAsync(
            Source(SourceType.AzureFiles, """{"shareName":"share"}"""),
            CancellationToken.None);

        Assert.Equal("azure://share/eng/spec.md", result.Documents[0].UriReference);
    }

    [Fact]
    public void AzureSecret_ParsesJsonPayload()
    {
        var s = AzureFilesConnector.AzureSecret.FromJson(
            """{"connectionString":"cs","accountName":"acc","accountKey":"key"}""");
        Assert.Equal("cs", s!.ConnectionString);
        Assert.Equal("acc", s.AccountName);
        Assert.Equal("key", s.AccountKey);

        var legacy = AzureFilesConnector.AzureSecret.FromJson("DefaultEndpointsProtocol=https;AccountName=x");
        Assert.Equal("DefaultEndpointsProtocol=https;AccountName=x", legacy!.ConnectionString);
        Assert.Null(AzureFilesConnector.AzureSecret.FromJson(null));
    }
}
