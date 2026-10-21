using KnowledgeHub.Server.Caching;
using KnowledgeHub.Server.Context;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20261021-context-test-coverage RF-2 — ContextSnapshotService parity
/// on real Postgres via Testcontainers (jsonb payload column). Skips when
/// Docker is unavailable — same convention as <see cref="PostgresVectorStoreLiveTests"/>.
/// </summary>
[Collection(nameof(PgVectorCollection))]
public sealed class ContextToolsPostgresTests
{
    private readonly PgVectorFixture _fixture;

    public ContextToolsPostgresTests(PgVectorFixture fixture) => _fixture = fixture;

    private sealed class PgFactory(string connString)
        : IDbContextFactory<KnowledgeHubDbContext>
    {
        public KnowledgeHubDbContext CreateDbContext() => New();

        public Task<KnowledgeHubDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult<KnowledgeHubDbContext>(New());

        private PostgresKnowledgeHubDbContext New() => new(
            new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
                .UseNpgsql(connString).Options);
    }

    private ContextSnapshotService NewService(string connString)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var cache = new ContextCacheService(
            sp.GetRequiredService<HybridCache>(),
            new NoopInvalidationBus(),
            Options.Create(new ContextOptions()),
            Options.Create(new KnowledgeHub.Server.Configuration.CacheOptions()),
            NullLogger<ContextCacheService>.Instance);
        return new ContextSnapshotService(
            new PgFactory(connString),
            Options.Create(new ContextOptions { MaxSnapshotsPerRepo = 3 }),
            cache,
            NullLogger<ContextSnapshotService>.Instance);
    }

    private static async Task<PostgresKnowledgeHubDbContext> MigrateAsync(string cs)
    {
        var db = new PostgresKnowledgeHubDbContext(
            new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
                .UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    [Fact]
    public async Task SaveAndRecover_OnPostgres_ParityWithSqlite()
    {
        if (!_fixture.Available) return;
        await using var db = await MigrateAsync(_fixture.ConnectionString!);
        var svc = NewService(_fixture.ConnectionString!);
        var repo = RepoScope.Parse("Pg/Owner-Repo");
        var items = new List<ContextItem>
        {
            new() { Kind = "decision", Title = "t", Content = "pg works" }
        };

        var saved = await svc.SaveAsync(repo, items, "lbl", "idem-1",
            "none", compacted: false, source: "rest", createdByKeyId: null, default);
        Assert.Equal(1, saved.Snapshot.Version);

        // jsonb column roundtrips the payload.
        var latest = await svc.GetLatestAsync(RepoScope.Parse("pg/owner-repo"), default);
        Assert.NotNull(latest);
        Assert.Equal("pg works", ContextSnapshotService.ItemsOf(latest!)[0].Content);
        Assert.Equal("rest", latest!.Source);

        // Idempotency dedup + monotonic versions.
        var dup = await svc.SaveAsync(repo, items, "lbl", "idem-1",
            "none", false, "rest", null, default);
        Assert.True(dup.IdempotentHit);
        var second = await svc.SaveAsync(repo, items, null, null,
            "none", false, "rest", null, default);
        Assert.Equal(2, second.Snapshot.Version);
    }
}
