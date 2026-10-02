using System.Globalization;
using System.Text.Json;

namespace KnowledgeHub.Shared.Tooling;

/// Resolved input kinds for a schema-driven tool form (SPEC-20260914-playground-tool-form RF-001).
public enum ToolFieldKind
{
    String,
    Integer,
    Number,
    Boolean,
    StringList,
    StringOrStringList,
    Choice,
    Json
}

/// <summary>One rendered form field derived from a tool's inputSchema property.</summary>
public sealed class ToolField
{
    public required string Name { get; init; }
    public required ToolFieldKind Kind { get; init; }
    /// <summary>Human label, e.g. <c>string | string[]</c> — never the literal "anyOf".</summary>
    public required string TypeLabel { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    /// <summary>True → text/select input; false → JSON textarea.</summary>
    public bool Simple => Kind is not ToolFieldKind.Json;
    public IReadOnlyList<string>? EnumValues { get; init; }
    public int? MaxItems { get; init; }
    public string? Placeholder { get; init; }
    public string? TextValue { get; set; }

    /// <summary>ValueKinds accepted by a Json-kind union field (null → any).</summary>
    internal IReadOnlySet<JsonValueKind>? UnionKinds { get; init; }
    /// <summary>Union contains a string member → bare text coerces to string.</summary>
    internal bool UnionAllowsString { get; init; }
}

/// <summary>
/// Turns a tool's <c>inputSchema</c> into ordered form fields and coerces raw
/// text back into argument JsonElements. Pure logic — lives in Shared so both
/// the WASM Playground and unit tests can reach it.
/// </summary>
public static class ToolArgumentBuilder
{
    private const string TypeString = "string";
    private const string TypeArray = "array";

    /// <summary>RF-001/RF-002: schema properties → ordered fields.</summary>
    public static IReadOnlyList<ToolField> ParseFields(JsonElement inputSchema)
    {
        var fields = new List<ToolField>();
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out var props) ||
            props.ValueKind != JsonValueKind.Object)
            return fields;

        var required = inputSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToHashSet()
            : [];

