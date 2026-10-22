using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Agent;

/// <summary>SPEC-20261021-agent-structured-output: request validation +
/// M.E.AI translation + response parsing for responseFormat.</summary>
public static partial class StructuredOutput
{
    public const string ProviderNoSupport = "provider_no_support";
    public const string InvalidJson = "invalid_json";
    public const string SchemaRejected = "schema_rejected";

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex NameRegex();

    /// <summary>RF-5: rejects malformed formats before any model call —
    /// throws <see cref="ArgumentException"/> (→ 400 / ToolError).</summary>
    public static void Validate(ResponseFormatSpec? spec)
    {
        if (spec is null) return;
        if (spec.Type != "json_schema")
            throw new ArgumentException(
                $"unsupported responseFormat.type '{spec.Type}' (expected \"json_schema\")");
        if (spec.JsonSchema is null)
            throw new ArgumentException("responseFormat.jsonSchema is required");
        if (spec.JsonSchema.Name is not { } name || !NameRegex().IsMatch(name))
            throw new ArgumentException(
                "responseFormat.jsonSchema.name must match ^[a-zA-Z0-9_-]{1,64}$");
        if (spec.JsonSchema.Schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("responseFormat.jsonSchema.schema must be a JSON object");
    }

    /// <summary>RF-2: single path — maps to M.E.AI ChatResponseFormat.</summary>
    public static ChatResponseFormat ToChatFormat(ResponseFormatSpec spec) =>
        ChatResponseFormat.ForJsonSchema(
            spec.JsonSchema!.Schema, spec.JsonSchema.Name);

    /// <summary>RF-3: parses the answer as JSON; returns the element or null.</summary>
    public static JsonElement? TryParse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        try
        {
            using var doc = JsonDocument.Parse(answer);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
