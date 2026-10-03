using System.Text.Json;
using System.Text.Json.Nodes;
using KnowledgeHub.Server.Mcp;
using KnowledgeHub.Server.Security;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Flows;

/// <summary>
/// <c>tool</c> / <c>knowledge</c>: invoke any catalog tool by name
/// ({tool: "search_knowledge", args: {...}}). <c>knowledge</c> is sugar —
/// same resolution with <c>search_knowledge</c> as the default tool.
/// Per-key source/tool scoping applies through ToolCallContext.Services.
/// </summary>
public sealed class ToolStepHandler : IFlowStepHandler
{
    public string Type => "tool";

    public async Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var toolName = ConfigString(step, "tool");
        if (string.IsNullOrWhiteSpace(toolName))
            throw new FlowStepException($"step '{step.Id}': 'tool' name is required");

        var args = step.Config?.TryGetPropertyValue("args", out var a) == true
            ? VariableResolver.ResolveNode(a, ctx)
            : null;
        var arguments = ToArguments(args);

        var catalog = ctx.Services.GetRequiredService<IDynamicToolCatalog>();
        var tools = await catalog.GetToolsAsync(ctx.Services, ct);
        var tool = tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
            throw new FlowStepException($"step '{step.Id}': tool '{toolName}' not in catalog (check key scope)");

        var result = await tool.Handler(new ToolCallContext
        {
            Services = ctx.Services,
            Arguments = arguments,
        }, ct);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        var output = new JsonObject
        {
            ["text"] = text,
            ["structured"] = result.StructuredContent is { } sc
                ? JsonNode.Parse(sc.GetRawText())
                : null,
            ["isError"] = result.IsError == true,
        };
        if (result.IsError == true)
            throw new FlowStepException($"step '{step.Id}': tool '{toolName}' returned error — {text}");
        return output;
    }

    private static Dictionary<string, JsonElement>? ToArguments(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return null;
        var dict = new Dictionary<string, JsonElement>(obj.Count, StringComparer.Ordinal);
        foreach (var (key, value) in obj)
            dict[key] = value is null ? default : JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        return dict;
    }

    internal static string? ConfigString(FlowStepDto step, string key) =>
        step.Config?.TryGetPropertyValue(key, out var v) == true && v is JsonValue jv
            ? ScalarString(jv)
            : null;

    private static string ScalarString(JsonValue jv) =>
        jv.TryGetValue<string>(out var s) ? s : jv.ToJsonString();
}

/// <summary><c>knowledge</c>: sugar over <see cref="ToolStepHandler"/> —
/// defaults <c>tool</c> to <c>search_knowledge</c>.</summary>
public sealed class KnowledgeStepHandler : IFlowStepHandler
{
    public string Type => "knowledge";

    public Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var config = (step.Config?.DeepClone() as JsonObject) ?? new JsonObject();
        config["tool"] ??= "search_knowledge";
        // Convenience: {question} maps to ask_knowledge's question arg.
        if (config.TryGetPropertyValue("question", out var q) && q is not null)
        {
            config["tool"] = "ask_knowledge";
            var args = (config["args"] as JsonObject) ?? new JsonObject();
            args["question"] = q.DeepClone();
            config["args"] = args;
        }
        var rewritten = step with { Config = config };
        return _inner.ExecuteAsync(rewritten, ctx, engine, ct);
    }

    private static readonly ToolStepHandler _inner = new();
}

/// <summary><c>llm</c>: single-turn chat completion ({prompt, system?, model?})
/// over the ambient IChatClient — same provider path the agent loop uses.</summary>
public sealed class LlmStepHandler : IFlowStepHandler
{
    public string Type => "llm";

    public async Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var client = ctx.Services.GetService<IChatClient>()
            ?? throw new FlowStepException($"step '{step.Id}': no chat provider configured (Chat:Provider)");
        var prompt = ToolStepHandler.ConfigString(step, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new FlowStepException($"step '{step.Id}': 'prompt' is required");
        prompt = VariableResolver.ResolveString(prompt, ctx);

        var system = ToolStepHandler.ConfigString(step, "system");
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new ChatMessage(ChatRole.System, VariableResolver.ResolveString(system, ctx)));
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        var options = new ChatOptions();
        if (step.Config?.TryGetPropertyValue("model", out var m) == true && m is JsonValue mv
            && mv.TryGetValue<string>(out var modelName) && !string.IsNullOrWhiteSpace(modelName))
            options.ModelId = VariableResolver.ResolveString(modelName, ctx);
        if (step.Config?.TryGetPropertyValue("maxTokens", out var t) == true && t is JsonValue tv
            && tv.TryGetValue<int>(out var maxTokens))
            options.MaxOutputTokens = maxTokens;

