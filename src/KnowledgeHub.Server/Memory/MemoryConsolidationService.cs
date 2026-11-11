using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-memory-observations-consolidation RF-001: one run per bank —
/// the singleton lock is shared by manual calls, queued ops and the worker.
/// </summary>
public sealed class MemoryConsolidationLocks
{
    private readonly ConcurrentDictionary<Guid, byte> _running = new();
    public bool TryAcquire(Guid bankId) => _running.TryAdd(bankId, 0);
    public void Release(Guid bankId) => _running.TryRemove(bankId, out _);
    public bool IsRunning(Guid bankId) => _running.ContainsKey(bankId);
}

/// <summary>
/// Post-consolidation event (SPEC-20261111-memory-observations-consolidation
/// "Fora de escopo" — mental-models consumes it in its own SPEC).
/// </summary>
public interface IMemoryConsolidationHook
{
    Task OnConsolidatedAsync(MemoryBank bank, MemoryConsolidateResult result, CancellationToken ct);
}

public sealed class NoOpMemoryConsolidationHook : IMemoryConsolidationHook
{
    public Task OnConsolidatedAsync(MemoryBank bank, MemoryConsolidateResult result, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// SPEC-20261111-memory-observations-consolidation RF-002/RF-003/RF-004/RF-005:
/// consolidates unconsolidated <c>world|experience</c> units into
/// <c>observation</c> units. Per batch: optional decision-model triage → dedup
/// adjudication for near-duplicates → one LLM call deciding
/// create|update|delete|skip → actions applied in a single transaction, each
/// create/update append-only recorded in <see cref="ObservationHistory"/>.
/// Facts are stamped <see cref="MemoryUnit.ConsolidatedAt"/>; whole-batch LLM
/// parse failures stamp <see cref="MemoryUnit.ConsolidationFailedAt"/> for
/// backoff-bounded retry (RF-001).
/// </summary>
public sealed class MemoryConsolidationService(
    KnowledgeHubDbContext db,
    MemoryConfigResolver config,
    MemoryConsolidationLocks locks,
    IServiceProvider sp,
    IEmbeddingProvider embeddings,
    IEnumerable<IMemoryConsolidationHook> hooks,
    ILogger<MemoryConsolidationService> logger)
{
    /// <summary>Acquires the per-bank lock then runs until no pending batch remains.</summary>
    public async Task<MemoryConsolidateResult> RunAsync(MemoryBank bank, CancellationToken ct)
    {
        if (!locks.TryAcquire(bank.Id))
            throw new MemoryConflictException(
                $"a consolidation run is already in progress for bank '{bank.BankKey}'");
        try
        {
            var result = await RunLockedAsync(bank, ct);
            foreach (var hook in hooks)
                await hook.OnConsolidatedAsync(bank, result, ct);
            return result;
        }
        finally
        {
            locks.Release(bank.Id);
        }
    }

    private async Task<MemoryConsolidateResult> RunLockedAsync(MemoryBank bank, CancellationToken ct)
    {
        var chat = sp.GetService<IChatClient>()
            ?? throw new MemoryServiceUnavailableException(
                "memory consolidation requires a configured chat provider (Chat:*)");
        var batchSize = Math.Clamp(config.ConsolidationBatchSize(bank), 1, 200);
        var backoff = config.ConsolidationRetryBackoff(bank);
        var runStart = DateTime.UtcNow;
        var stats = new Counters();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Facts failed during THIS run are never re-picked: the cutoff is
            // the run start (bounded further back by the retry backoff).
            var retryAfter = runStart - backoff;
            if (retryAfter > runStart) retryAfter = runStart;
            var batch = await db.MemoryUnits
                .Where(u => u.BankId == bank.Id && !u.IsDeleted
                    && (u.FactType == "world" || u.FactType == "experience")
                    && u.ConsolidatedAt == null
                    && (u.ConsolidationFailedAt == null || u.ConsolidationFailedAt < retryAfter))
                .OrderBy(u => u.MentionedAt)
                .Take(batchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;
            await ProcessBatchAsync(bank, chat, batch, stats, ct);
        }

        logger.LogInformation(
            "consolidate: bank {Bank} → {Processed} processed ({Created}c/{Updated}u/{Deleted}d/{Skipped}s/{Failed}f)",
            bank.BankKey, stats.Processed, stats.Created, stats.Updated, stats.Deleted,
            stats.Skipped, stats.Failed);
        return new MemoryConsolidateResult(bank.Id, bank.BankKey, stats.Processed,
            stats.Created, stats.Updated, stats.Deleted, stats.Skipped, stats.Failed);
    }

    /// <summary>RF-003: audit trail of one observation (version order).</summary>
    public async Task<IReadOnlyList<ObservationHistoryDto>> HistoryAsync(
        string? bankKey, Guid observationId, MemoryBankService banks, CancellationToken ct)
    {
        var bank = await banks.ResolveAsync(bankKey, ct, create: false);
        var exists = await db.MemoryUnits.AsNoTracking()
            .AnyAsync(u => u.BankId == bank.Id && u.Id == observationId
                && u.FactType == "observation", ct);
        if (!exists)
            throw new MemoryNotFoundException($"observation '{observationId}' not found");
        var rows = await db.ObservationHistories.AsNoTracking()
            .Where(h => h.ObservationUnitId == observationId)
            .OrderBy(h => h.Version)
            .Select(h => new
            {
                h.Id,
                h.ObservationUnitId,
                h.Version,
                h.Text,
                h.AddedSourceIdsJson,
                h.ProofCount,
                h.Action,
                h.CreatedAt
            })
            .ToListAsync(ct);
        return rows.Select(h => new ObservationHistoryDto(h.Id, h.ObservationUnitId,
            h.Version, h.Text, ParseSources(h.AddedSourceIdsJson), h.ProofCount,
            h.Action, h.CreatedAt)).ToList();
    }

    // ---------------- batch pipeline ----------------

    /// <summary>Max history version per observation — loaded once per batch.</summary>
    private Dictionary<Guid, int> _versions = [];

    private async Task ProcessBatchAsync(MemoryBank bank, IChatClient chat,
        List<MemoryUnit> facts, Counters stats, CancellationToken ct)
    {
        var candidates = await LoadCandidatesAsync(bank, facts, ct);
        _versions = await db.ObservationHistories
            .Where(h => h.BankId == bank.Id)
            .GroupBy(h => h.ObservationUnitId)
            .Select(g => new { g.Key, Max = g.Max(h => h.Version) })
            .ToDictionaryAsync(x => x.Key, x => x.Max, ct);
        var remaining = new List<MemoryUnit>(facts);

        // RF-005 — optional decision-model triage: restate folds directly,
        // chatter is stamped without an action. Failure → all go to the LLM.
        if (config.ConsolidationTriage(bank) == "decision-model")
        {
            var verdicts = await TriageAsync(chat, facts, candidates, ct);
            if (verdicts is not null)
            {
                remaining = [];
                foreach (var fact in facts)
                {
                    var v = verdicts.GetValueOrDefault(fact.Id);
                    if (v is ("restate" or "changes", { } obsId)
                        && candidates.FirstOrDefault(c => c.Id == obsId) is { } obs)
                    {
                        FoldEvidence(obs, [fact.Id]);
                        Stamp(fact);
                        stats.Updated++;
                        stats.Processed++;
                    }
                    else if (v.Verdict == "chatter")
                    {
                        Stamp(fact);
                        stats.Skipped++;
                        stats.Processed++;
                    }
                    else
                    {
                        remaining.Add(fact);
                    }
                }
            }
        }

        // RF-004 — dedup adjudication: near-duplicate (fact, observation)
        // pairs decided in ONE LLM call ('same' folds, 'distinct' continues).
        var threshold = config.ConsolidationDedupThreshold(bank);
        var pairs = remaining
            .SelectMany(f => candidates
                .Where(c => Similarity(f, c) >= threshold)
                .Select(c => (Fact: f, Obs: c)))
            .ToList();
        if (pairs.Count > 0)
        {
            var verdicts = await AdjudicateAsync(chat, pairs, ct);
            if (verdicts is not null)
            {
                foreach (var (fact, obs) in pairs)
                {
                    if (verdicts.GetValueOrDefault(fact.Id) == "same" && remaining.Remove(fact))
                    {
                        FoldEvidence(obs, [fact.Id]);
                        Stamp(fact);
                        stats.Updated++;
                        stats.Processed++;
                    }
                }
            }
        }

        List<MemoryUnit> toEmbed = [];
        if (remaining.Count > 0)
        {
            var actions = await DecideAsync(chat, remaining, candidates, ct);
            if (actions is null)
            {
                // Whole-batch parse failure → retryable failure stamp, not partial.
                foreach (var f in remaining)
                {
                    f.ConsolidationFailedAt = DateTime.UtcNow;
                    stats.Failed++;
                    stats.Processed++;
                }
            }
            else
            {
                toEmbed = ApplyActions(bank, actions, remaining, candidates, stats);
            }
        }

        // Embeddings are best-effort — the semantic arm degrades, never blocks.
        foreach (var obs in toEmbed)
            await TryEmbedAsync(obs, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<MemoryUnit>> LoadCandidatesAsync(
        MemoryBank bank, List<MemoryUnit> facts, CancellationToken ct)
    {
        var pool = await db.MemoryUnits
            .Where(u => u.BankId == bank.Id && !u.IsDeleted && u.FactType == "observation")
            .OrderByDescending(u => u.ProofCount)
            .ThenByDescending(u => u.MentionedAt)
            .Take(200)
            .ToListAsync(ct);
        var topK = Math.Clamp(config.ConsolidationCandidateTopK(bank), 0, 100);
        if (topK == 0 || pool.Count <= topK)
            return pool;
        return pool
            .Select(o => (o, Score: facts.Max(f => Similarity(f, o))))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.o)
            .ToList();
    }

    /// <summary>Applies the LLM's decisions; returns observations needing (re-)embed.</summary>
    private List<MemoryUnit> ApplyActions(MemoryBank bank, List<ConsolidationAction> actions,
        List<MemoryUnit> remaining, List<MemoryUnit> candidates, Counters stats)
    {
        var byAlias = remaining.Select((f, i) => (f, i))
            .ToDictionary(x => Alias("f", x.i), x => x.f, StringComparer.OrdinalIgnoreCase);
        var obsByAlias = candidates.Select((o, i) => (o, i))
            .ToDictionary(x => Alias("o", x.i), x => x.o, StringComparer.OrdinalIgnoreCase);
        var toEmbed = new List<MemoryUnit>();
        var touched = new HashSet<Guid>();

        foreach (var action in actions)
        {
            var sources = action.FactIds
                .Select(id => byAlias.GetValueOrDefault(Norm(id)))
                .Where(f => f is not null).Cast<MemoryUnit>().ToList();
            foreach (var f in sources)
                touched.Add(f.Id);

            switch (action.Action)
            {
                case "create" when action.Text is { Length: > 0 } text
                    && HasEvidence(action, sources):
                    if (CreateObservation(bank, text, sources) is { } created)
                        toEmbed.Add(created);
                    else
                        stats.Updated++; // folded into an identical observation
                    stats.Created++;
                    break;
                case "update" when action.Text is { Length: > 0 } text
                    && obsByAlias.GetValueOrDefault(Norm(action.ObservationId)) is { } target
                    && sources.Count > 0:
                    UpdateObservation(target, text, sources);
                    toEmbed.Add(target);
                    stats.Updated++;
                    break;
                case "delete" when obsByAlias.GetValueOrDefault(Norm(action.ObservationId)) is { } del:
                    DeleteObservation(del, sources);
                    stats.Deleted++;
                    break;
                default:
                    if (sources.Count > 0) stats.Skipped++;
                    break;
            }
            foreach (var f in sources)
                Stamp(f);
        }

        // Facts the LLM did not reference are skipped — never left pending.
        foreach (var f in remaining.Where(f => !touched.Contains(f.Id)))
        {
            Stamp(f);
            stats.Skipped++;
        }
        stats.Processed += remaining.Count;
        return toEmbed;
    }

    /// <summary>Returns the new observation, or null when it folded into a dupe.</summary>
    private MemoryUnit? CreateObservation(MemoryBank bank, string text, List<MemoryUnit> sources)
    {
        var sourceIds = sources.Select(f => f.Id).ToList();
        var hash = MemoryHasher.Hash(text);
        var dupe = db.MemoryUnits.Local.FirstOrDefault(u =>
            u.BankId == bank.Id && !u.IsDeleted && u.FactType == "observation"
            && u.ContentHash == hash);
        if (dupe is not null)
        {
            FoldEvidence(dupe, sourceIds);
            return null;
        }
        var obs = new MemoryUnit
        {
            BankId = bank.Id,
            Text = text,
            FactType = "observation",
            Context = sources.FirstOrDefault()?.Context,
            MentionedAt = DateTime.UtcNow,
            ContentHash = hash,
            SourceMemoryIdsJson = JsonSerializer.Serialize(sourceIds),
            ProofCount = sourceIds.Count,
            Tags = sources.SelectMany(s => s.Tags).Distinct().Take(20).ToList()
        };
        db.MemoryUnits.Add(obs);
        AddHistory(obs, text, sourceIds, "create");
        return obs;
    }

    private void UpdateObservation(MemoryUnit obs, string newText, List<MemoryUnit> sources)
    {
        var added = sources.Select(f => f.Id).ToList();
        obs.Text = newText;
        obs.ContentHash = MemoryHasher.Hash(newText);
        var merged = ParseSources(obs.SourceMemoryIdsJson).Union(added).ToList();
        obs.SourceMemoryIdsJson = JsonSerializer.Serialize(merged);
        obs.ProofCount = merged.Count;
        AddHistory(obs, newText, added, "update");
    }

    /// <summary>RF-004 fold: fact becomes extra evidence of an existing observation.</summary>
    private void FoldEvidence(MemoryUnit obs, IReadOnlyList<Guid> newSourceIds)
    {
        var merged = ParseSources(obs.SourceMemoryIdsJson).Union(newSourceIds).ToList();
        obs.SourceMemoryIdsJson = JsonSerializer.Serialize(merged);
        obs.ProofCount = merged.Count;
        AddHistory(obs, obs.Text, newSourceIds, "update");
    }

    private void DeleteObservation(MemoryUnit obs, List<MemoryUnit> sources)
    {
        obs.IsDeleted = true;
        AddHistory(obs, obs.Text, sources.Select(f => f.Id).ToList(), "delete");
    }

    private void AddHistory(MemoryUnit obs, string text, IReadOnlyList<Guid> addedIds, string action)
    {
        var version = _versions.GetValueOrDefault(obs.Id, 0) + 1;
        _versions[obs.Id] = version;
        db.ObservationHistories.Add(new ObservationHistory
        {
            ObservationUnitId = obs.Id,
            BankId = obs.BankId,
            Version = version,
            Text = text,
            AddedSourceIdsJson = JsonSerializer.Serialize(addedIds),
            ProofCount = obs.ProofCount,
            Action = action
        });
    }

    private static void Stamp(MemoryUnit fact) => fact.ConsolidatedAt = DateTime.UtcNow;

    private async Task TryEmbedAsync(MemoryUnit obs, CancellationToken ct)
    {
        try
        {
            var vec = await embeddings.EmbedDocumentAsync(obs.Text, ct);
            obs.Embedding = MemoryRetainService.ToBytes(vec);
            obs.EmbeddingModel = embeddings.ModelId;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "observation embedding failed — semantic arm degrades");
        }
    }

    // ---------------- LLM calls ----------------

    /// <summary>RF-002: one consolidation decision call per batch.</summary>
    private async Task<List<ConsolidationAction>?> DecideAsync(
        IChatClient chat, List<MemoryUnit> facts, List<MemoryUnit> candidates, CancellationToken ct)
    {
        var prompt = $$"""
            You are a memory consolidation engine. Merge NEW FACTS into OBSERVATIONS.

            Rules:
            - An observation is a durable conclusion backed by verbatim evidence from the facts.
            - "create": new observation — MUST include "evidence" with at least one exact quote copied verbatim from a source fact text.
            - "update": refine an existing observation's "text" to absorb the new facts — never drop prior evidence, keep it self-contained.
            - "delete": the observation is contradicted or wrong — remove it.
            - "skip": the fact adds no durable knowledge (redundant, small talk, already covered).
            - Keep the SOURCE LANGUAGE. Observation text must be timeless (no "recently"/"yesterday").

            NEW FACTS:
            {{string.Join('\n', facts.Select((f, i) => $"{Alias("f", i)}: {f.Text}"))}}

            CANDIDATE OBSERVATIONS:
            {{(candidates.Count == 0 ? "(none)" : string.Join('\n', candidates.Select((o, i) => $"{Alias("o", i)} (proof {o.ProofCount}): {o.Text}")))}}

            Answer ONLY with a JSON array (every f* id in exactly one action):
            [{"action":"create","fact_ids":["f1"],"text":"...","evidence":["verbatim quote"]},
             {"action":"update","observation_id":"o1","fact_ids":["f2"],"text":"refined text"},
             {"action":"delete","observation_id":"o2"},
             {"action":"skip","fact_ids":["f3"]}]
            """;
        try
        {
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            return ParseActions(response.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "consolidation decide call failed");
            return null;
        }
    }

    /// <summary>RF-005: cheap pre-pass classifying each fact vs candidates.</summary>
    private async Task<Dictionary<Guid, (string Verdict, Guid? ObsId)>?> TriageAsync(
        IChatClient chat, List<MemoryUnit> facts, List<MemoryUnit> candidates, CancellationToken ct)
    {
        var prompt = $$"""
            Classify each NEW FACT against the OBSERVATIONS:
            - "restate": restates an existing observation — include its o* id (folds as extra evidence, no rewrite).
            - "changes": updates/contradicts an existing observation — include its o* id.
            - "new": durable information not covered by any observation.
            - "chatter": not a durable fact (small talk, filler, pleasantries).

            NEW FACTS:
            {{string.Join('\n', facts.Select((f, i) => $"{Alias("f", i)}: {f.Text}"))}}

            OBSERVATIONS:
            {{(candidates.Count == 0 ? "(none)" : string.Join('\n', candidates.Select((o, i) => $"{Alias("o", i)}: {o.Text}")))}}

            JSON array: [{"fact_id":"f1","verdict":"restate|changes|new|chatter","observation_id":"o1"}]
            """;
        try
        {
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            return ParseTriage(response.Text, facts, candidates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "consolidation triage failed — falling back to full LLM");
            return null;
        }
    }

    /// <summary>RF-004: single adjudication call for near-duplicate pairs.</summary>
    private async Task<Dictionary<Guid, string>?> AdjudicateAsync(
        IChatClient chat, List<(MemoryUnit Fact, MemoryUnit Obs)> pairs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pairs.Count; i++)
            sb.AppendLine($"{Alias("p", i)} — fact: \"{pairs[i].Fact.Text}\"  ~  observation: \"{pairs[i].Obs.Text}\"");
        var prompt = $$"""
            For each pair decide whether the new fact is the SAME information already
            carried by the observation ("same" → fold it as extra evidence) or genuinely
            DISTINCT ("distinct" → it deserves its own consolidation decision).

            PAIRS:
            {{sb}}

            JSON array: [{"pair":"p1","decision":"same|distinct"}]
            """;
        try
        {
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            return ParseAdjudication(response.Text, pairs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "consolidation adjudication failed — pairs go to full LLM");
            return null;
        }
    }

    // ---------------- parsing (tolerant, deterministic) ----------------

    internal sealed record ConsolidationAction(
        string Action, List<string> FactIds, string? ObservationId,
        string? Text, List<string> Evidence);

    internal static List<ConsolidationAction>? ParseActions(string? raw)
    {
        var arr = JsonArray(raw);
        if (arr is null) return null;
        var result = new List<ConsolidationAction>();
        foreach (var el in arr.Value)
        {
            var action = el.TryGetProperty("action", out var a) ? a.GetString()?.ToLowerInvariant() : null;
            if (action is not ("create" or "update" or "delete" or "skip"))
                continue;
            result.Add(new ConsolidationAction(
                action,
                Strings(el, "fact_ids"),
                el.TryGetProperty("observation_id", out var o) ? o.GetString() : null,
                el.TryGetProperty("text", out var t) ? t.GetString() : null,
                Strings(el, "evidence")));
        }
        return result;
    }

    private static Dictionary<Guid, (string, Guid?)>? ParseTriage(
        string? raw, List<MemoryUnit> facts, List<MemoryUnit> candidates)
    {
        var arr = JsonArray(raw);
        if (arr is null) return null;
        var obsByAlias = candidates.Select((o, i) => (o, i))
            .ToDictionary(x => Alias("o", x.i), x => x.o.Id, StringComparer.OrdinalIgnoreCase);
        var fByAlias = facts.Select((f, i) => (f, i))
            .ToDictionary(x => Alias("f", x.i), x => x.f.Id, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<Guid, (string, Guid?)>();
        foreach (var el in arr.Value)
        {
            if (!el.TryGetProperty("fact_id", out var id) || !el.TryGetProperty("verdict", out var v))
                continue;
            var fid2 = fByAlias.GetValueOrDefault(Norm(id.GetString()), Guid.Empty);
            if (fid2 == Guid.Empty) continue;
            var verdict = v.GetString()?.ToLowerInvariant() ?? "new";
            Guid? obsId = null;
            if (el.TryGetProperty("observation_id", out var o)
                && obsByAlias.TryGetValue(Norm(o.GetString()), out var oid))
                obsId = oid;
            result[fid2] = (verdict, obsId);
        }
        return result;
    }

    private static Dictionary<Guid, string>? ParseAdjudication(
        string? raw, List<(MemoryUnit Fact, MemoryUnit Obs)> pairs)
    {
        var arr = JsonArray(raw);
        if (arr is null) return null;
        var byAlias = pairs.Select((p, i) => (p, i))
            .ToDictionary(x => Alias("p", x.i), x => x.p.Fact.Id, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<Guid, string>();
        foreach (var el in arr.Value)
        {
            var alias = el.TryGetProperty("pair", out var p) ? p.GetString()
                : el.TryGetProperty("fact_id", out var f2) ? f2.GetString() : null;
            if (alias is null || !byAlias.TryGetValue(Norm(alias), out var fid))
                continue;
            if (el.TryGetProperty("decision", out var d))
                result[fid] = d.GetString()?.ToLowerInvariant() ?? "distinct";
        }
        return result;
    }

    private static JsonElement.ArrayEnumerator? JsonArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.StartsWith("```"))
            text = text[(text.IndexOf('\n') + 1)..].TrimEnd('`', ' ', '\n');
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement.Clone().EnumerateArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> Strings(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString()).Where(s => s is { Length: > 0 })
                .Cast<string>().ToList()
            : [];

    /// <summary>Evidence rule: at least one quote appears verbatim in a source fact.</summary>
    private static bool HasEvidence(ConsolidationAction action, List<MemoryUnit> sources) =>
        action.Evidence.Count > 0 && sources.Count > 0
        && action.Evidence.Any(q => sources.Any(f =>
            f.Text.Contains(q, StringComparison.OrdinalIgnoreCase)));

    private double Similarity(MemoryUnit a, MemoryUnit b)
    {
        if (a.Embedding is not null && b.Embedding is not null
            && a.EmbeddingModel == b.EmbeddingModel)
            return MemoryRecallService.Cosine(
                MemoryRetainService.FromBytes(a.Embedding),
                MemoryRetainService.FromBytes(b.Embedding));
        var ta = MemoryRecallService.Tokenize(a.Text);
        var tb = MemoryRecallService.Tokenize(b.Text);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        return (double)ta.Intersect(tb).Count() / ta.Union(tb).Count();
    }

    private static List<Guid> ParseSources(string? json) =>
        json is { Length: > 0 } j
            ? JsonSerializer.Deserialize<List<Guid>>(j) ?? []
            : [];

    internal static string Alias(string prefix, int i) => $"{prefix}{i + 1}";
    private static string Norm(string? id) => (id ?? "").Trim();

    private sealed class Counters
    {
        public int Processed, Created, Updated, Deleted, Skipped, Failed;
    }
}
