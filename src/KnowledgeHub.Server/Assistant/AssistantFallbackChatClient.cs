using System.Diagnostics;
using System.Runtime.CompilerServices;
using KnowledgeHub.Server.Telemetry;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Assistant;

/// <summary>
/// Decorator that routes a sub-task call to the assistant client and falls back
/// to the main model on error/timeout (SPEC-20260929-a2a-assistant-delegation
/// RF-004/RF-005): emits an <c>assistant.delegate</c> span, increments
/// <c>knowledgehub.assistant.calls</c> per attempt and
/// <c>knowledgehub.assistant.fallbacks</c> when the main model takes over.
/// </summary>
public sealed class AssistantFallbackChatClient(
    IChatClient assistant, IChatClient main, string subtask, TimeSpan timeout,
    ILogger logger) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var span = KnowledgeHubActivity.Start("assistant.delegate");
        span?.SetTag("assistant.subtask", subtask);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var response = await assistant.GetResponseAsync(messages, options, timeoutCts.Token);
            KnowledgeHubMetrics.AssistantCalls.Add(1, new TagList { { "subtask", subtask }, { "outcome", "ok" } });
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "assistant sub-task {Subtask} failed — falling back to main model", subtask);
            KnowledgeHubActivity.Fail(span, ex);
            KnowledgeHubMetrics.AssistantCalls.Add(1, new TagList { { "subtask", subtask }, { "outcome", "error" } });
            KnowledgeHubMetrics.AssistantFallbacks.Add(1, new TagList { { "subtask", subtask } });
            return await main.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    // Sub-tasks never stream — go straight to the main model when one does.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        main.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : main.GetService(serviceType, serviceKey);

    public void Dispose() { /* assistant/main lifetimes owned by the provider/DI */ }
}