        var response = await client.GetResponseAsync(messages, options, ct);
        return new JsonObject
        {
            ["text"] = response.Text ?? "",
            ["modelId"] = response.ModelId,
        };
    }
}

/// <summary>
/// <c>http</c>: outbound HTTP call ({url, method?, headers?, body?,
/// allowPrivateHosts?, secretRef?}) through the shared SSRF egress guard.
/// <c>secretRef</c> names an integration secret whose value is attached as an
/// auth header — the secret never flows through templating/run records.
/// </summary>
public sealed class HttpStepHandler : IFlowStepHandler
{
    public string Type => "http";

    public async Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var url = ToolStepHandler.ConfigString(step, "url");
        if (string.IsNullOrWhiteSpace(url))
            throw new FlowStepException($"step '{step.Id}': 'url' is required");
        url = VariableResolver.ResolveString(url, ctx);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new FlowStepException($"step '{step.Id}': url must be absolute http(s)");

        var method = ToolStepHandler.ConfigString(step, "method") ?? "GET";
        var allowPrivate = ConfigBool(step, "allowPrivateHosts", defaultValue: false);

        var handler = new EgressPolicyHandler(allowPrivateNetworks: allowPrivate)
        {
            InnerHandler = new HttpClientHandler(),
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(10, ctx.Limits.StepTimeoutSeconds)),
        };

        using var request = await BuildRequestAsync(step, ctx, uri, method, ct);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ReadResponseAsync(response, ctx.Limits.MaxHttpBodyBytes, ct);
    }

    private static async Task<HttpRequestMessage> BuildRequestAsync(
        FlowStepDto step, FlowExecContext ctx, Uri uri, string method, CancellationToken ct)
    {
        var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), uri);

        // Secret by reference: {secretRef: "provider", secretHeader?: "Authorization",
        // secretPrefix?: "Bearer "} — resolved inside the handler, never templated.
        if (ToolStepHandler.ConfigString(step, "secretRef") is { } secretProvider
            && !string.IsNullOrWhiteSpace(secretProvider))
        {
            var store = ctx.Services.GetRequiredService<IIntegrationSecretStore>();
            var secret = await store.GetAsync(secretProvider, ct)
                ?? throw new FlowStepException($"step '{step.Id}': secret '{secretProvider}' not stored");
            var header = ToolStepHandler.ConfigString(step, "secretHeader") ?? "Authorization";
            var prefix = ToolStepHandler.ConfigString(step, "secretPrefix") ?? "Bearer ";
            request.Headers.TryAddWithoutValidation(header, prefix + secret);
        }

        if (step.Config?.TryGetPropertyValue("headers", out var h) == true && h is JsonObject headers)
        {
            foreach (var (key, value) in headers)
            {
                if (value is null) continue;
                request.Headers.TryAddWithoutValidation(key, VariableResolver.ResolveNode(value, ctx)?.ToJsonString().Trim('"'));
            }
        }

        if (step.Config?.TryGetPropertyValue("body", out var b) == true && b is not null)
        {
            var resolved = VariableResolver.ResolveNode(b, ctx);
            var isRawString = resolved is JsonValue jv && jv.TryGetValue<string>(out _);
            var payload = isRawString
                ? ((JsonValue)resolved!).GetValue<string>()
                : resolved?.ToJsonString() ?? "";
            request.Content = new StringContent(
                payload, System.Text.Encoding.UTF8, isRawString ? "text/plain" : "application/json");
        }

        return request;
    }

    private static async Task<JsonObject> ReadResponseAsync(
        HttpResponseMessage response, int maxBodyBytes, CancellationToken ct)
    {
        var bodyBytes = await ReadCappedAsync(response.Content, maxBodyBytes, ct);
        var bodyText = System.Text.Encoding.UTF8.GetString(bodyBytes);
        JsonNode? body;
        try { body = JsonNode.Parse(bodyText); }
        catch (JsonException) { body = JsonValue.Create(bodyText); }

        var responseHeaders = new JsonObject();
        foreach (var (key, values) in response.Headers)
            responseHeaders[key] = JsonValue.Create(string.Join(",", values));

        return new JsonObject
        {
            ["status"] = (int)response.StatusCode,
            ["ok"] = response.IsSuccessStatusCode,
            ["headers"] = responseHeaders,
            ["body"] = body,
        };
    }

    private static bool ConfigBool(FlowStepDto step, string key, bool defaultValue) =>
        step.Config?.TryGetPropertyValue(key, out var v) == true && v is JsonValue jv
            && jv.TryGetValue<bool>(out var b) ? b : defaultValue;

    private static async Task<byte[]> ReadCappedAsync(
        HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[Math.Min(maxBytes, 1024 * 1024)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0) break;
            read += n;
        }
        return buffer[..read];
    }
}

