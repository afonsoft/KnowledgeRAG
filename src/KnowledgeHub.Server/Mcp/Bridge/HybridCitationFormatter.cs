using System.Text;
using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Mcp.Bridge;

/// <summary>
/// Hybrid citation blocks (SPEC-20260927-mcp-dynamic-rag-action-bridge RF-002):
/// static document citations stay as-is; live executions get
/// <c>[Live Tool: name @ timestamp]</c> lines so consumers can tell grounded
/// document facts from real-time data.
/// </summary>
public static class HybridCitationFormatter
{
    /// <summary>"[Live Tool: sql_query @ 2026-09-27T15:10:02Z — {...}]" lines.</summary>
    public static string FormatLiveCitations(IReadOnlyList<LiveToolExecution> executions)
    {
        if (executions.Count == 0)
            return "";
        var sb = new StringBuilder("\n\nLive MCP Citations:");
        foreach (var e in executions)
        {
            sb.Append("\n[Live Tool: ").Append(e.ToolName)
              .Append(" @ ").Append(e.TimestampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
              .Append(" — ").Append(e.ArgsSummary).Append(']');
            if (e.IsError)
                sb.Append(" (error)");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Synthetic context items carrying live outputs into answer synthesis —
    /// clearly labelled so the model grounds on the real values instead of
    /// inventing them (RF-002: no hallucinated dynamic data).
    /// </summary>
    public static IReadOnlyList<SearchResultItem> AsContextItems(
        IReadOnlyList<LiveToolExecution> executions)
    {
        var items = new List<SearchResultItem>();
        foreach (var e in executions.Where(e => !e.IsError))
        {
            items.Add(new SearchResultItem
            {
                ChunkText = $"[Live Tool: {e.ToolName} executed at " +
                    $"{e.TimestampUtc:yyyy-MM-dd'T'HH:mm:ss'Z'} UTC with {e.ArgsSummary}]\n" +
                    e.OutputPreview,
                DocumentTitle = $"[Live Tool: {e.ToolName}]",
                SourceName = "live-mcp",
                SourceId = Guid.Empty,
                Score = 1.0,
                UriReference = $"live://tool/{e.ToolName}"
            });
        }
        return items;
    }
}
