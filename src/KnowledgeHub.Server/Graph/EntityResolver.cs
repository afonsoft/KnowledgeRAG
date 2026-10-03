using System.Text.RegularExpressions;

namespace KnowledgeHub.Server.Graph;

/// <summary>
/// Entity-resolution rules (SPEC-20260923-graphrag RF-003):
/// normalized-name match merges into the existing node of the same type;
/// same name with a different type yields a distinct node + conflict alias —
/// never a silent cross-type merge.
/// </summary>
public static partial class EntityResolver
{
    /// <summary>Ontology kinds accepted from the extractor — fixed MVP set.</summary>
    public static readonly IReadOnlySet<string> EdgeKinds = new HashSet<string>
    {
        "DEPENDS_ON", "USES", "PUBLISHED_IN", "OWNED_BY", "AFFECTED_BY", "MENTIONS"
    };

    /// <summary>Lower, trim, collapse punctuation/whitespace runs to a single space.
    /// "DB-Y", "db_y" and "db  y" all normalize to "db y".</summary>
    public static string Normalize(string name) =>
        PunctuationRuns().Replace(name.Trim().ToLowerInvariant(), " ").Trim();

    /// <summary>Unknown/unsupported types collapse to the catch-all.</summary>
    public static string NormalizeType(string? type)
    {
        var normalized = type?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "service" or "database" or "api" or "person" or "team" or "concept" => normalized,
            _ => "concept"
        };
    }

    /// <summary>Unknown/unsupported kinds collapse to MENTIONS.</summary>
    public static string NormalizeKind(string? kind) =>
        kind is not null && EdgeKinds.Contains(kind.Trim().ToUpperInvariant())
            ? kind.Trim().ToUpperInvariant()
            : "MENTIONS";

    [GeneratedRegex(@"[\s\-_./\\:;(){}\[\]<>*#|]+")]
    private static partial Regex PunctuationRuns();
}