/// <summary><c>condition</c>: pick a branch by predicate and run its steps.
/// Config: {branches: [{when: {left, op, right}, steps: [...]}], else: [...]}.
/// Ops: eq|neq|contains|startsWith|endsWith|gt|gte|lt|lte|truthy|exists.</summary>
public sealed class ConditionStepHandler : IFlowStepHandler
{
    public string Type => "condition";

    public async Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var branches = step.Config?.TryGetPropertyValue("branches", out var br) == true
            && br is JsonArray arr
                ? arr
                : throw new FlowStepException($"step '{step.Id}': 'branches' array is required");

        JsonArray? selected = null;
        foreach (var b in branches.OfType<JsonObject>())
        {
            var when = b["when"] as JsonObject;
            if (when is null || Evaluate(when, ctx))
            {
                selected = b["steps"] as JsonArray;
                break;
            }
        }
        selected ??= step.Config?["else"] as JsonArray;
        if (selected is null)
            return null; // no branch matched and no else — condition is a no-op

        var nested = ParseSteps(selected, step.Id);
        var traceStart = ctx.Trace.Count;
        await engine.ExecuteStepsAsync(nested, ctx, ct);
        var nestedTrace = ctx.Trace.Skip(traceStart);
        return new JsonObject
        {
            ["matched"] = true,
            ["steps"] = new JsonArray(nestedTrace.Select(r => (JsonNode)new JsonObject
            {
                ["stepId"] = r.StepId,
                ["status"] = r.Status,
                ["output"] = r.Output,
                ["error"] = r.Error,
            }).ToArray()),
        };
    }

    private static bool Evaluate(JsonObject when, FlowExecContext ctx)
    {
        var left = ResolveOperand(when["left"], ctx);
        var op = when["op"]?.GetValue<string>() ?? "eq";
        var right = ResolveOperand(when["right"], ctx);

        return op.ToLowerInvariant() switch
        {
            "exists" => left is not null,
            "truthy" => IsTruthy(left),
            "eq" => Compare(left, right) == 0,
            "neq" => Compare(left, right) != 0,
            "contains" => Text(left) is { } ltc && ltc.Contains(Text(right) ?? "", StringComparison.OrdinalIgnoreCase),
            "startswith" => Text(left) is { } lts && lts.StartsWith(Text(right) ?? "", StringComparison.OrdinalIgnoreCase),
            "endswith" => Text(left) is { } lte && lte.EndsWith(Text(right) ?? "", StringComparison.OrdinalIgnoreCase),
            "gt" => Compare(left, right) > 0,
            "gte" => Compare(left, right) >= 0,
            "lt" => Compare(left, right) < 0,
            "lte" => Compare(left, right) <= 0,
            _ => throw new FlowStepException($"condition: unknown op '{op}'"),
        };
    }

    private static JsonNode? ResolveOperand(JsonNode? node, FlowExecContext ctx) =>
        node is null ? null : VariableResolver.ResolveNode(node, ctx);

    private static bool IsTruthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d.CompareTo(0) != 0,
        JsonValue v when v.TryGetValue<string>(out var s) => !string.IsNullOrEmpty(s),
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => true,
    };

    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    /// <summary>Numeric comparison when both sides parse as double; otherwise
    /// ordinal string compare.</summary>
    private static int Compare(JsonNode? left, JsonNode? right)
    {
        if (TryDouble(left, out var l) && TryDouble(right, out var r))
            return l.CompareTo(r);
        var lt = Text(left);
        var rt = Text(right);
        if (lt is null)
            return rt is null ? 0 : -1;
        if (rt is null)
            return 1;
        return string.Equals(lt, rt, StringComparison.Ordinal) ? 0
            : string.CompareOrdinal(lt, rt);
    }

    private static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v)
            return false;
        if (v.TryGetValue<double>(out var d)) { value = d; return true; }
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        return v.TryGetValue<string>(out var s) && double.TryParse(s, out value);
    }

    internal static List<FlowStepDto> ParseSteps(JsonArray array, string parentId)
    {
        var steps = new List<FlowStepDto>();
        var i = 0;
        foreach (var node in array)
        {
            var step = node?.Deserialize<FlowStepDto>(SharedJson.Options)
                ?? throw new FlowStepException($"step '{parentId}': invalid nested step at index {i}");
            steps.Add(step);
            i++;
        }
        return steps;
    }
}

