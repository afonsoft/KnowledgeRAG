using System.Text.Json;
using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Ingestion;
using KnowledgeHub.Server.Ingestion.Connectors;
using KnowledgeHub.Server.Ingestion.Connectors.Cloud;
using KnowledgeHub.Server.Ingestion.Staging;
using KnowledgeHub.Server.Security;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using ServerChunking = KnowledgeHub.Server.Ingestion.Chunking;
using SyncOptions = KnowledgeHub.Server.Services.SyncOptions;

namespace KnowledgeHub.Tests.Unit.Server.Ingestion;

// Covers SPEC-20260926-ingestion-connector-integrity: fetch contract carries
// failures/truncation, unchanged stubs keep content, transient failure is not a
// remote delete, queue-full recovers, source delete is ordered and busts cache.
[Collection("SearchTelemetry")]
public sealed class ConnectorIntegrityTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _sp;

    public ConnectorIntegrityTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var sc = new ServiceCollection();
        sc.AddDbContext<KnowledgeHubDbContext>(o => o.UseSqlite(_conn));
        sc.AddSingleton<IVectorStore, StubVectorStore>();
        _sp = sc.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        _conn.Dispose();
    }

    // ---- contract ----

    [Fact]
    public void FetchResult_CarriesFailedUris_AndTruncated()
    {
        var r = new FetchResult([], [], FailedUris: ["u://x"], Truncated: true);
        Assert.True(r.Truncated);
        Assert.Equal("u://x", Assert.Single(r.FailedUris!));
    }

    [Fact]
    public void RemoteObject_Fingerprint_Empty_WhenNoMarkers()
    {
        var noMarkers = new RemoteObject("k", "", DateTimeOffset.MinValue, 1);
        var withEtag = new RemoteObject("k", "e1", DateTimeOffset.MinValue, 1);
        Assert.Equal("", noMarkers.Fingerprint);
        Assert.StartsWith("e1|", withEtag.Fingerprint);
    }

    // ---- CloudConnectorBase ----

    private sealed class FailGateway(List<RemoteObject> objects, HashSet<string> failKeys, bool truncated = false) : IRemoteObjectGateway
    {
        public bool Truncated { get; private set; } = truncated;

        public async IAsyncEnumerable<RemoteObject> ListAsync(
            string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var o in objects)
            {
                if (prefix is not null
                    && !o.Key.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal)
                    && !o.Key.Equals(prefix, StringComparison.Ordinal))
                    continue;
                yield return o;
            }
            await Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
        {
            if (failKeys.Contains(key))
                throw new InvalidOperationException("transient 503");
            return Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"content of {key}")));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStaging(string root) : IStagingStorageService
    {
        public string GetStagingDirectory(Guid sourceId)
        {
            var dir = Path.Combine(root, sourceId.ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public Task CleanupStagingAsync(Guid sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CleanupOrphanedStagingAsync(IReadOnlySet<Guid> known, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private static KnowledgeSource S3Source() => new()
    {
        Id = Guid.NewGuid(),
        Name = "s3",
        SourceType = SourceType.AwsS3,
        ConfigurationJson = JsonSerializer.Serialize(new
        {
            bucketName = "b",
            region = "us-east-1",
            accessKeyId = "ak"
        })
    };

    private AwsS3Connector S3Connector(IRemoteObjectGateway gw)
    {
        var c = new AwsS3Connector(
            new FakeSecrets(),
            new FakeStaging(Path.Combine(Path.GetTempPath(), $"kh_ci_{Guid.NewGuid():N}")),
            NullLogger<AwsS3Connector>.Instance);
        c.GatewayOverride = (_, _, _) => Task.FromResult(gw);
        return c;
    }

    [Fact]
    public async Task CloudFetch_DownloadFailure_LandsInFailedUris_NotDocuments()
    {
        var gw = new FailGateway(
            [new RemoteObject("a.txt", "e1", new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), 5),
             new RemoteObject("b.txt", "e2", new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), 5)],
            failKeys: ["b.txt"]);
        var result = await S3Connector(gw).FetchAsync(S3Source(), CancellationToken.None);

        var doc = Assert.Single(result.Documents);
        Assert.Equal("s3://b/a.txt", doc.UriReference);
        Assert.Equal("s3://b/b.txt", Assert.Single(result.FailedUris!));
    }

    [Fact]
    public async Task CloudFetch_GatewayTruncated_SetsFlag()
    {
        var gw = new FailGateway([new RemoteObject("a.txt", "e1", new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), 5)], [], truncated: true);
        var result = await S3Connector(gw).FetchAsync(S3Source(), CancellationToken.None);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task CloudFetch_EmptyFingerprint_AlwaysDownloads()
    {
        // Remote item with no change markers must never short-circuit as "unchanged".
        var gw = new FailGateway([new RemoteObject("a.txt", "", DateTimeOffset.MinValue, 5)], []);
        var existing = new Dictionary<string, string> { ["s3://b/a.txt"] = "" };
        var result = await S3Connector(gw).FetchAsync(S3Source(), existing, CancellationToken.None);

        var doc = Assert.Single(result.Documents);
        Assert.Equal("content of a.txt", doc.TextContent); // downloaded, not a stub
    }

    // ---- IngestionService pipeline ----

    private sealed class StubVectorStore : IVectorStore
    {
        public List<(Guid docId, int count)> Upserts { get; } = [];
        public List<Guid> DeletedDocs { get; } = [];
        public int? Dimensions => 384;

        public Task UpsertAsync(Guid chunkId, Guid documentId, Guid sourceId, float[] vector, string model,
            IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        {
            Upserts.Add((documentId, 1));
            return Task.CompletedTask;
        }

        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
        {
            DeletedDocs.Add(documentId);
            return Task.CompletedTask;
        }

        public Task DeleteBySourceAsync(Guid sourceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] queryVector, string model, int topK,
            IReadOnlyCollection<Guid>? sourceIds = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VectorHit>>([]);
    }

    private sealed class FakeEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "fake";
        public int Dimensions => 384;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(new float[384]);
    }

    private sealed class FakeSanitizer : IContentSanitizer
    {
        public string? FailOn { get; set; }
        public IReadOnlyList<string> Scan(string text)
        {
            if (FailOn is not null && text.Contains(FailOn, StringComparison.Ordinal))
                throw new InvalidOperationException("sanitizer boom");
            return [];
        }
    }

    private sealed class FakeGraphSettings : IGraphSettingsService
    {
        public GraphSettingsSnapshot GetEffective() => new(false, 0, 0, 0, "test");
        public Task<GraphSettingsDto> DescribeAsync(CancellationToken ct = default) =>
            Task.FromResult(new GraphSettingsDto
            {
                Enabled = false,
                MaxChunksPerSync = 0,
                MaxChunkChars = 0,
                MaxResults = 0,
                Source = "test",
                EnvConfigured = false
            });
        public Task SaveAsync(SaveGraphSettingsRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private sealed class FakeEmbeddingSettings : IEmbeddingSettingsService
    {
        public EmbeddingOptions GetEffectiveOptions() => new();
        public (int MaxTokens, int OverlapTokens) GetChunking() => (500, 50);
        public Task<EmbeddingSettingsDto> DescribeAsync(CancellationToken ct = default) =>
            Task.FromResult(new EmbeddingSettingsDto
            {
                Provider = "deterministic",
                Dimensions = 384,
                MaxTokens = 500,
                OverlapTokens = 50,
                HasApiKey = false,
                ApiKeySource = "none",
                Source = "env"
            });
        public Task SaveAsync(SaveEmbeddingSettingsRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveKeyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Invalidate() { }
    }

    private sealed class FakeConnector(
        SourceType type,
        Func<KnowledgeSource, IReadOnlyDictionary<string, string>, FetchResult> produce)
        : IIncrementalSourceConnector, IItemFetchConnector
    {
        public RawDocument? ItemToReturn { get; set; }
        public int ItemFetches { get; private set; }
        public SourceType Type => type;

        public Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken ct) =>
            FetchAsync(source, new Dictionary<string, string>(), ct);

        public Task<FetchResult> FetchAsync(
            KnowledgeSource source, IReadOnlyDictionary<string, string> existingFingerprints, CancellationToken ct) =>
            Task.FromResult(produce(source, existingFingerprints));

        public Task<RawDocument?> FetchItemAsync(KnowledgeSource source, string uriReference, CancellationToken ct)
        {
            ItemFetches++;
            return Task.FromResult(ItemToReturn);
        }
    }

    private IngestionService NewIngestion(ISourceConnector connector, FakeSanitizer? sanitizer = null,
        IDistributedCache? cache = null, FakeBus? bus = null) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(),
            new FakeEmbeddings(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            [connector],
            cache ?? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            sanitizer ?? new FakeSanitizer(),
            new FakeGraphSettings(),
            new FakeEmbeddingSettings(),
            NullLogger<IngestionService>.Instance,
            bus);

    private async Task<KnowledgeSource> SeedSourceWithDocAsync(
        string uri, string content, string hash, string chunkerConfigHash = "x")
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var source = new KnowledgeSource
        {
            Id = Guid.NewGuid(),
            Name = "src",
            SourceType = SourceType.AwsS3,
            ConfigurationJson = "{}",
            IsActive = true
        };
        db.Sources.Add(source);
        db.Documents.Add(new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            KnowledgeSourceId = source.Id,
            Title = "t",
            UriReference = uri,
            RawContent = content,
            ContentHash = hash,
            ChunkerVersion = ServerChunking.ChunkerSelector.CurrentVersion,
            ChunkerConfigHash = chunkerConfigHash,
            IndexedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<(List<KnowledgeDocument> docs, int chunks)> DumpDocs(Guid sourceId)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var docs = await db.Documents.Where(d => d.KnowledgeSourceId == sourceId).ToListAsync();
        var chunks = await db.Chunks.CountAsync(c => c.Document.KnowledgeSourceId == sourceId);
        return (docs, chunks);
    }

    [Fact]
    public async Task Sync_FailedUris_AreNotDeleted()
    {
        var source = await SeedSourceWithDocAsync("s3://b/gone.txt", "keep me", "fp1");
        var connector = new FakeConnector(SourceType.AwsS3,
            (_, _) => new FetchResult([], ["warn"], FailedUris: ["s3://b/gone.txt"]));

        await NewIngestion(connector).SyncAsync(source.Id);

        var (docs, _) = await DumpDocs(source.Id);
        Assert.Single(docs);
    }

    [Fact]
    public async Task Sync_TruncatedListing_SkipsDeletePass()
    {
        var source = await SeedSourceWithDocAsync("s3://b/beyond.txt", "keep me", "fp1");
        var connector = new FakeConnector(SourceType.AwsS3,
            (_, _) => new FetchResult([], ["warn"], Truncated: true));

        await NewIngestion(connector).SyncAsync(source.Id);

        var (docs, _) = await DumpDocs(source.Id);
        Assert.Single(docs);
    }

    [Fact]
    public async Task Sync_EmptyStub_ReusesStoredContent()
    {
        var source = await SeedSourceWithDocAsync("s3://b/a.txt", "the real content", "fp1", chunkerConfigHash: "stale");
        var connector = new FakeConnector(SourceType.AwsS3,
            (_, _) => new FetchResult(
                [new RawDocument("s3://b/a.txt", "a", "", "fp1")], []));

        await NewIngestion(connector).SyncAsync(source.Id, new SyncOptions { ForceReindex = true });

        var (docs, chunks) = await DumpDocs(source.Id);
        var doc = Assert.Single(docs);
        Assert.Equal("the real content", doc.RawContent);
        Assert.True(chunks > 0);
    }

    [Fact]
    public async Task Sync_EmptyStubAndNoStoredContent_FetchesOnDemand()
    {
        var source = await SeedSourceWithDocAsync("s3://b/a.txt", "", "fp1", chunkerConfigHash: "stale");
        var connector = new FakeConnector(SourceType.AwsS3,
            (_, _) => new FetchResult(
                [new RawDocument("s3://b/a.txt", "a", "", "fp1")], []))
        {
            ItemToReturn = new RawDocument("s3://b/a.txt", "a", "fetched fresh")
        };

        await NewIngestion(connector).SyncAsync(source.Id, new SyncOptions { ForceReindex = true });

        var (docs, _) = await DumpDocs(source.Id);
        Assert.Equal("fetched fresh", Assert.Single(docs).RawContent);
        Assert.Equal(1, connector.ItemFetches);
    }

    [Fact]
    public async Task Sync_DocFailure_DoesNotLeakPartialEntities()
    {
        var source = await SeedSourceWithDocAsync("s3://b/a.txt", "x", "old");
        var connector = new FakeConnector(SourceType.AwsS3,
            (_, _) => new FetchResult(
                [new RawDocument("s3://b/bad.txt", "bad", "BOOM content"),
                 new RawDocument("s3://b/good.txt", "good", "fine content")], []));
        var sanitizer = new FakeSanitizer { FailOn = "BOOM" };

        await NewIngestion(connector, sanitizer).SyncAsync(source.Id);

        var (docs, _) = await DumpDocs(source.Id);
        Assert.DoesNotContain(docs, d => d.UriReference == "s3://b/bad.txt");
        Assert.Contains(docs, d => d.UriReference == "s3://b/good.txt");
    }

    // ---- queue-full ----

    [Fact]
    public async Task Enqueue_QueueFull_MarksJobFailed()
    {
        var sc = new ServiceCollection();
        sc.AddDbContext<KnowledgeHubDbContext>(o => o.UseSqlite(_conn));
        var sp = sc.BuildServiceProvider();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ingestion:QueueSize"] = "1" }).Build();
        var queue = new IngestionQueue(sp.GetRequiredService<IServiceScopeFactory>(), cfg);

        Guid NewSource()
        {
            using var s = sp.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
            var src = new KnowledgeSource { Name = $"s-{Guid.NewGuid():N}", SourceType = SourceType.WebPage, ConfigurationJson = "{}" };
            d.Sources.Add(src);
            d.SaveChanges();
            return src.Id;
        }
        var a = NewSource(); var b = NewSource();
        await queue.EnqueueAsync(a, "autosync", CancellationToken.None);
        await Assert.ThrowsAsync<QueueFullException>(() => queue.EnqueueAsync(b, "autosync", CancellationToken.None));

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var failedJob = await db.IngestionJobs.SingleAsync(j => j.SourceId == b);
        Assert.Equal("failed", failedJob.Status);
        sp.Dispose();
    }

    // ---- Azure helpers ----

    [Theory]
    [InlineData(null, "docs", "docs")]
    [InlineData("", "docs", "docs")]
    [InlineData("docs", "docs", "docs")]        // fallback case — no dir/dir
    [InlineData("sub", "docs", "docs/sub")]
    [InlineData("docs/sub", "docs", "docs/sub")] // already absolute under root
    [InlineData("sub", "", "sub")]
    public void Azure_ResolveRoot_NoDoubleDirectory(string? prefix, string rootDir, string expected) =>
        Assert.Equal(expected, AzureShareGateway.ResolveRoot(prefix, rootDir));

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abc==", true)]
    [InlineData("SharedAccessSignature=sv=2024&sig=abc==", true)]
    [InlineData("dGVzdGtleXdpdGhwYWRkaW5n==", false)] // base64 account key with padding
    [InlineData("plainaccountkey", false)]
    public void Azure_CredentialClassifier(string value, bool isConnString) =>
        Assert.Equal(isConnString, AzureCredentialClassifier.LooksLikeConnectionString(value));

    [Fact]
    public void Csv_IsSupportedExtension() =>
        Assert.Contains(".csv", DocumentFileConnector.SupportedExtensions);

    // ---- source delete ----

    private sealed class FakeSecrets : IIntegrationSecretStore
    {
        public Task<string?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<string?>("secret");
        public Task SetAsync(string provider, string secret, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IntegrationSecretInfo?> GetInfoAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<IntegrationSecretInfo?>(null);
    }

    private sealed class FakeBus : ICacheInvalidationBus
    {
        public List<string> Topics { get; } = [];
        public event EventHandler<string>? Received { add { } remove { } }
        public Task PublishAsync(string topic, CancellationToken ct = default)
        {
            Topics.Add(topic);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Delete_Source_GoneBeforeCleanup_AndBumpsIndexVersion()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeHubDbContext>();
        var source = new KnowledgeSource
        {
            Id = Guid.NewGuid(),
            Name = "s",
            SourceType = SourceType.WebPage,
            ConfigurationJson = "{\"url\":\"https://x\"}",
            IsActive = true
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync();

        var vectors = new StubVectorStore();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var bus = new FakeBus();
        var svc = new KnowledgeHub.Server.Services.KnowledgeSourceService(
            db, new FakeNotifier(), new FakeSecrets(), null, vectors, null, cache, bus);

        var result = await svc.DeleteAsync(source.Id);

        Assert.Null(result.Error);
        Assert.True(result.Value);
        Assert.Empty(db.Sources.Where(s => s.Id == source.Id));
        var version = await cache.GetStringAsync(CacheKeys.IndexVersion);
        Assert.False(string.IsNullOrEmpty(version));
        Assert.Contains("index-version", bus.Topics);
    }

    private sealed class FakeNotifier : KnowledgeHub.Server.Mcp.IToolCatalogChangeNotifier
    {
        public long Version => 0;
        public Task NotifyToolsChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
