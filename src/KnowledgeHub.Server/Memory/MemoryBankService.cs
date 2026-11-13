using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-001/RF-003: bank resolution + CRUD,
/// scoped by (BankKey, ApiKeyId). <see cref="MemoryBank.ServerWideKey"/>
/// represents cookie/admin callers so unrestricted sessions share one
/// default bank per slug.
/// </summary>
public sealed class MemoryBankService(KnowledgeHubDbContext db, ICallerScopeProvider callerScope)
{
    /// <summary>Scope key for the current caller (null → ServerWideKey).</summary>
    public async Task<Guid> ScopeKeyAsync(CancellationToken ct) =>
        (await callerScope.GetAsync(ct)).ApiKeyId ?? MemoryBank.ServerWideKey;

    private const int MaxMergeHops = 8;

    /// <summary>Resolve or auto-create a bank for the caller ("default" when key null).</summary>
    public async Task<MemoryBank> ResolveAsync(string? bankKey, CancellationToken ct, bool create = true)
    {
        var key = string.IsNullOrWhiteSpace(bankKey) ? "default" : bankKey.Trim();
        if (key.Length > 200)
            throw new MemoryBadRequestException("bankKey exceeds 200 characters");
        var scope = await ScopeKeyAsync(ct);
        var bank = await db.MemoryBanks
            .FirstOrDefaultAsync(b => b.BankKey == key && b.ApiKeyId == scope, ct);
        if (bank is null && create)
        {
            bank = new MemoryBank { BankKey = key, ApiKeyId = scope, Name = key };
            db.MemoryBanks.Add(bank);
            await db.SaveChangesAsync(ct);
        }
        if (bank is null)
            throw new MemoryNotFoundException($"memory bank '{key}' not found");
        return await FollowMergeAsync(bank, key, ct);
    }

    /// <summary>SPEC-20261113 RF-002: merged banks redirect to their target
    /// (≤8 hops, cycle-safe) — old keys keep working after a merge.</summary>
    private async Task<MemoryBank> FollowMergeAsync(MemoryBank bank, string key, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { bank.Id };
        var hops = 0;
        while (bank.MergedIntoBankId is { } next)
        {
            if (++hops > MaxMergeHops || !seen.Add(next))
                throw new MemoryBadRequestException(
                    $"memory bank '{key}' merge chain cycles or exceeds {MaxMergeHops} hops");
            bank = await db.MemoryBanks.FindAsync([next], ct)
                ?? throw new MemoryNotFoundException($"memory bank '{key}' merge target missing");
        }
        return bank;
    }

    public async Task<IReadOnlyList<MemoryBankDto>> ListAsync(bool includeMerged, CancellationToken ct)
    {
        var scope = await ScopeKeyAsync(ct);
        var q = db.MemoryBanks.AsNoTracking().Where(b => b.ApiKeyId == scope);
        if (!includeMerged)
            q = q.Where(b => b.MergedIntoBankId == null);
        return await q
            .OrderBy(b => b.BankKey)
            .Select(b => new MemoryBankDto(
                b.Id, b.BankKey, b.Name, b.Mission, b.DispositionJson, b.ConfigJson,
                b.CreatedAt, b.Units.Count(u => !u.IsDeleted), b.MergedIntoBankId != null))
            .ToListAsync(ct);
    }

    public async Task<MemoryBank> CreateAsync(string bankKey, string name,
        string? mission, string? dispositionJson, string? configJson, CancellationToken ct)
    {
        var key = bankKey.Trim();
        if (key.Length is 0 or > 200)
            throw new MemoryBadRequestException("bankKey is required (max 200 chars)");
        var scope = await ScopeKeyAsync(ct);
        var exists = await db.MemoryBanks.AnyAsync(b => b.BankKey == key && b.ApiKeyId == scope, ct);
        if (exists)
            throw new MemoryBadRequestException($"memory bank '{key}' already exists in this scope");
        var bank = new MemoryBank
        {
            BankKey = key,
            ApiKeyId = scope,
            Name = string.IsNullOrWhiteSpace(name) ? key : name,
            Mission = mission,
            DispositionJson = dispositionJson,
            ConfigJson = configJson
        };
        db.MemoryBanks.Add(bank);
        await db.SaveChangesAsync(ct);
        return bank;
    }

    public async Task<MemoryBank> UpdateAsync(string bankKey, string? name,
        string? mission, string? dispositionJson, string? configJson, CancellationToken ct)
    {
        var bank = await ResolveAsync(bankKey, ct, create: false);
        if (name is not null) bank.Name = name;
        if (mission is not null) bank.Mission = mission;
        if (dispositionJson is not null) bank.DispositionJson = dispositionJson;
        if (configJson is not null) bank.ConfigJson = configJson;
        await db.SaveChangesAsync(ct);
        return bank;
    }

    /// <summary>Hard delete — cascade removes units, documents, links, operations.</summary>
    public async Task<bool> DeleteAsync(string bankKey, CancellationToken ct)
    {
        var bank = await ResolveAsync(bankKey, ct, create: false);
        db.MemoryBanks.Remove(bank);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
