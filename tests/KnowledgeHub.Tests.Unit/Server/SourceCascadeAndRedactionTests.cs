using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Telemetry;
using KnowledgeHub.Server.VectorStore;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>
/// SPEC-20260925-pgvector-source-cascade + SPEC-20260925-log-sinks-and-redaction.
/// </summary>
[Collection("SearchTelemetry")]
public sealed class SourceCascadeAndRedactionTests
{
    private static async Task<KnowledgeHubDbContext> CreateDbAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<(KnowledgeSource, KnowledgeDocument, DocumentChunk)> SeedAsync(
        KnowledgeHubDbContext db, string name)
    {
        var s = new KnowledgeSource { Name = name, SourceType = SourceType.ObsidianVault };
        var d = new KnowledgeDocument { KnowledgeSourceId = s.Id, Title = "d", UriReference = $"vault/{name}.md" };
        var c = new DocumentChunk { KnowledgeDocumentId = d.Id, TextContent = "x" };
        db.Sources.Add(s);
        db.Documents.Add(d);
        db.Chunks.Add(c);
        await db.SaveChangesAsync();
        return (s, d, c);
    }

    [Fact]
    public async Task DeleteBySource_SqliteStore_ClearsOnlyThatSource()
    {
        await using var db = await CreateDbAsync();
        var (s1, d1, c1) = await SeedAsync(db, "s1");
        var (s2, d2, c2) = await SeedAsync(db, "s2");

        var store = new SqliteVectorStore(db);
        await store.UpsertAsync(c1.Id, d1.Id, s1.Id, new float[] { 1, 0 }, "m");
        await store.UpsertAsync(c2.Id, d2.Id, s2.Id, new float[] { 1, 0 }, "m");

        await store.DeleteBySourceAsync(s1.Id, CancellationToken.None);

        // ExecuteUpdateAsync bypasses the tracker — re-read untracked rows.
        var r1 = await db.Chunks.AsNoTracking().FirstAsync(c => c.Id == c1.Id);
        var r2 = await db.Chunks.AsNoTracking().FirstAsync(c => c.Id == c2.Id);
        Assert.Null(r1.Embedding);
        Assert.Null(r1.EmbeddingModel);
        Assert.NotNull(r2.Embedding); // untouched
    }

    [Fact]
    public async Task DeleteBySource_SqliteVec_RemovesRows()
    {
        await using var db = await CreateDbAsync();
        var (s1, d1, c1) = await SeedAsync(db, "s1");
        var (s2, d2, c2) = await SeedAsync(db, "s2");

        var vec = new SqliteVecVectorStore(db, dimensions: 2);
        await vec.UpsertAsync(c1.Id, d1.Id, s1.Id, new float[] { 1, 0 }, "m");
        await vec.UpsertAsync(c2.Id, d2.Id, s2.Id, new float[] { 1, 0 }, "m");

        await vec.DeleteBySourceAsync(s1.Id, CancellationToken.None);

        var hits = await vec.SearchAsync(new float[] { 1, 0 }, "m", topK: 10);
        Assert.Single(hits);
        Assert.Equal(c2.Id, hits[0].ChunkId);
    }

    // ---- SensitiveDataEnricher ---------------------------------------------

    private sealed class TestPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }

    private static LogEvent EventWith(string name, string value) => new(
        DateTimeOffset.Now, LogEventLevel.Information, null,
        new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()),
        [new LogEventProperty(name, new ScalarValue(value))]);

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("accessToken")]
    [InlineData("connectionString")]
    [InlineData("password")]
    [InlineData("X-Api-Key")]
    public void Enricher_SensitiveName_Redacted(string name)
    {
        var evt = EventWith(name, "sk-supersecret123");
        new SensitiveDataEnricher().Enrich(evt, new TestPropertyFactory());
        Assert.Equal("***REDACTED***", ((ScalarValue)evt.Properties[name]).Value);
    }

    [Fact]
    public void Enricher_TokenShapeInValue_Scrubbed()
    {
        var evt = EventWith("detail", "auth failed for aft_abc123def456");
        new SensitiveDataEnricher().Enrich(evt, new TestPropertyFactory());
        var v = (string)((ScalarValue)evt.Properties["detail"]).Value!;
        Assert.DoesNotContain("abc123", v);
        Assert.Contains("REDACTED", v);
    }

    [Fact]
    public void Enricher_ConnStringPassword_Scrubbed()
    {
        var evt = EventWith("conn", "Server=db;User Id=u;Password=hunter2;Database=x");
        new SensitiveDataEnricher().Enrich(evt, new TestPropertyFactory());
        var v = (string)((ScalarValue)evt.Properties["conn"]).Value!;
        Assert.DoesNotContain("hunter2", v);
        Assert.Contains("REDACTED", v);
    }

    [Fact]
    public void Enricher_InnocentValue_Untouched()
    {
        var evt = EventWith("doc", "vault/notes/keyboard-shortcuts.md");
        new SensitiveDataEnricher().Enrich(evt, new TestPropertyFactory());
        Assert.Equal("vault/notes/keyboard-shortcuts.md",
            ((ScalarValue)evt.Properties["doc"]).Value);
    }
}
