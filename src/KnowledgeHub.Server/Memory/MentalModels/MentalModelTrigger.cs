using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;

namespace KnowledgeHub.Server.Memory.MentalModels;

/// <summary>
/// SPEC-20261111-mental-models-pages RF-001: declarative refresh trigger.
/// <code>
/// {"mode":"delta|full","refreshAfterConsolidation":true,
///  "refreshIntervalMinutes":60,"fullRefreshIntervalMinutes":1440,
///  "minRefreshIntervalSeconds":300,
///  "factTypes":["observation"],"tagsMatch":"any","tags":["projeto"],
///  "refreshCron":"@every 60m","fullRefreshCron":"@every 24h"}
/// </code>
/// </summary>
public sealed record MentalModelTrigger(
    string Mode = "delta",
    bool RefreshAfterConsolidation = false,
    int? RefreshIntervalMinutes = null,
    int? FullRefreshIntervalMinutes = null,
    int MinRefreshIntervalSeconds = 300,
    IReadOnlyList<string>? FactTypes = null,
    string TagsMatch = "any",
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>Opinionated defaults for subtype 'page'.</summary>
    public static MentalModelTrigger PageDefault() => new(
        Mode: "delta",
        RefreshAfterConsolidation: true,
        FactTypes: ["observation"]);

    /// <summary>Defaults for subtype 'mental_model'.</summary>
    public static MentalModelTrigger ModelDefault() => new();

    public static MentalModelTrigger Parse(string? json, string subtype)
    {
        var defaults = subtype == "page" ? PageDefault() : ModelDefault();
        if (string.IsNullOrWhiteSpace(json))
            return defaults;
        JsonElement el;
        try
        {
            using var doc = JsonDocument.Parse(json);
            el = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new MemoryBadRequestException("trigger must be a JSON object");
        }
        if (el.ValueKind != JsonValueKind.Object)
            throw new MemoryBadRequestException("trigger must be a JSON object");

        var mode = Str(el, "mode") ?? defaults.Mode;
        if (mode is not ("delta" or "full"))
            throw new MemoryBadRequestException("trigger.mode must be 'delta' or 'full'");
        var tagsMatch = Str(el, "tagsMatch") ?? defaults.TagsMatch;
        if (tagsMatch is not ("any" or "all"))
            throw new MemoryBadRequestException("trigger.tagsMatch must be 'any' or 'all'");
        var factTypes = StrList(el, "factTypes") ?? defaults.FactTypes;
        if (factTypes is not null)
            foreach (var f in factTypes)
                if (!MemoryUnit.FactTypes.Contains(f))
                    throw new MemoryBadRequestException(
                        $"trigger.factTypes '{f}' invalid — allowed: {string.Join(',', MemoryUnit.FactTypes)}");

        var interval = PosInt(el, "refreshIntervalMinutes")
            ?? ParseEvery(Str(el, "refreshCron"), "refreshCron");
        var fullInterval = PosInt(el, "fullRefreshIntervalMinutes")
            ?? ParseEvery(Str(el, "fullRefreshCron"), "fullRefreshCron");
        var minInterval = Int(el, "minRefreshIntervalSeconds") ?? defaults.MinRefreshIntervalSeconds;
        if (minInterval < 0)
            throw new MemoryBadRequestException("trigger.minRefreshIntervalSeconds must be >= 0");

        return new MentalModelTrigger(mode,
            Bool(el, "refreshAfterConsolidation") ?? defaults.RefreshAfterConsolidation,
            interval ?? defaults.RefreshIntervalMinutes,
            fullInterval ?? defaults.FullRefreshIntervalMinutes,
            minInterval, factTypes, tagsMatch,
            StrList(el, "tags") ?? defaults.Tags);
    }

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(new
    {
        mode = Mode,
        refreshAfterConsolidation = RefreshAfterConsolidation,
        refreshIntervalMinutes = RefreshIntervalMinutes,
        fullRefreshIntervalMinutes = FullRefreshIntervalMinutes,
        minRefreshIntervalSeconds = MinRefreshIntervalSeconds,
        factTypes = FactTypes,
        tagsMatch = TagsMatch,
        tags = Tags
    });

    /// <summary>Does this model's scope cover a unit with these fact types/tags?</summary>
    public bool ScopeMatches(string factType, IReadOnlyList<string> unitTags)
    {
        if (FactTypes is not null && !FactTypes.Contains(factType))
            return false;
        if (Tags is null || Tags.Count == 0)
            return true;
        var unit = unitTags.Select(t => t.ToLowerInvariant()).ToHashSet();
        return TagsMatch == "all"
            ? Tags.All(t => unit.Contains(t.ToLowerInvariant()))
            : Tags.Any(t => unit.Contains(t.ToLowerInvariant()));
    }

    // ---- helpers ----

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool? Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static int? Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i) ? i : null;

    private static int? PosInt(JsonElement el, string name) =>
        Int(el, name) is { } i
            ? i > 0 ? i : throw new MemoryBadRequestException($"trigger.{name} must be > 0")
            : null;

    private static List<string>? StrList(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                .Where(s => s is { Length: > 0 }).Select(s => s!).ToList()
            : null;

    /// <summary>Accepts "@every Nm|Nh" and "*/N * * * *" (every N minutes).</summary>
    private static int? ParseEvery(string? cron, string field)
    {
        if (cron is null)
            return null;
        var c = cron.Trim();
        if (c.StartsWith("@every ", StringComparison.OrdinalIgnoreCase))
        {
            var span = c[7..].Trim();
            var mult = span.EndsWith('h') ? 60 : span.EndsWith('m') ? 1 : 0;
            if (mult > 0 && int.TryParse(span[..^1], out var n) && n > 0)
                return n * mult;
        }
        if (c.StartsWith("*/") && c.EndsWith(" * * * *", StringComparison.Ordinal)
            && int.TryParse(c[2..c.IndexOf(' ')], out var n2) && n2 > 0)
            return n2;
        throw new MemoryBadRequestException(
            $"trigger.{field} malformed — use '@every Nm|Nh' or '*/N * * * *' (or {field[..^4]}IntervalMinutes)");
    }

    /// <summary>Validates + normalizes a folder path; '' for root.</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var p = path.Trim().Trim('/');
        if (p.StartsWith('/') || path.Contains('\\'))
            throw new MemoryBadRequestException("path must be relative, '/'-separated");
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 10)
            throw new MemoryBadRequestException("path too deep (max 10 folders)");
        foreach (var seg in segments)
        {
            if (seg is "." or "..")
                throw new MemoryBadRequestException("path segments '.'/'..' not allowed");
            if (!System.Text.RegularExpressions.Regex.IsMatch(seg, @"^[a-zA-Z0-9][a-zA-Z0-9-_ ]*$"))
                throw new MemoryBadRequestException($"path segment '{seg}' invalid");
        }
        return string.Join('/', segments.Select(s => s.ToLowerInvariant()));
    }
}
