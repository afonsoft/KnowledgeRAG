using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Data;

// SPEC-20260926-unified-database-provider: unsealed so PostgresKnowledgeHubDbContext
// can own the second migration set in the same assembly (EF resolves migrations
// by the concrete context type annotated on each migration class). The ctor takes
// untyped DbContextOptions so the pooled subclass can pass DbContextOptions<TImpl>.
public class KnowledgeHubDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<KnowledgeSource> Sources => Set<KnowledgeSource>();
    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();
    public DbSet<DocumentChunk> Chunks => Set<DocumentChunk>();
    public DbSet<ToolApproval> Approvals => Set<ToolApproval>();
    public DbSet<ConversationThread> Threads => Set<ConversationThread>();
    public DbSet<ConversationMessage> ThreadMessages => Set<ConversationMessage>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ApiKeyUsageEvent> ApiKeyUsageEvents => Set<ApiKeyUsageEvent>();
    public DbSet<IntegrationSecret> IntegrationSecrets => Set<IntegrationSecret>();
    public DbSet<IntegrationState> IntegrationStates => Set<IntegrationState>();
    public DbSet<ChatSettings> ChatSettings => Set<ChatSettings>();

    public DbSet<EmbeddingSettings> EmbeddingSettings => Set<EmbeddingSettings>();
    public DbSet<GraphSettings> GraphSettings => Set<GraphSettings>();
    public DbSet<ApiKeyChatSettings> ApiKeyChatSettings => Set<ApiKeyChatSettings>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<EvalBaseline> EvalBaselines => Set<EvalBaseline>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<KgNode> KgNodes => Set<KgNode>();
    public DbSet<KgEdge> KgEdges => Set<KgEdge>();
    public DbSet<KgAlias> KgAliases => Set<KgAlias>();
    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
    /// <summary>SPEC-20260926-mcp-sdk-alignment RF-004: durable MCP task handles.</summary>
    public DbSet<McpTask> McpTasks => Set<McpTask>();
    /// <summary>SPEC-20260927-rag-evaluation-triad-metrics: persisted triad scores.</summary>
    public DbSet<RagEvaluationEntity> RagEvaluations => Set<RagEvaluationEntity>();

    /// <summary>Configura as entidades do modelo: chaves, índices, tamanhos e relacionamentos.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnowledgeSource>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.SourceType).HasConversion<string>().HasMaxLength(32);
            e.HasMany(s => s.Documents)
                .WithOne(d => d.Source)
                .HasForeignKey(d => d.KnowledgeSourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeDocument>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Title).IsRequired().HasMaxLength(500);
            e.Property(d => d.UriReference).IsRequired().HasMaxLength(1000);
            e.HasIndex(d => new { d.KnowledgeSourceId, d.UriReference }).IsUnique();
            e.HasMany(d => d.Chunks)
                .WithOne(c => c.Document)
                .HasForeignKey(c => c.KnowledgeDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestionJob>(e =>
        {
            e.HasKey(j => j.Id);
            e.Property(j => j.Kind).IsRequired().HasMaxLength(16);
            e.Property(j => j.Status).IsRequired().HasMaxLength(16);
            e.HasIndex(j => new { j.SourceId, j.Status });
            e.HasOne(j => j.Source)
                .WithMany()
                .HasForeignKey(j => j.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ToolApproval>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.ToolName).IsRequired().HasMaxLength(200);
            e.Property(a => a.RequestedBy).IsRequired().HasMaxLength(32);
            e.Property(a => a.Status).IsRequired().HasMaxLength(16);
            e.HasIndex(a => a.Status);
        });

        modelBuilder.Entity<ConversationThread>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Title).IsRequired().HasMaxLength(300);
            e.HasMany(t => t.Messages)
                .WithOne(m => m.Thread!)
                .HasForeignKey(m => m.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Role).IsRequired().HasMaxLength(16);
            e.Property(m => m.ToolName).HasMaxLength(200);
            e.HasIndex(m => m.ThreadId);
        });

        modelBuilder.Entity<DocumentChunk>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.TextContent).IsRequired();
            // SPEC-20260926-unified-database-provider RF-002: byte[] maps to
            // bytea under Npgsql, BLOB under Sqlite — keyed per provider by
            // ProviderAwareModelCacheKeyFactory.
            e.Property(c => c.Embedding).HasColumnType(Database.IsNpgsql() ? "bytea" : "BLOB");
            e.Property(c => c.ChunkKind).IsRequired().HasMaxLength(16);
            e.Property(c => c.SymbolPath).HasMaxLength(300);
            e.Property(c => c.SuspicionFlags).HasMaxLength(200);
        });

        modelBuilder.Entity<SecurityEvent>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Flags).IsRequired().HasMaxLength(200);
            e.HasIndex(s => s.SourceId);
            e.HasIndex(s => s.CreatedAt);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Username).IsRequired().HasMaxLength(64);
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.PasswordHash).IsRequired();
            e.HasMany(u => u.ApiKeys)
                .WithOne(k => k.User!)
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.Property(k => k.Name).IsRequired().HasMaxLength(100);
            e.Property(k => k.KeyHash).IsRequired().HasMaxLength(64);
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.Property(k => k.Prefix).IsRequired().HasMaxLength(16);
            e.Property(k => k.ProtectedKey).IsRequired(false);
            e.HasMany(k => k.UsageEvents)
                .WithOne(u => u.ApiKey!)
                .HasForeignKey(u => u.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiKeyUsageEvent>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.HttpMethod).IsRequired().HasMaxLength(16);
            e.Property(u => u.Path).IsRequired().HasMaxLength(256);
            e.Property(u => u.UserAgent).HasMaxLength(200);
            e.HasIndex(u => new { u.ApiKeyId, u.Timestamp });
        });

        modelBuilder.Entity<IntegrationSecret>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Provider).IsRequired().HasMaxLength(64);
            e.HasIndex(s => s.Provider).IsUnique();
            e.Property(s => s.ProtectedValue).IsRequired();
            e.Property(s => s.KeyHint).IsRequired().HasMaxLength(8);
        });

        modelBuilder.Entity<IntegrationState>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Provider).IsRequired().HasMaxLength(64);
            e.HasIndex(s => s.Provider).IsUnique();
        });

        modelBuilder.Entity<ChatSettings>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Endpoint).IsRequired().HasMaxLength(512);
            e.Property(s => s.Model).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<EmbeddingSettings>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Provider).IsRequired().HasMaxLength(50);
            e.Property(s => s.Endpoint).HasMaxLength(512);
            e.Property(s => s.Model).HasMaxLength(200);
            e.Property(s => s.ModelPath).HasMaxLength(512);
        });

        modelBuilder.Entity<GraphSettings>(e =>
        {
            e.HasKey(s => s.Id);
        });

        modelBuilder.Entity<ApiKeyChatSettings>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Endpoint).HasMaxLength(512);
            e.Property(s => s.Model).HasMaxLength(200);
            e.HasOne(s => s.ApiKey)
                .WithOne(k => k.ChatSettings)
                .HasForeignKey<ApiKeyChatSettings>(s => s.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.ApiKeyId).IsUnique();
        });

        modelBuilder.Entity<EvalRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.DatasetHash).IsRequired().HasMaxLength(64);
            e.Property(r => r.BaselineName).HasMaxLength(100);
            e.HasIndex(r => r.StartedAt);
        });

        modelBuilder.Entity<EvalBaseline>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.Name).IsRequired().HasMaxLength(100);
            e.Property(b => b.DatasetHash).IsRequired().HasMaxLength(64);
            e.HasIndex(b => b.Name).IsUnique();
        });

        modelBuilder.Entity<KgNode>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.Name).IsRequired().HasMaxLength(300);
            e.Property(n => n.NormalizedName).IsRequired().HasMaxLength(300);
            e.Property(n => n.Type).IsRequired().HasMaxLength(64);
            // Identity: normalized name + type — same name with another type is
            // a distinct node (conflict surfaced via aliases, never merged).
            e.HasIndex(n => new { n.NormalizedName, n.Type }).IsUnique();
            e.HasIndex(n => n.NormalizedName);
        });

        modelBuilder.Entity<KgEdge>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(32);
            e.Property(x => x.PromptVersion).HasMaxLength(16);
            e.HasIndex(x => x.FromNodeId);
            e.HasIndex(x => x.ToNodeId);
            e.HasIndex(x => x.EvidenceChunkId);
            e.HasIndex(x => x.KnowledgeDocumentId);
            e.HasIndex(x => x.KnowledgeSourceId);
            e.HasOne(x => x.From).WithMany().HasForeignKey(x => x.FromNodeId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.To).WithMany().HasForeignKey(x => x.ToNodeId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.KnowledgeDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KgAlias>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.AliasNormalized).IsRequired().HasMaxLength(300);
            e.Property(a => a.Reason).IsRequired().HasMaxLength(16);
            // A normalized spelling maps to one row per node — conflict rows
            // intentionally share the spelling across typed nodes.
            e.HasIndex(a => new { a.AliasNormalized, a.KgNodeId }).IsUnique();
            e.HasIndex(a => a.AliasNormalized);
            e.HasOne(a => a.Node).WithMany().HasForeignKey(a => a.KgNodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<McpTask>(e =>
        {
            e.HasKey(t => t.TaskId);
            e.Property(t => t.TaskId).HasMaxLength(80);
            e.Property(t => t.Status).IsRequired().HasMaxLength(24);
            e.Property(t => t.StatusMessage).HasMaxLength(500);
            e.HasIndex(t => t.Status);
        });

        modelBuilder.Entity<RagEvaluationEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.QueryId).IsRequired().HasMaxLength(120);
            e.Property(r => r.Question).IsRequired().HasMaxLength(1000);
            e.HasIndex(r => r.TimestampUtc);
            e.HasIndex(r => r.FlaggedAsHallucination);
        });
    }
}
