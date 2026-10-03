using System.Text.Json.Nodes;

namespace KnowledgeHub.Server.Flows;

/// <summary>
/// <c>{{path}}</c> templating over flow state (SPEC agent-flows). Paths:
/// <c>vars.&lt;name&gt;</c> (or a bare input name), <c>steps.&lt;id&gt;.output.&lt;path&gt;</c>,
/// <c>steps.&lt;id&gt;.error</c>. Segments support <c>items[0].name</c> traversal
/// like AnythingLLM's resp.data.users[0] syntax.
/// </summary>
public static class VariableResolver
{
    /// <summary>Interpolate a string. A string that is exactly one
    /// <c>{{expr}}</c> returns the referenced value's natural JSON
    /// representation; embedded references stringify scalars and
    /// serialize objects/arrays.</summary>
    public static string ResolveString(string template, FlowExecContext ctx)
    {
        if (IsSingleExpression(template, out var expr))
        {
            var node = ResolvePath(expr, ctx);
            return node is null ? "" : ToStringValue(node);
        }
        return Interpolate(template, ctx);
    }

    /// <summary>Typed resolution: exact single-expression strings return the
    /// JsonNode itself (keeps arrays/objects/scalars for downstream steps);
    /// other strings interpolate; objects/arrays resolve recursively.</summary>
    public static JsonNode? ResolveNode(JsonNode? node, FlowExecContext ctx)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return IsSingleExpression(s, out var expr)
                    ? ResolvePath(expr, ctx)?.DeepClone()
                    : JsonValue.Create(Interpolate(s, ctx));
            case JsonObject obj:
                var resolved = new JsonObject();
                foreach (var (key, value) in obj)
                    resolved[key] = ResolveNode(value, ctx);
                return resolved;
            case JsonArray arr:
                var resolvedArr = new JsonArray();
                foreach (var item in arr)
                    resolvedArr.Add(ResolveNode(item, ctx));
                return resolvedArr;
            default:
                return node.DeepClone();
        }
    }

    /// <summary>Resolve a path expression against vars/step outputs.</summary>
    public static JsonNode? ResolvePath(string path, FlowExecContext ctx)
    {
        var segments = ParseSegments(path);
        if (segments.Count == 0)
            return null;

        JsonNode? current;
        var head = segments[0];
        if (head == "vars" && segments.Count > 1)
        {
            current = ctx.Vars[segments[1]];
            segments = segments.Skip(2).ToList();
        }
        else if (head == "steps" && segments.Count > 2)
        {
            var stepId = segments[1];
            // steps.<id>.output[.path] | steps.<id>.error | steps.<id>[.path]
            if (segments[2] == "error")
            {
                ctx.StepErrors.TryGetValue(stepId, out var err);
                current = err is null ? null : JsonValue.Create(err);
            }
            else
            {
                ctx.StepOutputs.TryGetValue(stepId, out var output);
                current = output;
            }
            segments = segments[2] == "error" || segments[2] == "output"
                ? segments.Skip(3).ToList()
                : segments.Skip(2).ToList();
        }
        else
        {
            // Bare name → vars.<name>
            current = ctx.Vars[head];
            segments = segments.Skip(1).ToList();
        }

        foreach (var seg in segments)
            current = WalkSegment(current, seg);
        return current;
    }

    private static JsonNode? WalkSegment(JsonNode? node, string segment)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject o:
                return o.TryGetPropertyValue(segment, out var v) ? v : null;
            case JsonArray a:
                return int.TryParse(segment, out var idx) && idx >= 0 && idx < a.Count ? a[idx] : null;
            default:
                return null;
        }
    }

    /// <summary>Split "a.b[0].c" into segments [a, b, 0, c]. Quoted keys
    /// [\"key.with.dots\"] are supported.</summary>
    internal static List<string> ParseSegments(string path)
    {
        var segments = new List<string>();
        var cur = new System.Text.StringBuilder();
        var inBracket = false;
        var inQuote = false;
        for (var i = 0; i < path.Length; i++)
        {
            var ch = path[i];
            if (inBracket)
            {
                if (inQuote)
                {
                    if (ch == '"') inQuote = false;
                    else cur.Append(ch);
                }
                else if (ch == '"') inQuote = true;
                else if (ch == ']') { segments.Add(cur.ToString()); cur.Clear(); inBracket = false; }
                else cur.Append(ch);
            }
            else if (ch == '.')
            {
                if (cur.Length > 0) { segments.Add(cur.ToString()); cur.Clear(); }
            }
            else if (ch == '[') { if (cur.Length > 0) { segments.Add(cur.ToString()); cur.Clear(); } inBracket = true; }
            else cur.Append(ch);
        }
        if (cur.Length > 0) segments.Add(cur.ToString());
        return segments;
    }

    private static bool IsSingleExpression(string s, out string expr)
    {
        expr = "";
        var trimmed = s.Trim();
        if (!trimmed.StartsWith("{{", StringComparison.Ordinal) || !trimmed.EndsWith("}}", StringComparison.Ordinal))
            return false;
        var inner = trimmed[2..^2];
        // Exactly one expression — no nested braces inside.
        if (inner.Contains("{{", StringComparison.Ordinal) || inner.Contains("}}", StringComparison.Ordinal))
            return false;
        expr = inner.Trim();
        return expr.Length > 0;
    }

    private static string Interpolate(string template, FlowExecContext ctx)
    {
        var result = new System.Text.StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            var start = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (start < 0) { result.Append(template, i, template.Length - i); break; }
            var end = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0) { result.Append(template, i, template.Length - i); break; }
            result.Append(template, i, start - i);
            var expr = template[(start + 2)..end].Trim();
            var node = ResolvePath(expr, ctx);
            if (node is not null) result.Append(ToStringValue(node));
            i = end + 2;
        }
        return result.ToString();
    }

    private static string ToStringValue(JsonNode node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(),
    };
}
