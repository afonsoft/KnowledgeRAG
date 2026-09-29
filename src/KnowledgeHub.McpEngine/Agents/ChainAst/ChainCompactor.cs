using System.Text;
using Microsoft.Extensions.AI;

namespace KnowledgeHub.McpEngine.Agents.ChainAst;

/// <summary>
/// Structural compactor (RF-003/RF-004):
/// <list type="number">
///   <item>Truncate <em>tool outputs</em> over <c>MaxBodyPairBytes</c> in place
///   (keeps the CallId — integrity preserved).</item>
///   <item>While still over budget, fold the oldest sections into a single
///   <see cref="BodyPairType.SummarizedSection"/> pair whose assistant text
///   starts with the standard <c>**summarized content:**</c> marker.</item>
///   <item>Reasoning content inside a folded section is dropped but replaced
///   with a synthetic <c>skip_thought_signature</c> block so providers that
///   require reasoning retention keep validating (RF-004).</item>
/// </list>
/// The active trailing sections (≥ <c>KeepMinLastSections</c>) are never
/// summarized or reordered.
/// </summary>
public sealed class ChainCompactor(ChainCompactionOptions options) : IChainCompactor
{
    public const string SummaryMarker = "**summarized content:**";
    public const string SkipThoughtSignature = "skip_thought_signature";

    public Task<ChainAST> CompactAsync(ChainAST ast, CancellationToken ct = default)
    {
        if (!options.EnableChainCompaction || ast.EstimateBytes() <= options.MaxTotalHistoryBytes)
            return Task.FromResult(ast);

        var keep = Math.Max(1, options.KeepMinLastSections);
        var protectedFrom = Math.Max(0, ast.Sections.Count - keep);

        // Pass 1 — truncate oversized tool outputs inside foldable sections.
        for (var i = 0; i < protectedFrom; i++)
            foreach (var pair in ast.Sections[i].Body)
                foreach (var msg in pair.ToolMessages)
                    TruncateToolResults(msg);

        // Pass 2 — fold oldest sections into one summarized block.
        var fold = protectedFrom;
        if (fold > 0)
        {
            var summary = BuildSummary(ast.Sections.Take(fold));
            var folded = new ChainSection
            {
                Body =
                {
                    new BodyPair
                    {
                        AiMessage = new ChatMessage(ChatRole.Assistant,
                            $"{SummaryMarker}\n{summary}"),
                        Type = BodyPairType.SummarizedSection
                    }
                }
            };
            // RF-004: reasoning was dropped with the folded sections — emit a
            // synthetic signature so providers that require thought retention
            // (Gemini/Claude thinking models) still accept the history.
            if (ast.Sections.Take(fold).SelectMany(s => s.Body).Any(p => p.HasReasoning))
                folded.Body[0].AiMessage.Contents.Insert(0,
                    new TextReasoningContent("") { ProtectedData = SkipThoughtSignature });
            ast.Sections.RemoveRange(0, fold);
            ast.Sections.Insert(0, folded);
        }

        return Task.FromResult(ast);
    }

    /// <summary>Cuts oversized text payloads inside tool-result contents —
    /// the CallId/function name are untouched so pairing survives.</summary>
    private void TruncateToolResults(ChatMessage toolMessage)
    {
        foreach (var result in toolMessage.Contents
            .OfType<FunctionResultContent>()
            .Where(r => r.Result is string))
        {
            var text = (string)result.Result!;
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes <= options.MaxBodyPairBytes)
                continue;
            // Byte-safe truncation (avoid splitting a surrogate/UTF-8 seq).
            var chars = Math.Max(0, options.MaxBodyPairBytes / 4);
            result.Result = text.Length <= chars
                ? text
                : text[..chars] + $"\n…[truncated {bytes}B→{options.MaxBodyPairBytes}B]";
        }
    }

    /// <summary>Condenses folded sections: user prompt, tool names used and
    /// the final assistant answer — bounded to keep the marker block small.</summary>
    private static string BuildSummary(IEnumerable<ChainSection> sections)
    {
        var sb = new StringBuilder();
        foreach (var s in sections)
        {
            var ask = s.Headers.LastOrDefault(h => h.Role == ChatRole.User)?.Text;
            var tools = s.Body.SelectMany(p => p.Calls)
                .Select(c => c.Call.Name).Where(n => n is not null).Distinct();
            var answer = s.Body.LastOrDefault()?.AiMessage.Text;

            if (!string.IsNullOrWhiteSpace(ask))
                sb.Append("- user asked: ").AppendLine(Trim(ask));
            var toolList = string.Join(", ", tools);
            if (toolList.Length > 0)
                sb.Append("  tools used: ").AppendLine(toolList);
            if (!string.IsNullOrWhiteSpace(answer))
                sb.Append("  answered: ").AppendLine(Trim(answer));
        }
        var text = sb.ToString();
        return text.Length <= 2048 ? text : text[..2048] + "…";
        static string Trim(string s) =>
            s.Length <= 300 ? s.ReplaceLineEndings(" ") : s[..300].ReplaceLineEndings(" ") + "…";
    }
}
