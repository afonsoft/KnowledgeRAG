using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

// SonarQube S3236: o segundo argumento de ThrowIfNullOrWhiteSpace é a mensagem de erro (chave de config), não um parâmetro caller-info — falso positivo.
#pragma warning disable S3236

// SonarQube S1075: endpoint e path 'v1/chat/completions' vêm de Chat:Endpoint/protocolo OpenAI — não há URI hardcoded.
#pragma warning disable S1075

namespace KnowledgeHub.Server.Chat;

/// <summary>
/// OpenAI-compatible chat (SPEC-20260914-llm-answer-synthesis RF-001):
/// POST {Endpoint}/v1/chat/completions {model, messages, temperature?, max_tokens?}
/// → choices[0].message.content. Works against OpenAI and compatible gateways.
/// </summary>
public sealed class OpenAiChatClient : HttpChatClient
{
    private readonly string? _apiKey;

    public OpenAiChatClient(HttpClient http, ChatProviderOptions options) : base(http, options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Endpoint, "Chat:Endpoint");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model, "Chat:Model");
        http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
        _apiKey = options.ApiKey;
    }

    protected override async Task<ChatResponse> SendAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var temperature = (double?)(options?.Temperature) ?? Options.Temperature ?? ChatJson.DefaultTemperature;
        var maxTokens = options?.MaxOutputTokens ?? Options.MaxTokens ?? ChatJson.DefaultMaxTokens;

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(new OpenAiChatRequest(
                Options.Model!,
                MapMessages(messages).ToArray(),
                temperature,
                maxTokens,
                MapTools(options?.Tools)))
        };
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ChatProviderException($"OpenAI chat failed with HTTP {(int)response.StatusCode}", (int)response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(cancellationToken);
        var message = payload?.Choices?.FirstOrDefault()?.Message
            ?? throw new ChatProviderException("OpenAI returned an empty chat response");

        return new ChatResponse(ToChatMessage(message)) { ModelId = Options.Model };
    }

    /// <summary>Serializes assistant tool-call history and tool results for the next round.</summary>
    private static IEnumerable<OpenAiChatMessage> MapMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var m in messages)
        {
            var calls = m.Contents.OfType<FunctionCallContent>().ToList();
            var results = m.Contents.OfType<FunctionResultContent>().ToList();
            // RF-101 (SPEC-20260926-review-backlog-remediation): a tool message's
            // payload lives in FunctionResultContent.Result — m.Text only sees
            // TextContent and serialized the result as "" to the provider.
            yield return new OpenAiChatMessage(
                m.Role == ChatRole.Tool ? "tool" : m.Role.Value,
                results.Count > 0 ? ResultText(results[0]) : m.Text ?? "",
                calls.Count == 0 ? []
                    : calls.Select(c => new OpenAiToolCall(c.CallId ?? "", new OpenAiToolCallFunction(
                        c.Name ?? "",
                        JsonSerializer.Serialize(c.Arguments ?? EmptyArguments, JsonSerializerOptions.Web)))).ToArray(),
                results.Count == 0 ? "" : results[0].CallId);
        }
    }

    /// <summary>Mapeia as tools para o formato OpenAI; array vazio quando não há
    /// tools e schema default quando o tool não declara parameters — gateways
    /// restritos rejeitam campos nulos.</summary>
    private static OpenAiTool[] MapTools(IList<AITool>? tools) =>
        tools?.OfType<AIFunction>().Select(f => new OpenAiTool(new OpenAiToolFunction(
            f.Name, f.Description ?? "", ChatJson.SchemaOrDefault(f.JsonSchema)))).ToArray() ?? [];

    /// <summary>Arguments default para tool calls sem argumentos serializáveis.</summary>
    private static readonly Dictionary<string, object?> EmptyArguments = new();

    /// <summary>Wire form of a tool result — strings pass through, other
    /// payload types serialize as JSON.</summary>
    internal static string ResultText(FunctionResultContent result) =>
        result.Result switch
        {
            null => "",
            string s => s,
            var other => JsonSerializer.Serialize(other, JsonSerializerOptions.Web)
        };

    private static ChatMessage ToChatMessage(OpenAiChatMessage message)
    {
        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(message.Content))
            contents.Add(new TextContent(message.Content));
        foreach (var call in message.ToolCalls ?? [])
        {
            var arguments = new Dictionary<string, object?>();
            if (call.Function?.Arguments is { Length: > 0 } argsJson)
                arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? arguments;
            contents.Add(new FunctionCallContent(
                call.Id ?? Guid.NewGuid().ToString("N"),
                call.Function?.Name ?? "",
                arguments));
        }
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private sealed record OpenAiChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] OpenAiChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("tools")] OpenAiTool[] Tools);

    private sealed record OpenAiChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("tool_calls")] OpenAiToolCall[] ToolCalls,
        [property: JsonPropertyName("tool_call_id")] string ToolCallId);

    private sealed record OpenAiTool(
        [property: JsonPropertyName("function")] OpenAiToolFunction Function)
    {
        [JsonPropertyName("type")]
        public string Type => "function";
    }

    private sealed record OpenAiToolFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonElement Parameters);

    private sealed record OpenAiToolCall(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("function")] OpenAiToolCallFunction? Function);

    private sealed record OpenAiToolCallFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments);

    private sealed record OpenAiChatChoice(
        [property: JsonPropertyName("message")] OpenAiChatMessage? Message);

    private sealed record OpenAiChatResponse(
        [property: JsonPropertyName("choices")] OpenAiChatChoice[]? Choices);
}
