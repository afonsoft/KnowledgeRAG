using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using KnowledgeHub.Server.Graph;
using KnowledgeHub.Server.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-004: memory recall — semantic,
/// lexical, entity-graph and temporal arms fused with
/// <see cref="RrfFuser"/> (k=60). The temporal arm only exists when a window
/// is supplied (RF-004/AC-4).
/// </summary>
public sealed class MemoryRecallService(
    KnowledgeHubDbContext db,
    MemoryBankService banks,
    IEmbeddingProvider embeddings,
    ILogger<MemoryRecallService> logger)
{
    private const int ArmTopN = 40;
    private const int ScanCap = 5_000;

    public async Task<MemoryRecallResult> RecallAsync(
        string? bankKey, MemoryRecallRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Query))
            throw new MemoryBadRequestException("query is required");
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var topK = Math.Clamp(req.TopK, 1, 100);

        TimeSpan? window = null;
        if (req.Window is { Length: > 0 } w && TemporalDateParser.TryParseWindow(w, out var span))
            window = span;
        else if (req.Window is { Length: > 0 })
            throw new MemoryBadRequestException($"window '{req.Window}' is not a supported window");

        // Candidate universe: bank units (soft-delete + fact-type filtered).
        var query = db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted);
        if (req.FactType is { Length: > 0 } ft)
        {
            if (!MemoryUnit.FactTypes.Contains(ft))
                throw new MemoryBadRequestException($"factType must be one of {string.Join('|', MemoryUnit.FactTypes)}");
            query = query.Where(u => u.FactType == ft);
        }
        var units = await query
            .OrderByDescending(u => u.MentionedAt)
            .Take(ScanCap)
            .Select(u => new
            {
                u.Id, u.Text, u.FactType, u.Context, u.EventDate, u.MentionedAt,
                u.Tags, u.Embedding, u.EmbeddingModel
            })
            .ToListAsync(ct);

        var arms = new List<(string Arm, IReadOnlyList<Guid> Ranked)>();
        var used = new List<string>();

        // Arm 1 — semantic: cosine vs stored embeddings (model-matched only).
        try
        {
            var qvec = await embeddings.EmbedQueryAsync(req.Query, ct);
            var ranked = units
                .Where(u => u.Embedding is not null && u.EmbeddingModel == embeddings.ModelId)
                .Select(u => (u.Id, Score: Cosine(qvec, MemoryRetainService.FromBytes(u.Embedding!))))
                .Where(x => x.Score > 0.05)
                .OrderByDescending(x => x.Score)
                .Take(ArmTopN)
                .Select(x => x.Id)
                .ToList();
            // Arm names match RrfFuser's tracked arms (vector|lexical|graph);
            // "temporal" fuses into the score without a rank field — arm
            // provenance is reported from the arm lists below.
            if (ranked.Count > 0) { arms.Add(("vector", ranked)); used.Add("semantic"); }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "memory recall: semantic arm unavailable");
        }

        // Arm 2 — lexical: query-token overlap with IDF weighting.
        var qTokens = Tokenize(req.Query);
        if (qTokens.Count > 0)
        {
            var docTokens = units.ToDictionary(u => u.Id, u => Tokenize(u.Text));
            var df = new Dictionary<string, int>();
            foreach (var toks in docTokens.Values)
                foreach (var t in toks)
                    df[t] = df.GetValueOrDefault(t) + 1;
            var n = Math.Max(units.Count, 1);
            var ranked = docTokens
                .Select(kv => (kv.Key, Score: qTokens.Sum(t =>
                    kv.Value.Contains(t) ? Math.Log(1.0 + n / (1.0 + df.GetValueOrDefault(t, 0))) : 0)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(ArmTopN)
                .Select(x => x.Key)
                .ToList();
            if (ranked.Count > 0) { arms.Add(("lexical", ranked)); used.Add("lexical"); }
        }

        // Arm 3 — graph: entities mentioned in the query → unit links.
        var entityNames = qTokens.Select(t => EntityResolver.Normalize(t)).ToHashSet();
        if (entityNames.Count > 0)
        {
            var entityIds = await db.MemoryUnitEntities.AsNoTracking()
                .Where(ue => ue.BankId == bank.Id && entityNames.Contains(ue.Entity.NormalizedName))
                .GroupBy(ue => ue.UnitId)
                .Select(g => new { g.Key, Matches = g.Select(x => x.EntityId).Distinct().Count() })
                .ToListAsync(ct);
            var ranked = entityIds.OrderByDescending(x => x.Matches).Take(ArmTopN).Select(x => x.Key).ToList();
            if (ranked.Count > 0) { arms.Add(("graph", ranked)); used.Add("graph"); }
        }

        // Arm 4 — temporal: exists only when a window was supplied.
        if (window is { } windowSpan)
        {
            var cutoff = DateTime.UtcNow - windowSpan;
            var ranked = units
                .Where(u => u.EventDate >= cutoff || u.MentionedAt >= cutoff)
                .OrderByDescending(u => u.EventDate ?? u.MentionedAt)
                .Take(ArmTopN)
                .Select(u => u.Id)
                .ToList();
            if (ranked.Count > 0) { arms.Add(("temporal", ranked)); used.Add("temporal"); }
        }

        var fused = RrfFuser.Fuse(arms, topK * 4);
        if (fused.Count == 0)
            return new MemoryRecallResult(bank.Id, [], units.Count, used);

        // Normalize fused score to [0,1] against the best hit for minScore.
        var max = fused.Max(f => f.Fused);
        var byId = units.ToDictionary(u => u.Id);
        var entityLookup = await db.MemoryUnitEntities.AsNoTracking()
            .Where(ue => ue.BankId == bank.Id && fused.Select(f => f.ChunkId).Contains(ue.UnitId))
            .Select(ue => new { ue.UnitId, ue.Entity.Name })
            .ToListAsync(ct);
        var entitiesByUnit = entityLookup.GroupBy(e => e.UnitId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).Distinct().ToList());

        var results = new List<RecalledMemory>();
        var budgetChars = req.Budget is { } b ? Math.Max(b, 0) * 4 : int.MaxValue;
        var spent = 0;
        foreach (var hit in fused)
        {
            var score = max > 0 ? hit.Fused / max : 0;
            if (req.MinScore is { } min && score < min) continue;
            if (!byId.TryGetValue(hit.ChunkId, out var u)) continue;
            if (spent + u.Text.Length > budgetChars) continue;
            spent += u.Text.Length;
            var hitArms = arms.Where(a => a.Ranked.Contains(u.Id))
                .Select(a => a.Arm == "vector" ? "semantic" : a.Arm).ToList();
            results.Add(new RecalledMemory(
                u.Id, u.Text, u.FactType, u.Context, u.EventDate, u.MentionedAt,
                u.Tags, entitiesByUnit.GetValueOrDefault(u.Id, []),
                Math.Round(score, 4), hitArms));
            if (results.Count >= topK) break;
        }
        return new MemoryRecallResult(bank.Id, results, units.Count, used);
    }

    internal static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (Math.Sqrt(na) * Math.Sqrt(nb)) : 0;
    }

    internal static HashSet<string> Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '"', '\'').ToLowerInvariant())
            .Where(t => t.Length > 2)
            .ToHashSet();
}