/// <summary><c>foreach</c>: iterate an array expression over the nested
/// <c>steps</c> body; each iteration binds vars.item/vars.index (or config
/// <c>as</c>/<c>indexAs</c>) and collects the last body step's output.</summary>
public sealed class ForEachStepHandler : IFlowStepHandler
{
    public string Type => "foreach";

    public async Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var itemsNode = step.Config?.TryGetPropertyValue("items", out var items) == true
            ? VariableResolver.ResolveNode(items, ctx)
            : null;
        if (itemsNode is not JsonArray array)
            throw new FlowStepException($"step '{step.Id}': 'items' must resolve to an array");
        if (step.Steps is not { Count: > 0 })
            throw new FlowStepException($"step '{step.Id}': foreach requires nested 'steps'");

        var asName = ToolStepHandler.ConfigString(step, "as") ?? "item";
        var indexAs = ToolStepHandler.ConfigString(step, "indexAs") ?? "index";
        var outputs = new JsonArray();

        foreach (var item in array)
        {
            if (ctx.IterationsCount >= ctx.Limits.MaxIterations)
                throw new FlowStepException(
                    $"step '{step.Id}': iteration budget exceeded (Flow:MaxIterations={ctx.Limits.MaxIterations})");
            ctx.IterationsCount++;
            ct.ThrowIfCancellationRequested();

            ctx.Vars[asName] = item?.DeepClone();
            ctx.Vars[indexAs] = ctx.IterationsCount - 1;

            await engine.ExecuteStepsAsync(step.Steps, ctx, ct);
            var lastBodyStep = step.Steps[^1].Id;
            ctx.StepOutputs.TryGetValue(lastBodyStep, out var bodyOutput);
            outputs.Add(bodyOutput?.DeepClone());
        }
        return outputs;
    }
}

/// <summary><c>transform</c>: shape data — output = resolved {template}.</summary>
public sealed class TransformStepHandler : IFlowStepHandler
{
    public string Type => "transform";

    public Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var template = step.Config?.TryGetPropertyValue("template", out var t) == true
            ? t
            : throw new FlowStepException($"step '{step.Id}': 'template' is required");
        return Task.FromResult(VariableResolver.ResolveNode(template, ctx));
    }
}

/// <summary><c>output</c>: set the run's output value ({value}).</summary>
public sealed class OutputStepHandler : IFlowStepHandler
{
    public string Type => "output";

    public Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var value = step.Config?.TryGetPropertyValue("value", out var v) == true
            ? v
            : null;
        return Task.FromResult(VariableResolver.ResolveNode(value, ctx));
    }
}

/// <summary><c>approval</c>: mid-flow HITL gate ({message}) — marks the
/// run's pending gate; the engine suspends and FlowService persists a
/// ToolApproval. On resume the step's output becomes the resolution.
/// Rejected inside <c>foreach</c> bodies (loop state is not resumable).</summary>
public sealed class ApprovalStepHandler : IFlowStepHandler
{
    public string Type => "approval";

    public Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var message = ToolStepHandler.ConfigString(step, "message")
            ?? $"step '{step.Id}' requires approval";
        ctx.PendingApproval = new PendingApproval
        {
            StepId = step.Id,
            Message = VariableResolver.ResolveString(message, ctx),
        };
        return Task.FromResult<JsonNode?>(new JsonObject { ["pending"] = true });
    }
}

/// <summary><c>fail</c>: explicit run abort ({message}).</summary>
public sealed class FailStepHandler : IFlowStepHandler
{
    public string Type => "fail";

    public Task<JsonNode?> ExecuteAsync(
        FlowStepDto step, FlowExecContext ctx, FlowEngine engine, CancellationToken ct)
    {
        var message = ToolStepHandler.ConfigString(step, "message") ?? $"step '{step.Id}' failed the flow";
        throw new FlowStepException(VariableResolver.ResolveString(message, ctx));
    }
}
