using System.Text;
using System.Text.Json;
using KnowledgeHub.Sdk;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Sdk;

/// <summary>Unit tests for the client SDK's wire parsing — SSE frames, the
/// {"seq","data"} envelope and typed structuredContent deserialization.</summary>
public class SseReaderTests
{
    [Fact]
    public async Task ReadAsync_ParsesEventAndData()
    {
        const string body = "event: meta\ndata: {\"seq\":1,\"data\":{\"grade\":\"weak\"}}\n\n" +
                            "event: token\ndata: {\"seq\":2,\"data\":{\"text\":\"hello\"}}\n\n";
        var events = await Read(body);
        Assert.Equal(2, events.Count);
        Assert.Equal("meta", events[0].Event);
        Assert.Equal("weak", events[0].Data.GetProperty("data").GetProperty("grade").GetString());
        Assert.Equal("token", events[1].Event);
    }

    [Fact]
    public async Task ReadAsync_SkipsHeartbeatComments()
    {
        const string body = ": keep-alive\n\nevent: done\ndata: {\"seq\":1,\"data\":{}}\n\n";
        var events = await Read(body);
        Assert.Single(events);
        Assert.Equal("done", events[0].Event);
    }

    [Fact]
    public async Task ReadAsync_HandlesMultiLineData()
    {
        const string body = "event: token\ndata: {\"a\":1\ndata: ,\"b\":2}\n\n";
        var events = await Read(body);
        Assert.Single(events);
        Assert.Equal(1, events[0].Data.GetProperty("a").GetInt32());
        Assert.Equal(2, events[0].Data.GetProperty("b").GetInt32());
    }

    [Fact]
    public async Task ReadAsync_DefaultsToMessageEventType()
    {
        const string body = "data: {\"x\":1}\n\n";
        var events = await Read(body);
        Assert.Single(events);
        Assert.Equal("message", events[0].Event);
    }

    private static async Task<List<(string Event, JsonElement Data)>> Read(string body)
    {
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var list = new List<(string, JsonElement)>();
        await foreach (var e in SseReader.ReadAsync(ms, CancellationToken.None))
            list.Add(e);
        return list;
    }
}

public class HubModelDeserializationTests
{
    [Fact]
    public void SearchStructuredContent_Deserializes()
    {
        const string json = """
        {
          "results": [{
            "chunkText": "some chunk", "documentTitle": "Doc", "sourceName": "vault",
            "sourceId": "11111111-1111-1111-1111-111111111111", "score": 0.9,
            "uriReference": "obsidian://doc", "sectionPath": "A/B",
            "components": ["Entity"], "isRelaxed": false
          }],
          "grade": "sufficient", "retried": false, "totalMatches": 1,
          "limitModeApplied": "fixed", "truncatedByTokens": false, "filterRelaxed": false,
          "warnings": null
        }
        """;
        var result = JsonDocument.Parse(json).RootElement.Deserialize<HubSearchResult>(HubJson.Options)!;
        Assert.Single(result.Results);
        Assert.Equal("some chunk", result.Results[0].ChunkText);
        Assert.Equal(["Entity"], result.Results[0].Components);
        Assert.Equal("sufficient", result.Grade);
        Assert.Equal(1, result.TotalMatches);
    }

    [Fact]
    public void AskStructuredContent_Deserializes()
    {
        const string json = """
        {
          "answer": "The answer [1]", "citations": [{
            "index": 1, "source": "vault", "title": "Doc", "uri": "obsidian://doc",
            "path": "notes/doc.md", "score": 0.8
          }],
          "latencyMs": 123.4, "model": "gpt-x", "generated": true,
          "insufficientEvidence": false, "retrievalGrade": "sufficient",
          "retried": false, "cached": false, "truncatedByTokens": false
        }
        """;
        var result = JsonDocument.Parse(json).RootElement.Deserialize<HubAskAnswer>(HubJson.Options)!;
        Assert.Equal("The answer [1]", result.Answer);
        Assert.Single(result.Citations);
        Assert.Equal("notes/doc.md", result.Citations[0].Path);
        Assert.True(result.Generated);
        Assert.Equal("sufficient", result.RetrievalGrade);
    }

    [Fact]
    public void AgentResult_Deserializes()
    {
        const string json = """
        {
          "answer": "done", "steps": [{"iteration":1,"tool":"search_knowledge","argsSummary":"q","isError":false,"elapsedMs":10}],
          "toolCalls": ["search_knowledge"], "iterations": 1, "latencyMs": 50,
          "limitReached": false, "awaitingApprovalId": "22222222-2222-2222-2222-222222222222",
          "pendingTool": "write_note", "threadId": null
        }
        """;
        var result = JsonDocument.Parse(json).RootElement.Deserialize<HubAgentResult>(HubJson.Options)!;
        Assert.Single(result.Steps);
        Assert.NotNull(result.AwaitingApprovalId);
        Assert.Equal("write_note", result.PendingTool);
        Assert.Equal(["search_knowledge"], result.ToolCalls);
    }

    [Fact]
    public void HubToolResult_ThrowIfError_RaisesWithText()
    {
        var err = new HubToolResult { Text = "agent run failed: no provider", IsError = true };
        var ex = Assert.Throws<KnowledgeHubToolException>(() => err.ThrowIfError());
        Assert.Contains("no provider", ex.Message);
    }
}
