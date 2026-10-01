using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Chat;

/// <summary>
/// Thin <see cref="IChatClient"/> over provider HTTP APIs — same raw-HTTP approach
/// as the embedding providers (no heavy SDK dependency). Non-streaming only;
/// streaming lands with SPEC-20260914-streaming-answers.
/// </summary>
public abstract class HttpChatClient(HttpClient http, ChatProviderOptions options) : IChatClient // NOSONAR S3881 — HttpClient injetado pelo DI, lifetime externo
{
    protected HttpClient Http { get; } = http;
    protected ChatProviderOptions Options { get; } = options;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Options.TimeoutSeconds));
        try
        {
            return await SendAsync(messages.ToList(), options, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatProviderException($"chat provider timed out after {Options.TimeoutSeconds}s", isTimeout: true);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatProviderException($"chat provider request failed: {ex.Message}");
        }
    }

    protected abstract Task<ChatResponse> SendAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken);

    public virtual IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("streaming is not supported by this chat client yet");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
