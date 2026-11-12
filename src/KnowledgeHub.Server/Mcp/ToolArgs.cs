using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp;

/// <summary>Argument extraction + validation helpers for dynamic tools (SPEC-04 RF-002).</summary>
public static class ToolArgs
{
    public static string RequiredString(ToolCallContext ctx, string name) =>
        OptionalString(ctx, name) is { Length: > 0 } value
            ? value
            : throw new McpProtocolException($"missing required argument '{name}'", McpErrorCode.InvalidParams);

    public static string? OptionalString(ToolCallContext ctx, string name) =>
        TryGet(ctx, name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    public static int OptionalInt(ToolCallContext ctx, string name, int fallback, int max)
    {
        if (!TryGet(ctx, name, out var el))
            return fallback;
        el = Coerce(ctx, name, el);
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var value))
            throw new McpProtocolException($"argument '{name}' must be an integer", McpErrorCode.InvalidParams);
        if (value <= 0)
            return fallback;
        return Math.Min(value, max);
    }

    /// <summary>Nullable variant — distinguishes "not passed" (null) from 0
    /// (SPEC-20260927-chunk-window-retrieval-and-autocut: windowSize=0 is meaningful).</summary>
    public static int? OptionalIntOrNull(ToolCallContext ctx, string name)
    {
        if (!TryGet(ctx, name, out var el))
            return null;
        el = Coerce(ctx, name, el);
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var value))
            throw new McpProtocolException($"argument '{name}' must be an integer", McpErrorCode.InvalidParams);
        return value;
    }

    /// <summary>Top-level numeric arg (minScore etc.) — JSON-stringified
    /// numbers coerce like the rest (SPEC-20261111-mcp-ergonomics-wave2).</summary>
    public static double? OptionalDouble(ToolCallContext ctx, string name)
    {
        if (!TryGet(ctx, name, out var el))
            return null;
        el = Coerce(ctx, name, el);
        return el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)
            ? d
            : throw new McpProtocolException($"argument '{name}' must be a number", McpErrorCode.InvalidParams);
    }

    /// <summary>Top-level array-or-null arg — tolerates coercion without the
    /// object-only restriction of <see cref="OptionalObject"/>.</summary>
    public static JsonElement? OptionalElement(ToolCallContext ctx, string name)
    {
        if (!TryGet(ctx, name, out var el))
            return null;
        return Coerce(ctx, name, el);
    }

    public static bool? OptionalBool(ToolCallContext ctx, string name)
    {
        if (!TryGet(ctx, name, out var el))
            return null;
        el = Coerce(ctx, name, el);
        return el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : null;
    }

    /// <summary>SPEC-20261001-mcp-recall-ergonomics: object-typed arg
    /// (minScores/temporalWindow). A present non-object value is a caller
    /// error — unknown args stay tolerated, wrong types do not (AC-5).</summary>
    public static JsonElement? OptionalObject(ToolCallContext ctx, string name)
    {
        if (!TryGet(ctx, name, out var el))
            return null;
        el = Coerce(ctx, name, el);
        return el.ValueKind == JsonValueKind.Object
            ? el
            : throw new McpProtocolException($"argument '{name}' must be an object", McpErrorCode.InvalidParams);
    }

    /// <summary>Optional numeric property inside an object arg.</summary>
    public static double? OptionalScore(JsonElement obj, string qualifiedName)
    {
        var prop = qualifiedName[(qualifiedName.IndexOf('.') + 1)..];
        if (!obj.TryGetProperty(prop, out var p))
            return null;
        return p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : throw new McpProtocolException(
                $"argument '{qualifiedName}' must be a number", McpErrorCode.InvalidParams);
    }

    /// <summary>Optional string property inside an object arg.</summary>
    public static string? OptionalProp(JsonElement? obj, string qualifiedName)
    {
        if (obj is not { } o)
            return null;
        var prop = qualifiedName[(qualifiedName.IndexOf('.') + 1)..];
        if (!o.TryGetProperty(prop, out var p))
            return null;
        return p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : throw new McpProtocolException(
                $"argument '{qualifiedName}' must be a string", McpErrorCode.InvalidParams);
    }

    public static string[]? OptionalStringArray(ToolCallContext ctx, string name)
    {
        if (!TryGet(ctx, name, out var el))
            return null;
        el = Coerce(ctx, name, el);
        if (el.ValueKind != JsonValueKind.Array)
            return null;
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }

    /// <summary>SPEC-20261111-mcp-ergonomics-wave2 RF-001: LLM clients
    /// frequently send typed arguments as JSON-stringified values
    /// (<c>"5"</c>, <c>"{\"a\":1}"</c>, <c>"[\"x\",\"y\"]"</c>). When the
    /// declared arg type is non-string, parse the payload and re-read the
    /// typed element; unparseable strings fall through to the normal
    /// validation error of each accessor. Coercion is logged at Debug so bad
    /// clients stay observable.</summary>
    private static JsonElement Coerce(ToolCallContext ctx, string name, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String)
            return el;
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return el;
        var first = s.TrimStart()[0];
        if (first is not ('{' or '[' or '"' or 't' or 'f' or 'n' or '-' or >= '0' and <= '9'))
            return el;
        try
        {
            using var doc = JsonDocument.Parse(s);
            var coerced = doc.RootElement.Clone();
            ctx.Services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                ?.CreateLogger("KnowledgeHub.Server.Mcp.ToolArgs")
                .LogDebug("Coerced JSON-stringified argument '{Name}' ({Kind})", name, coerced.ValueKind);
            return coerced;
        }
        catch (JsonException)
        {
            return el;
        }
    }

    private static bool TryGet(ToolCallContext ctx, string name, out JsonElement element)
    {
        if (ctx.Arguments is not null && ctx.Arguments.TryGetValue(name, out var el))
        {
            element = el;
            return true;
        }
        element = default;
        return false;
    }
}

/// <summary>Helpers for building CallToolResult payloads.</summary>
public static class ToolResults
{
    public static ValueTask<CallToolResult> Text(string text) =>
        ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            IsError = false
        });

    public static ValueTask<CallToolResult> Error(string message) =>
        ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = message }],
            IsError = true
        });

    /// <summary>Text + structuredContent payload (SPEC-20260914-llm-answer-synthesis RF-002).</summary>
    public static ValueTask<CallToolResult> Structured(string text, object structured) =>
        ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = JsonSerializer.SerializeToElement(structured, JsonSerializerOptions.Web),
            IsError = false
        });
}
