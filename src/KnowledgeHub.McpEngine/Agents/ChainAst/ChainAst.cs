using Microsoft.Extensions.AI;

namespace KnowledgeHub.McpEngine.Agents.ChainAst;

/// <summary>
/// Chain AST — structured view of a flat <see cref="ChatMessage"/> history
/// (SPEC-20260927-chain-ast-thread-compactor RF-001). A
/// <see cref="ChainSection"/> bundles control/header messages (system prompt,
/// a user turn) with the <see cref="BodyPair"/>s that follow — each pairing an
/// assistant message with every tool-response message answering its
/// <see cref="FunctionCallContent"/>s.
/// </summary>
public enum BodyPairType
{
    /// <summary>Assistant message carrying tool calls + their responses.</summary>
    RequestResponse,
    /// <summary>Assistant message with no tool calls (final answer / plain turn).</summary>
    Completion,
    /// <summary>Synthetic condensed summary produced by the compactor.</summary>
    SummarizedSection
}

/// <summary>One tool call and its (possibly pending) response.</summary>
public sealed class ToolCallPair
{
    public required FunctionCallContent Call { get; init; }
    public FunctionResultContent? Result { get; set; }
}

/// <summary>A cohesive assistant turn: the AI message plus every
/// Tool-role message that resolves its calls.</summary>
public sealed class BodyPair
{
    public required ChatMessage AiMessage { get; set; }

    /// <summary>Tool-role messages paired to this AI message's call ids.</summary>
    public List<ChatMessage> ToolMessages { get; } = [];

    /// <summary>Call ↔ response correlation kept per call for repair.</summary>
    public List<ToolCallPair> Calls { get; } = [];

    public required BodyPairType Type { get; set; }

    /// <summary>True when the AI message carries thinking/reasoning content
    /// (RF-004 — preserved across compaction, signature-protected).</summary>
    public bool HasReasoning =>
        AiMessage.Contents.Any(c => c is TextReasoningContent);
}

/// <summary>Header messages (system/user control turn) + the body pairs
/// answering them.</summary>
public sealed class ChainSection
{
    public List<ChatMessage> Headers { get; } = [];
    public List<BodyPair> Body { get; } = [];
}

/// <summary>The parsed conversation tree; <see cref="ToChatMessages"/>
/// flattens back to provider order — Header, AI, Tool, AI, …</summary>
public sealed class ChainAST
{
    public List<ChainSection> Sections { get; } = [];

    /// <summary>Tool-role messages whose CallId matched no assistant call —
    /// kept for inspection/telemetry; excluded from <see cref="ToChatMessages"/>
    /// so the flattened list is always provider-safe (RF-002).</summary>
    public List<ChatMessage> Orphans { get; set; } = [];

    public List<ChatMessage> ToChatMessages()
    {
        var flat = new List<ChatMessage>();
        foreach (var s in Sections)
        {
            flat.AddRange(s.Headers);
            foreach (var p in s.Body)
            {
                flat.Add(p.AiMessage);
                flat.AddRange(p.ToolMessages);
            }
        }
        return flat;
    }

    /// <summary>Byte estimate over text-bearing content (UTF-8).</summary>
    public int EstimateBytes()
    {
        return ToChatMessages()
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Sum(t => t.Text is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(t.Text));
    }
}
