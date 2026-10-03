using A2A;

namespace KnowledgeHub.Server.A2A;

/// <summary>
/// Audit 2026-10-03: single source for the delegable A2A skills. Previously
/// the Agent Card (<see cref="A2AEndpointExtensions.BuildAgentCard"/>) and
/// <see cref="KnowledgeHubA2AAgent.DelegableSkills"/> kept separate hard-coded
/// lists — a drift between them would advertise a skill the dispatcher
/// rejects (or hide one it accepts).
/// </summary>
public static class A2aSkillCatalog
{
    private const string MimeText = "text/plain";
    private const string MimeJson = "application/json";

    /// <summary>Skills this agent advertises and accepts for delegation.</summary>
    public static IReadOnlyList<AgentSkill> Skills { get; } =
    [
        new AgentSkill
        {
            Id = "ask_knowledge",
            Name = "Ask knowledge",
            Description = "Grounded Q&A over the indexed knowledge base — returns a synthesized answer with [n] citations.",
            Tags = ["rag", "qa", "knowledge"],
            Examples = ["What changed in the last release?"],
            InputModes = [MimeText, MimeJson],
            OutputModes = [MimeText, MimeJson]
        },
        new AgentSkill
        {
            Id = "search_knowledge",
            Name = "Search knowledge",
            Description = "Hybrid semantic + lexical search across all active sources — ranked passages with provenance.",
            Tags = ["search", "retrieval"],
            Examples = ["rate limiting policy"],
            InputModes = [MimeText, MimeJson],
            OutputModes = [MimeText, MimeJson]
        },
        new AgentSkill
        {
            Id = "agent_chat",
            Name = "Agent chat",
            Description = "Multi-turn agentic loop with tool-calling over the live catalog.",
            Tags = ["agent", "chat", "tools"],
            Examples = ["Summarize today's ingestion run"],
            InputModes = [MimeText],
            OutputModes = [MimeText, MimeJson]
        },
        new AgentSkill
        {
            Id = "read_document",
            Name = "Read document",
            Description = "Reads a full markdown document from a connected vault by path — input is a JSON object with a `path` field.",
            Tags = ["docs", "read"],
            Examples = ["roadmap/2026.md"],
            InputModes = [MimeJson],
            OutputModes = [MimeText]
        },
        new AgentSkill
        {
            Id = "write_knowledge",
            Name = "Write knowledge",
            Description = "Creates a document in the connected vault — input is a JSON object with `title` and `content` (optional `source`, `tags`). Requires a write-capable credential.",
            Tags = ["docs", "write", "knowledge"],
            Examples = ["{\"title\": \"Runbook\", \"content\": \"Restart steps…\"}"],
            InputModes = [MimeJson],
            OutputModes = [MimeText]
        },
        new AgentSkill
        {
            Id = "write_note",
            Name = "Write note",
            Description = "Writes a markdown note into the Obsidian vault — input is a JSON object with `title` and `content` (optional `path`, `tags`). Requires a write-capable credential.",
            Tags = ["docs", "write", "obsidian"],
            Examples = ["{\"title\": \"Daily log\", \"content\": \"…\"}"],
            InputModes = [MimeJson],
            OutputModes = [MimeText]
        }
    ];

    /// <summary>Ids accepted by the dispatcher (advertised skills ⊆ delegable).</summary>
    public static IReadOnlySet<string> DelegableIds { get; } =
        Skills.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
