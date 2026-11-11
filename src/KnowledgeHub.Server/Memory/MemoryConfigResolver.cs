using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Server.Memory;

/// <summary>
/// SPEC-20261111-agent-memory-retain RF-007 / memory config hierarchy
/// (SPEC-20261111-agent-rules-hardening "hierárquica"): resolves effective
/// memory settings per bank — bank ConfigJson overrides process defaults.
/// </summary>
public sealed class MemoryConfigResolver(IConfiguration config)
{
    /// <summary>Upper bound of units produced by a single retain call → 400.</summary>
    public int MaxUnitsPerRetain(MemoryBank? bank = null) =>
        OverrideInt(bank, "maxUnitsPerRetain") ?? config.GetValue("Memory:MaxUnitsPerRetain", 500);

    /// <summary>Default extraction strategy when the caller omits `strategy`.</summary>
    public string DefaultStrategy(MemoryBank? bank = null) =>
        OverrideString(bank, "defaultStrategy") ?? config.GetValue("Memory:DefaultStrategy", "extract");

    /// <summary>Max chars of one memory unit text → 400 when exceeded.</summary>
    public int MaxUnitTextLength(MemoryBank? bank = null) =>
        OverrideInt(bank, "maxUnitTextLength") ?? config.GetValue("Memory:MaxUnitTextLength", 4_000);

    /// <summary>Days two units may be apart and still earn a temporal link.</summary>
    public int TemporalLinkWindowDays(MemoryBank? bank = null) =>
        OverrideInt(bank, "temporalLinkWindowDays") ?? config.GetValue("Memory:TemporalLinkWindowDays", 7);

    // ---- SPEC-20261111-memory-observations-consolidation ----

    /// <summary>Units consolidated per batch inside one run.</summary>
    public int ConsolidationBatchSize(MemoryBank? bank = null) =>
        OverrideInt(bank, "consolidationBatchSize") ?? config.GetValue("Memory:Consolidation:BatchSize", 50);

    /// <summary>Candidate observations sent to the consolidation prompt.</summary>
    public int ConsolidationCandidateTopK(MemoryBank? bank = null) =>
        OverrideInt(bank, "consolidationCandidateTopK") ?? config.GetValue("Memory:Consolidation:CandidateTopK", 20);

    /// <summary>Similarity (cosine or lexical Jaccard) that sends a (fact, observation)
    /// pair to LLM adjudication instead of straight to create.</summary>
    public double ConsolidationDedupThreshold(MemoryBank? bank = null) =>
        OverrideDouble(bank, "consolidationDedupThreshold") ?? config.GetValue("Memory:Consolidation:DedupThreshold", 0.92);

    /// <summary>off | decision-model — opt-in cheap-model triage (RF-005).</summary>
    public string ConsolidationTriage(MemoryBank? bank = null) =>
        OverrideString(bank, "consolidationTriage") ?? config.GetValue("Memory:Consolidation:Triage", "off");

    /// <summary>How long a ConsolidationFailedAt stamp blocks reprocessing.</summary>
    public TimeSpan ConsolidationRetryBackoff(MemoryBank? bank = null) =>
        TimeSpan.FromMinutes(
            OverrideDouble(bank, "consolidationRetryBackoffMinutes")
            ?? config.GetValue("Memory:Consolidation:RetryBackoffMinutes", 5));

    /// <summary>Worker tick — process-static (not bank-overridable).</summary>
    public TimeSpan ConsolidationInterval() =>
        TimeSpan.FromMinutes(config.GetValue("Memory:Consolidation:IntervalMinutes", 15));

    private static int? OverrideInt(MemoryBank? bank, string key) =>
        Override(bank, key) is { ValueKind: JsonValueKind.Number } el && el.TryGetInt32(out var v) ? v : null;

    private static double? OverrideDouble(MemoryBank? bank, string key) =>
        Override(bank, key) is { ValueKind: JsonValueKind.Number } el && el.TryGetDouble(out var v) ? v : null;

    private static string? OverrideString(MemoryBank? bank, string key) =>
        Override(bank, key) is { ValueKind: JsonValueKind.String } el ? el.GetString() : null;

    private static JsonElement Override(MemoryBank? bank, string key)
    {
        if (bank?.ConfigJson is not { } json)
            return default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var el) ? el : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
