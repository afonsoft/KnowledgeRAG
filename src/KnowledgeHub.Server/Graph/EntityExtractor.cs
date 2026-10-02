using System.Text;
using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Graph;

/// <summary>Structured output of one extraction call.</summary>
public sealed record ExtractedEntity(string Name, string? Type);

/// <summary>A relation between two entity names; <see cref="EvidenceIndex"/>
/// is the 1-based chunk marker the model cited.</summary>
public sealed record ExtractedRelation(string From, string To, string? Kind, int EvidenceIndex);

public sealed record ExtractionResult(
    IReadOnlyList<ExtractedEntity> Entities,
    IReadOnlyList<ExtractedRelation> Relations);

/// <summary>
/// LLM entity/relation extraction (SPEC-20260923-graphrag RF-001): strict
/// JSON-schema prompt over a document's chunks, lenient parse — malformed
/// output is skipped, never fatal to sync.
/// </summary>
public sealed class EntityExtractor(
    IChatClient? chatClient,
    Settings.IGraphSettingsService graphSettings)
{
    /// <summary>Prompt schema version — stamped on every edge (RF guardrail).</summary>
    public const string PromptVersion = "v1";

    /// <summary>Extracts entities+relations for one document's chunks.
    /// Returns null when no chat provider is configured or the model returns
    /// unusable output — callers treat null as "skipped".</summary>
    public async Task<ExtractionResult?> ExtractAsync(
        IReadOnlyList<DocumentChunk> chunks, CancellationToken ct)
    {
        if (chatClient is null || chunks.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append(
            "Extract entities and the relationships between them from the passages below. " +
            "Reply with ONLY a JSON object — no prose, no markdown fences:\n" +
            "{\"entities\":[{\"name\":\"<display name>\",\"type\":\"service|database|api|person|team|concept\"}]," +
            "\"relations\":[{\"from\":\"<entity name>\",\"to\":\"<entity name>\"," +
            "\"kind\":\"DEPENDS_ON|USES|PUBLISHED_IN|OWNED_BY|AFFECTED_BY|MENTIONS\"," +
            "\"evidence\":<1-based passage index>}]}\n\nPassages:\n");
        for (var i = 0; i < chunks.Count; i++)
        {
            var text = chunks[i].TextContent;
            var cap = graphSettings.GetEffective().MaxChunkChars;
            if (text.Length > cap)
                text = text[..cap];
            sb.Append('[').Append(i + 1).Append("] ").Append(text).Append("\n\n");
        }

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, sb.ToString())],
            new ChatOptions { Temperature = 0, MaxOutputTokens = 2000 }, ct);
        return Parse(response.Text);
    }

    /// <summary>Lenient JSON parse — tolerates prose around the object,
    /// markdown fences and missing fields; returns null on wholesale failure.</summary>
    internal static ExtractionResult? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            return new ExtractionResult(ParseEntities(root), ParseRelations(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<ExtractedEntity> ParseEntities(JsonElement root)
    {
        var entities = new List<ExtractedEntity>();
        if (!root.TryGetProperty("entities", out var ents) || ents.ValueKind != JsonValueKind.Array)
            return entities;
        foreach (var e in ents.EnumerateArray())
        {
            var name = GetString(e, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            entities.Add(new ExtractedEntity(name, GetString(e, "type")));
        }
        return entities;
    }

    private static List<ExtractedRelation> ParseRelations(JsonElement root)
    {
        var relations = new List<ExtractedRelation>();
        if (!root.TryGetProperty("relations", out var rels) || rels.ValueKind != JsonValueKind.Array)
            return relations;
        foreach (var r in rels.EnumerateArray())
        {
            var from = GetString(r, "from");
            var to = GetString(r, "to");
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                continue;
            relations.Add(new ExtractedRelation(from, to, GetString(r, "kind"), GetEvidence(r)));
        }
        return relations;
    }

    private static string? GetString(JsonElement el, string property)
        => el.TryGetProperty(property, out var v) ? v.GetString() : null;

    private static int GetEvidence(JsonElement relation)
        => relation.TryGetProperty("evidence", out var ev) && ev.TryGetInt32(out var idx)
            ? idx : 0;
}
