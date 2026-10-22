using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20261021-agent-streaming-events RF-1/2/3/6: a streaming-capable provider
// emits reasoning/tool_call_chunk/usage as fine-grained SSE events.
public class RichStreamingApiTests : IClassFixture<RichStreamingApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-rich-{Guid.NewGuid():N}.db");
        public RichChatClient Chat { get; } = new();

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

    /// <summary>Streaming-capable scripted client: round 1 emits a reasoning
    /// delta + a tool call; round 2 emits text deltas + a usage update.</summary>
    public sealed class RichChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var hasToolResult = messages
                .SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
            if (!hasToolResult)
            {
                yield return new ChatResponseUpdate
                {
                    Contents = [new TextReasoningContent("thinking about the query")]
                };
                yield return new ChatResponseUpdate
                {
                    Contents = [new FunctionCallContent("call-1", "search_knowledge",
                        new Dictionary<string, object?> { ["query"] = "x" })]
                };
            }
            else
            {
                yield return new ChatResponseUpdate { Contents = [new TextContent("rich ")] };
                yield return new ChatResponseUpdate { Contents = [new TextContent("answer")] };
                yield return new ChatResponseUpdate
                {
                    ModelId = "rich-model",
                    Contents = [new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 20, OutputTokenCount = 7, TotalTokenCount = 27
                    })]
                };
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private readonly HttpClient _client;

    public RichStreamingApiTests(Fixture factory) => _client = TestAuth.Login(factory);

    private static List<(string Event, JsonElement Data)> ParseSse(string body)
    {
        var events = new List<(string, JsonElement)>();
        string? type = null;
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
                type = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal) && type is not null)
            {
                events.Add((type, JsonDocument.Parse(line[5..]).RootElement.Clone()));
                type = null;
            }
        }
        return events;
    }

    [Fact]
    public async Task AgentStream_EmitsReasoningToolCallChunkAndUsage()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/agent/stream")
        {
            Content = JsonContent.Create(new { prompt = "search then answer" })
        };
        using var response = await _client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var events = ParseSse(await response.Content.ReadAsStringAsync());
        var types = events.Select(e => e.Event).ToList();

        // RF-1: reasoning deltas surface as their own event.
        var reasoning = Assert.Single(events, e => e.Event == "reasoning");
        Assert.Equal("thinking about the query",
            reasoning.Data.GetProperty("data").GetProperty("delta").GetString());

        // RF-2: streamed function calls surface as tool_call_chunk.
        var chunk = Assert.Single(events, e => e.Event == "tool_call_chunk");
        Assert.Equal("search_knowledge",
            chunk.Data.GetProperty("data").GetProperty("name").GetString());
        Assert.Contains("query",
            chunk.Data.GetProperty("data").GetProperty("argsDelta").GetString());

        // RF-3: usage event + aggregate on done.
        var usage = Assert.Single(events, e => e.Event == "usage");
        Assert.Equal(20, usage.Data.GetProperty("data").GetProperty("inputTokens").GetInt32());
        Assert.Equal("rich-model", usage.Data.GetProperty("data").GetProperty("modelId").GetString());

        var done = events.Last();
        Assert.Equal("done", done.Event);
        Assert.Equal("rich answer", done.Data.GetProperty("data").GetProperty("answer").GetString());
        Assert.Equal(27, done.Data.GetProperty("data")
            .GetProperty("usage").GetProperty("totalTokens").GetInt32());

        // RF-5: classic events still present and ordered.
        Assert.True(types.IndexOf("tool_start") < types.IndexOf("tool_end"));
        Assert.Equal("done", types[^1]);
    }
}
