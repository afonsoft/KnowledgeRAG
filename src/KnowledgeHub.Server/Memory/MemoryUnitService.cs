using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Memory;

/// <summary>SPEC-20261111-agent-memory-retain RF-003: unit CRUD scoped by bank.</summary>
public sealed class MemoryUnitService(KnowledgeHubDbContext db, MemoryBankService banks)
{
    public async Task<IReadOnlyList<MemoryUnitDto>> ListAsync(
        string? bankKey, string? factType, string? tag, int limit, int offset,
        string? sessionId, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var q = db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted);
        if (factType is { Length: > 0 })
            q = q.Where(u => u.FactType == factType);
        if (tag is { Length: > 0 })
            q = q.Where(u => u.Tags.Contains(tag));
        if (sessionId is { Length: > 0 })
            q = q.Where(u => u.Document != null && u.Document.SourceSessionId == sessionId);
        var rows = await q.Include(u => u.Document)
            .OrderByDescending(u => u.MentionedAt)
            .Skip(Math.Max(offset, 0)).Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<MemoryUnitDto> GetAsync(string? bankKey, Guid id, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var u = await db.MemoryUnits.AsNoTracking().Include(x => x.Document)
            .FirstOrDefaultAsync(x => x.BankId == bank.Id && x.Id == id && !x.IsDeleted, ct)
            ?? throw new MemoryBadRequestException($"memory unit '{id}' not found");
        return ToDto(u);
    }

    public sealed record MemoryUnitPatch(
        string? Text, string? Context, IReadOnlyList<string>? Tags,
        IReadOnlyDictionary<string, string>? Metadata);

    /// <summary>Patch text/context/tags/metadata — rehashes + re-embeds when text changes.</summary>
    public async Task<MemoryUnitDto> UpdateAsync(
        string? bankKey, Guid id, MemoryUnitPatch patch,
        Embeddings.IEmbeddingProvider embeddings, CancellationToken ct)
    {
        var (text, context, tags, metadata) = patch;
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var u = await db.MemoryUnits.Include(x => x.Document)
            .FirstOrDefaultAsync(x => x.BankId == bank.Id && x.Id == id && !x.IsDeleted, ct)
            ?? throw new MemoryBadRequestException($"memory unit '{id}' not found");
        if (text is { Length: > 0 } newText)
        {
            u.Text = newText;
            u.ContentHash = MemoryHasher.Hash(newText);
            try
            {
                u.Embedding = MemoryRetainService.ToBytes(await embeddings.EmbedDocumentAsync(newText, ct));
                u.EmbeddingModel = embeddings.ModelId;
            }
            catch { /* semantic arm degrades — keep old embedding off */ u.Embedding = null; }
        }
        if (context is not null) u.Context = context;
        if (tags is not null) u.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(20).ToList();
        if (metadata is not null) u.MetadataJson = JsonSerializer.Serialize(metadata);
        await db.SaveChangesAsync(ct);
        return ToDto(u);
    }

    /// <summary>Soft delete by default; hard delete removes the row.</summary>
    public async Task<bool> DeleteAsync(string? bankKey, Guid id, bool hard, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var u = await db.MemoryUnits
            .FirstOrDefaultAsync(x => x.BankId == bank.Id && x.Id == id, ct)
            ?? throw new MemoryBadRequestException($"memory unit '{id}' not found");
        if (hard) db.MemoryUnits.Remove(u);
        else u.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    internal static MemoryUnitDto ToDto(MemoryUnit u) => new(
        u.Id, u.Text, u.FactType, u.Context, u.EventDate, u.OccurredStart, u.OccurredEnd,
        u.MentionedAt, u.Tags,
        u.MetadataJson is { } m
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(m)
            : null,
        u.ContentHash, u.Document != null ? u.Document.ExternalId : null, u.ChunkIndex,
        u.FactType == "observation" ? MemoryRecallService.ParseSources(u.SourceMemoryIdsJson) : null,
        u.FactType == "observation" ? u.ProofCount : null,
        // SPEC-20261112-memory-prompt-provenance RF-004 (preview ≤500 chars).
        u.Document?.SourcePrompt is { Length: > 0 } sp
            ? sp[..Math.Min(500, sp.Length)] : null,
        u.Document?.SourceSessionId, u.Document?.Origin);
}
