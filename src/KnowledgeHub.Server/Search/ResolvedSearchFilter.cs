using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;

namespace KnowledgeHub.Server.Search;

/// <summary>
/// Validated, typed form of <see cref="SearchFilter"/>
/// (SPEC-20260923-retrieval-quality RF-003). Resolution fails fast with a
/// friendly error for invalid enum/date values.
/// </summary>
public sealed record ResolvedSearchFilter(
    SourceType? SourceType,
    string? PathPrefix,
    DateTimeOffset? IndexedAfter,
    string? Language,
    string? Expansion = null,
    string? ContextExpand = null,
    bool? UseGraph = null,
    int? WindowSize = null,
    string? LimitMode = null,
    int? AutocutSensitivity = null,
    IReadOnlyList<string>? SubQueries = null,
    bool? AllowRelaxation = null,
    string? Budget = null,
    SearchMinScores? MinScores = null,
    DateTimeOffset? TemporalStart = null,
    DateTimeOffset? TemporalEnd = null)
{
    public bool IsEmpty =>
        SourceType is null && PathPrefix is null && IndexedAfter is null && Language is null;

    /// <summary>SPEC-20261001-mcp-recall-ergonomics RF-001: normalized budget
    /// level for this call (null = high).</summary>
    public string? Budget { get; init; } = Budget;
    public SearchMinScores? MinScores { get; init; } = MinScores;
    public DateTimeOffset? TemporalStart { get; init; } = TemporalStart;
    public DateTimeOffset? TemporalEnd { get; init; } = TemporalEnd;

    /// <summary>RF-001: low/mid budgets skip LLM query expansion unless the
    /// caller sets <c>expand</c> explicitly; high keeps the configured default.</summary>
    public bool SkipsExpansion => Budget is "low" or "mid";

    /// <summary>SPEC-20260927-chunk-window-retrieval-and-autocut RF-004: the limit
    /// mode that applies to this call (per-call arg → <c>Search:LimitMode</c>).</summary>
    public string EffectiveLimitMode(IConfiguration cfg) =>
        LimitMode ?? cfg.GetValue("Search:LimitMode", "fixed");

    /// <summary>Stable fingerprint for the v2 result-cache key (RF-005).
    /// Expansion is part of result identity even when other filters are empty.</summary>
    public string Fingerprint() =>
        (IsEmpty ? "-" : $"{SourceType}|{PathPrefix}|{IndexedAfter:O}|{Language}")
        + (Expansion is null ? "" : $"|expand:{Expansion}")
        + (ContextExpand is null or "none" ? "" : $"|ctx:{ContextExpand}")
        + (UseGraph is { } g ? $"|graph:{Convert.ToInt32(g)}" : "")
        + (WindowSize is null ? "" : $"|win:{WindowSize}")
        + (LimitMode is null ? "" : $"|lim:{LimitMode}")
        + (AutocutSensitivity is null ? "" : $"|acs:{AutocutSensitivity}")
        + (SubQueries is { Count: > 0 } sq ? $"|sub:{string.Join('|', sq)}" : "")
        + (AllowRelaxation is { } r ? $"|relax:{Convert.ToInt32(r)}" : "")
        + (Budget is null ? "" : $"|budget:{Budget}")
        + (MinScores is { } ? $"|min:{MinScores.Semantic:R}|{MinScores.Lexical:R}|{MinScores.Final:R}" : "")
        + (TemporalStart is null ? "" : $"|tstart:{TemporalStart:O}")
        + (TemporalEnd is null ? "" : $"|tend:{TemporalEnd:O}");

    public static bool TryResolve(
        SearchFilter? filter, out ResolvedSearchFilter resolved, out string? error)
    {
        resolved = new ResolvedSearchFilter(null, null, null, null);
        error = null;
        if (filter is null)
            return true;

        // SPEC-20260924-query-expansion-hyde RF-003 / SPEC-20260924-hierarchical-retrieval
        // RF-001 / SPEC-20260927-chunk-window-retrieval-and-autocut RF-004 /
        // SPEC-20261001-mcp-recall-ergonomics RF-001: per-call overrides resolve
        // here — fail fast with a friendly error for invalid values.
        if (!TryParseSourceType(filter.SourceType, out var sourceType, out error)
            || !TryParseDate(filter.IndexedAfter, "indexedAfter", out var indexedAfter, out error)
            || !TryParseChoice(filter.Expand, "expand", ["off", "multi", "hyde", "both"], out var expansion, out error)
            || !TryParseChoice(filter.ContextExpand, "contextExpand", ["none", "window", "section"], out var contextExpand, out error)
            || !CheckRange(filter.WindowSize, "windowSize", 0, 3, out error)
            || !TryParseChoice(filter.LimitMode, "limitMode", ["fixed", "autocut"], out var limitMode, out error)
            || !CheckRange(filter.AutocutSensitivity, "autocutSensitivity", 1, 3, out error)
            || !TryParseChoice(filter.Budget, "budget", ["low", "mid", "high"], out var budget, out error)
            || !TryResolveMinScores(filter.MinScores, out var minScores, out error)
            || !TryResolveTemporalWindow(filter, out var temporalStart, out var temporalEnd, out error))
            return false;

        // SPEC-20260927-multiquery: caller-supplied sub-queries — blank entries
        // dropped, capped at 4 arms (guardrail: bounded fan-out per call).
        var subQueries = filter.SubQueries?
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim())
            .Take(4)
            .ToList();

        resolved = new ResolvedSearchFilter(
            sourceType,
            string.IsNullOrWhiteSpace(filter.PathPrefix) ? null : filter.PathPrefix,
            indexedAfter,
            string.IsNullOrWhiteSpace(filter.Language) ? null : filter.Language,
            expansion,
            contextExpand,
            filter.UseGraph,
            filter.WindowSize,
            limitMode,
            filter.AutocutSensitivity,
            subQueries is { Count: > 0 } ? subQueries : null,
            filter.AllowRelaxation,
            budget,
            minScores,
            temporalStart,
            temporalEnd);
        return true;
    }

    private static bool TryParseSourceType(
        string? raw, out SourceType? value, out string? error)
    {
        value = null;
        error = null;
        if (raw is not { Length: > 0 })
            return true;
        if (!Enum.TryParse<SourceType>(raw, ignoreCase: true, out var parsed))
        {
            error = $"invalid sourceType '{raw}'";
            return false;
        }
        value = parsed;
        return true;
    }

    /// <summary>ISO-8601 date filter — <paramref name="field"/> is the public
    /// arg name used in the error message.</summary>
    private static bool TryParseDate(
        string? raw, string field, out DateTimeOffset? value, out string? error)
    {
        value = null;
        error = null;
        if (raw is not { Length: > 0 })
            return true;
        if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            error = $"invalid {field} '{raw}' (expected ISO-8601)";
            return false;
        }
        value = parsed;
        return true;
    }

    /// <summary>Lowercased enum-like string filter against an allowlist.</summary>
    private static bool TryParseChoice(
        string? raw, string field, string[] allowed, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (raw is not { Length: > 0 })
            return true;
        var lowered = raw.ToLowerInvariant();
        if (!allowed.Contains(lowered))
        {
            error = $"invalid {field} '{raw}' (expected: {string.Join(" | ", allowed)})";
            return false;
        }
        value = lowered;
        return true;
    }

    private static bool CheckRange(
        int? raw, string field, int min, int max, out string? error)
    {
        error = null;
        if (raw < min || raw > max)
        {
            error = $"invalid {field} '{raw}' (expected: {min}-{max})";
            return false;
        }
        return true;
    }

    /// <summary>SPEC-20261001-mcp-recall-ergonomics RF-003: per-stage score
    /// floors — all within 0-1; an all-null object resolves to none.</summary>
    private static bool TryResolveMinScores(
        SearchMinScores? ms, out SearchMinScores? minScores, out string? error)
    {
        minScores = null;
        error = null;
        if (ms is null)
            return true;
        if (ms is { Semantic: < 0 or > 1 }
            || ms is { Lexical: < 0 or > 1 }
            || ms is { Final: < 0 or > 1 })
        {
            error = "invalid minScores — semantic/lexical/final must be within 0-1";
            return false;
        }
        minScores = ms.Semantic is null && ms.Lexical is null && ms.Final is null
            ? null : ms;
        return true;
    }

    /// <summary>SPEC-20261001-mcp-recall-ergonomics RF-004: explicit recency
    /// window — both ends ISO-8601, start must precede end.</summary>
    private static bool TryResolveTemporalWindow(
        SearchFilter filter,
        out DateTimeOffset? temporalStart,
        out DateTimeOffset? temporalEnd,
        out string? error)
    {
        temporalStart = null;
        temporalEnd = null;
        error = null;
        if (!TryParseDate(filter.TemporalStart, "temporalWindow.start", out temporalStart, out error)
            || !TryParseDate(filter.TemporalEnd, "temporalWindow.end", out temporalEnd, out error))
            return false;
        if (temporalStart is { } s2 && temporalEnd is { } e2 && s2 > e2)
        {
            error = "invalid temporalWindow — start must precede end";
            return false;
        }
        return true;
    }

    /// <summary>Human-readable scope descriptor for envelope metadata
    /// (originalFilter/appliedFilter) — no PII beyond caller-supplied params.</summary>
    public static string DescribeScope(Guid? sourceId, ResolvedSearchFilter? filter)
    {
        var parts = new List<string>();
        if (sourceId is { } sid) parts.Add($"sourceId={sid:N}");
        if (filter?.SourceType is { } st) parts.Add($"sourceType={st}");
        if (filter?.PathPrefix is { } pp) parts.Add($"pathPrefix={pp}");
        return parts.Count > 0 ? string.Join(",", parts) : "global";
    }
}
