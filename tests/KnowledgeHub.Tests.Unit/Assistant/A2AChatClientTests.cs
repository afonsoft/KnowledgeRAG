using System.Net;
using System.Text;
using KnowledgeHub.Server.Assistant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnowledgeHub.Tests.Unit.Assistant;

/// <summary>
/// SPEC-20260929-a2a-assistant-delegation RF-003: A2AChatClient maps
/// IChatClient calls onto A2A message/send — Message results and Task
/// artifacts/status both surface as plain assistant text.
/// </summary>
public sealed class A2AChatClientTests
{
    private sealed class StubRpc(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
    }

    private static A2AChatClient Sut(string resultJson) => new(
        new A2A.A2AClient(new Uri("http://a2a.test"), new HttpClient(new StubRpc(
            $$"""{"jsonrpc":"2.0","id":1,"result":{{resultJson}}}"""))), "agent_chat");

    [Fact]
    public async Task GetResponseAsync_MessageResult_ReturnsText()
    {
        var sut = Sut(
            """{"message":{"role":"ROLE_AGENT","parts":[{"text":"hello agent"}],"messageId":"r1"}}""");
        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("hello agent", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_TaskResult_PrefersArtifactText()
    {
        var sut = Sut(
            """{"task":{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_COMPLETED"},"artifacts":[{"artifactId":"a1","parts":[{"text":"artifact answer"}]}]}}""");
        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("artifact answer", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_TaskWithoutArtifact_FallsBackToStatusMessage()
    {
        var sut = Sut(
            """{"task":{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_COMPLETED","message":{"role":"ROLE_AGENT","parts":[{"text":"status text"}],"messageId":"m"}}}}""");
        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("status text", response.Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_YieldsSingleUpdate()
    {
        var sut = Sut(
            """{"message":{"role":"ROLE_AGENT","parts":[{"text":"streamed"}],"messageId":"r1"}}""");
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            updates.Add(u);
        Assert.Single(updates);
        Assert.Contains("streamed", updates[0].Text);
    }

    [Fact]
    public async Task GetService_ReturnsSelf()
    {
        var sut = Sut(
            """{"message":{"role":"ROLE_AGENT","parts":[{"text":"x"}],"messageId":"r"}}""");
        Assert.Same(sut, sut.GetService(typeof(A2AChatClient)));
        Assert.Null(sut.GetService(typeof(string)));
        await Task.CompletedTask;
    }
}

/// <summary>RF-004: timeout path and pass-through members of the fallback decorator.</summary>
public sealed class AssistantFallbackChatClientTests
{
    private sealed class HangsChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "never"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(Marker) ? new Marker() : null;
        public void Dispose() { }
        public sealed class Marker;
    }

    [Fact]
    public async Task Timeout_FallsBackToMain()
    {
        var sut = new AssistantFallbackChatClient(
            new HangsChatClient(), new StubChatClient("main-ok"), "rewrite",
            TimeSpan.FromMilliseconds(50), NullLogger.Instance);
        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
        Assert.Equal("main-ok", response.Text);
    }

    [Fact]
    public async Task Streaming_DelegatesToMain()
    {
        var sut = new AssistantFallbackChatClient(
            new HangsChatClient(), new StubChatClient("main-stream"), "grade",
            TimeSpan.FromSeconds(5), NullLogger.Instance);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")]))
            updates.Add(u);
        Assert.Contains("main-stream", updates[0].Text);
    }

    [Fact]
    public async Task GetService_DelegatesToMain()
    {
        var sut = new AssistantFallbackChatClient(
            new HangsChatClient(), new StubChatClient("x"), "grade",
            TimeSpan.FromSeconds(5), NullLogger.Instance);
        Assert.Same(sut, sut.GetService(typeof(AssistantFallbackChatClient)));
        Assert.IsType<StubChatClient.Marker>(sut.GetService(typeof(StubChatClient.Marker)));
        await Task.CompletedTask;
    }
}
