using KnowledgeHub.Server.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Resilience;

/// <summary>
/// <see cref="IChatClient"/> decorator implementing RF-002: tries the primary
/// provider, and on an eligible failure walks the configured fallback
/// providers (built lazily through <see cref="ChatClientFactory"/>) up to the
/// policy budget. Successful fallback responses carry audit metadata on
/// <see cref="ChatResponse.AdditionalProperties"/>:
/// <c>fallbackTriggered</c>, <c>originalProvider</c>, <c>fallbackProvider</c>,
/// <c>fallbackReason</c>, <c>attemptNumber</c>.
/// Non-streaming only — a stream already yielded content cannot be replayed
/// safely, so <see cref="GetStreamingResponseAsync"/> delegates to whichever
/// provider answered (fallback evaluation happens on the first response).
/// </summary>
public sealed class ResilientChatClient : IChatClient
{
    private readonly IChatClient _primary;
    private readonly IReadOnlyList<ChatProviderOptions> _fallbackOptions;
    private readonly Func<ChatProviderOptions, IChatClient?> _builder;
    private readonly IFallbackPolicyEngine _policy;
    private readonly ILogger<ResilientChatClient> _logger;
    private readonly string _primaryName;
    private readonly List<IChatClient?> _built = [];

    public ResilientChatClient(
        IChatClient primary,
        string primaryName,
        IReadOnlyList<ChatProviderOptions> fallbacks,
        IHttpClientFactory httpFactory,
        IFallbackPolicyEngine policy,
        ILogger<ResilientChatClient> logger)
        : this(primary, primaryName, fallbacks,
            o => ChatClientFactory.Create(o, httpFactory), policy, logger)
    {
    }

    /// <summary>Test seam — alternates are built through <paramref name="builder"/>.</summary>
    internal ResilientChatClient(
        IChatClient primary,
        string primaryName,
        IReadOnlyList<ChatProviderOptions> fallbacks,
        Func<ChatProviderOptions, IChatClient?> builder,
        IFallbackPolicyEngine policy,
        ILogger<ResilientChatClient> logger)
    {
        _primary = primary;
        _primaryName = primaryName;
        _fallbackOptions = fallbacks;
        _builder = builder;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>Returns the inner client unchanged when the policy is Disabled
    /// or no fallbacks are configured — keeps the hot path decorator-free.</summary>
    public static IChatClient Wrap(
        IChatClient primary, string primaryName,
        IReadOnlyList<ChatProviderOptions> fallbacks,
        IHttpClientFactory httpFactory, IFallbackPolicyEngine policy,
        ILogger<ResilientChatClient> logger) =>
        policy.Mode is FallbackMode.Disabled || fallbacks.Count == 0
            ? primary
            : new ResilientChatClient(primary, primaryName, fallbacks, httpFactory, policy, logger);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        var i = 0;
        // Lazily materializes each alternate only when reached.
        foreach (var (client, name) in CandidateClients())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await client.GetResponseAsync(messages, options, cancellationToken);
                if (i > 0)
                {
                    response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    response.AdditionalProperties["fallbackTriggered"] = true;
                    response.AdditionalProperties["originalProvider"] = _primaryName;
                    response.AdditionalProperties["fallbackProvider"] = name;
                    response.AdditionalProperties["fallbackReason"] =
                        FallbackErrorClassifier.ReasonFor(failures[^1]);
                    response.AdditionalProperties["attemptNumber"] = i + 1;
                    _logger.LogWarning(
                        "chat fallback: {Original} → {Fallback} after {Reason} (attempt {Attempt})",
                        _primaryName, name, FallbackErrorClassifier.ReasonFor(failures[^1]), i + 1);
                }
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       || !cancellationToken.IsCancellationRequested)
            {
                failures.Add(ex);
                var decision = _policy.Evaluate(ex, "chat", i, cancellationToken);
                if (!decision.ShouldFallback)
                    break;
            }
            i++;
        }

        // RF-004 edge case: every provider failed → aggregate the history;
        // a single failure rethrows unchanged (Observe/Disabled semantics).
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(
            $"all {failures.Count} chat providers failed", failures);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        // Streaming can't be replayed mid-flight; resolution delegates to the
        // primary. Fallback evaluation happens in GetResponseAsync callers.
        _primary.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : _primary.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        _primary.Dispose();
        foreach (var c in _built)
            c?.Dispose();
    }

    /// <summary>Primary first, then one lazy client per configured fallback —
    /// unbuildable providers (missing key/endpoint) are skipped (edge case).</summary>
    private IEnumerable<(IChatClient Client, string Name)> CandidateClients()
    {
        yield return (_primary, _primaryName);
        for (var i = 0; i < _fallbackOptions.Count; i++)
        {
            if (_built.Count <= i)
            {
                IChatClient? built = null;
                try
                {
                    built = _builder(_fallbackOptions[i]);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "chat fallback provider {Provider} failed to build — skipped",
                        _fallbackOptions[i].Provider);
                }
                _built.Add(built);
            }
            if (_built[i] is { } c)
                yield return (c, _fallbackOptions[i].Provider ?? $"fallback-{i}");
        }
    }
}
