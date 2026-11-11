using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Server.Memory.MentalModels;

public sealed record MentalModelDto(
    Guid Id, string Subtype, string Name, string Path, string SourceQuery,
    MentalModelTrigger Trigger, string Content,
    DateTime? LastRefreshedAt, DateTime? LastMemorySeenAt,
    string? LastRefreshError, IReadOnlyList<string> Tags,
    bool Stale, DateTime CreatedAt, DateTime? UpdatedAt);

public sealed record PageTreeNode(
    string Name, string Path, bool IsFolder,
    Guid? PageId, IReadOnlyList<PageTreeNode> Children);

public sealed record PageSearchHit(Guid Id, string Name, string Path, string Snippet);

/// <summary>
/// SPEC-20261111-mental-models-pages RF-001/RF-003/RF-004/RF-005: CRUD for
/// mental models + pages, folder tree, cheap DB reads (page_get is the
/// recommended session-start path — zero LLM).
/// </summary>
public sealed class MentalModelService(
    KnowledgeHubDbContext db,
    MemoryBankService banks,
    VaultPageProjector projector,
    IServiceProvider sp)
{
    public async Task<MentalModelDto> CreateAsync(
        string? bankKey, string subtype, string name, string? path,
        string sourceQuery, string? triggerJson, IReadOnlyList<string>? tags,
        CancellationToken ct)
    {
        if (subtype is not ("mental_model" or "page"))
            throw new MemoryBadRequestException("subtype must be 'mental_model' or 'page'");
        if (string.IsNullOrWhiteSpace(name))
            throw new MemoryBadRequestException("name is required");
        if (string.IsNullOrWhiteSpace(sourceQuery))
            throw new MemoryBadRequestException("sourceQuery is required");
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var norm = MentalModelTrigger.NormalizePath(path);
        var trigger = MentalModelTrigger.Parse(triggerJson, subtype);
        var exists = await db.MemoryMentalModels.AnyAsync(
            m => m.BankId == bank.Id && m.Path == norm && m.Name == name, ct);
        if (exists)
            throw new MemoryConflictException($"model '{name}' already exists at '{norm}'");

        var model = new MemoryMentalModel
        {
            BankId = bank.Id,
            Subtype = subtype,
            Name = name.Trim(),
            Path = norm,
            SourceQuery = sourceQuery.Trim(),
            TriggerJson = trigger.ToJson().GetRawText(),
            Tags = (tags ?? []).Select(t => t.ToLowerInvariant()).ToList(),
            Content = ""
        };
        db.MemoryMentalModels.Add(model);
        await db.SaveChangesAsync(ct);
        return ToDto(model, trigger, stale: true);
    }

    public async Task<IReadOnlyList<MentalModelDto>> ListAsync(
        string? bankKey, string? subtype, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var q = db.MemoryMentalModels.AsNoTracking().Where(m => m.BankId == bank.Id);
        if (subtype is not null)
            q = q.Where(m => m.Subtype == subtype);
        var models = await q.OrderBy(m => m.Path).ThenBy(m => m.Name).ToListAsync(ct);
        var modelsById = models.ToDictionary(m => m.Id);
        var staleIds = await StaleIdsAsync(bank, models, ct);
        return models.Select(m => ToDto(m, Trigger(m), staleIds.Contains(m.Id))).ToList();
    }

    public async Task<MentalModelDto> GetAsync(
        string? bankKey, Guid? id, string? path, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var model = await FindAsync(bank, id, path, null, ct)
            ?? throw new MemoryNotFoundException("mental model not found");
        var stale = (await StaleIdsAsync(bank, [model], ct)).Contains(model.Id);
        return ToDto(model, Trigger(model), stale);
    }

    /// <summary>RF-005: cheap DB read for page content; opt-in distributed cache.</summary>
    public async Task<MentalModelDto> GetPageAsync(
        string? bankKey, Guid? id, string? path, CancellationToken ct)
    {
        var cache = sp.GetService<IDistributedCache>();
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var key = $"page:{bank.Id}:{id?.ToString() ?? MentalModelTrigger.NormalizePath(path)}";
        var model = await FindAsync(bank, id, path, "page", ct)
            ?? throw new MemoryNotFoundException("page not found");
        // Cache stores the model row id → content; invalidated on refresh/edit.
        if (cache is not null)
        {
            var cached = await cache.GetStringAsync(key, ct);
            if (cached is not null && Guid.TryParse(cached.Split('|')[0], out var cid) && cid == model.Id)
            {
                var dto = ToDto(model, Trigger(model), stale: false);
                return dto with { Content = cached[(cached.IndexOf('|') + 1)..] };
            }
            await cache.SetStringAsync(key, $"{model.Id}|{model.Content}", ct);
        }
        var stale = (await StaleIdsAsync(bank, [model], ct)).Contains(model.Id);
        return ToDto(model, Trigger(model), stale);
    }

    public static async Task InvalidatePageCacheAsync(
        IServiceProvider sp, MemoryMentalModel model, CancellationToken ct)
    {
        var cache = sp.GetService<IDistributedCache>();
        if (cache is null)
            return;
        await cache.RemoveAsync($"page:{model.BankId}:{model.Id}", ct);
        await cache.RemoveAsync($"page:{model.BankId}:{model.Path}", ct);
    }

    public async Task<MentalModelDto> UpdateAsync(
        string? bankKey, Guid id, string? name, string? path, string? sourceQuery,
        string? triggerJson, IReadOnlyList<string>? tags, string? content,
        bool manualEdit, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var model = await db.MemoryMentalModels.FirstOrDefaultAsync(
            m => m.BankId == bank.Id && m.Id == id, ct)
            ?? throw new MemoryNotFoundException("mental model not found");
        var norm = path is null ? model.Path : MentalModelTrigger.NormalizePath(path);
        var newName = name?.Trim() ?? model.Name;
        var (oldPath, oldName) = (model.Path, model.Name);
        if (newName != model.Name || norm != model.Path)
        {
            var dup = await db.MemoryMentalModels.AnyAsync(
                m => m.BankId == bank.Id && m.Id != id && m.Path == norm && m.Name == newName, ct);
            if (dup)
                throw new MemoryConflictException($"model '{newName}' already exists at '{norm}'");
        }
        if (triggerJson is not null)
            model.TriggerJson = MentalModelTrigger.Parse(triggerJson, model.Subtype)
                .ToJson().GetRawText();
        if (sourceQuery is { Length: > 0 })
            model.SourceQuery = sourceQuery.Trim();
        if (tags is not null)
            model.Tags = tags.Select(t => t.ToLowerInvariant()).ToList();
        var contentChanged = content is not null && content != model.Content;
        if (contentChanged)
        {
            model.Content = content!;
            await AppendHistoryAsync(model, manualEdit: true, ct);
        }
        model.Name = newName;
        model.Path = norm;
        model.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await InvalidatePageCacheAsync(sp, model, ct);
        if (newName != oldName || norm != oldPath)
            await projector.MoveAsync(model, bank.BankKey, oldPath, oldName, ct);
        else if (contentChanged)
            await projector.ProjectAsync(model, bank.BankKey, ct);
        var stale = (await StaleIdsAsync(bank, [model], ct)).Contains(model.Id);
        return ToDto(model, Trigger(model), stale);
    }

    public async Task<bool> DeleteAsync(string? bankKey, Guid id, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var model = await db.MemoryMentalModels.FirstOrDefaultAsync(
            m => m.BankId == bank.Id && m.Id == id, ct)
            ?? throw new MemoryNotFoundException("mental model not found");
        db.MemoryMentalModels.Remove(model);
        await db.SaveChangesAsync(ct);
        await InvalidatePageCacheAsync(sp, model, ct);
        await projector.RemoveAsync(model, bank.BankKey, ct);
        return true;
    }

    /// <summary>Folder tree of pages for the bank (RF-003).</summary>
    public async Task<IReadOnlyList<PageTreeNode>> PageTreeAsync(string? bankKey, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var pages = await db.MemoryMentalModels.AsNoTracking()
            .Where(m => m.BankId == bank.Id && m.Subtype == "page")
            .OrderBy(m => m.Path).ThenBy(m => m.Name).ToListAsync(ct);
        var root = new List<MutableNode>();
        foreach (var p in pages)
        {
            var current = root;
            if (p.Path.Length > 0)
                foreach (var seg in p.Path.Split('/'))
                {
                    var folder = current.FirstOrDefault(n => n.IsFolder && n.Name == seg);
                    if (folder is null)
                    {
                        folder = new MutableNode(seg, true);
                        current.Add(folder);
                    }
                    current = folder.Children;
                }
            current.Add(new MutableNode(p.Name, false) { PageId = p.Id });
        }
        return root.Select(Freeze).ToList();

        static PageTreeNode Freeze(MutableNode n) =>
            new(n.Name, n.IsFolder ? n.Name : "", n.IsFolder, n.PageId,
                n.Children.Select(Freeze).ToList());
    }

    private sealed class MutableNode(string name, bool isFolder)
    {
        public string Name { get; } = name;
        public bool IsFolder { get; } = isFolder;
        public Guid? PageId { get; set; }
        public List<MutableNode> Children { get; } = [];
    }

    /// <summary>Simple token-overlap search over page Name+Content (RF-004).</summary>
    public async Task<IReadOnlyList<PageSearchHit>> SearchPagesAsync(
        string? bankKey, string query, int limit, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var pages = await db.MemoryMentalModels.AsNoTracking()
            .Where(m => m.BankId == bank.Id && m.Subtype == "page").ToListAsync(ct);
        var terms = MemoryRecallService.Tokenize(query).ToHashSet();
        return pages.Select(p =>
            {
                var hay = $"{p.Name}\n{p.Content}";
                var score = MemoryRecallService.Tokenize(hay).Count(terms.Contains);
                return (p, score);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(x => new PageSearchHit(x.p.Id, x.p.Name, x.p.Path,
                Snippet(x.p.Content, terms)))
            .ToList();

        static string Snippet(string content, HashSet<string> terms)
        {
            var idx = -1;
            foreach (var t in terms)
            {
                var i = content.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (idx < 0 || i < idx))
                    idx = i;
            }
            if (idx < 0)
                return content.Length <= 160 ? content : content[..160];
            var start = Math.Max(0, idx - 40);
            var len = Math.Min(160, content.Length - start);
            return content.Substring(start, len);
        }
    }

    public async Task<MemoryMentalModel?> FindAsync(
        MemoryBank bank, Guid? id, string? path, string? subtype, CancellationToken ct)
    {
        var q = db.MemoryMentalModels.AsNoTracking().Where(m => m.BankId == bank.Id);
        if (subtype is not null)
            q = q.Where(m => m.Subtype == subtype);
        if (id is { } g)
            return await q.FirstOrDefaultAsync(m => m.Id == g, ct);
        if (path is null)
            return null;
        // path arg = "folder/name" or just "name" — the folder part follows
        // path normalization; the name part is free text (accents, spaces).
        var slash = path.LastIndexOf('/');
        var (folderRaw, name) = slash >= 0 ? (path[..slash], path[(slash + 1)..]) : ("", path);
        var folder = MentalModelTrigger.NormalizePath(folderRaw);
        var candidates = await q.Where(m => m.Path == folder).ToListAsync(ct);
        return candidates.FirstOrDefault(
            m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ids of models with newer in-scope units than their watermark.</summary>
    public async Task<HashSet<Guid>> StaleIdsAsync(
        MemoryBank bank, List<MemoryMentalModel> models, CancellationToken ct)
    {
        var stale = new HashSet<Guid>();
        if (models.Count == 0)
            return stale;
        // Latest MentionedAt per (factType, tag) would be ideal; cheap version:
        // for each model, any unit in scope with MentionedAt > watermark.
        foreach (var m in models)
        {
            var trigger = Trigger(m);
            var q = db.MemoryUnits.AsNoTracking()
                .Where(u => u.BankId == bank.Id && !u.IsDeleted);
            if (trigger.FactTypes is not null)
                q = q.Where(u => trigger.FactTypes.Contains(u.FactType));
            if (m.LastMemorySeenAt is { } wm)
                q = q.Where(u => u.MentionedAt > wm);
            // Tag scoping can't translate to SQL for primitive collections on
            // all providers — materialize ids only when tag filter exists.
            if (trigger.Tags is { Count: > 0 })
            {
                var cands = await q.Select(u => new { u.FactType, u.Tags })
                    .Take(500).ToListAsync(ct);
                if (cands.Any(c => trigger.ScopeMatches(c.FactType, c.Tags)))
                    stale.Add(m.Id);
            }
            else if (await q.AnyAsync(ct))
            {
                stale.Add(m.Id);
            }
        }
        return stale;
    }

    internal async Task AppendHistoryAsync(
        MemoryMentalModel model, bool manualEdit, CancellationToken ct)
    {
        var version = await db.MentalModelHistories
            .Where(h => h.ModelId == model.Id)
            .Select(h => (int?)h.Version).MaxAsync(ct) ?? 0;
        db.MentalModelHistories.Add(new MentalModelHistory
        {
            ModelId = model.Id,
            Version = version + 1,
            Content = model.Content,
            ManualEdit = manualEdit
        });
    }

    internal static MentalModelTrigger Trigger(MemoryMentalModel m) =>
        MentalModelTrigger.Parse(m.TriggerJson, m.Subtype);

    private static MentalModelDto ToDto(
        MemoryMentalModel m, MentalModelTrigger trigger, bool stale) =>
        new(m.Id, m.Subtype, m.Name, m.Path, m.SourceQuery, trigger, m.Content,
            m.LastRefreshedAt, m.LastMemorySeenAt, m.LastRefreshError, m.Tags, stale,
            m.CreatedAt, m.UpdatedAt);
}
