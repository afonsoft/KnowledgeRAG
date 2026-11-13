using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Memory.MentalModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261112-memory-context-timeline-stats (gentle-ai/engram parity):
/// deterministic, zero-LLM session-start digest (<see cref="ContextAsync"/>),
/// chronological bucket view (<see cref="TimelineAsync"/>) and per-bank health
/// metrics (<see cref="StatsAsync"/>). The "automatic on session start" is an
/// agent-side convention documented in skills/knowledge-hub — the tool itself
/// is a cheap deterministic query.
/// </summary>
public sealed class MemoryContextService(
    KnowledgeHubDbContext db,
    MemoryBankService banks,
    MentalModelService mentalModels,
    IServiceProvider sp)
{
    public sealed record DirectiveDigest(string? Name, int Priority, IReadOnlyList<string> Tags);
    public sealed record FactDigest(Guid Id, string FactType, string Text,
        DateTime? OccurredStart, DateTime MentionedAt);
    public sealed record ObservationDigest(Guid Id, string Text, int ProofCount);
    public sealed record StaleModelDigest(Guid Id, string Subtype, string Name,
        string Path, DateTime? LastRefreshedAt);
    public sealed record FailureDigest(string Kind, string? Error, DateTime CreatedAt);

    public sealed record MemoryContextResult(
        MemoryBankDto Bank,
        IReadOnlyList<DirectiveDigest> Directives,
        IReadOnlyList<FactDigest> RecentFacts,
        IReadOnlyList<ObservationDigest> TopObservations,
        IReadOnlyList<StaleModelDigest> StaleModels,
        IReadOnlyList<FailureDigest> RecentFailures,
        MemoryStatsResult Stats,
        bool Synthesized,
        string? SynthesizedText,
        /// <summary>SPEC-20261114: open sessions (kind=session,status=running).</summary>
        IReadOnlyList<MemorySessionService.SessionDto>? OpenSessions = null,
        /// <summary>SPEC-20261114: most recent completed session + summary.</summary>
        MemorySessionService.SessionDto? LastCompletedSession = null);

    public sealed record TimelineItem(Guid Id, string FactType, string TextPreview,
        IReadOnlyList<string> Entities, DateTime OccurredAt,
        string? SourcePrompt = null, string? SessionId = null, string? Origin = null);
    public sealed record TimelineBucket(string Bucket, int Count, IReadOnlyList<TimelineItem> Items);
    public sealed record MemoryTimelineResult(Guid BankId, string GroupBy,
        IReadOnlyList<TimelineBucket> Buckets);

    public sealed record MentalModelStats(int Total, int Stale);
    public sealed record ObservationStats(int Total, double AvgProofCount, int MaxProofCount);
    public sealed record MemoryStatsResult(
        Guid BankId, string BankKey, int TotalUnits,
        IReadOnlyDictionary<string, int> UnitsByFactType,
        int UnitsWithEmbedding,
        IReadOnlyDictionary<string, int> LinksByKind,
        int Documents, int Directives,
        MentalModelStats MentalModels, int Pages,
        ObservationStats Observations,
        IReadOnlyDictionary<string, int> OperationsByStatus,
        DateTime? LastConsolidatedAt,
        DateTime? OldestUnconsolidatedFact,
        int DedupedRetains);

    public sealed record GlobalStatsResult(
        IReadOnlyList<MemoryStatsResult> Banks, int TotalBanks, int TotalUnits);

    private const int MaxFactPreview = 240;

    /// <summary>RF-001: session-start digest — directives, recent facts, top
    /// observations, stale models, recent failures, mini-stats. No LLM unless
    /// <paramref name="synthesize"/> is on AND a chat provider is configured.</summary>
    public async Task<MemoryContextResult> ContextAsync(
        string? bankKey, int topK, DateTime? since, bool synthesize, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        topK = Math.Clamp(topK, 1, 50);

        var directives = await db.MemoryDirectives.AsNoTracking()
            .Where(d => d.BankId == bank.Id && d.IsActive)
            .OrderByDescending(d => d.Priority).ThenBy(d => d.Name)
            .Take(topK)
            .Select(d => new DirectiveDigest(d.Name, d.Priority, d.Tags))
            .ToListAsync(ct);

        var factsQ = db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted && u.FactType != "observation");
        if (since is { } s)
            factsQ = factsQ.Where(u => u.MentionedAt >= s || u.OccurredStart >= s);
        var recentFacts = await factsQ
            .OrderByDescending(u => u.OccurredStart ?? u.MentionedAt)
            .Take(topK)
            .Select(u => new FactDigest(u.Id, u.FactType, u.Text, u.OccurredStart, u.MentionedAt))
            .ToListAsync(ct);

        var topObs = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted && u.FactType == "observation")
            .OrderByDescending(u => u.ProofCount).ThenByDescending(u => u.MentionedAt)
            .Take(topK)
            .Select(u => new ObservationDigest(u.Id, u.Text, u.ProofCount))
            .ToListAsync(ct);

        var models = await db.MemoryMentalModels.AsNoTracking()
            .Where(m => m.BankId == bank.Id)
            .ToListAsync(ct);
        var staleIds = await mentalModels.StaleIdsAsync(bank, models, ct);
        var stale = models.Where(m => staleIds.Contains(m.Id))
            .Select(m => new StaleModelDigest(m.Id, m.Subtype, m.Name, m.Path, m.LastRefreshedAt))
            .ToList();

        var failures = await db.MemoryOperations.AsNoTracking()
            .Where(o => o.BankId == bank.Id && o.Status == "failed")
            .OrderByDescending(o => o.CreatedAt)
            .Take(topK)
            .Select(o => new FailureDigest(o.Kind, o.Error, o.CreatedAt))
            .ToListAsync(ct);

        var stats = await StatsForBankAsync(bank, staleIds.Count, ct);
        var dto = new MemoryBankDto(bank.Id, bank.BankKey, bank.Name, bank.Mission,
            bank.DispositionJson, bank.ConfigJson, bank.CreatedAt, stats.TotalUnits);

        // SPEC-20261114: open sessions + where the last one stopped.
        var sessionOps = await db.MemoryOperations.AsNoTracking()
            .Where(o => o.BankId == bank.Id && o.Kind == MemorySessionService.SessionKind)
            .OrderByDescending(o => o.StartedAt)
            .Take(topK)
            .ToListAsync(ct);
        var sessions = sessionOps.Select(MemorySessionService.ToDto).ToList();

        var (synth, synthText) = await TrySynthesizeAsync(
            bank, directives, recentFacts, topObs, stale, failures, stats, synthesize, ct);
        return new MemoryContextResult(dto, directives, recentFacts, topObs,
            stale, failures, stats, synth, synthText,
            sessions.Where(s => s.Status == "running").ToList(),
            sessions.FirstOrDefault(s => s.Status == "done"));
    }

    /// <summary>RF-002: chronological buckets ordered by OccurredStart
    /// (when the fact happened) falling back to MentionedAt (when retained).</summary>
    public async Task<MemoryTimelineResult> TimelineAsync(
        string? bankKey, DateTime? from, DateTime? to,
        IReadOnlyList<string>? factTypes, string groupBy, int limit, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        if (groupBy is not ("day" or "hour" or "week"))
            throw new MemoryBadRequestException("groupBy must be day|hour|week");
        if (from is { } f && to is { } t && f > t)
            throw new MemoryBadRequestException("'from' must be <= 'to'");

        var q = db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted);
        if (factTypes is { Count: > 0 })
            q = q.Where(u => factTypes.Contains(u.FactType));
        if (from is { } f2)
            q = q.Where(u => (u.OccurredStart ?? u.MentionedAt) >= f2);
        if (to is { } t2)
            q = q.Where(u => (u.OccurredStart ?? u.MentionedAt) <= t2);

        var rows = await q
            .OrderByDescending(u => u.OccurredStart ?? u.MentionedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(u => new
            {
                u.Id,
                u.FactType,
                u.Text,
                At = u.OccurredStart ?? u.MentionedAt,
                u.Document!.SourcePrompt,
                u.Document.SourceSessionId,
                u.Document.Origin
            })
            .ToListAsync(ct);

        var unitIds = rows.Select(r => r.Id).ToList();
        var entities = await db.MemoryUnitEntities.AsNoTracking()
            .Where(e => unitIds.Contains(e.UnitId))
            .Select(e => new { e.UnitId, e.Entity.Name })
            .ToListAsync(ct);
        var byUnit = entities.GroupBy(e => e.UnitId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).Take(8).ToList());

        var buckets = rows
            .GroupBy(r => BucketKey(r.At, groupBy))
            .OrderByDescending(g => g.Key)
            .Select(g => new TimelineBucket(g.Key, g.Count(),
                g.Select(r => new TimelineItem(r.Id, r.FactType,
                        r.Text.Length > MaxFactPreview ? r.Text[..MaxFactPreview] : r.Text,
                        byUnit.GetValueOrDefault(r.Id, []), r.At,
                        r.SourcePrompt is { Length: > 0 } sp
                            ? sp[..Math.Min(500, sp.Length)] : null,
                        r.SourceSessionId, r.Origin))
                    .ToList()))
            .ToList();
        return new MemoryTimelineResult(bank.Id, groupBy, buckets);
    }

    /// <summary>RF-003: per-bank health metrics; <paramref name="bankKey"/>
    /// null aggregates every bank in the caller's scope.</summary>
    public async Task<object> StatsAsync(string? bankKey, CancellationToken ct)
    {
        if (bankKey is not null)
        {
            var bank = await banks.ResolveAsync(bankKey, ct, create: false);
            return await StatsForBankAsync(bank, null, ct);
        }
        var scope = await banks.ScopeKeyAsync(ct);
        var all = await db.MemoryBanks.AsNoTracking()
            .Where(b => b.ApiKeyId == scope)
            .OrderBy(b => b.BankKey)
            .ToListAsync(ct);
        var results = new List<MemoryStatsResult>(all.Count);
        foreach (var b in all)
            results.Add(await StatsForBankAsync(b, null, ct));
        return new GlobalStatsResult(results, all.Count, results.Sum(r => r.TotalUnits));
    }

    internal async Task<MemoryStatsResult> StatsForBankAsync(
        MemoryBank bank, int? staleCount, CancellationToken ct)
    {
        var unitsByType = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted)
            .GroupBy(u => u.FactType)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var embedded = await db.MemoryUnits.AsNoTracking()
            .CountAsync(u => u.BankId == bank.Id && !u.IsDeleted && u.Embedding != null, ct);

        var unitIds = db.MemoryUnits.Where(u => u.BankId == bank.Id && !u.IsDeleted)
            .Select(u => u.Id);
        var linksByKind = await db.MemoryLinks.AsNoTracking()
            .Where(l => unitIds.Contains(l.FromUnitId) || unitIds.Contains(l.ToUnitId))
            .GroupBy(l => l.LinkType)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var documents = await db.MemoryDocuments.AsNoTracking()
            .CountAsync(d => d.BankId == bank.Id, ct);
        var directives = await db.MemoryDirectives.AsNoTracking()
            .CountAsync(d => d.BankId == bank.Id && d.IsActive, ct);

        var modelRows = await db.MemoryMentalModels.AsNoTracking()
            .Where(m => m.BankId == bank.Id)
            .ToListAsync(ct);
        var stale = staleCount ?? (await mentalModels.StaleIdsAsync(bank, modelRows, ct)).Count;
        var pages = modelRows.Count(m => m.Subtype == "page");

        var obsAgg = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted && u.FactType == "observation")
            .GroupBy(_ => 1)
            .Select(g => new { N = g.Count(), Avg = g.Average(u => (double)u.ProofCount), Max = g.Max(u => u.ProofCount) })
            .FirstOrDefaultAsync(ct);

        var opsByStatus = await db.MemoryOperations.AsNoTracking()
            .Where(o => o.BankId == bank.Id)
            .GroupBy(o => o.Status)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var lastConsolidated = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && u.ConsolidatedAt != null)
            .MaxAsync(u => (DateTime?)u.ConsolidatedAt, ct);
        var oldestUnconsolidated = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted && u.FactType != "observation"
                && u.ConsolidatedAt == null)
            .MinAsync(u => (DateTime?)u.MentionedAt, ct);

        // Delta-retain hits: retain ops that preserved ≥1 unchanged chunk.
        var deduped = await db.MemoryOperations.AsNoTracking()
            .CountAsync(o => o.BankId == bank.Id && o.ResultJson != null
                && o.ResultJson.Contains("\"unitsPreserved\":")
                && !o.ResultJson.Contains("\"unitsPreserved\":0"), ct);

        return new MemoryStatsResult(
            bank.Id, bank.BankKey, unitsByType.Values.Sum(),
            unitsByType, embedded, linksByKind,
            documents, directives,
            new MentalModelStats(modelRows.Count, stale), pages,
            new ObservationStats(obsAgg?.N ?? 0, obsAgg?.Avg ?? 0, obsAgg?.Max ?? 0),
            opsByStatus, lastConsolidated, oldestUnconsolidated, deduped);
    }

    /// <summary>Markdown digest for content[0].text — readable without parsing JSON.</summary>
    internal static string BuildDigest(MemoryContextResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Memory context — {r.Bank.Name} ({r.Bank.BankKey})");
        sb.AppendLine($"{r.Stats.TotalUnits} unit(s) · {r.Directives.Count} directive(s) · {r.StaleModels.Count} stale model(s)");
        if (r.SynthesizedText is { } synth)
        {
            sb.AppendLine();
            sb.AppendLine(synth);
            return sb.ToString();
        }
        if (r.Directives.Count > 0)
        {
            sb.AppendLine("\n## Directives");
            foreach (var d in r.Directives)
                sb.AppendLine($"- [p{d.Priority}] {d.Name ?? "(unnamed)"}");
        }
        var open = r.OpenSessions ?? [];
        if (open.Count > 0 || r.LastCompletedSession is not null)
        {
            sb.AppendLine("\n## Sessions");
            foreach (var s in open)
                sb.AppendLine($"- OPEN {s.SessionId} since {s.StartedAt:u}{(s.Label is null ? "" : $" — {s.Label}")}");
            if (r.LastCompletedSession is { } last)
                sb.AppendLine($"- last ended {last.FinishedAt:u}{(last.Summary is null ? "" : $" — {last.Summary}")}");
        }
        if (r.RecentFacts.Count > 0)
        {
            sb.AppendLine("\n## Recent facts");
            foreach (var f in r.RecentFacts)
                sb.AppendLine($"- ({f.FactType}) {f.Text}");
        }
        if (r.TopObservations.Count > 0)
        {
            sb.AppendLine("\n## Top observations");
            foreach (var o in r.TopObservations)
                sb.AppendLine($"- (×{o.ProofCount}) {o.Text}");
        }
        if (r.StaleModels.Count > 0)
        {
            sb.AppendLine("\n## Stale models/pages — consider refresh");
            foreach (var m in r.StaleModels)
                sb.AppendLine($"- {m.Name} ({m.Subtype}, {m.Path})");
        }
        if (r.RecentFailures.Count > 0)
        {
            sb.AppendLine("\n## Recent operation failures");
            foreach (var f in r.RecentFailures)
                sb.AppendLine($"- {f.Kind} @ {f.CreatedAt:u}: {f.Error}");
        }
        return sb.ToString();
    }

    private static string BucketKey(DateTime at, string groupBy) => groupBy switch
    {
        "hour" => at.ToString("yyyy-MM-dd HH:00"),
        "week" => $"{ISOWeek(at)}",
        _ => at.ToString("yyyy-MM-dd"),
    };

    private static string ISOWeek(DateTime d)
    {
        var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
        var week = cal.GetWeekOfYear(d, System.Globalization.CalendarWeekRule.FirstFourDayWeek,
            DayOfWeek.Monday);
        return $"{d.Year}-W{week:D2}";
    }

    private async Task<(bool, string?)> TrySynthesizeAsync(
        MemoryBank bank,
        IReadOnlyList<DirectiveDigest> directives,
        IReadOnlyList<FactDigest> facts,
        IReadOnlyList<ObservationDigest> obs,
        IReadOnlyList<StaleModelDigest> stale,
        IReadOnlyList<FailureDigest> failures,
        MemoryStatsResult stats, bool synthesize, CancellationToken ct)
    {
        var chat = synthesize ? sp.GetService<IChatClient>() : null;
        if (chat is null)
            return (false, null);
        var prompt = new StringBuilder()
            .AppendLine("Summarize this memory bank digest in <=6 short bullet points for an agent starting a session. Plain prose, no headers.")
            .AppendLine($"Bank: {bank.Name} — {stats.TotalUnits} units, {stats.Observations.Total} observations, {stale.Count} stale models.")
            .AppendLine("Directives: " + string.Join("; ", directives.Select(d => d.Name)))
            .AppendLine("Recent: " + string.Join("; ", facts.Select(f => f.Text)))
            .AppendLine("Observations: " + string.Join("; ", obs.Select(o => o.Text)));
        var resp = await chat.GetResponseAsync(prompt.ToString(), cancellationToken: ct);
        return (true, resp.Text);
    }
}
