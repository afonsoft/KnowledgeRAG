using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnowledgeHub.Server.Eval;

/// <summary>
/// SPEC-20260923-eval-harness RF-001: one evaluation case. A hit is a result
/// whose <c>UriReference</c> equals an <see cref="ExpectedUris"/> entry or whose
/// chunk text contains every <see cref="ExpectedTextMarkers"/> marker.
/// </summary>
public sealed record EvalCase
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public IReadOnlyList<string> ExpectedUris { get; init; } = [];
    public IReadOnlyList<string> ExpectedTextMarkers { get; init; } = [];
    public string? Mode { get; init; }
    public int? TopK { get; init; }
    public bool ExpectNoAnswer { get; init; }
    /// <summary>Flow subject: when set, the case runs the flow (slug) instead
    /// of a knowledge search — the flow output is scored against the
    /// expected markers/URIs.</summary>
    public string? FlowSlug { get; init; }
    /// <summary>Input values for the flow run ({name: value}). Free text
    /// callers can use {"input": "..."}.</summary>
    public JsonObject? FlowInputs { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>Versioned dataset parser — JSON array of cases with per-index errors.</summary>
public static class EvalDataset
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parses the dataset; returns errors instead of throwing.</summary>
    public static (IReadOnlyList<EvalCase> Cases, IReadOnlyList<string> Errors) Parse(string json)
    {
        List<EvalCase>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<EvalCase>>(json, Json);
        }
        catch (JsonException ex)
        {
            return ([], [$"dataset JSON inválido: {ex.Message}"]);
        }
        if (raw is null || raw.Count == 0)
            return ([], ["dataset vazio — pelo menos um caso é obrigatório"]);
        return (raw, Validate(raw));
    }

    /// <summary>Per-case field validation; errors carry the case index.</summary>
    private static List<string> Validate(List<EvalCase> raw)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
            ValidateCase(raw[i], i, seen, errors);
        return errors;
    }

    private static void ValidateCase(EvalCase c, int i, HashSet<string> seen, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(c.Id))
            errors.Add($"case[{i}]: id obrigatório");
        else if (!seen.Add(c.Id))
            errors.Add($"case[{i}]: id duplicado '{c.Id}'");
        if (string.IsNullOrWhiteSpace(c.Question))
            errors.Add($"case[{i}]: question obrigatória");
        if (!c.ExpectNoAnswer && c.ExpectedUris.Count == 0 && c.ExpectedTextMarkers.Count == 0)
            errors.Add($"case[{i}]: expectedUris ou expectedTextMarkers obrigatórios (ou expectNoAnswer)");
        if (c.Mode is not null && c.Mode is not ("hybrid" or "semantic" or "lexical") && c.FlowSlug is null)
            errors.Add($"case[{i}]: mode inválido '{c.Mode}'");
        if (c.TopK is <= 0)
            errors.Add($"case[{i}]: topK deve ser positivo");
    }
}
