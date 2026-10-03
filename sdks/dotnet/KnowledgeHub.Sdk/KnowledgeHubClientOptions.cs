namespace KnowledgeHub.Sdk;

/// <summary>Options for <see cref="KnowledgeHubClient"/>.</summary>
public sealed class KnowledgeHubClientOptions
{
    /// <summary>Hub base URL, e.g. <c>http://localhost:5009</c> or <c>https://rag.example.com</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>The <c>aft_*</c> API key sent as a Bearer token on MCP and REST calls.</summary>
    public required string ApiKey { get; init; }

    /// <summary>MCP endpoint path relative to <see cref="BaseUrl"/>. Default <c>/mcp</c>.</summary>
    public string McpPath { get; init; } = "/mcp";

    /// <summary>Client name reported in the MCP initialize handshake.</summary>
    public string ClientName { get; init; } = "knowledgehub-dotnet-sdk";

    /// <summary>Timeout for a single MCP call. Default 5 minutes — task-eligible
    /// tools (firecrawl/tavily) may legitimately run for minutes when the client
    /// does not negotiate the tasks extension.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
