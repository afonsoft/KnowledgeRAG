using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;

namespace KnowledgeHub.Server.Graph;

/// <summary>Formatted tool response: structured payload + short text + flags.</summary>
public sealed record FormattedGraphResponse(
    object Payload,
    string Summary,
    bool ResponseTruncated);

/// <summary>
/// Serializes temporal search results for tool output with a defensive size
/// ceiling (SPEC-20260927-temporal-episodic-knowledge-graph RF-005): payloads
/// above <see cref="TemporalGraphRetriever.MaxGraphResponsePreviewBytes"/>
/// are condensed to tabular summaries so they cannot flood the LLM context.
/// </summary>
public static class TemporalGraphFormatter
{
    /// <summary>Full-fidelity payload — labelled nodes, weighted edges.</summary>
    public static object ToPayload(TemporalSearchResult result) => new
    {
        totalNodes = result.Nodes.Count,
        totalEdges = result.Edges.Count,
        truncated = result.Truncated,
        windowStartUtc = result.WindowStart,
        windowEndUtc = result.WindowEnd,
        episode = result.Episode is null ? null : new
        {
            id = result.Episode.Id,
            kind = result.Episode.Kind,
            summary = result.Episode.Summary,
            createdAt = result.Episode.CreatedAt
        },
        nodes = result.Nodes.Select(n => new
        {
            id = n.Id,
            name = n.Name,
            type = n.Type,
            labels = n.Labels,
            observedAt = n.ObservedAt,
            validFrom = n.ValidFrom,
            validTo = n.ValidTo,
            episodeId = n.EpisodeId
        }),
        edges = result.Edges.Select(e => new
        {
            from = NodeName(result.Nodes, e.FromNodeId),
            to = NodeName(result.Nodes, e.ToNodeId),
            kind = e.Kind,
            weight = e.Weight,
            observedAt = e.ObservedAt,
            validTo = e.ValidTo,
            episodeId = e.EpisodeId,
            evidence = new
            {
                chunkId = e.EvidenceChunkId,
                documentId = e.KnowledgeDocumentId,
                sourceId = e.KnowledgeSourceId
            }
        })
    };

    /// <summary>
    /// Applies the 8 KB ceiling: full payload when it fits, otherwise a
    /// condensed tabular form (names/types/dates only); arrays are trimmed
    /// tail-first until under the cap. Always reports the truncation flag.
    /// </summary>
    public static FormattedGraphResponse EnforceSizeLimit(
        TemporalSearchResult result,
        int maxBytes = TemporalGraphRetriever.MaxGraphResponsePreviewBytes)
    {
        var full = ToPayload(result);
        if (JsonSize(full) <= maxBytes)
            return new FormattedGraphResponse(full, SummaryOf(result), false);

        // Condensed form: cluster summary + bare identifiers, evidence dropped.
        var condensedNodes = result.Nodes
            .Select(n => new { name = n.Name, type = n.Type, observedAt = n.ObservedAt })
            .ToList();
        var condensedEdges = result.Edges
            .Select(e => $"{NodeName(result.Nodes, e.FromNodeId)} -{e.Kind}-> {NodeName(result.Nodes, e.ToNodeId)}")
            .ToList();

        var omitted = 0;
        while (condensedNodes.Count + condensedEdges.Count > 0)
        {
            var payload = CondensedPayload(result, condensedNodes, condensedEdges, omitted);
            if (JsonSize(payload) <= maxBytes)
            {
                return new FormattedGraphResponse(payload,
                    SummaryOf(result) +
                    $" — response condensed to fit {maxBytes} bytes ({omitted} item(s) omitted)",
                    true);
            }
            // Drop the oldest tail item — edges first (they render longer).
            if (condensedEdges.Count > 0)
                condensedEdges.RemoveAt(condensedEdges.Count - 1);
            else
                condensedNodes.RemoveAt(condensedNodes.Count - 1);
            omitted++;
        }
        return new FormattedGraphResponse(
            CondensedPayload(result, condensedNodes, condensedEdges, omitted),
            SummaryOf(result) + $" — response condensed to fit {maxBytes} bytes",
            true);
    }

    private static object CondensedPayload(
        TemporalSearchResult result,
        IReadOnlyList<object> nodes, IReadOnlyList<string> edges, int omitted) => new
        {
            totalNodes = result.Nodes.Count,
            totalEdges = result.Edges.Count,
            truncated = true,
            responseTruncated = true,
            warning = "response exceeded the safe preview size — condensed",
            windowStartUtc = result.WindowStart,
            windowEndUtc = result.WindowEnd,
            episode = result.Episode is null ? null : new
            {
                id = result.Episode.Id,
                kind = result.Episode.Kind,
                summary = result.Episode.Summary
            },
            omitted,
            nodes,
            edges
        };

    private static int JsonSize(object payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerOptions.Web).Length;

    private static string NodeName(IReadOnlyList<KgNode> nodes, Guid id) =>
        nodes.FirstOrDefault(n => n.Id == id)?.Name ?? id.ToString("N")[..8];

    private static string SummaryOf(TemporalSearchResult result) =>
        $"{result.Nodes.Count} node(s), {result.Edges.Count} edge(s)" +
        (result.Episode is not null ? $" in episode {result.Episode.Id:N}" : "");
}
