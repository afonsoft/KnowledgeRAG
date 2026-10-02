using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeHub.Tests.Integration;

// SPEC-20260914-streaming-answers: SSE endpoints emit ordered token/tool/done events.
public class StreamingApiTests : IClassFixture<StreamingApiTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-stream-{Guid.NewGuid():N}.db");
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

    private readonly Fixture _factory;
    private readonly HttpClient _client;
    private readonly string _dir;

    public StreamingApiTests(Fixture factory)
    {
        _factory = factory;
        _client = TestAuth.Login(factory);
        _dir = Path.Join(Path.GetTempPath(), $"stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

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

    private async Task<string> PostSseAsync(string url, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").First());
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task AgentStream_EmitsToolAndDone_InOrder()
    {
        var body = await PostSseAsync("/api/agent/stream", new { prompt = "search then answer" });
        var events = ParseSse(body);

        var types = events.Select(e => e.Event).ToList();
        Assert.Contains("tool_start", types);
        Assert.Contains("tool_end", types);
        Assert.Equal("done", types[^1]);

        // sequence numbers are monotonic
        var seqs = events.Select(e => e.Data.GetProperty("seq").GetInt32()).ToList();
        Assert.Equal(seqs.Order().ToList(), seqs);

        var toolStart = events.First(e => e.Event == "tool_start");
        Assert.Equal("search_knowledge", toolStart.Data.GetProperty("data").GetProperty("tool").GetString());

        // done carries the same shape as the sync endpoint
        var done = events.Last().Data.GetProperty("data");
        Assert.Equal("agent final answer", done.GetProperty("answer").GetString());
        Assert.Equal(2, done.GetProperty("iterations").GetInt32());
        Assert.Single(done.GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public async Task AskStream_EmitsTokensAndDone()
    {
        // Seed a document source so the context is non-empty.
        var file = Path.Join(_dir, "seed.txt");
        await File.WriteAllTextAsync(file, "knowledge about streaming");
        var create = await _client.PostAsJsonAsync("/api/sources", new
        {
            name = $"stream-{Guid.NewGuid():N}",
            type = "DocumentFile",
            configuration = new { path = _dir },
            isActive = true
        });
        create.EnsureSuccessStatusCode();
        var source = await create.Content.ReadFromJsonAsync<KnowledgeHub.Shared.Contracts.KnowledgeSourceDto>();
        await _client.PostAsync($"/api/sources/{source!.Id}/sync?wait=true", null);

        var body = await PostSseAsync("/api/ask/stream", new { question = "what about streaming?" });
        var events = ParseSse(body);

        var types = events.Select(e => e.Event).ToList();
        Assert.Contains("token", types);
        Assert.Equal("done", types[^1]);

        var done = events.Last().Data.GetProperty("data");
        // ScriptedChatClient returns "agent final answer" — buffered pseudo-tokens
        // must reassemble into the same answer.
        var tokens = string.Concat(events.Where(e => e.Event == "token")
            .Select(e => e.Data.GetProperty("data").GetProperty("delta").GetString()));
        Assert.Equal(done.GetProperty("answer").GetString(), tokens);
        Assert.True(done.GetProperty("generated").GetBoolean());
    }

    [Fact]
    public async Task AgentStream_MissingPrompt_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/agent/stream", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
