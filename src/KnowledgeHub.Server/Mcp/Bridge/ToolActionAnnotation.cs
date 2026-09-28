using System.Text.Json;

namespace KnowledgeHub.Server.Mcp.Bridge;

/// <summary>How a tool nomination was found.</summary>
public enum ToolActionOrigin
{
    /// <summary>Explicit <c>&lt;!-- mcp-tool: name k="v" --&gt;</c> marker in a chunk.</summary>
    Marker,
    /// <summary>The question text names the tool directly.</summary>
    Question
}

/// <summary>
/// A live-action nomination detected in retrieved context
/// (SPEC-20260927-mcp-dynamic-rag-action-bridge RF-001): tool name plus the
/// arguments recovered from the source (may be empty — the bridge synthesizes
/// defaults only when the schema allows it).
/// </summary>
public sealed record ToolActionAnnotation(
    string ToolName,
    IReadOnlyDictionary<string, JsonElement> Args,
    ToolActionOrigin Origin,
    Guid? ChunkId);
