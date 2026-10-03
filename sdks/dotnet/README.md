# KnowledgeHub.Sdk (.NET)

Official .NET client SDK for the [Knowledge MCP Hub](https://github.com/afonsoft/KnowledgeRAG). Wraps the MCP server (Streamable HTTP) with a typed facade for the stable tools and exposes the live tool catalog as `AIFunction`s for `Microsoft.Extensions.AI` / Semantic Kernel — LangChain-style plug-and-play for .NET agents.

## Install

```bash
dotnet add package KnowledgeHub.Sdk
```

## Usage

```csharp
using KnowledgeHub.Sdk;
using Microsoft.Extensions.AI;

await using var kh = await KnowledgeHubClient.ConnectAsync(
    "http://localhost:5009", apiKey: "aft_...");

// Typed facade — stable tools
var answer = await kh.AskAsync("What is the retry policy?");
foreach (var c in answer.Citations)
    Console.WriteLine($"[{c.Index}] {c.Title} — {c.Path}");

var hits = await kh.SearchAsync("deployment steps", topK: 5);
var agent = await kh.AgentChatAsync("Summarize the onboarding doc");
if (agent.AwaitingApprovalId is { } id)
    Console.WriteLine($"paused for HITL approval: {agent.PendingTool}");

// LLM integration — plug the hub's whole tool catalog into any IChatClient
IReadOnlyList<AIFunction> tools = await kh.AsAIToolsAsync();
var response = await chatClient.GetResponseAsync(messages,
    new ChatOptions { Tools = [.. tools] });

// Raw catalog + calls (dynamic per key scope / registered sources)
var tools = await kh.ListToolsAsync();
var raw = await kh.CallToolAsync("find_dependencies",
    new Dictionary<string, object?> { ["component"] = "MyService" });

// Streaming (token-by-token + tool events)
await foreach (var ev in kh.StreamAskAsync("..."))
    if (ev.Type == "token") Console.Write(ev.Data.GetProperty("text").GetString());
await foreach (var ev in kh.StreamAgentAsync("...")) { /* meta/token/tool_start/... */ }
```

## Notes

- **Auth**: every call sends `Authorization: Bearer aft_*`. Create keys in the hub UI (`/api-keys`) — the tool catalog you see is scoped by the key.
- **Dynamic catalog**: tools like `read_document`/`write_note` only exist when an Obsidian source is registered; upstream tools (firecrawl/tavily/context7) depend on configured integrations. `ListToolsAsync` is the source of truth.
- **Errors**: JSON-RPC tool errors and `isError` results surface as `KnowledgeHubToolException`.
- **Targets**: net8.0. Deps: `ModelContextProtocol` 2.2.x + `Microsoft.Extensions.AI.Abstractions`.
