using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Tests.Unit.Resilience;

// SPEC-20260927-tool-and-model-resilience-fallback ACs: 429→fallback,
// observe→rethrow, 401→immediate, cancellation→abort, all-fail→aggregate,
// unbuildable alternate skipped.
public sealed class ResilientChatClientTests
{
    private sealed class FakeChatClient(Exception? error = null, string reply = "ok") : IChatClient
    {
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (error is not null)
                throw error;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class DummyFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static IFallbackPolicyEngine Policy(string mode, int max = 2) =>
        new FallbackPolicyEngine(
            Options.Create(new FallbackOptions { Mode = mode, MaxFallbackAttempts = max }),
            NullLogger<FallbackPolicyEngine>.Instance);

    private static readonly ChatMessage[] Prompt = [new(ChatRole.User, "hi")];

    /// <summary>Builder seam: maps each fallback option to a prepared client.</summary>
    private static ResilientChatClient Sut(
        IChatClient primary, string mode,
        IReadOnlyList<(ChatProviderOptions options, IChatClient? client)> alternates)
    {
        var map = alternates.ToDictionary(a => a.options, a => a.client);
        return new ResilientChatClient(primary, "primary",
            alternates.Select(a => a.options).ToList(),
            o => map[o], Policy(mode), NullLogger<ResilientChatClient>.Instance);
    }

    private static ChatProviderOptions Opt(string provider = "ollama", string model = "m") =>
        new() { Provider = provider, Model = model };

    // Mode disabled → decorator bypassed entirely (Wrap returns primary).
    [Fact]
    public async Task Disabled_ReturnsPrimaryUnwrapped()
    {
        var primary = new FakeChatClient();
        var wrapped = ResilientChatClient.Wrap(primary, "primary",
            [Opt()], new DummyFactory(), Policy("disabled"),
            NullLogger<ResilientChatClient>.Instance);
        Assert.Same(primary, wrapped);
        var r = await wrapped.GetResponseAsync(Prompt);
        Assert.Equal("ok", r.Text);
    }

    // AC-1: Enforce + 429 on primary → secondary answers, metadata attached.
    [Fact]
    public async Task Enforce429_FallsBackWithMetadata()
    {
        var primary = new FakeChatClient(new ChatProviderException("limited", 429));
        var secondary = new FakeChatClient(reply: "from-ollama");
        var sut = Sut(primary, "enforce", [(Opt("ollama", "qwen"), secondary)]);

        var response = await sut.GetResponseAsync(Prompt);

        Assert.Equal("from-ollama", response.Text);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, secondary.Calls);
        var meta = response.AdditionalProperties!;
        Assert.Equal(true, meta["fallbackTriggered"]);
        Assert.Equal("primary", meta["originalProvider"]);
        Assert.Equal("ollama", meta["fallbackProvider"]);
        Assert.Equal("HttpError_429", meta["fallbackReason"]);
        Assert.Equal(2, meta["attemptNumber"]);
    }

    // AC-2: Observe → transient failure rethrows; no alternate is called.
    [Fact]
    public async Task Observe_RethrowsOriginal()
    {
        var primary = new FakeChatClient(new ChatProviderException("limited", 429));
        var secondary = new FakeChatClient();
        var sut = Sut(primary, "observe", [(Opt(), secondary)]);
        await Assert.ThrowsAsync<ChatProviderException>(() => sut.GetResponseAsync(Prompt));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, secondary.Calls);
    }

    // AC-3: 401 permanent → immediate rethrow, alternate untouched.
    [Fact]
    public async Task Permanent401_NoFallback()
    {
        var primary = new FakeChatClient(new ChatProviderException("unauthorized", 401));
        var secondary = new FakeChatClient();
        var sut = Sut(primary, "enforce", [(Opt(), secondary)]);
        var ex = await Assert.ThrowsAsync<ChatProviderException>(() => sut.GetResponseAsync(Prompt));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(0, secondary.Calls);
    }

    // AC-4: caller cancellation aborts before any alternate runs.
    [Fact]
    public async Task Cancellation_AbortsFallback()
    {
        using var cts = new CancellationTokenSource();
        var primary = new FakeChatClient(new ChatProviderException("limited", 429));
        var secondary = new FakeChatClient();
        var sut = Sut(primary, "enforce", [(Opt(), secondary)]);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.GetResponseAsync(Prompt, cancellationToken: cts.Token));
        Assert.Equal(0, secondary.Calls);
    }

    // Edge: all providers fail → AggregateException with the attempt history.
    [Fact]
    public async Task AllProvidersFail_Aggregates()
    {
        var primary = new FakeChatClient(new ChatProviderException("down", 503));
        var secondary = new FakeChatClient(new ChatProviderException("down", 503));
        var tertiary = new FakeChatClient(new ChatProviderException("down", 503));
        var sut = Sut(primary, "enforce",
            [(Opt("ollama", "a"), secondary), (Opt("openai", "b"), tertiary)]);

        var ex = await Assert.ThrowsAsync<AggregateException>(() => sut.GetResponseAsync(Prompt));
        Assert.Equal(3, ex.InnerExceptions.Count);
    }

    // Edge: unbuildable alternate (null from builder) is skipped → tertiary wins.
    [Fact]
    public async Task UnbuildableAlternate_IsSkipped()
    {
        var primary = new FakeChatClient(new ChatProviderException("limited", 429));
        var tertiary = new FakeChatClient(reply: "tertiary");
        var sut = Sut(primary, "enforce",
            [(Opt("ollama", "broken"), null), (Opt("openai", "works"), tertiary)]);
        var response = await sut.GetResponseAsync(Prompt);
        Assert.Equal("tertiary", response.Text);
    }

    // Single failure with no alternates → original exception rethrown intact.
    [Fact]
    public async Task SingleFailure_RethrowsUnwrapped()
    {
        var primary = new FakeChatClient(new ChatProviderException("down", 503));
        var sut = Sut(primary, "enforce", []);
        var ex = await Assert.ThrowsAsync<ChatProviderException>(() => sut.GetResponseAsync(Prompt));
        Assert.Equal(503, ex.StatusCode);
    }

    // Budget: MaxFallbackAttempts=1 → only one alternate tried.
    [Fact]
    public async Task BudgetCapsAlternates()
    {
        var policy = Policy("enforce", max: 1);
        var primary = new FakeChatClient(new ChatProviderException("limited", 429));
        var second = new FakeChatClient(new ChatProviderException("down", 503));
        var third = new FakeChatClient(reply: "never");
        var opt2 = Opt("ollama", "a");
        var opt3 = Opt("openai", "b");
        var map = new Dictionary<ChatProviderOptions, IChatClient?> { [opt2] = second, [opt3] = third };
        var sut = new ResilientChatClient(primary, "primary", [opt2, opt3],
            o => map[o], policy, NullLogger<ResilientChatClient>.Instance);

        await Assert.ThrowsAsync<AggregateException>(() => sut.GetResponseAsync(Prompt));
        Assert.Equal(1, second.Calls);
        Assert.Equal(0, third.Calls);
    }
}