        foreach (var p in props.EnumerateObject())
            fields.Add(ParseField(p.Name, p.Value, required.Contains(p.Name)));
        return fields;
    }

    /// <summary>RF-003: raw text → argument JsonElement. Empty optional → ValueKind.Undefined (omit).</summary>
    public static bool TryBuildArgument(ToolField field, string? raw, out JsonElement value, out string? error)
    {
        value = default;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        return field.Kind switch
        {
            ToolFieldKind.String => BuildString(raw, out value, out error),
            ToolFieldKind.Choice => BuildChoice(field, raw, out value, out error),
            ToolFieldKind.Integer or ToolFieldKind.Number => BuildNumber(field, raw, out value, out error),
            ToolFieldKind.Boolean => BuildBoolean(field, raw, out value, out error),
            ToolFieldKind.StringList or ToolFieldKind.StringOrStringList => BuildList(field, raw, out value, out error),
            ToolFieldKind.Json => BuildJson(field, raw, out value, out error),
            _ => Unsupported(field, out value, out error)
        };
    }

    private static bool BuildString(string raw, out JsonElement value, out string? error)
    {
        value = JsonSerializer.SerializeToElement(raw);
        error = null;
        return true;
    }

    private static bool BuildChoice(ToolField field, string raw, out JsonElement value, out string? error)
    {
        var trimmed = raw.Trim();
        if (field.EnumValues is { } allowed && !allowed.Contains(trimmed))
        {
            value = default;
            error = $"esperado {field.TypeLabel}";
            return false;
        }
        value = JsonSerializer.SerializeToElement(trimmed);
        error = null;
        return true;
    }

    private static bool BuildNumber(ToolField field, string raw, out JsonElement value, out string? error)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            value = default;
            error = $"esperado {field.TypeLabel}";
            return false;
        }
        value = JsonSerializer.SerializeToElement(n);
        error = null;
        return true;
    }

    private static bool BuildBoolean(ToolField field, string raw, out JsonElement value, out string? error)
    {
        if (!bool.TryParse(raw.Trim(), out var b))
        {
            value = default;
            error = $"esperado {field.TypeLabel}";
            return false;
        }
        value = JsonSerializer.SerializeToElement(b);
        error = null;
        return true;
    }

    private static bool BuildList(ToolField field, string raw, out JsonElement value, out string? error)
    {
        var items = SplitList(raw);
        if (items.Count == 0)
        {
            value = default;
            error = null;
            return true;
        }
        if (field.MaxItems is { } max && items.Count > max)
        {
            value = default;
            error = $"máximo de {max} itens";
            return false;
        }
        value = field.Kind == ToolFieldKind.StringOrStringList && items.Count == 1
            ? JsonSerializer.SerializeToElement(items[0])
            : JsonSerializer.SerializeToElement(items);
        error = null;
        return true;
    }

    private static bool BuildJson(ToolField field, string raw, out JsonElement value, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (field.UnionKinds is { } kinds && !kinds.Contains(doc.RootElement.ValueKind))
            {
                value = default;
                error = $"esperado {field.TypeLabel}";
                return false;
            }
            value = doc.RootElement.Clone();
            error = null;
            return true;
        }
        catch (JsonException)
        {
            if (field.UnionAllowsString)
            {
                value = JsonSerializer.SerializeToElement(raw);
                error = null;
                return true;
            }
            value = default;
            error = $"esperado {field.TypeLabel}";
            return false;
        }
    }

    private static bool Unsupported(ToolField field, out JsonElement value, out string? error)
    {
        value = default;
        error = $"tipo não suportado ({field.TypeLabel})";
        return false;
    }

    /// <summary>RF-006: root-level <c>examples</c> — array of complete argument objects.</summary>
    public static IReadOnlyList<JsonElement> GetExamples(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind == JsonValueKind.Object &&
            inputSchema.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array)
            return ex.EnumerateArray().Select(e => e.Clone()).ToList();
        return [];
    }

    /// <summary>RF-007: fill field TextValues from a complete-args example object.</summary>
    public static void FillFromArguments(IReadOnlyList<ToolField> fields, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return;
        foreach (var f in fields)
            if (args.TryGetProperty(f.Name, out var el))
                f.TextValue = ToRawText(f, el);
    }

    /// <summary>RF-007: no examples → minimal skeleton from required fields + kinds.</summary>
    public static void FillSkeleton(IReadOnlyList<ToolField> fields)
    {
        foreach (var f in fields.Where(f => f.Required && string.IsNullOrWhiteSpace(f.TextValue)))
            f.TextValue = f.Placeholder ?? f.Kind switch
            {
                ToolFieldKind.Integer or ToolFieldKind.Number => "1",
                ToolFieldKind.Boolean => "true",
                ToolFieldKind.StringList => "a, b",
                ToolFieldKind.StringOrStringList => "a",
                ToolFieldKind.Choice => f.EnumValues?.FirstOrDefault() ?? "valor",
                ToolFieldKind.Json => "{}",
                _ => "exemplo"
            };
    }

    private static ToolField ParseField(string name, JsonElement schema, bool required)
    {
        var description = schema.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        var placeholder = ExtractExample(schema);

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
            return ParseUnion(name, anyOf, description, required, placeholder);

        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var values = enumEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
            if (values.Count > 0)
                return new ToolField
                {
                    Name = name,
                    Kind = ToolFieldKind.Choice,
                    TypeLabel = string.Join(" | ", values),
                    Description = description,
                    Required = required,
                    EnumValues = values,
                    Placeholder = placeholder ?? values[0]
                };
        }

        var kind = KindForSchema(schema, out var label, out var maxItems);
        return new ToolField
        {
            Name = name,
            Kind = kind,
            TypeLabel = label,
            Description = description,
            Required = required,
            MaxItems = maxItems,
            Placeholder = placeholder ?? DefaultPlaceholder(kind)
        };
    }

    private static ToolField ParseUnion(string name, JsonElement anyOf, string? description, bool required, string? placeholder)
    {
        var nonNull = anyOf.EnumerateArray().Where(m => !IsNullMember(m)).ToList();

        // T | null → optional field of T.
        if (nonNull.Count == 1)
        {
            var single = nonNull[0];
            var kind = KindForSchema(single, out var label, out var maxItems);
            return new ToolField
            {
                Name = name,
                Kind = kind,
                TypeLabel = $"{label} | null",
                Description = description,
                Required = false,
                MaxItems = maxItems,
                Placeholder = placeholder ?? DefaultPlaceholder(kind)
            };
        }

        // Union ⊆ {string, array-of-string} → comma-separated text input.
        var memberKinds = nonNull.Select(m => KindForSchema(m, out var l, out var mi) is var k ? (k, l, mi) : default).ToList();
        if (memberKinds.Count > 0 && memberKinds.All(m => m.k is ToolFieldKind.String or ToolFieldKind.StringList))
        {
            return new ToolField
            {
                Name = name,
                Kind = ToolFieldKind.StringOrStringList,
                TypeLabel = string.Join(" | ", memberKinds.Select(m => m.l).Distinct()),
                Description = description,
                Required = required,
                MaxItems = memberKinds.Select(m => m.mi).Where(m => m.HasValue).Max(),
                Placeholder = placeholder
            };
        }

        // Other unions → JSON textarea validated against member ValueKinds.
        var unionKinds = new HashSet<JsonValueKind>();
        var unionAllowsString = false;
        var labels = new List<string>();
        foreach (var m in nonNull)
        {
            var t = SchemaType(m);
            labels.Add(t is TypeArray && IsStringItems(m) ? "string[]" : t);
            foreach (var k in ValueKindsFor(t))
                unionKinds.Add(k);
            unionAllowsString |= t == TypeString;
        }
        return new ToolField
        {
            Name = name,
            Kind = ToolFieldKind.Json,
            TypeLabel = string.Join(" | ", labels.Distinct()),
            Description = description,
            Required = required,
            UnionKinds = unionKinds,
            UnionAllowsString = unionAllowsString,
            Placeholder = placeholder ?? "{ }"
        };
    }

    private static ToolFieldKind KindForSchema(JsonElement schema, out string label, out int? maxItems)
    {
        var type = SchemaType(schema);
        maxItems = schema.TryGetProperty("maxItems", out var mi) && mi.ValueKind == JsonValueKind.Number
            ? mi.GetInt32()
            : null;
        label = type is TypeArray && IsStringItems(schema) ? "string[]" : type;
        return type switch
        {
            TypeString => ToolFieldKind.String,
            "integer" => ToolFieldKind.Integer,
            "number" => ToolFieldKind.Number,
            "boolean" => ToolFieldKind.Boolean,
            TypeArray when IsStringItems(schema) => ToolFieldKind.StringList,
            _ => ToolFieldKind.Json
        };
    }

    private static string SchemaType(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object &&
           schema.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : "object";

    private static bool IsNullMember(JsonElement m) => SchemaType(m) == "null";

    private static bool IsStringItems(JsonElement schema)
        => schema.TryGetProperty("items", out var items) &&
           items.ValueKind == JsonValueKind.Object &&
           items.TryGetProperty("type", out var it) &&
           it.ValueKind == JsonValueKind.String && it.GetString() == TypeString;

    private static IEnumerable<JsonValueKind> ValueKindsFor(string schemaType) => schemaType switch
    {
        TypeString => [JsonValueKind.String],
        "integer" or "number" => [JsonValueKind.Number],
        "boolean" => [JsonValueKind.True, JsonValueKind.False],
        TypeArray => [JsonValueKind.Array],
        "null" => [JsonValueKind.Null],
        _ => [JsonValueKind.Object]
    };

    private static List<string> SplitList(string raw)
        => raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? ExtractExample(JsonElement schema)
    {
        if (schema.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array &&
            ex.EnumerateArray().FirstOrDefault() is { ValueKind: not JsonValueKind.Undefined } first)
            return first.ValueKind == JsonValueKind.String ? first.GetString() : first.GetRawText();
        if (schema.TryGetProperty("default", out var def))
            return def.ValueKind == JsonValueKind.String ? def.GetString() : def.GetRawText();
        return null;
    }

    private static string? DefaultPlaceholder(ToolFieldKind kind) => kind switch
    {
        ToolFieldKind.Json => "{ }",
        _ => null
    };

    private static string ToRawText(ToolField f, JsonElement el) => f.Kind switch
    {
        ToolFieldKind.String or ToolFieldKind.Choice => el.ValueKind == JsonValueKind.String ? el.GetString()! : el.GetRawText(),
        ToolFieldKind.Integer or ToolFieldKind.Number or ToolFieldKind.Boolean => el.GetRawText().Trim(),
        ToolFieldKind.StringList or ToolFieldKind.StringOrStringList => ElemListText(el),
        _ => el.GetRawText()
    };

    private static string ElemListText(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
            return string.Join(", ", el.EnumerateArray().Select(ElemText));
        return el.ValueKind == JsonValueKind.String ? el.GetString()! : el.GetRawText();
    }

    private static string? ElemText(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
}
