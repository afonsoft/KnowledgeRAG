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

    private static int? OverrideInt(MemoryBank? bank, string key) =>
        Override(bank, key) is { ValueKind: JsonValueKind.Number } el && el.TryGetInt32(out var v) ? v : null;

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
