using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KnowledgeHub.Server.Data;

/// <summary>
/// Design-time factories for <c>dotnet ef</c>. The Postgres set is generated with
/// <c>dotnet ef migrations add &lt;Name&gt; --context PostgresKnowledgeHubDbContext
/// --output-dir Migrations/Postgres</c>; Sqlite keeps the default context and
/// <c>Migrations/</c> root.
/// </summary>
public sealed class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<KnowledgeHubDbContext>
{
    public KnowledgeHubDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KnowledgeHubDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new KnowledgeHubDbContext(options);
    }
}

public sealed class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresKnowledgeHubDbContext>
{
    public PostgresKnowledgeHubDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
            .UseNpgsql("Host=localhost;Database=kh_design;Username=postgres;Password=postgres") // NOSONAR S2068 — factory design-time (dotnet ef) p/ localhost, não é credencial de deploy
            .Options;
        return new PostgresKnowledgeHubDbContext(options);
    }
}
