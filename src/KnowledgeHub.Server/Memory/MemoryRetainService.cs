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

/// <summary>Retain pipeline collaborators — keeps the service ctor under the DI param limit.</summary>
public sealed record MemoryRetainDeps(
    MemoryBankService Banks,
    MemoryConfigResolver Config,
    MemoryEntityLinker Linker,
    IMemoryDefenseScreen Defense,
    IServiceProvider Sp,
    Embeddings.IEmbeddingProvider Embeddings,
    IMemoryConsolidationTrigger Consolidation);

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-002/RF-005: the retain pipeline —
/// defense screen → (verbatim | LLM extract) → dedupe/delta-retain by
/// ContentHash → embeddings → entity + temporal links. One transaction per
/// retain so async cancellation leaves no partial units.
/// </summary>
public sealed class MemoryRetainService(
    KnowledgeHubDbContext db,
    MemoryRetainDeps deps,
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
        var bank = await deps.Banks.ResolveAsync(bankKey, ct);
        return await ExecuteAsync(bank, req, ct);
    }

    /// <summary>State carried through one retain execution.</summary>
    private sealed class RetainRun
    {
        public required MemoryBank Bank { get; init; }
        public required MemoryRetainRequest Req { get; init; }
        public required DateTime MentionedAt { get; init; }
        public required List<MemoryUnit> NewUnits { get; init; }
        public required Dictionary<string, Guid> SeenHashes { get; init; }
        public required List<Guid> DedupedIds { get; init; }
        public required Dictionary<int, List<MemoryUnit>> Preserved { get; init; }
        public MemoryDocument? Doc { get; set; }
        public Provenance? Provenance { get; init; }
        public int PreservedCount => Preserved.Values.Sum(v => v.Count);
    }

    /// <summary>SPEC-20261112-memory-prompt-provenance: screened + validated
    /// provenance for one retain batch (lives on the MemoryDocument).</summary>
    private sealed record Provenance(
        string? Prompt, string? SessionId, string Origin,
        string? PromptHash, bool Truncated, MemoryDocument? SeenDoc)
    {
        public bool HasAny => Prompt is not null || SessionId is not null || Origin != "explicit";

        /// <summary>ExternalId for provenance-only documents — deterministic on
        /// the dedupe key so repeats resolve to the same row.</summary>
        public string ExternalId =>
            $"prov:{SessionId ?? "nosession"}:{PromptHash?[..16] ?? Guid.NewGuid().ToString("N")[..16]}";

        public void ApplyTo(MemoryDocument doc)
        {
            if (Prompt is not null) doc.SourcePrompt = Prompt;
            if (SessionId is not null) doc.SourceSessionId = SessionId;
            if (PromptHash is not null) doc.PromptHash = PromptHash;
            doc.Origin = Origin;
            if (Truncated)
                doc.MetadataJson = JsonSerializer.Serialize(new { promptTruncated = true });
        }
    }

    /// <summary>Executes against an already-resolved bank (worker path).</summary>
    public async Task<MemoryRetainResult> ExecuteAsync(
        MemoryBank bank, MemoryRetainRequest req, CancellationToken ct)
    {
        var maxUnits = deps.Config.MaxUnitsPerRetain(bank);
        var strategy = req.Strategy is { Length: > 0 } s ? s : deps.Config.DefaultStrategy(bank);
        if (strategy is not ("extract" or "verbatim"))
            throw new MemoryBadRequestException($"strategy must be 'extract' or 'verbatim' (got '{strategy}')");

        // SPEC-20261112-memory-prompt-provenance RF-002/RF-003: provenance is
        // validated + screened + deduped BEFORE any extraction work.
        var provenance = await PrepareProvenanceAsync(bank, req, ct);
        if (provenance.SeenDoc is { } seenDoc)
            return new MemoryRetainResult(bank.Id, [], 0, 0,
                PromptSeen: true, DocumentId: seenDoc.ExternalId);

        var chunks = Chunk(req.Content);
        var run = new RetainRun
        {
            Bank = bank,
            Req = req,
            MentionedAt = req.Timestamp?.UtcDateTime ?? DateTime.UtcNow,
            NewUnits = [],
            SeenHashes = await ExistingHashesAsync(bank, ct),
            DedupedIds = [],
            Preserved = [],
            Provenance = provenance
        };

        // RF-005 delta-retain: same (bank, externalId) — per-chunk hash reuse.
        var docHash = MemoryHasher.Hash(req.Content);
        var early = await PrepareDeltaAsync(run, chunks, docHash, ct);
        if (early is not null)
            return early;

        if (strategy == "verbatim")
            await RetainVerbatimAsync(run, chunks, maxUnits, ct);
        else
            await RetainExtractAsync(run, chunks, maxUnits, ct);

        // RF-005: create the logical document row on first retain of an id;
        // RF-002: also create one when provenance is supplied without an id —
        // the document is the batch container for prompt/session/origin.
        if (run.Doc is null && (req.DocumentId is { Length: > 0 } || provenance.HasAny))
        {
            run.Doc = new MemoryDocument
            {
                BankId = bank.Id,
                ExternalId = req.DocumentId is { Length: > 0 } extId
                    ? extId
                    : provenance.ExternalId,
                Title = req.DocumentTitle,
                ContentHash = docHash
            };
            db.MemoryDocuments.Add(run.Doc);
            foreach (var u in run.NewUnits.Concat(run.Preserved.Values.SelectMany(v => v)))
                u.Document = run.Doc;
        }
        if (run.Doc is { } doc)
            provenance.ApplyTo(doc);

        await db.SaveChangesAsync(ct);
        // SPEC-20261111-memory-observations-consolidation RF-001 (a): signal the
        // consolidation worker when new facts landed and a chat provider exists.
        if (run.NewUnits.Count > 0 && deps.Sp.GetService<IChatClient>() is not null)
            deps.Consolidation.Signal(bank.Id);
        var allIds = run.Preserved.Values.SelectMany(v => v.Select(u => u.Id))
            .Concat(run.NewUnits.Select(u => u.Id))
            .Concat(run.DedupedIds).Distinct().ToList();
        var preservedCount = allIds.Count - run.NewUnits.Count;
        logger.LogInformation("retain: bank {Bank} strategy {Strategy} → {New} new units, {Kept} preserved",
            bank.BankKey, strategy, run.NewUnits.Count, preservedCount);
        return new MemoryRetainResult(bank.Id, allIds, run.NewUnits.Count, preservedCount,
            DocumentId: run.Doc?.ExternalId);
    }

    /// <summary>
    /// SPEC-20261112-memory-prompt-provenance RF-002/RF-003: validates origin,
    /// defense-screens sourcePrompt (same policy as content), truncates to the
    /// configured cap, hashes it, and detects a previous retain of the same
    /// (bank, sessionId, promptHash) — the prompt-level dedupe.
    /// </summary>
    private async Task<Provenance> PrepareProvenanceAsync(
        MemoryBank bank, MemoryRetainRequest req, CancellationToken ct)
    {
        var origin = string.IsNullOrWhiteSpace(req.Origin) ? "explicit" : req.Origin.Trim();
        if (origin is not ("explicit" or "passive" or "import" or "system"))
            throw new MemoryBadRequestException(
                $"origin must be explicit|passive|import|system (got '{req.Origin}')");

        var sessionId = req.SessionId?.Trim();
        if (sessionId is { Length: > 128 })
            sessionId = sessionId[..128];
        if (sessionId is { Length: 0 })
            sessionId = null;

        string? prompt = null;
        var truncated = false;
        if (req.SourcePrompt is { Length: > 0 } rawPrompt)
        {
            // Prompts carry secrets just like content — same defense policy.
            var screened = await deps.Defense.ScreenAsync(rawPrompt, bank, ct);
            var cap = deps.Config.ProvenanceMaxPromptLength(bank);
            prompt = screened.Length > cap ? screened[..cap] : screened;
            truncated = screened.Length > cap;
        }
        var hash = prompt is not null ? MemoryHasher.Hash(prompt) : null;

        MemoryDocument? seen = null;
        if (hash is not null && sessionId is not null)
            seen = await db.MemoryDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.BankId == bank.Id
                    && d.SourceSessionId == sessionId && d.PromptHash == hash, ct);
        return new Provenance(prompt, sessionId, origin, hash, truncated, seen);
    }

    /// <summary>Bank-wide hash dedupe set: real ids for existing units, Empty for this run.</summary>
    private async Task<Dictionary<string, Guid>> ExistingHashesAsync(MemoryBank bank, CancellationToken ct)
    {
        var existing = await db.MemoryUnits.AsNoTracking()
            .Where(u => u.BankId == bank.Id && !u.IsDeleted)
            .Select(u => new { u.ContentHash, u.Id })
            .ToListAsync(ct);
        var seen = new Dictionary<string, Guid>();
        foreach (var e in existing)
            seen.TryAdd(e.ContentHash, e.Id);
        return seen;
    }

    /// <summary>
    /// RF-005 delta-retain: finds the existing document for (bank, externalId),
    /// marks unchanged chunks preserved + stale units deleted. Returns an early
    /// result when the doc hash is unchanged (nothing to do).
    /// </summary>
    private async Task<MemoryRetainResult?> PrepareDeltaAsync(
        RetainRun run, List<string> chunks, string docHash, CancellationToken ct)
    {
        if (run.Req.DocumentId is not { Length: > 0 } externalId)
            return null;
        var doc = await db.MemoryDocuments
            .Include(d => d.Units)
            .FirstOrDefaultAsync(d => d.BankId == run.Bank.Id && d.ExternalId == externalId, ct);
        run.Doc = doc;
        if (doc?.ContentHash == docHash)
        {
            return new MemoryRetainResult(run.Bank.Id,
                doc.Units.Where(u => !u.IsDeleted).Select(u => u.Id).ToList(),
                0, doc.Units.Count(u => !u.IsDeleted));
        }
        if (doc is null)
            return null;

        var chunkHashes = chunks.Select(MemoryHasher.Hash).ToHashSet();
        var stale = new List<MemoryUnit>();
        foreach (var u in doc.Units.Where(u => !u.IsDeleted))
        {
            if (u.ChunkIndex is { } idx && idx < chunks.Count && chunkHashes.Contains(u.ContentHash))
            {
                if (!run.Preserved.TryGetValue(idx, out var bucket))
                    run.Preserved[idx] = bucket = [];
                bucket.Add(u);
            }
            else
            {
                stale.Add(u);
            }
        }
        foreach (var u in stale)
            u.IsDeleted = true;
        doc.ContentHash = docHash;
        doc.UpdatedAt = DateTime.UtcNow;
        if (run.Req.DocumentTitle is not null) doc.Title = run.Req.DocumentTitle;
        return null;
    }

    private async Task RetainVerbatimAsync(RetainRun run, List<string> chunks, int maxUnits, CancellationToken ct)
    {
        var maxText = deps.Config.MaxUnitTextLength(run.Bank);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (run.Preserved.ContainsKey(i)) continue;
            var text = await deps.Defense.ScreenAsync(chunks[i], run.Bank, ct);
            if (text.Length > maxText)
                throw new MemoryBadRequestException($"content exceeds maxUnitTextLength ({maxText})");
            var hash = MemoryHasher.Hash(text);
            if (run.SeenHashes.TryGetValue(hash, out var dup))
            {
                if (dup != Guid.Empty) run.DedupedIds.Add(dup);
                continue;
            }
            run.SeenHashes[hash] = Guid.Empty;
            if (run.NewUnits.Count + run.PreservedCount >= maxUnits)
                throw new MemoryBadRequestException($"retain exceeds maxUnitsPerRetain ({maxUnits})");
            run.NewUnits.Add(await NewUnitAsync(run, i, text, "experience",
                run.Req.Context, hash, ct));
        }
    }

    private async Task RetainExtractAsync(RetainRun run, List<string> chunks, int maxUnits, CancellationToken ct)
    {
        var maxText = deps.Config.MaxUnitTextLength(run.Bank);
        var chat = deps.Sp.GetService<IChatClient>()
            ?? throw new MemoryServiceUnavailableException(
                "extract strategy requires a configured chat provider — use strategy 'verbatim' or configure Chat:*");
        var total = run.PreservedCount;
        for (var i = 0; i < chunks.Count; i++)
        {
            if (run.Preserved.ContainsKey(i)) continue;
            var screened = await deps.Defense.ScreenAsync(chunks[i], run.Bank, ct);
            var facts = await ExtractAsync(chat, screened, run.Req.Context, run.MentionedAt, ct);
            foreach (var fact in facts)
            {
                var text = fact.Text.Length > maxText ? fact.Text[..maxText] : fact.Text;
                var hash = MemoryHasher.Hash(text);
                if (run.SeenHashes.TryGetValue(hash, out var dup) && dup != Guid.Empty)
                { run.DedupedIds.Add(dup); continue; }
                run.SeenHashes[hash] = Guid.Empty;
                if (++total > maxUnits)
                    throw new MemoryBadRequestException($"retain exceeds maxUnitsPerRetain ({maxUnits})");
                var unit = await NewUnitAsync(run, i, text, fact.FactType,
                    fact.Context ?? run.Req.Context, hash, ct);
                unit.EventDate = fact.EventDate;
                unit.OccurredStart = fact.OccurredStart;
                unit.OccurredEnd = fact.OccurredEnd;
                run.NewUnits.Add(unit);
                await deps.Linker.LinkEntitiesAsync(unit, run.Bank.Id, fact.Entities, ct);
                await deps.Linker.LinkTemporalNeighborsAsync(unit, deps.Config.TemporalLinkWindowDays(run.Bank), ct);
            }
        }
    }

    private async Task<MemoryUnit> NewUnitAsync(RetainRun run, int chunkIndex,
        string text, string factType, string? context, string hash, CancellationToken ct)
    {
        var unit = new MemoryUnit
        {
            BankId = run.Bank.Id,
            DocumentId = run.Doc?.Id,
            ChunkIndex = chunkIndex,
            Text = text,
            FactType = MemoryUnit.FactTypes.Contains(factType) ? factType : "experience",
            Context = context,
            MentionedAt = run.MentionedAt,
            EventDate = run.MentionedAt,
            ContentHash = hash,
            Tags = run.Req.Tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(20).ToList() ?? [],
            MetadataJson = run.Req.Metadata is { Count: > 0 } ? JsonSerializer.Serialize(run.Req.Metadata) : null
        };
        db.MemoryUnits.Add(unit);
        try
        {
            var vec = await deps.Embeddings.EmbedDocumentAsync(text, ct);
            unit.Embedding = ToBytes(vec);
            unit.EmbeddingModel = deps.Embeddings.ModelId;
        }
        catch (Exception ex)
        {
            // Embedding failure must not lose the memory — the semantic arm
            // simply skips units without vectors.
            logger.LogWarning(ex, "memory unit embedding failed (bank {Bank})", run.Bank.BankKey);
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
            return doc.RootElement.EnumerateArray()
                .Select(ParseFact)
                .Where(f => f is not null)
                .Select(f => f!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ExtractedFact? ParseFact(JsonElement el)
    {
        var factText = el.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(factText))
            return null;
        return new ExtractedFact(
            factText,
            el.TryGetProperty("fact_type", out var ft) ? ft.GetString() ?? "world" : "world",
            el.TryGetProperty("context", out var c) ? c.GetString() : null,
            ParseDate(el, "event_date"), ParseDate(el, "occurred_start"), ParseDate(el, "occurred_end"),
            ParseEntities(el));
    }

    private static List<ExtractedEntity> ParseEntities(JsonElement el)
    {
        var entities = new List<ExtractedEntity>();
        if (!el.TryGetProperty("entities", out var ents) || ents.ValueKind != JsonValueKind.Array)
            return entities;
        foreach (var e in ents.EnumerateArray())
            if (e.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                entities.Add(new ExtractedEntity(n.GetString()!,
                    e.TryGetProperty("type", out var ty) ? ty.GetString() : null));
        return entities;
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
