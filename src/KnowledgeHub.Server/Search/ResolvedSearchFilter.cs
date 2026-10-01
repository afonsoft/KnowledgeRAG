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

        SourceType? sourceType = null;
        if (filter.SourceType is { Length: > 0 } st)
        {
            if (!Enum.TryParse<SourceType>(st, ignoreCase: true, out var parsed))
            {
                error = $"invalid sourceType '{st}'";
                return false;
            }
            sourceType = parsed;
        }

        DateTimeOffset? indexedAfter = null;
        if (filter.IndexedAfter is { Length: > 0 } ia)
        {
            if (!DateTimeOffset.TryParse(ia, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                error = $"invalid indexedAfter '{ia}' (expected ISO-8601)";
                return false;
            }
            indexedAfter = parsedDate;
        }

        // SPEC-20260924-query-expansion-hyde RF-003: per-call override.
        string? expansion = null;
        if (filter.Expand is { Length: > 0 } ex)
        {
            if (ex.ToLowerInvariant() is not ("off" or "multi" or "hyde" or "both"))
            {
                error = $"invalid expand '{ex}' (expected: off | multi | hyde | both)";
                return false;
            }
            expansion = ex.ToLowerInvariant();
        }

        // SPEC-20260924-hierarchical-retrieval RF-001: per-call hit-context override.
        string? contextExpand = null;
        if (filter.ContextExpand is { Length: > 0 } ce)
        {
            if (ce.ToLowerInvariant() is not ("none" or "window" or "section"))
            {
                error = $"invalid contextExpand '{ce}' (expected: none | window | section)";
                return false;
            }
            contextExpand = ce.ToLowerInvariant();
        }

        // SPEC-20260927-chunk-window-retrieval-and-autocut RF-004: per-call knobs.
        if (filter.WindowSize is < 0 or > 3)
        {
            error = $"invalid windowSize '{filter.WindowSize}' (expected: 0-3)";
            return false;
        }

        string? limitMode = null;
        if (filter.LimitMode is { Length: > 0 } lm)
        {
            if (lm.ToLowerInvariant() is not ("fixed" or "autocut"))
            {
                error = $"invalid limitMode '{lm}' (expected: fixed | autocut)";
                return false;
            }
            limitMode = lm.ToLowerInvariant();
        }

        if (filter.AutocutSensitivity is < 1 or > 3)
        {
            error = $"invalid autocutSensitivity '{filter.AutocutSensitivity}' (expected: 1-3)";
            return false;
        }

        // SPEC-20260927-multiquery: caller-supplied sub-queries — blank entries
        // dropped, capped at 4 arms (guardrail: bounded fan-out per call).
        var subQueries = filter.SubQueries?
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim())
            .Take(4)
            .ToList();

        // SPEC-20261001-mcp-recall-ergonomics RF-001/RF-003/RF-004: budget,
        // score floors and the temporal window resolve here — fail fast with a
        // friendly error for invalid values.
        string? budget = null;
        if (filter.Budget is { Length: > 0 } b)
        {
            if (b.ToLowerInvariant() is not ("low" or "mid" or "high"))
            {
                error = $"invalid budget '{filter.Budget}' (expected: low | mid | high)";
                return false;
            }
            budget = b.ToLowerInvariant();
        }

        SearchMinScores? minScores = null;
        if (filter.MinScores is { } ms)
        {
            if (ms is { Semantic: < 0 or > 1 }
                || ms is { Lexical: < 0 or > 1 }
                || ms is { Final: < 0 or > 1 })
            {
                error = "invalid minScores — semantic/lexical/final must be within 0-1";
                return false;
            }
            minScores = ms.Semantic is null && ms.Lexical is null && ms.Final is null
                ? null : ms;
        }

        DateTimeOffset? temporalStart = null, temporalEnd = null;
        if (filter.TemporalStart is { Length: > 0 } ts)
        {
            if (!DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ps))
            {
                error = $"invalid temporalWindow.start '{ts}' (expected ISO-8601)";
                return false;
            }
            temporalStart = ps;
        }
        if (filter.TemporalEnd is { Length: > 0 } te)
        {
            if (!DateTimeOffset.TryParse(te, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var pe))
            {
                error = $"invalid temporalWindow.end '{te}' (expected ISO-8601)";
                return false;
            }
            temporalEnd = pe;
        }
        if (temporalStart is { } s2 && temporalEnd is { } e2 && s2 > e2)
        {
            error = "invalid temporalWindow — start must precede end";
            return false;
        }

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
