namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// SPEC-20261001-a2a-task-durability RF-004: ambient provenance for write
/// tools — which channel/caller produced a document. Registered scoped; the
/// A2A agent overrides the defaults for the request scope before delegating
/// to the catalog. Write tools stamp the values into document frontmatter
/// (<c>origin:</c> block) so the source of a write is visible in the file and
/// the admin UI.
/// </summary>
public sealed class WriteOriginContext
{
    /// <summary>Originating channel — <c>mcp</c> (tool calls incl. REST
    /// playground) or <c>a2a</c> (delegated agent calls).</summary>
    public string Channel { get; set; } = "mcp";
    /// <summary><c>aft_*</c> key id of the caller, when authenticated by key.</summary>
    public string? KeyId { get; set; }
    /// <summary>Caller agent name — A2A callers may advertise it via message
    /// metadata (<c>agentName</c>).</summary>
    public string? AgentName { get; set; }
}
