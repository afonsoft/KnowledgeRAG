using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Memory;

/// <summary>SPEC-20261111-memory-reflect-directives RF-001.</summary>
public sealed record MemoryDirectiveDto(
    Guid Id, string? Name, string Content, int Priority,
    bool IsActive, IReadOnlyList<string> Tags, DateTime CreatedAt);

/// <summary>
/// SPEC-20261111-memory-reflect-directives RF-001: directive CRUD scoped by
/// bank. Directives order by <see cref="MemoryDirective.Priority"/> desc;
/// empty <see cref="MemoryDirective.Tags"/> = bank-global.
/// </summary>
public sealed class MemoryDirectiveService(KnowledgeHubDbContext db, MemoryBankService banks)
{
    public async Task<IReadOnlyList<MemoryDirectiveDto>> ListAsync(
        string? bankKey, bool includeInactive, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var q = db.MemoryDirectives.AsNoTracking().Where(d => d.BankId == bank.Id);
        if (!includeInactive)
            q = q.Where(d => d.IsActive);
        return await q.OrderByDescending(d => d.Priority).ThenBy(d => d.CreatedAt)
            .Select(d => new MemoryDirectiveDto(d.Id, d.Name, d.Content, d.Priority,
                d.IsActive, d.Tags, d.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<MemoryDirectiveDto> CreateAsync(string? bankKey, string? name,
        string content, int priority, bool? isActive, IReadOnlyList<string>? tags,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new MemoryBadRequestException("content is required");
        if (content.Length > 2000)
            throw new MemoryBadRequestException("content exceeds 2000 characters");
        var bank = await banks.ResolveAsync(bankKey, ct);
        var d = new MemoryDirective
        {
            BankId = bank.Id,
            Name = name?.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 200)] : null,
            Content = content.Trim(),
            Priority = priority,
            IsActive = isActive ?? true,
            Tags = tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(20).ToList() ?? []
        };
        db.MemoryDirectives.Add(d);
        await db.SaveChangesAsync(ct);
        return new MemoryDirectiveDto(d.Id, d.Name, d.Content, d.Priority,
            d.IsActive, d.Tags, d.CreatedAt);
    }

    /// <summary>Patch active flag/priority/content — stamps UpdatedAt.</summary>
    public async Task<MemoryDirectiveDto> UpdateAsync(string? bankKey, Guid id,
        bool? isActive, int? priority, string? content, IReadOnlyList<string>? tags,
        CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var d = await db.MemoryDirectives
            .FirstOrDefaultAsync(x => x.BankId == bank.Id && x.Id == id, ct)
            ?? throw new MemoryNotFoundException($"directive '{id}' not found");
        if (isActive is { } a) d.IsActive = a;
        if (priority is { } p) d.Priority = p;
        if (content is { Length: > 0 } c) d.Content = c;
        if (tags is not null)
            d.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(20).ToList();
        d.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new MemoryDirectiveDto(d.Id, d.Name, d.Content, d.Priority,
            d.IsActive, d.Tags, d.CreatedAt);
    }

    public async Task<bool> DeleteAsync(string? bankKey, Guid id, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var d = await db.MemoryDirectives
            .FirstOrDefaultAsync(x => x.BankId == bank.Id && x.Id == id, ct)
            ?? throw new MemoryNotFoundException($"directive '{id}' not found");
        db.MemoryDirectives.Remove(d);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
