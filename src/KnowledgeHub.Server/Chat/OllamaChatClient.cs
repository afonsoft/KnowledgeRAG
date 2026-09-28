using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Chat;

/// <summary>
/// Ollama chat (SPEC-20260914-llm-answer-synthesis RF-001):
/// POST {Endpoint}/api/chat {model, messages, stream:false, options}
/// → message.content. Errors are sanitized — never echo endpoint internals beyond status.
/// </summary>
public sealed class OllamaChatClient : HttpChatClient
{
    public OllamaChatClient(HttpClient http, ChatProviderOptions options) : base(http, options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Endpoint, "Chat:Endpoint");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model, "Chat:Model");
        http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
    }

    protected override async Task<ChatResponse> SendAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var temperature = (double?)(options?.Temperature) ?? Options.Temperature ?? ChatJson.DefaultTemperature;
        var maxTokens = options?.MaxOutputTokens ?? Options.MaxTokens ?? ChatJson.DefaultMaxTokens;
        var sampling = new Dictionary<string, object>
        {
            ["temperature"] = temperature,
            ["num_predict"] = maxTokens
        };

        var request = new OllamaChatRequest(
            Options.Model!,
            MapMessages(messages).ToArray(),
            sampling,
            MapTools(options?.Tools).ToArray());

        var response = await Http.PostAsJsonAsync("api/chat", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ChatProviderException($"Ollama chat failed with HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken);
        var message = payload?.Message
            ?? throw new ChatProviderException("Ollama returned an empty chat response");

        return new ChatResponse(ToChatMessage(message)) { ModelId = Options.Model };
    }

    /// <summary>Serializes assistant tool-call history and tool results for the next round.</summary>
    private static IEnumerable<OllamaChatMessage> MapMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var m in messages)
        {
            var calls = m.Contents.OfType<FunctionCallContent>().ToList();
            var results = m.Contents.OfType<FunctionResultContent>().ToList();
            // RF-101: tool result payload is FunctionResultContent.Result,
            // not m.Text — the provider was receiving an empty result.
            yield return new OllamaChatMessage(
                m.Role == ChatRole.Tool ? "tool" : m.Role.Value,
                results.Count > 0 ? ResultText(results[0]) : m.Text ?? "",
                calls.Count == 0 ? []
                    : calls.Select(c => new OllamaToolCall(new OllamaToolCallFunction(
                        c.Name ?? "", ToJsonObject(c.Arguments)))).ToArray(),
                results.Count == 0 ? "" : results[0].CallId);
        }
    }

    /// <summary>Mapeia as tools para o formato Ollama; schema default quando o
    /// tool não declara parameters.</summary>
    private static IEnumerable<OllamaTool> MapTools(IList<AITool>? tools) =>
        tools?.OfType<AIFunction>().Select(f => new OllamaTool(new OllamaToolFunction(
            f.Name, f.Description ?? "", ChatJson.SchemaOrDefault(f.JsonSchema)))) ?? [];

    private static ChatMessage ToChatMessage(OllamaChatMessage message)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(message.Content))
            contents.Add(new TextContent(message.Content));
        foreach (var call in message.ToolCalls ?? [])
        {
            var arguments = new Dictionary<string, object?>();
            if (call.Function is { } fn && fn.Arguments.ValueKind == JsonValueKind.Object)
                arguments = fn.Arguments.Deserialize<Dictionary<string, object?>>() ?? arguments;
            contents.Add(new FunctionCallContent(
                Guid.NewGuid().ToString("N"),
                call.Function?.Name ?? "",
                arguments));
        }
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    /// <summary>Wire form of a tool result — strings pass through, other
    /// payload types serialize as JSON.</summary>
    internal static string ResultText(FunctionResultContent result) =>
        result.Result switch
        {
            null => "",
            string s => s,
            var other => JsonSerializer.Serialize(other, JsonSerializerOptions.Web)
        };

    private static JsonElement ToJsonObject(IDictionary<string, object?>? arguments) =>
        JsonSerializer.SerializeToElement(
            arguments ?? new Dictionary<string, object?>(), JsonSerializerOptions.Web);

    private sealed record OllamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] OllamaChatMessage[] Messages,
        [property: JsonPropertyName("options")] Dictionary<string, object> Sampling,
        [property: JsonPropertyName("tools")] OllamaTool[] Tools)
    {
        [JsonPropertyName("stream")]
        public bool Stream => false;
    }

    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("tool_calls")] OllamaToolCall[] ToolCalls,
        [property: JsonPropertyName("tool_call_id")] string ToolCallId);

    private sealed record OllamaTool(
        [property: JsonPropertyName("function")] OllamaToolFunction Function)
    {
        [JsonPropertyName("type")]
        public string Type => "function";
    }

    private sealed record OllamaToolFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    private sealed record OllamaToolCall(
        [property: JsonPropertyName("function")] OllamaToolCallFunction? Function);

    private sealed record OllamaToolCallFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] JsonElement Arguments);

    private sealed record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaChatMessage? Message);
}
