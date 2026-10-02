using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Shared.Contracts;
using KnowledgeHub.Server.Embeddings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Server;

// Covers SPEC-20260914-embedding-dimension-guard RF-001..004.
public sealed class EmbeddingCompatibilityCheckTests : IDisposable
{
    private readonly List<IDisposable> _owned = new();
    [Fact]
    public async Task EmptyStore_LogsInfo_NoWarnings()
    {
        using var db = CreateDb();
        var logger = new ListLogger();

        await EmbeddingCompatibilityCheck.RunAsync(db, new FakeProvider("deterministic:hash384", 384), logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task MatchingStore_LogsOk()
    {
        using var db = CreateDb();
        await SeedChunkAsync(db, "deterministic:hash384", 384);
        var logger = new ListLogger();

        await EmbeddingCompatibilityCheck.RunAsync(db, new FakeProvider("deterministic:hash384", 384), logger);

        var info = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("OK"));
        Assert.Contains("384", info.Message);
    }

    [Fact]
    public async Task DimensionMismatch_LogsError_WithBothNumbers()
    {
        using var db = CreateDb();
        await SeedChunkAsync(db, "deterministic:hash384", 128);
        var logger = new ListLogger();

        await EmbeddingCompatibilityCheck.RunAsync(db, new FakeProvider("deterministic:hash384", 384), logger);

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("128", error.Message);
        Assert.Contains("384", error.Message);
    }

    [Fact]
    public async Task ModelMismatch_LogsWarning_ListingStoredModel()
    {
        using var db = CreateDb();
        await SeedChunkAsync(db, "deterministic:hash384", 384);
        var logger = new ListLogger();

        await EmbeddingCompatibilityCheck.RunAsync(db, new FakeProvider("ollama:nomic-embed-text", 384), logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("deterministic:hash384", warning.Message);
    }

    private KnowledgeHubDbContext CreateDb()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        _owned.Add(connection);
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new KnowledgeHubDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task SeedChunkAsync(KnowledgeHubDbContext db, string model, int dims)
    {
        var source = new KnowledgeSource { Name = "test", SourceType = SourceType.ObsidianVault };
        var document = new KnowledgeDocument
        {
            KnowledgeSourceId = source.Id,
            Title = "doc",
            UriReference = "vault/doc.md",
        };
        var chunk = new DocumentChunk
        {
            KnowledgeDocumentId = document.Id,
            TextContent = "text",
            Embedding = EmbeddingVectorCodec.ToBytes(new float[dims]),
            EmbeddingModel = model,
        };
        db.Sources.Add(source);
        db.Documents.Add(document);
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();
    }

    private sealed class FakeProvider(string modelId, int dimensions) : IEmbeddingProvider
    {
        public string ModelId => modelId;
        public int Dimensions => dimensions;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult(new float[dimensions]);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var d in _owned)
            d.Dispose();
    }

}