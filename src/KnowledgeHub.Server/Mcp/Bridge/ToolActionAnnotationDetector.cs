using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Mcp.Bridge;

/// <summary>
/// Detects live-action nominations in retrieved chunks and in the question
/// itself (SPEC-20260927-mcp-dynamic-rag-action-bridge RF-001):
/// explicit <c>&lt;!-- mcp-tool: name key="value" --&gt;</c> markers embedded in
/// indexed documents (opt-in — untrusted content, see RF-001 of
/// SPEC-20260929-live-actions-bridge-hardening), and direct tool-name
/// mentions in the question.
/// Deterministic — no LLM call, so it is cheap and unit-testable.
/// </summary>
public static class ToolActionAnnotationDetector
{
    // Bounded execution — user content must never stall retrieval (S6444).
    private static readonly Regex MarkerPattern = new(
        @"<!--\s*mcp-tool:\s*(?<name>[A-Za-z0-9_\-.]+)(?<args>.*?)-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ArgPattern = new(
        @"(?<key>[A-Za-z0-9_]+)\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Tools that must never be auto-triggered: the RAG/meta surface
    /// itself (a "search_knowledge" mention is not a live action) and anything
    /// that mutates state — the bridge only ever fires read-only tools.</summary>
    private static readonly HashSet<string> NeverLiveTools = new(StringComparer.Ordinal)
    {
        "search_knowledge", "ask_knowledge", "agent_chat", "write_knowledge",
        "read_document", "write_note", "set_api_key_settings", "set_chat_settings",
        "find_dependencies", "find_dependents", "find_path", "analyze_impact",
        "search_graph_temporal", "search_graph_recent", "search_graph_diverse",
        "search_graph_relationships", "search_graph_episode"
    };

    /// <summary>Only nominations resolvable against this set are returned —
    /// the caller-visible catalog is already caller-scope filtered, which is
    /// what enforces the "same security scope" guardrail.</summary>
    /// <param name="allowDocumentMarkers">SPEC-20260929-live-actions-bridge-
    /// hardening RF-001: trust boundary — <c>mcp-tool</c> markers embedded in
    /// <em>indexed document chunks</em> are untrusted content (a stored
    /// document could plant one to fire external tools) and are ignored
    /// unless the operator opted in. Direct tool mentions in the user's own
    /// question are always honoured.</param>
    public static IReadOnlyList<ToolActionAnnotation> Detect(
        string? question,
        IReadOnlyList<SearchResultItem> results,
        IReadOnlyList<CatalogTool> visibleTools,
        int maxNominations,
        bool allowDocumentMarkers = false)
    {
        var byName = visibleTools
            .Where(t => t.ReadOnly && !NeverLiveTools.Contains(t.Name)
                && !t.Name.StartsWith("query_", StringComparison.Ordinal))
            .ToDictionary(t => t.Name, StringComparer.Ordinal);
        if (byName.Count == 0 || maxNominations <= 0)
            return [];

        var annotations = new List<ToolActionAnnotation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1) Explicit markers inside retrieved chunks — highest precedence,
        // but gated: chunk text is untrusted indexed content.
        if (allowDocumentMarkers)
            CollectMarkerAnnotations(results, byName, maxNominations, annotations, seen);

        // 2) The question names a live tool directly (word-boundary match).
        if (!string.IsNullOrWhiteSpace(question) && annotations.Count < maxNominations)
            CollectQuestionAnnotations(question, byName, maxNominations, annotations, seen);

        return annotations;
    }

    /// <summary>Source 1 — markers inside retrieved chunks (RF-001 trust
    /// boundary: flagged content is never executed).</summary>
    private static void CollectMarkerAnnotations(
        IReadOnlyList<SearchResultItem> results,
        Dictionary<string, CatalogTool> byName,
        int maxNominations,
        List<ToolActionAnnotation> annotations,
        HashSet<string> seen)
    {
        foreach (var hit in results)
        {
            if (annotations.Count >= maxNominations)
                break;
            if (hit.SuspicionFlags is not null)
                continue; // never execute instructions inside flagged content
            foreach (var g in MarkerPattern.Matches(hit.ChunkText).Select(m => m.Groups))
            {
                if (annotations.Count >= maxNominations)
                    break;
                var name = g["name"].Value;
                if (!byName.ContainsKey(name) || !seen.Add(name))
                    continue;
                annotations.Add(new ToolActionAnnotation(
                    name, ParseArgs(g["args"].Value),
                    ToolActionOrigin.Marker, hit.ChunkId));
            }
        }
    }

    /// <summary>Source 2 — the question names a live tool directly
    /// (word-boundary match, longest names first).</summary>
    private static void CollectQuestionAnnotations(
        string question,
        Dictionary<string, CatalogTool> byName,
        int maxNominations,
        List<ToolActionAnnotation> annotations,
        HashSet<string> seen)
    {
        foreach (var name in byName.Keys
            .Where(n => Mentions(question, n) && !seen.Contains(n))
            .OrderByDescending(k => k.Length))
        {
            if (annotations.Count >= maxNominations)
                break;
            seen.Add(name);
            annotations.Add(new ToolActionAnnotation(
                name, new Dictionary<string, JsonElement>(),
                ToolActionOrigin.Question, null));
        }
    }

    private static bool Mentions(string question, string toolName)
    {
        var idx = 0;
        while ((idx = question.IndexOf(toolName, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = idx == 0 || !char.IsLetterOrDigit(question[idx - 1]);
            var afterIdx = idx + toolName.Length;
            var after = afterIdx >= question.Length || !char.IsLetterOrDigit(question[afterIdx]);
            if (before && after)
                return true;
            idx = afterIdx;
        }
        return false;
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseArgs(string raw)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var g in ArgPattern.Matches(raw).Select(m => m.Groups))
            args[g["key"].Value] =
                JsonSerializer.SerializeToElement(g["value"].Value);
        return args;
    }
}
