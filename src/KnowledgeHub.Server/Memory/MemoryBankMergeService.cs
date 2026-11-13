using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261113-memory-bank-merge RF-001/RF-005: merges one bank into another
/// inside the caller's key scope. Every bank-scoped row is re-parented in one
/// transaction; semantic collisions resolve "target wins" deterministically;
/// the source becomes a <see cref="MemoryBank.MergedIntoBankId"/> tombstone so
/// old keys keep resolving. Audited as a <c>bank_merge</c> MemoryOperation.
/// </summary>
public sealed class MemoryBankMergeService(KnowledgeHubDbContext db, MemoryBankService banks)
{
    public sealed record MemoryBankMergeResult(
        bool Merged, bool AlreadyMerged, Guid TargetBankId,
        IReadOnlyDictionary<string, int> Moved,
        IReadOnlyDictionary<string, int> Skipped);

    private static readonly string[] Tables =
        ["units", "documents", "directives", "mentalModels", "operations", "unitEntities", "observationHistories"];

    public async Task<MemoryBankMergeResult> MergeAsync(
        string sourceKey, string targetKey, CancellationToken ct)
    {
        // RF-001: re-parenting + tombstone commit atomically — ExecuteUpdate
        // statements run outside the SaveChanges batch, so an explicit
        // transaction (execution-strategy aware for retrying providers) is
        // required for "one transaction".
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var result = await MergeCoreAsync(sourceKey, targetKey, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private async Task<MemoryBankMergeResult> MergeCoreAsync(
        string sourceKey, string targetKey, CancellationToken ct)
    {
        var scope = await banks.ScopeKeyAsync(ct);
        var source = await db.MemoryBanks
            .FirstOrDefaultAsync(b => b.BankKey == sourceKey.Trim() && b.ApiKeyId == scope, ct)
            ?? throw new MemoryNotFoundException($"memory bank '{sourceKey}' not found");
        var target = await banks.ResolveAsync(targetKey, ct, create: false);

        if (source.Id == target.Id)
            throw new MemoryBadRequestException("cannot merge a bank into itself");
        if (source.MergedIntoBankId is { } already)
        {
            if (already == target.Id)
                return new MemoryBankMergeResult(false, true, target.Id, Zero(), Zero());
            var real = await db.MemoryBanks.FindAsync([already], ct);
            throw new MemoryBadRequestException(
                $"bank '{sourceKey}' is already merged into '{real?.BankKey ?? already.ToString()}'");
        }

        // --- collision sets (target wins) ---
        var targetUnitHashes = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == target.Id && !u.IsDeleted)
            .Select(u => u.ContentHash).ToListAsync(ct);
        var targetDocIds = await db.MemoryDocuments.AsNoTracking()
            .Where(d => d.BankId == target.Id).Select(d => d.ExternalId).ToListAsync(ct);
        var targetDirectiveNames = await db.MemoryDirectives.AsNoTracking()
            .Where(d => d.BankId == target.Id).Select(d => d.Name).ToListAsync(ct);
        var targetModelNames = await db.MemoryMentalModels.AsNoTracking()
            .Where(m => m.BankId == target.Id).Select(m => m.Name).ToListAsync(ct);

        // --- units: move non-colliding, soft-delete collisions in place ---
        var collidingUnits = await db.MemoryUnits
            .Where(u => u.BankId == source.Id && !u.IsDeleted
                && targetUnitHashes.Contains(u.ContentHash)).ToListAsync(ct);
        foreach (var u in collidingUnits) u.IsDeleted = true;
        var movedUnitIds = await db.MemoryUnits
            .Where(u => u.BankId == source.Id && !u.IsDeleted)
            .Select(u => u.Id).ToListAsync(ct);

        var moved = new Dictionary<string, int>(Tables.Select(t => KeyValuePair.Create(t, 0)));
        var skipped = new Dictionary<string, int>(Tables.Select(t => KeyValuePair.Create(t, 0)));
        skipped["units"] = collidingUnits.Count;

        moved["units"] = await db.MemoryUnits
            .Where(u => movedUnitIds.Contains(u.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.BankId, target.Id), ct);

        var sourceDocs = await db.MemoryDocuments.Where(d => d.BankId == source.Id).ToListAsync(ct);
        skipped["documents"] = sourceDocs.Count(d => targetDocIds.Contains(d.ExternalId));
        foreach (var d in sourceDocs.Where(d => !targetDocIds.Contains(d.ExternalId)))
            d.BankId = target.Id;
        moved["documents"] = sourceDocs.Count - skipped["documents"];

        var sourceDirectives = await db.MemoryDirectives.Where(d => d.BankId == source.Id).ToListAsync(ct);
        skipped["directives"] = sourceDirectives.Count(d => d.Name is not null && targetDirectiveNames.Contains(d.Name));
        foreach (var d in sourceDirectives.Where(d => !(d.Name is not null && targetDirectiveNames.Contains(d.Name))))
            d.BankId = target.Id;
        moved["directives"] = sourceDirectives.Count - skipped["directives"];

        var sourceModels = await db.MemoryMentalModels.Where(m => m.BankId == source.Id).ToListAsync(ct);
        skipped["mentalModels"] = sourceModels.Count(m => targetModelNames.Contains(m.Name));
        foreach (var m in sourceModels.Where(m => !targetModelNames.Contains(m.Name)))
            m.BankId = target.Id;
        moved["mentalModels"] = sourceModels.Count - skipped["mentalModels"];

        moved["operations"] = await db.MemoryOperations
            .Where(o => o.BankId == source.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.BankId, target.Id), ct);
        moved["unitEntities"] = await db.MemoryUnitEntities
            .Where(e => movedUnitIds.Contains(e.UnitId))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.BankId, target.Id), ct);
        moved["observationHistories"] = await db.ObservationHistories
            .Where(h => movedUnitIds.Contains(h.ObservationUnitId))
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.BankId, target.Id), ct);

        // Config fill-gaps: target wins; source only fills absent fields.
        target.Mission ??= source.Mission;
        target.DispositionJson ??= source.DispositionJson;
        target.ConfigJson ??= source.ConfigJson;
        source.MergedIntoBankId = target.Id;

        // RF-005 audit — counts land AFTER re-parenting so the audit op itself
        // is recorded on the target (moved["operations"] + 1 for the caller).
        db.MemoryOperations.Add(new MemoryOperation
        {
            BankId = target.Id,
            ApiKeyId = scope,
            Kind = "bank_merge",
            Status = "succeeded",
            PayloadJson = JsonSerializer.Serialize(new { sourceKey, targetKey }),
            ResultJson = JsonSerializer.Serialize(new { moved, skipped }),
            StartedAt = DateTime.UtcNow,
            FinishedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return new MemoryBankMergeResult(true, false, target.Id, moved, skipped);
    }

    private static IReadOnlyDictionary<string, int> Zero() =>
        Tables.ToDictionary(t => t, _ => 0);
}
