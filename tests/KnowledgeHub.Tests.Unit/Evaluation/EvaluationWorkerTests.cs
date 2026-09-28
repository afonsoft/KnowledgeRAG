using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Evaluation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Evaluation;

// Covers PR #367 follow-up: RagEvaluations retention (90 days) pruned on write.
public class EvaluationWorkerTests
{
    [Fact]
    public async Task ProcessAsync_PrunesEvaluationsOlderThanRetention()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>().UseSqlite(conn).Options;

        await using (var seed = new KnowledgeHubDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.RagEvaluations.Add(new RagEvaluationEntity
            {
                QueryId = "old",
                Question = "stale question",
                TimestampUtc = DateTimeOffset.UtcNow.AddDays(-91)
            });
            seed.RagEvaluations.Add(new RagEvaluationEntity
            {
                QueryId = "recent",
                Question = "recent question",
                TimestampUtc = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        var worker = new EvaluationWorker(
            new Fakes.NoopRagEvaluationEnqueuer(),
            new RagTriadEvaluator(),
            new TestDbFactory(options),
            NullLogger<EvaluationWorker>.Instance);

        await worker.ProcessAsync(
            new RagEvaluationTask("new", "fresh question", ["some context"], "some answer", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await using var db = new KnowledgeHubDbContext(options);
        var remaining = await db.RagEvaluations.Select(e => e.QueryId).ToListAsync();
        Assert.DoesNotContain("old", remaining);
        Assert.Contains("recent", remaining);
        Assert.Contains("new", remaining);
    }

    private sealed class TestDbFactory(DbContextOptions<KnowledgeHubDbContext> options)
        : IDbContextFactory<KnowledgeHubDbContext>
    {
        public KnowledgeHubDbContext CreateDbContext() => new(options);

        public Task<KnowledgeHubDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgeHubDbContext(options));
    }
}
