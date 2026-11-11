using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Memory;

/// <summary>Extract mode requires a configured chat provider (503).</summary>
public sealed class MemoryServiceUnavailableException(string message) : Exception(message);

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-002/RF-005: the retain pipeline —
/// defense screen → (verbatim | LLM extract) → dedupe/delta-retain by
/// ContentHash → embeddings → entity + temporal links. One transaction per
/// retain so async cancellation leaves no partial units.
/// </summary>
public sealed class MemoryRetainService(
    KnowledgeHubDbContext db,
    MemoryBankService banks,
    MemoryConfigResolver config,
    MemoryEntityLinker linker,
    IMemoryDefenseScreen defense,
    IServiceProvider sp,
    Embeddings.IEmbeddingProvider embeddings,
    IMemoryConsolidationTrigger consolidation,
    ILogger<MemoryRetainService> logger)
{
    /// <summary>
    /// Split content into delta-comparable chunks — one per blank-line
    /// paragraph so a re-retain reuses unchanged paragraphs exactly (RF-005);
    /// paragraphs longer than ~2k split at the boundary.
    /// </summary>
    internal static List<string> Chunk(string content)
    {
        var chunks = new List<string>();
        foreach (var para in content.Replace("\r\n", "\n").Split("\n\n"))
        {
            var p = para.Trim();
            if (p.Length == 0) continue;
            for (var i = 0; i < p.Length; i += 2000)
                chunks.Add(p.Substring(i, Math.Min(2000, p.Length - i)));
        }
        if (chunks.Count == 0 && content.Trim().Length > 0) chunks.Add(content.Trim());
        return chunks;
    }

    public async Task<MemoryRetainResult> RetainAsync(
        string? bankKey, MemoryRetainRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Content))
            throw new MemoryBadRequestException("content is required");
        var bank = await banks.ResolveAsync(bankKey, ct);
        return await ExecuteAsync(bank, req, ct);
    }

    /// <summary>Executes against an already-resolved bank (worker path).</summary>
    public async Task<MemoryRetainResult> ExecuteAsync(
        MemoryBank bank, MemoryRetainRequest req, CancellationToken ct)
    {
        var maxUnits = config.MaxUnitsPerRetain(bank);
        var maxText = config.MaxUnitTextLength(bank);
        var strategy = req.Strategy is { Length: > 0 } s ? s : config.DefaultStrategy(bank);
        if (strategy is not ("extract" or "verbatim"))
            throw new MemoryBadRequestException($"strategy must be 'extract' or 'verbatim' (got '{strategy}')");

        var mentionedAt = req.Timestamp?.UtcDateTime ?? DateTime.UtcNow;
        var chunks = Chunk(req.Content);

        // RF-005 delta-retain: same (bank, externalId) — per-chunk hash reuse.
        MemoryDocument? doc = null;
        var preserved = new Dictionary<int, List<MemoryUnit>>();
        var docHash = MemoryHasher.Hash(req.Content);
        if (req.DocumentId is { Length: > 0 } externalId)
        {
            doc = await db.MemoryDocuments
                .Include(d => d.Units)
                .FirstOrDefaultAsync(d => d.BankId == bank.Id && d.ExternalId == externalId, ct);
            if (doc?.ContentHash == docHash)
            {
                return new MemoryRetainResult(bank.Id,
                    doc.Units.Where(u => !u.IsDeleted).Select(u => u.Id).ToList(),
                    0, doc.Units.Count(u => !u.IsDeleted));
            }
            if (doc is not null)
            {
                var chunkHashes = chunks.Select(MemoryHasher.Hash).ToHashSet();
                var stale = new List<MemoryUnit>();
                foreach (var u in doc.Units.Where(u => !u.IsDeleted))
                {
                    if (u.ChunkIndex is { } idx && idx < chunks.Count && chunkHashes.Contains(u.ContentHash))
                        (preserved.TryGetValue(idx, out var l) ? l : preserved[idx] = []).Add(u);
                    else
                        stale.Add(u);
                }
                foreach (var u in stale)
                    u.IsDeleted = true;
                doc.ContentHash = docHash;
                doc.UpdatedAt = DateTime.UtcNow;
                if (req.DocumentTitle is not null) doc.Title = req.DocumentTitle;
            }
        }

        var newUnits = new List<MemoryUnit>();
        // Dedupe covers the whole bank (not just this document): a re-retain
        // of an existing fact reports it preserved instead of duplicating.
        var existingByHash = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted)
            .Select(u => new { u.ContentHash, u.Id })
            .ToListAsync(ct);
        var seenHashes = new Dictionary<string, Guid>();
        foreach (var e in existingByHash)
            seenHashes.TryAdd(e.ContentHash, e.Id);
        var dedupedIds = new List<Guid>();

        if (strategy == "verbatim")
        {
            for (var i = 0; i < chunks.Count; i++)
            {
                if (preserved.ContainsKey(i)) continue;
                var text = await defense.ScreenAsync(chunks[i], bank, ct);
                if (text.Length > maxText)
                    throw new MemoryBadRequestException($"content exceeds maxUnitTextLength ({maxText})");
                var hash = MemoryHasher.Hash(text);
                if (seenHashes.TryGetValue(hash, out var dup))
                {
                    if (dup != Guid.Empty) dedupedIds.Add(dup);
                    continue;
                }
                seenHashes[hash] = Guid.Empty;
                if (newUnits.Count + preserved.Values.Sum(v => v.Count) >= maxUnits)
                    throw new MemoryBadRequestException($"retain exceeds maxUnitsPerRetain ({maxUnits})");
                newUnits.Add(await NewUnitAsync(bank, doc, i, text, "experience",
                    req.Context, mentionedAt, hash, req, ct));
            }
        }
        else
        {
            var chat = sp.GetService<IChatClient>()
                ?? throw new MemoryServiceUnavailableException(
                    "extract strategy requires a configured chat provider — use strategy 'verbatim' or configure Chat:*");
            var total = preserved.Values.Sum(v => v.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                if (preserved.ContainsKey(i)) continue;
                var screened = await defense.ScreenAsync(chunks[i], bank, ct);
                var facts = await ExtractAsync(chat, screened, req.Context, mentionedAt, ct);
                foreach (var fact in facts)
                {
                    var text = fact.Text.Length > maxText ? fact.Text[..maxText] : fact.Text;
                    var hash = MemoryHasher.Hash(text);
                    if (seenHashes.TryGetValue(hash, out var dup) && dup != Guid.Empty)
                    { dedupedIds.Add(dup); continue; }
                    seenHashes[hash] = Guid.Empty;
                    if (++total > maxUnits)
                        throw new MemoryBadRequestException($"retain exceeds maxUnitsPerRetain ({maxUnits})");
                    var unit = await NewUnitAsync(bank, doc, i, text, fact.FactType,
                        fact.Context ?? req.Context, mentionedAt, hash, req, ct);
                    unit.EventDate = fact.EventDate;
                    unit.OccurredStart = fact.OccurredStart;
                    unit.OccurredEnd = fact.OccurredEnd;
                    newUnits.Add(unit);
                    await linker.LinkEntitiesAsync(unit, bank.Id, fact.Entities, ct);
                    await linker.LinkTemporalNeighborsAsync(unit, config.TemporalLinkWindowDays(bank), ct);
                }
            }
        }

        // RF-005: create the logical document row on first retain of an id.
        if (doc is null && req.DocumentId is { Length: > 0 } extId)
        {
            doc = new MemoryDocument
            {
                BankId = bank.Id,
                ExternalId = extId,
                Title = req.DocumentTitle,
                ContentHash = docHash
            };
            db.MemoryDocuments.Add(doc);
            foreach (var u in newUnits.Concat(preserved.Values.SelectMany(v => v)))
                u.Document = doc;
        }

        await db.SaveChangesAsync(ct);
        // SPEC-20261111-memory-observations-consolidation RF-001 (a): signal the
        // consolidation worker when new facts landed and a chat provider exists.
        if (newUnits.Count > 0 && sp.GetService<IChatClient>() is not null)
            consolidation.Signal(bank.Id);
        var allIds = preserved.Values.SelectMany(v => v.Select(u => u.Id))
            .Concat(newUnits.Select(u => u.Id))
            .Concat(dedupedIds).Distinct().ToList();
        var preservedCount = allIds.Count - newUnits.Count;
        logger.LogInformation("retain: bank {Bank} strategy {Strategy} → {New} new units, {Kept} preserved",
            bank.BankKey, strategy, newUnits.Count, preservedCount);
        return new MemoryRetainResult(bank.Id, allIds, newUnits.Count, preservedCount);
    }

    private async Task<MemoryUnit> NewUnitAsync(MemoryBank bank, MemoryDocument? doc, int chunkIndex,
        string text, string factType, string? context, DateTime mentionedAt,
        string hash, MemoryRetainRequest req, CancellationToken ct)
    {
        var unit = new MemoryUnit
        {
            BankId = bank.Id,
            DocumentId = doc?.Id,
            ChunkIndex = chunkIndex,
            Text = text,
            FactType = MemoryUnit.FactTypes.Contains(factType) ? factType : "experience",
            Context = context,
            MentionedAt = mentionedAt,
            EventDate = mentionedAt,
            ContentHash = hash,
            Tags = req.Tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(20).ToList() ?? [],
            MetadataJson = req.Metadata is { Count: > 0 } ? JsonSerializer.Serialize(req.Metadata) : null
        };
        db.MemoryUnits.Add(unit);
        try
        {
            var vec = await embeddings.EmbedDocumentAsync(text, ct);
            unit.Embedding = ToBytes(vec);
            unit.EmbeddingModel = embeddings.ModelId;
        }
        catch (Exception ex)
        {
            // Embedding failure must not lose the memory — the semantic arm
            // simply skips units without vectors.
            logger.LogWarning(ex, "memory unit embedding failed (bank {Bank})", bank.BankKey);
        }
        return unit;
    }

    /// <summary>LLM fact extraction — JSON-only contract, tolerant parsing.</summary>
    internal static async Task<List<ExtractedFact>> ExtractAsync(
        IChatClient chat, string content, string? context, DateTime referenceNow, CancellationToken ct)
    {
        var prompt = $$"""
            You are a memory extraction engine. Extract discrete facts from the content below.

            Rules:
            - Preserve the SOURCE LANGUAGE of each fact — do not translate.
            - Classify each fact: "world" = objective fact about the world/domain; "experience" = something the agent/user did, observed or decided (first-person experience).
            - Resolve pronouns and coreferences to concrete names.
            - Resolve relative dates ("yesterday", "last week") to absolute ISO-8601 using REFERENCE_DATE.
            - Split composite statements into atomic facts. Answer ONLY with a JSON array.

            REFERENCE_DATE: {{referenceNow:yyyy-MM-dd HH:mm:ss 'UTC'}}
            CONTEXT: {{context ?? "(none)"}}
            CONTENT:
            {{content}}

            JSON array format:
            [{"text":"...","fact_type":"world|experience","context":"...","event_date":"YYYY-MM-DD","occurred_start":"YYYY-MM-DD","occurred_end":"YYYY-MM-DD","entities":[{"name":"...","type":"service|database|api|person|team|concept"}]}]
            Omit null fields. Return [] if no facts.
            """;
        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
        return ParseFacts(response.Text);
    }

    internal static List<ExtractedFact> ParseFacts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        var text = raw.Trim();
        if (text.StartsWith("```"))
            text = text[(text.IndexOf('\n') + 1)..].TrimEnd('`', ' ', '\n');
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return [];
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var facts = new List<ExtractedFact>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var factText = el.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(factText))
                    continue;
                var entities = new List<ExtractedEntity>();
                if (el.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
                    foreach (var e in ents.EnumerateArray())
                        if (e.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                            entities.Add(new ExtractedEntity(n.GetString()!,
                                e.TryGetProperty("type", out var ty) ? ty.GetString() : null));
                facts.Add(new ExtractedFact(
                    factText,
                    el.TryGetProperty("fact_type", out var ft) ? ft.GetString() ?? "world" : "world",
                    el.TryGetProperty("context", out var c) ? c.GetString() : null,
                    ParseDate(el, "event_date"), ParseDate(el, "occurred_start"), ParseDate(el, "occurred_end"),
                    entities));
            }
            return facts;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTime? ParseDate(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var d) && d.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(d.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var v)
            ? v.UtcDateTime : null;

    internal static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static float[] FromBytes(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }
}
