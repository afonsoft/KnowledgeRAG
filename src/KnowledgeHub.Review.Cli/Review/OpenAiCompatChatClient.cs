using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

// SonarQube S1075: o trailing slash do BaseAddress é exigência do protocolo para
// resolução relativa de 'v1/chat/completions' — não há URI/delimiter hardcoded.
#pragma warning disable S1075

namespace KnowledgeHub.Review.Review;

/// <summary>
/// Minimal OpenAI-compatible <see cref="IChatClient"/> for the CLI —
/// POST {endpoint}/v1/chat/completions. Same contract as the server's
/// OpenAiChatClient; works against OpenAI, OmniRoute and Ollama /v1.
/// </summary>
public sealed class OpenAiCompatChatClient : IChatClient
{
    private const string ChatCompletionsPath = "v1/chat/completions";

    private readonly HttpClient _http;
    private readonly string _model;

    public OpenAiCompatChatClient(HttpClient http, string endpoint, string model, string? apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _http = http;
        _model = model;
        _http.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = options?.ModelId ?? _model,
            messages = messages.Select(m => new { role = m.Role.Value, content = m.Text }).ToArray(),
            temperature = options?.Temperature,
            max_tokens = options?.MaxOutputTokens,
            response_format = new { type = "json_object" },
        };
        using var response = await _http.PostAsJsonAsync(ChatCompletionsPath, payload, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM call failed HTTP {(int)response.StatusCode}: {raw[..Math.Min(500, raw.Length)]}");

        using var doc = JsonDocument.Parse(raw);
        var text = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = options?.ModelId ?? _model };
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Review CLI uses non-streaming calls.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _http.Dispose();
}
