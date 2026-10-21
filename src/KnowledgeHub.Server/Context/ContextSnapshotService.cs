using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace KnowledgeHub.Server.Context;

/// <summary>
/// EF-facing persistence for <see cref="ContextSnapshot"/>
/// (SPEC-20261004-context-snapshots RF-002/004/005). All database access for
/// the feature lives here — compaction stays EF-free in
/// <see cref="ContextCompactionService"/> (layering guardrail).
/// Version allocation is <c>max(Version)+1</c> inside a transaction with
/// unique-violation retry (SQLite and Postgres).
/// </summary>
public sealed class ContextSnapshotService
{
    private readonly IDbContextFactory<KnowledgeHubDbContext> _dbFactory;
    private readonly ContextOptions _options;
    private readonly ContextCacheService _cache;
    private readonly ILogger<ContextSnapshotService> _logger;

    private const int MaxSaveRetries = 3;

    public ContextSnapshotService(
        IDbContextFactory<KnowledgeHubDbContext> dbFactory,
        IOptions<ContextOptions> options,
        ContextCacheService cache,
        ILogger<ContextSnapshotService> logger)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
        _cache = cache;
        _logger = logger;
    }

    public sealed record SaveResult(
        ContextSnapshot Snapshot, int Pruned, bool IdempotentHit);

    public sealed record SnapshotMeta(
        Guid Id, int Version, string? Label, int TokenEstimate, int PayloadBytes,
        bool Compacted, string Strategy, string Source, DateTimeOffset CreatedAt);

    /// <summary>
    /// Grava um novo snapshot versionado. Idempotente por
    /// <paramref name="idempotencyKey"/> (UX parcial por repo) — retry MCP
    /// com a mesma chave retorna o registro existente.
    /// </summary>
    public async Task<SaveResult> SaveAsync(
        RepoScope repo,
        IReadOnlyList<ContextItem> items,
        string? label,
        string? idempotencyKey,
        string strategy,
        bool compacted,
        string source,
        Guid? createdByKeyId,
        CancellationToken ct)
    {
        var payloadJson = ContextPayload.Serialize(items);
        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes > _options.MaxPayloadBytes)
        {
            throw new McpProtocolException(
                $"payload_too_large: {payloadBytes} bytes exceeds {_options.MaxPayloadBytes}",
                McpErrorCode.InvalidParams);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (idempotencyKey is not null)
        {
            var existing = await db.ContextSnapshots
                .FirstOrDefaultAsync(s => s.RepoKey == repo.Key
                    && s.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return new SaveResult(existing, 0, true);
        }

        ContextSnapshot? saved = null;
        var pruned = 0;
        for (var attempt = 1; attempt <= MaxSaveRetries; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var nextVersion = (await db.ContextSnapshots
                .Where(s => s.RepoKey == repo.Key)
                .MaxAsync(s => (int?)s.Version, ct)) + 1 ?? 1;

            saved = new ContextSnapshot
            {
                Id = Guid.NewGuid(),
                RepoKey = repo.Key,
                Version = nextVersion,
                Label = label,
                IdempotencyKey = idempotencyKey,
                PayloadJson = payloadJson,
                PayloadBytes = payloadBytes,
                TokenEstimate = ContextPayload.EstimateTokens(payloadJson),
                Compacted = compacted,
                Strategy = strategy,
                Source = source,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByKeyId = createdByKeyId
            };
            db.ContextSnapshots.Add(saved);

            // Retenção por contagem na mesma transação (RF-002). A query só vê
            // as linhas persistidas — o novo snapshot ainda não foi salvo —,
            // então o limite aplicado aos antigos é Max-1 para o total final
            // ficar em Max.
            var excess = await db.ContextSnapshots
                .Where(s => s.RepoKey == repo.Key)
                .OrderByDescending(s => s.Version)
                .Skip(_options.MaxSnapshotsPerRepo - 1)
                .ToListAsync(ct);
            if (excess.Count > 0)
            {
                db.ContextSnapshots.RemoveRange(excess);
                pruned = excess.Count;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                break;
            }
            catch (DbUpdateException) when (attempt < MaxSaveRetries)
            {
                // Unique (RepoKey, Version) ou (RepoKey, IdempotencyKey)
                // sob concorrência — recalcula e tenta de novo.
                db.ChangeTracker.Clear();
                saved = null;
            }
        }

        if (saved is null)
            throw new McpProtocolException("context_save_conflict: version allocation failed after retries",
                McpErrorCode.InternalError);

        await _cache.SetLatestVersionAsync(repo.Key, saved.Version, ct);
        await _cache.InvalidateRepoAsync(repo.Key, ct);
        return new SaveResult(saved, pruned, false);
    }

    /// <summary>Snapshot mais recente do repo — ponteiro de cache quando
    /// disponível (RF-007), senão query por <c>CreatedAt</c>.</summary>
    public async Task<ContextSnapshot?> GetLatestAsync(RepoScope repo, CancellationToken ct)
    {
        var pointer = await _cache.TryGetLatestVersionAsync(repo.Key, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.ContextSnapshots.Where(s => s.RepoKey == repo.Key);
        return pointer is { } v
            ? await query.FirstOrDefaultAsync(s => s.Version == v, ct)
              ?? await query.OrderByDescending(s => s.Version).FirstOrDefaultAsync(ct)
            : await query.OrderByDescending(s => s.Version).FirstOrDefaultAsync(ct);
    }

    public async Task<ContextSnapshot?> GetByVersionAsync(
        RepoScope repo, int version, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ContextSnapshots
            .FirstOrDefaultAsync(s => s.RepoKey == repo.Key && s.Version == version, ct);
    }

    /// <summary>Snapshot mais recente com o rótulo (RF-005 label selector).</summary>
    public async Task<ContextSnapshot?> GetByLabelAsync(
        RepoScope repo, string label, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ContextSnapshots
            .Where(s => s.RepoKey == repo.Key && s.Label == label)
            .OrderByDescending(s => s.Version)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Lista metadados (sem payload) — REST <c>GET /snapshots</c>.</summary>
    public async Task<IReadOnlyList<SnapshotMeta>> ListAsync(
        RepoScope repo, int take, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ContextSnapshots
            .Where(s => s.RepoKey == repo.Key)
            .OrderByDescending(s => s.Version)
            .Take(take)
            .Select(s => new SnapshotMeta(s.Id, s.Version, s.Label, s.TokenEstimate,
                s.PayloadBytes, s.Compacted, s.Strategy, s.Source, s.CreatedAt))
            .ToListAsync(ct);
    }

    public static List<ContextItem> ItemsOf(ContextSnapshot snapshot) =>
        ContextPayload.Deserialize(snapshot.PayloadJson).Items;
}
