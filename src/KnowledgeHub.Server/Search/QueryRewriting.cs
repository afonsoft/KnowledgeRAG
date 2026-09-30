using KnowledgeHub.Server.Caching;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;

namespace KnowledgeHub.Server.Search;

/// <summary>
/// Optional pre-retrieval stage (SPEC-20260923-retrieval-quality RF-001):
/// rewrites the raw user query into a retrieval-oriented form. Fail-open —
/// implementations must return the original query when disabled or on error.
/// </summary>
public interface IQueryRewriter
{
    Task<string> RewriteAsync(string query, CancellationToken ct = default);

    /// <summary>SPEC-20260924-conversational-query-context: rewrites the query as
    /// standalone using the recent conversation (pronouns/references resolved).
    /// Default ignores the context — same as the parameterless overload.</summary>
    Task<string> RewriteAsync(string query, string? conversationContext, CancellationToken ct = default) =>
        RewriteAsync(query, ct);
}

/// <summary>
/// LLM-backed rewriter. Enabled via <c>Search:QueryRewrite:Enabled</c>
/// (default false) and requires a configured <see cref="IChatClient"/>.
/// Rewrites are cached in <see cref="IDistributedCache"/> keyed
/// <c>rewrite:{model}:{sha256(query)}</c> for 24h. Any provider failure falls
/// back to the original query.
/// </summary>
public sealed class LlmQueryRewriter(
    IServiceProvider services,
    IDistributedCache cache,
    IConfiguration configuration,
    ILogger<LlmQueryRewriter> logger) : IQueryRewriter
{
    // TTL: region policy ("rewrite:" prefix) — SPEC-20260925-cache-region-ttl-policies.

    private const string RewritePrompt =
        "Rewrite the user's text as a concise search query for a knowledge base: " +
        "expand acronyms, normalize terminology, remove conversational filler. " +
        "Return only the rewritten query — no explanation, no quotes.";

    private const string ContextualRewritePrompt =
        "Given the conversation so far and the user's latest text, rewrite the " +
        "latest text as a standalone search query for a knowledge base: resolve " +
        "pronouns and references using the conversation, expand acronyms, " +
        "normalize terminology. Return only the rewritten query — never answer it.";

    public async Task<string> RewriteAsync(string query, CancellationToken ct = default) =>
        await RewriteAsync(query, null, ct);

    public async Task<string> RewriteAsync(string query, string? conversationContext, CancellationToken ct = default)
    {
        if (!configuration.GetValue("Search:QueryRewrite:Enabled", false))
            return query;

        var chat = services.GetService<Assistant.IAssistantChatClientProvider>()
            ?.ForSubtask("rewrite", services.GetService<IChatClient>())
            ?? services.GetService<IChatClient>();
        if (chat is null)
            return query;

        var useContext = !string.IsNullOrWhiteSpace(conversationContext)
            && configuration.GetValue("Agent:QueryContext:Enabled", true);
        var key = $"rewrite:{configuration.GetValue("Chat:Model", "default")}:{CacheKeys.Hash(query)}" +
            (useContext ? $":{CacheKeys.Hash(conversationContext!)}" : "");
        var cached = await SafeCache.GetStringAsync(cache, key, logger, ct);
        if (cached is not null)
            return cached;

        var userContent = useContext
            ? $"Conversation so far:\n{conversationContext}\n\nLatest user text: {query}"
            : query;

        var llmSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await chat.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System,
                        useContext ? ContextualRewritePrompt : RewritePrompt),
                    new ChatMessage(ChatRole.User, userContent)
                ],
                new ChatOptions { Temperature = 0, MaxOutputTokens = 128 },
                ct);
            Telemetry.KnowledgeHubMetrics.LlmDuration.Record(llmSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", chat.GetType().Name),
                new KeyValuePair<string, object?>("model", response.ModelId),
                new KeyValuePair<string, object?>("kind", "rewrite"));
            var rewritten = response.Text?.Trim();
            if (string.IsNullOrEmpty(rewritten) || rewritten.Length > query.Length * 4)
            {
                logger.LogWarning("Query rewrite returned unusable output ({Length} chars) — using original",
                    rewritten?.Length ?? 0);
                return query;
            }

            logger.LogInformation("Query rewritten ({In} → {Out} chars)", query.Length, rewritten.Length);
            await SafeCache.SetStringAsync(cache, key, rewritten, null, logger, ct);
            return rewritten;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Query rewrite failed — using original query");
            return query;
        }
    }
}
