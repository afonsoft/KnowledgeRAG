using KnowledgeHub.Shared.Contracts;

namespace KnowledgeHub.Server.Services;

/// <summary>
/// SPEC-20260914-llm-answer-synthesis RF-002: turns ranked context into a final
/// answer with citations via the configured <c>IChatClient</c>.
/// </summary>
public interface IAnswerService
{
    /// <summary>True when a chat provider is configured (Chat:Provider != none).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Generates an answer grounded in <paramref name="context"/>. With empty context the
    /// answer explicitly declares the knowledge base had no matches (anti-hallucination).
    /// </summary>
    Task<AskResponse> AnswerAsync(
        string question, IReadOnlyList<SearchResultItem> context, CancellationToken cancellationToken = default,
        ResponseFormatSpec? responseFormat = null);

    /// <summary>
    /// Streaming variant (SPEC-20260914-streaming-answers): yields token events as the
    /// model produces them (chunked fallback when the provider cannot stream) and a
    /// final "done" carrying the same AskResponse as <see cref="AnswerAsync"/>.
    /// </summary>
    IAsyncEnumerable<SseEvent> StreamAsync(
        string question, IReadOnlyList<SearchResultItem> context, CancellationToken cancellationToken = default,
        ResponseFormatSpec? responseFormat = null);
}
