using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// A dynamically-generated MCP tool: protocol metadata plus its invoke handler.
/// Built per <c>tools/list</c> request so the catalog always mirrors live DB state
/// (SPEC-04 RF-001).
/// </summary>
public sealed record CatalogTool
{
    public required string Name { get; init; }
    /// <summary>Mutable: the catalog appends deployment instructions
    /// (<c>Mcp:Instructions</c>) on principal tools after build.</summary>
    public required string Description { get; set; }
    public required JsonObject InputSchema { get; init; }
    public bool ReadOnly { get; init; }
    /// <summary>SPEC-20260926-mcp-sdk-alignment RF-001: human-readable display
    /// name surfaced as <c>Tool.Title</c>/<c>annotations.title</c>.</summary>
    public string? Title { get; init; }
    /// <summary>RF-001: remaining <c>ToolAnnotations</c> hints (null = omitted).</summary>
    public bool? DestructiveHint { get; init; }
    public bool? IdempotentHint { get; init; }
    /// <summary>RF-001: the tool reaches out to the open world (upstream MCP,
    /// HTTP crawl, external API).</summary>
    public bool? OpenWorldHint { get; init; }
    /// <summary>RF-002: JSON Schema of <c>structuredContent</c> when the tool
    /// emits it.</summary>
    public JsonObject? OutputSchema { get; init; }
    /// <summary>SPEC-20260923-source-authorization RF-004: owning source for
    /// per-source tools (<c>query_*</c>, MCP-proxy re-exports) — null for
    /// source-independent tools. Drives per-key source scoping.</summary>
    public Guid? SourceId { get; init; }
    public required Func<ToolCallContext, CancellationToken, ValueTask<CallToolResult>> Handler { get; init; }
}
