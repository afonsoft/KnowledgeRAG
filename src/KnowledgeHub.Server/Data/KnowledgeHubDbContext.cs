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
    /// <summary>SPEC-20261021-model-capability-profiles RF-1: user overrides;
    /// builtin profiles live in <see cref="Agent.ModelProfiles"/>.</summary>
    public DbSet<ModelProfile> ModelProfiles => Set<ModelProfile>();

    public DbSet<EmbeddingSettings> EmbeddingSettings => Set<EmbeddingSettings>();
    public DbSet<GraphSettings> GraphSettings => Set<GraphSettings>();
    public DbSet<ApiKeyChatSettings> ApiKeyChatSettings => Set<ApiKeyChatSettings>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<EvalBaseline> EvalBaselines => Set<EvalBaseline>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<KgNode> KgNodes => Set<KgNode>();
    public DbSet<KgEdge> KgEdges => Set<KgEdge>();
    public DbSet<KgAlias> KgAliases => Set<KgAlias>();
    /// <summary>SPEC-20260927-temporal-episodic-knowledge-graph: episode registry.</summary>
    public DbSet<KgEpisode> KgEpisodes => Set<KgEpisode>();
    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
    /// <summary>SPEC-20260928-resilience-tool-fallback-wiring: single-row fallback overrides.</summary>
    public DbSet<ResilienceSettings> ResilienceSettings => Set<ResilienceSettings>();
    /// <summary>Single-row assistant-provider override (SPEC-20260929-a2a-assistant-delegation).</summary>
    public DbSet<AssistantSettings> AssistantSettings => Set<AssistantSettings>();
    /// <summary>SPEC-20260926-mcp-sdk-alignment RF-004: durable MCP task handles.</summary>
    public DbSet<McpTask> McpTasks => Set<McpTask>();
    /// <summary>SPEC-20261001-a2a-task-durability RF-001: durable A2A tasks.</summary>
    public DbSet<A2aTask> A2aTasks => Set<A2aTask>();
    /// <summary>SPEC-20260927-rag-evaluation-triad-metrics: persisted triad scores.</summary>
    public DbSet<RagEvaluationEntity> RagEvaluations => Set<RagEvaluationEntity>();
    // SPEC-20260927-cryptographic-evidence-provenance-chain: append-only
    // receipt log — no UPDATE/DELETE contract.
    public DbSet<EvidenceReceipt> EvidenceReceipts => Set<EvidenceReceipt>();

    public DbSet<AgentFlow> AgentFlows => Set<AgentFlow>();
    public DbSet<FlowRun> FlowRuns => Set<FlowRun>();
    public DbSet<FlowTrigger> FlowTriggers => Set<FlowTrigger>();
    /// <summary>SPEC-20261004-context-snapshots RF-002: repo-scoped context.</summary>
    public DbSet<ContextSnapshot> ContextSnapshots => Set<ContextSnapshot>();

    // SPEC-20261111-agent-memory-retain RF-001: agent memory layer.
    public DbSet<MemoryBank> MemoryBanks => Set<MemoryBank>();
    public DbSet<MemoryUnit> MemoryUnits => Set<MemoryUnit>();
    public DbSet<MemoryDocument> MemoryDocuments => Set<MemoryDocument>();
    public DbSet<MemoryLink> MemoryLinks => Set<MemoryLink>();
    public DbSet<MemoryUnitEntity> MemoryUnitEntities => Set<MemoryUnitEntity>();
    public DbSet<MemoryDirective> MemoryDirectives => Set<MemoryDirective>();
    public DbSet<MemoryOperation> MemoryOperations => Set<MemoryOperation>();
    public DbSet<ObservationHistory> ObservationHistories => Set<ObservationHistory>();
    public DbSet<MemoryMentalModel> MemoryMentalModels => Set<MemoryMentalModel>();
    public DbSet<MentalModelHistory> MentalModelHistories => Set<MentalModelHistory>();

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
            // SPEC-20260927-chunk-window-retrieval-and-autocut RF-001: neighbour
            // lookup by (document, index) — one indexed range scan per expansion.
            e.HasIndex(c => new { c.KnowledgeDocumentId, c.ChunkIndex });
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

        modelBuilder.Entity<ModelProfile>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.ModelPattern).IsRequired().HasMaxLength(200);
            e.Property(p => p.Source).IsRequired().HasMaxLength(20);
            e.HasIndex(p => p.ModelPattern).IsUnique();
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

        modelBuilder.Entity<ResilienceSettings>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Mode).HasMaxLength(20);
        });

        modelBuilder.Entity<AssistantSettings>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Mode).HasMaxLength(20);
            e.Property(s => s.Endpoint).HasMaxLength(512);
            e.Property(s => s.Model).HasMaxLength(200);
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
            // SPEC-20260927-temporal-episodic-knowledge-graph: temporal scans +
            // episode lookup.
            e.HasIndex(n => n.ObservedAt);
            e.HasIndex(n => n.EpisodeId);
            e.HasOne(n => n.Episode).WithMany().HasForeignKey(n => n.EpisodeId)
                .OnDelete(DeleteBehavior.SetNull);
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
            e.HasIndex(x => x.ObservedAt);
            e.HasIndex(x => x.EpisodeId);
            e.HasOne(x => x.Episode).WithMany().HasForeignKey(x => x.EpisodeId)
                .OnDelete(DeleteBehavior.SetNull);
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

        // SPEC-20260927-temporal-episodic-knowledge-graph RF-001.
        modelBuilder.Entity<KgEpisode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(24);
            e.Property(x => x.Summary).HasMaxLength(500);
            e.HasIndex(x => x.KnowledgeSourceId);
            e.HasIndex(x => x.ThreadId);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<McpTask>(e =>
        {
            e.HasKey(t => t.TaskId);
            e.Property(t => t.TaskId).HasMaxLength(80);
            e.Property(t => t.Status).IsRequired().HasMaxLength(24);
            e.Property(t => t.StatusMessage).HasMaxLength(500);
            e.HasIndex(t => t.Status);
        });

        modelBuilder.Entity<A2aTask>(e =>
        {
            e.HasKey(t => t.TaskId);
            e.Property(t => t.TaskId).HasMaxLength(80);
            e.Property(t => t.ContextId).IsRequired().HasMaxLength(80);
            e.Property(t => t.State).IsRequired().HasMaxLength(24);
            e.Property(t => t.CallerKeyId).HasMaxLength(80);
            e.HasIndex(t => t.ContextId);
            e.HasIndex(t => t.State);
            e.HasIndex(t => t.LastUpdatedAt);
        });

        modelBuilder.Entity<RagEvaluationEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.QueryId).IsRequired().HasMaxLength(120);
            e.Property(r => r.Question).IsRequired().HasMaxLength(1000);
            e.HasIndex(r => r.TimestampUtc);
            e.HasIndex(r => r.FlaggedAsHallucination);
        });

        // SPEC-20260927-cryptographic-evidence-provenance-chain RF-001.
        modelBuilder.Entity<EvidenceReceipt>(e =>
        {
            e.HasKey(r => r.ReceiptId);
            e.Property(r => r.ReceiptId).HasMaxLength(80);
            e.Property(r => r.SessionId).IsRequired().HasMaxLength(120);
            e.Property(r => r.EventType).IsRequired().HasMaxLength(32);
            e.Property(r => r.ActorType).IsRequired().HasMaxLength(16);
            e.Property(r => r.InputHash).IsRequired().HasMaxLength(64);
            e.Property(r => r.OutputHash).IsRequired().HasMaxLength(64);
            e.Property(r => r.ParentDigest).HasMaxLength(64);
            e.Property(r => r.ReceiptDigest).IsRequired().HasMaxLength(64);
            e.Property(r => r.Signature).IsRequired().HasMaxLength(128);
            e.Property(r => r.KeyId).IsRequired().HasMaxLength(40);
            e.Property(r => r.ThreadId).HasMaxLength(80);
            e.Property(r => r.ApiKeyId).HasMaxLength(80);
            e.HasIndex(r => r.SessionId);
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => r.ReceiptDigest);
        });

        modelBuilder.Entity<AgentFlow>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.Name).IsRequired().HasMaxLength(200);
            e.Property(f => f.Slug).IsRequired().HasMaxLength(120);
            e.HasIndex(f => f.Slug).IsUnique();
            e.Property(f => f.Description).HasMaxLength(2000);
            e.HasIndex(f => f.Enabled);
        });

        modelBuilder.Entity<FlowRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Status).IsRequired().HasMaxLength(16);
            e.HasIndex(r => r.FlowId);
            e.HasIndex(r => r.CreatedAt);
            e.HasIndex(r => r.PendingApprovalId);
            e.HasOne<AgentFlow>()
                .WithMany()
                .HasForeignKey(r => r.FlowId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FlowTrigger>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Kind).IsRequired().HasMaxLength(16);
            e.Property(t => t.Secret).HasMaxLength(80);
            e.HasIndex(t => t.Secret).IsUnique();
            e.HasIndex(t => t.FlowId);
            e.HasOne<AgentFlow>()
                .WithMany()
                .HasForeignKey(t => t.FlowId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SPEC-20261004-context-snapshots RF-002.
        modelBuilder.Entity<ContextSnapshot>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.RepoKey).IsRequired().HasMaxLength(201);
            e.Property(s => s.Label).HasMaxLength(200);
            e.Property(s => s.IdempotencyKey).HasMaxLength(128);
            e.Property(s => s.PayloadJson).IsRequired()
                .HasColumnType(Database.IsNpgsql() ? "jsonb" : "TEXT");
            e.Property(s => s.Strategy).IsRequired().HasMaxLength(32);
            e.Property(s => s.Source).IsRequired().HasMaxLength(64);
            // Latest-by-version and dedup lookups; the CreatedAt index serves
            // retention pruning and the REST list endpoint.
            e.HasIndex(s => new { s.RepoKey, s.Version }).IsUnique();
            e.HasIndex(s => new { s.RepoKey, s.IdempotencyKey }).IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.HasIndex(s => new { s.RepoKey, s.CreatedAt });
            e.HasIndex(s => new { s.RepoKey, s.Label });
        });

        // SPEC-20261111-agent-memory-retain RF-001: agent memory layer.
        modelBuilder.Entity<MemoryBank>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.BankKey).IsRequired().HasMaxLength(200);
            e.Property(b => b.Name).IsRequired().HasMaxLength(200);
            e.Property(b => b.Mission).HasMaxLength(2000);
            // (BankKey, ApiKeyId) is the caller-facing identity — the same
            // slug may exist under different keys without collision.
            e.HasIndex(b => new { b.BankKey, b.ApiKeyId }).IsUnique();
            // SPEC-20261113: merge tombstone — self-FK, never cascades.
            e.HasOne(b => b.MergedInto).WithMany()
                .HasForeignKey(b => b.MergedIntoBankId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(b => b.MergedIntoBankId);
            e.HasMany(b => b.Units).WithOne(u => u.Bank)
                .HasForeignKey(u => u.BankId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(b => b.Documents).WithOne(d => d.Bank)
                .HasForeignKey(d => d.BankId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(b => b.Directives).WithOne(d => d.Bank)
                .HasForeignKey(d => d.BankId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemoryUnit>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Text).IsRequired();
            e.Property(u => u.FactType).IsRequired().HasMaxLength(16);
            e.Property(u => u.Context).HasMaxLength(1000);
            e.Property(u => u.ContentHash).IsRequired().HasMaxLength(64);
            e.Property(u => u.MetadataJson).HasColumnType(Database.IsNpgsql() ? "jsonb" : "TEXT");
            e.Property(u => u.Embedding).HasColumnType(Database.IsNpgsql() ? "bytea" : "BLOB");
            e.Property(u => u.EmbeddingModel).HasMaxLength(200);
            // FactType domain check — providers share the same DDL shape.
            e.ToTable(t => t.HasCheckConstraint(
                "CK_MemoryUnits_FactType",
                "\"FactType\" IN ('world','experience','observation')"));
            e.HasIndex(u => new { u.BankId, u.IsDeleted, u.FactType });
            e.HasIndex(u => new { u.BankId, u.EventDate });
            e.HasIndex(u => new { u.BankId, u.ConsolidatedAt });
            e.HasOne(u => u.Document).WithMany(d => d.Units)
                .HasForeignKey(u => u.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MemoryDocument>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.ExternalId).IsRequired().HasMaxLength(200);
            e.Property(d => d.Title).HasMaxLength(500);
            e.Property(d => d.ContentHash).IsRequired().HasMaxLength(64);
            e.Property(d => d.MetadataJson).HasColumnType(Database.IsNpgsql() ? "jsonb" : "TEXT");
            // SPEC-20261112-memory-prompt-provenance RF-001.
            e.Property(d => d.SourcePrompt).HasMaxLength(2000);
            e.Property(d => d.SourceSessionId).HasMaxLength(128);
            e.Property(d => d.Origin).IsRequired().HasMaxLength(16)
                .HasDefaultValue("explicit");
            e.Property(d => d.PromptHash).HasMaxLength(64);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_MemoryDocuments_Origin",
                "\"Origin\" IN ('explicit','passive','import','system')"));
            e.HasIndex(d => new { d.BankId, d.SourceSessionId });
            e.HasIndex(d => new { d.BankId, d.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<MemoryLink>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.LinkType).IsRequired().HasMaxLength(16);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_MemoryLinks_LinkType",
                "\"LinkType\" IN ('temporal','semantic','causal')"));
            // Dedupe guard — insertion is idempotent on the unique triple.
            e.HasIndex(l => new { l.FromUnitId, l.ToUnitId, l.LinkType, l.EntityId }).IsUnique();
            e.HasIndex(l => l.ToUnitId);
            e.HasOne(l => l.FromUnit).WithMany()
                .HasForeignKey(l => l.FromUnitId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.ToUnit).WithMany()
                .HasForeignKey(l => l.ToUnitId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<MemoryUnitEntity>(e =>
        {
            e.HasKey(ue => new { ue.UnitId, ue.EntityId });
            e.HasIndex(ue => new { ue.BankId, ue.EntityId });
            e.HasOne(ue => ue.Unit).WithMany()
                .HasForeignKey(ue => ue.UnitId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ue => ue.Entity).WithMany()
                .HasForeignKey(ue => ue.EntityId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemoryDirective>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Name).HasMaxLength(200);
            e.Property(d => d.Content).IsRequired().HasMaxLength(2000);
            e.HasIndex(d => new { d.BankId, d.IsActive });
        });

        modelBuilder.Entity<MemoryOperation>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Kind).IsRequired().HasMaxLength(16);
            e.Property(o => o.Status).IsRequired().HasMaxLength(16);
            e.Property(o => o.PayloadJson).IsRequired();
            e.HasIndex(o => new { o.BankId, o.Status });
            e.HasIndex(o => new { o.ApiKeyId, o.Status });
            e.HasOne(o => o.Bank).WithMany()
                .HasForeignKey(o => o.BankId).OnDelete(DeleteBehavior.Cascade);
        });

        // SPEC-20261111-memory-observations-consolidation RF-003.
        modelBuilder.Entity<ObservationHistory>(e =>
        {
            e.HasKey(h => h.Id);
            e.Property(h => h.Text).IsRequired();
            e.Property(h => h.AddedSourceIdsJson).IsRequired();
            e.Property(h => h.Action).IsRequired().HasMaxLength(16);
            e.HasIndex(h => new { h.ObservationUnitId, h.Version }).IsUnique();
            e.HasIndex(h => h.BankId);
            e.HasOne(h => h.Observation).WithMany()
                .HasForeignKey(h => h.ObservationUnitId).OnDelete(DeleteBehavior.Cascade);
        });

        // SPEC-20261111-mental-models-pages RF-001.
        modelBuilder.Entity<MemoryMentalModel>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Subtype).IsRequired().HasMaxLength(16);
            e.ToTable(t => t.HasCheckConstraint("CK_MentalModel_Subtype",
                "\"Subtype\" IN ('mental_model','page')"));
            e.Property(m => m.Name).IsRequired().HasMaxLength(200);
            e.Property(m => m.Path).IsRequired().HasMaxLength(500);
            e.Property(m => m.SourceQuery).IsRequired().HasMaxLength(2000);
            e.Property(m => m.Content).IsRequired();
            e.HasIndex(m => new { m.BankId, m.Path, m.Name }).IsUnique();
            e.HasIndex(m => new { m.BankId, m.Subtype });
            e.HasOne(m => m.Bank).WithMany()
                .HasForeignKey(m => m.BankId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MentalModelHistory>(e =>
        {
            e.HasKey(h => h.Id);
            e.Property(h => h.Content).IsRequired();
            e.HasIndex(h => new { h.ModelId, h.Version }).IsUnique();
            e.HasOne(h => h.Model).WithMany()
                .HasForeignKey(h => h.ModelId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
