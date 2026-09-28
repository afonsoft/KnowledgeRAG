using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Evaluation;
using KnowledgeHub.Server.Services;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Server;

/// <summary>SPEC-20260914-llm-answer-synthesis RF-002: prompt, citations, fallbacks.</summary>
public sealed class AnswerServiceTests
{
    private static readonly ChatProviderOptions Options = new() { Provider = "ollama", Model = "m" };

    /// <summary>Answer cache disabled by default — behavior identical to pre-SPEC-8.</summary>
    private static AnswerService Svc(IChatClient? client, IConfiguration? config = null, IDistributedCache? cache = null) =>
        new(client, Options,
            cache ?? new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())),
            config ?? new ConfigurationBuilder().Build(),
            NullLogger<AnswerService>.Instance,
            NoopRagEvaluationEnqueuer.Instance);

    private static SearchResultItem Hit(string text = "ctx", string title = "Doc", int n = 1, SourceType type = SourceType.WebPage) =>
        new()
        {
            ChunkText = text,
            DocumentTitle = title,
            SourceName = "src",
            SourceId = Guid.NewGuid(),
            SourceType = type,
            Score = 0.9,
            UriReference = $"uri-{n}"
        };

    [Fact]
    public async Task EmptyContext_DeclaresNoMatches_WithoutCallingLlm()
    {
        var svc = Svc(new StubChatClient("should not be used"));
        var result = await svc.AnswerAsync("q?", []);

        Assert.Contains("no matching content", result.Answer);
        Assert.Empty(result.Citations);
        Assert.True(result.Generated);
        Assert.Null(result.Model);
    }

    [Fact]
    public async Task NoClient_ThrowsChatProviderException()
    {
        var svc = Svc(null);
        Assert.False(svc.IsConfigured);
        await Assert.ThrowsAsync<ChatProviderException>(() => svc.AnswerAsync("q?", [Hit()]));
    }

    [Fact]
    public async Task Answer_MapsValidCitationMarkers_ToRealHits()
    {
        var svc = Svc(new StubChatClient("Answer cites [1] and out-of-range [9]."));
        var context = new[] { Hit(title: "Alpha", n: 1), Hit(title: "Beta", n: 2) };
        var result = await svc.AnswerAsync("q?", context);

        Assert.True(result.Generated);
        Assert.Equal("stub-model", result.Model);
        var c = Assert.Single(result.Citations);
        Assert.Equal(1, c.Index);
        Assert.Equal("Alpha", c.Title);
        Assert.Equal("uri-1", c.Uri);
    }

    [Fact]
    public async Task Answer_VaultCitation_ExposesFilePath()
    {
        // Covers SPEC-20260922-tool-descriptions-en-us RF-003: vault citations carry
        // the vault-relative path accepted by read_document.
        var svc = Svc(new StubChatClient("Answer [1]."));
        var hit = Hit(n: 1, type: SourceType.ObsidianVault) with { UriReference = "folder/note.md" };
        var result = await svc.AnswerAsync("q?", [hit]);

        var c = Assert.Single(result.Citations);
        Assert.Equal("folder/note.md", c.Path);
        Assert.Equal("folder/note.md", c.Uri);
    }

    [Fact]
    public async Task Answer_NonVaultCitation_PathIsNull()
    {
        // Covers RF-003 edge case: non-file-backed sources expose no path.
        var svc = Svc(new StubChatClient("Answer [1]."));
        var result = await svc.AnswerAsync("q?", [Hit(n: 1, type: SourceType.WebPage)]);

        var c = Assert.Single(result.Citations);
        Assert.Null(c.Path);
    }

    [Fact]
    public async Task Answer_WithoutMarkers_AttachesAllContextAsCitations()
    {
        var svc = Svc(new StubChatClient("plain answer, no markers"));
        var result = await svc.AnswerAsync("q?", [Hit(n: 1), Hit(n: 2)]);
        Assert.Equal(2, result.Citations.Count);
    }

    [Fact]
    public async Task EmptyAnswer_ThrowsChatProviderException()
    {
        var svc = Svc(new StubChatClient("   "));
        await Assert.ThrowsAsync<ChatProviderException>(() => svc.AnswerAsync("q?", [Hit()]));
    }

    [Fact]
    public async Task AnswerCache_SecondIdenticalAsk_ServesFromCache()
    {
        // SPEC-20260923-agent-runtime-hardening RF-003 AC: repeated ask → 1 LLM call.
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Cache:AnswerCache:Enabled"] = "true" }).Build();
        var client = new CountingChatClient("cached answer [1]");
        var svc = Svc(client, config, cache);
        var ctx = new[] { Hit(n: 1) with { ChunkId = Guid.NewGuid() } };

        var first = await svc.AnswerAsync("same q?", ctx);
        var second = await svc.AnswerAsync("same q?", ctx);

        Assert.Equal(1, client.Calls);
        Assert.False(first.Cached);
        Assert.True(second.Cached);
        Assert.Equal(first.Answer, second.Answer);
    }

    [Fact]
    public async Task AnswerCache_Disabled_CallsLlmEveryTime()
    {
        var client = new CountingChatClient("answer [1]");
        var svc = Svc(client); // cache disabled by default
        var ctx = new[] { Hit(n: 1) with { ChunkId = Guid.NewGuid() } };

        await svc.AnswerAsync("q?", ctx);
        var again = await svc.AnswerAsync("q?", ctx);

        Assert.Equal(2, client.Calls);
        Assert.False(again.Cached);
    }

    [Fact]
    public void BuildUserPrompt_NumbersContext_AndAppendsQuestion()
    {
        var prompt = AnswerService.BuildUserPrompt("what?", [Hit(text: "body", title: "T", n: 3)]);
        Assert.Contains("[1] T — src (uri-3)", prompt);
        Assert.Contains("body", prompt);
        Assert.EndsWith("Question: what?", prompt);
    }

    [Fact]
    public void ChatClientFactory_NoneAndUnknown_ReturnNull()
    {
        var httpFactory = new StubHttpClientFactory();
        Assert.Null(ChatClientFactory.Create(new ChatProviderOptions { Provider = "none" }, httpFactory));
        Assert.Null(ChatClientFactory.Create(new ChatProviderOptions { Provider = "weird" }, httpFactory));
    }

    [Fact]
    public void ChatClientFactory_Ollama_ReturnsClient()
    {
        var client = ChatClientFactory.Create(
            new ChatProviderOptions { Provider = "ollama", Endpoint = "http://localhost:11434", Model = "llama3" },
            new StubHttpClientFactory());
        Assert.IsType<OllamaChatClient>(client);
    }

    private sealed class CountingChatClient(string text) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "stub-model" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "stub-model" });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NoopRagEvaluationEnqueuer : IRagEvaluationEnqueuer
    {
        public static readonly NoopRagEvaluationEnqueuer Instance = new();
        public System.Threading.Channels.ChannelReader<KnowledgeHub.Server.Evaluation.RagEvaluationTask> Reader { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<KnowledgeHub.Server.Evaluation.RagEvaluationTask>().Reader;
        public bool TryEnqueue(string queryId, string question, IReadOnlyList<string> contextChunks, string answer) => false;
    }
}
