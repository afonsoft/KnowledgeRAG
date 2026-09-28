using Microsoft.Extensions.AI;

namespace KnowledgeHub.McpEngine.Agents.ChainAst;

/// <summary>Compaction policy (<c>Agent:ContextManagement</c>).</summary>
public sealed class ChainCompactionOptions
{
    public bool EnableChainCompaction { get; set; } = true;
    public int MaxTotalHistoryBytes { get; set; } = 64 * 1024;
    public int MaxBodyPairBytes { get; set; } = 16 * 1024;
    public int KeepMinLastSections { get; set; } = 2;
    public bool AutoRepairBrokenToolCalls { get; set; } = true;
}

/// <summary>
/// Reduces a parsed conversation tree so it fits the LLM context window
/// (RF-003): oversized tool outputs are truncated first, then the oldest
/// sections collapse into a summarized block — the last
/// <see cref="ChainCompactionOptions.KeepMinLastSections"/> sections are
/// never touched.
/// </summary>
public interface IChainCompactor
{
    Task<ChainAST> CompactAsync(ChainAST ast, CancellationToken ct = default);
}
