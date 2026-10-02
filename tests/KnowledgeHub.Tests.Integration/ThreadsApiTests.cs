using System.Net;
using System.Net.Http.Json;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260914-conversation-threads: threadId carry-over, CRUD, window/summary.
public class ThreadsApiTests : IClassFixture<ThreadsApiTests.Fixture>, IClassFixture<ThreadsApiTests.TinyWindowFixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-threads-{Guid.NewGuid():N}.db");
        public AgentApiTests.ScriptedChatClient Chat { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(Chat));
        }
    }

    /// <summary>Same fixture with a tiny context window so history overflows fast.</summary>
    public sealed class TinyWindowFixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-threads-tiny-{Guid.NewGuid():N}.db");
        public AgentApiTests.ScriptedChatClient Chat { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["Agent:MaxContextTokens"] = "30" // ~120 chars — everything overflows quickly
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IChatClient>(Chat));
        }
    }

    private readonly Fixture _factory;
    private readonly TinyWindowFixture _tiny;
    private readonly HttpClient _client;

    public ThreadsApiTests(Fixture factory, TinyWindowFixture tiny)
    {
        _factory = factory;
        _tiny = tiny;
        _client = TestAuth.Login(factory);
    }

    [Fact]
    public async Task ThreadId_PreservesContextBetweenCalls()
    {
        var first = await _client.PostAsJsonAsync("/api/agent", new
        {
            prompt = "first question about mars",
            persist = true
        });
        var r1 = (await first.Content.ReadFromJsonAsync<AgentResponse>())!;
        Assert.NotNull(r1.ThreadId);

        var before = _factory.Chat.MessagesSeen.Count;
        var second = await _client.PostAsJsonAsync("/api/agent", new
        {
            prompt = "follow up",
            threadId = r1.ThreadId
        });
        second.EnsureSuccessStatusCode();

        // The second run's context must replay the prior turn.
        var seen = _factory.Chat.MessagesSeen.Skip(before).SelectMany(m => m).ToList();
        Assert.Contains(seen, m => m.Contains("first question about mars"));
        Assert.Contains(seen, m => m.Contains("agent final answer"));
    }

    [Fact]
    public async Task Threads_Crud_Works()
    {
        var create = await _client.PostAsJsonAsync("/api/threads", new { title = "t1" });
        create.EnsureSuccessStatusCode();
        var thread = (await create.Content.ReadFromJsonAsync<ThreadDto>())!;

        var detail = await _client.GetFromJsonAsync<ThreadDetailDto>($"/api/threads/{thread.Id}");
        Assert.Equal("t1", detail!.Thread.Title);

        var rename = await _client.PutAsJsonAsync($"/api/threads/{thread.Id}", new { title = "renamed" });
        rename.EnsureSuccessStatusCode();

        var delete = await _client.DeleteAsync($"/api/threads/{thread.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var missing = await _client.GetAsync($"/api/threads/{thread.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Threads_PostMessage_RunsAgentAndPersistsTurns()
    {
        var create = await _client.PostAsJsonAsync("/api/threads", new { title = "chat" });
        var thread = (await create.Content.ReadFromJsonAsync<ThreadDto>())!;

        var send = await _client.PostAsJsonAsync($"/api/threads/{thread.Id}/messages",
            new { content = "search then answer" });
        send.EnsureSuccessStatusCode();
        var result = (await send.Content.ReadFromJsonAsync<AgentResponse>())!;
        Assert.Equal("agent final answer", result.Answer);

        var detail = await _client.GetFromJsonAsync<ThreadDetailDto>($"/api/threads/{thread.Id}");
        var roles = detail!.Messages.Select(m => m.Role).ToList();
        Assert.Contains("user", roles);
        Assert.Contains("tool", roles);
        Assert.Contains("assistant", roles);
    }

    [Fact]
    public async Task Thread_LongHistory_PopulatesSummary()
    {
        var client = await TestAuth.LoginAsync(_tiny);
        var create = await client.PostAsJsonAsync("/api/threads", new { title = "long" });
        var thread = (await create.Content.ReadFromJsonAsync<ThreadDto>())!;

        // Two turns exceed the tiny window → older messages get summarized.
        await client.PostAsJsonAsync($"/api/threads/{thread.Id}/messages",
            new { content = new string('a', 200) });
        await client.PostAsJsonAsync($"/api/threads/{thread.Id}/messages",
            new { content = new string('b', 200) });

        // Summarization is fire-and-forget — poll until the summary lands.
        ThreadDetailDto? detail = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            detail = await client.GetFromJsonAsync<ThreadDetailDto>($"/api/threads/{thread.Id}");
            if (detail?.Summary is not null) break;
        }
        Assert.NotNull(detail?.Summary);
        Assert.Equal("summary text", detail!.Summary);
    }
}
