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
            // SPEC-20260929 RF-001: system/developer messages are pinned —
            // hoisted into the folded section's headers, never summarized
            // away. The parser parks every non-User role in Headers (incl.
            // future "developer" roles), so pin everything that isn't the
            // user's own prompt — instruction headers must survive.
            var pinned = ast.Sections.Take(fold)
                .SelectMany(s => s.Headers)
                .Where(h => h.Role != ChatRole.User)
                .ToList();

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
            folded.Headers.AddRange(pinned);
            // RF-004: reasoning was dropped with the folded sections — emit a
            // synthetic signature so providers that require thought retention
            // (Gemini/Claude thinking models) still accept the history.
            if (ast.Sections.Take(fold).SelectMany(s => s.Body).Any(p => p.HasReasoning))
                folded.Body[0].AiMessage.Contents.Insert(0,
                    new TextReasoningContent("") { ProtectedData = SkipThoughtSignature });
            ast.Sections.RemoveRange(0, fold);
            ast.Sections.Insert(0, folded);
        }

        // SPEC-20260929 RF-004: several individually-fine results can still
        // overshoot the total budget — tighten the per-item cap progressively
        // until the history fits (or nothing shrinkable remains).
        var cap = options.MaxBodyPairBytes;
        while (ast.EstimateBytes() > options.MaxTotalHistoryBytes && cap > 256)
        {
            cap /= 2;
            foreach (var s in ast.Sections)
                foreach (var pair in s.Body)
                    foreach (var msg in pair.ToolMessages)
                        TruncateToolResults(msg, cap);
        }

        return Task.FromResult(ast);
    }

    /// <summary>Cuts oversized payloads inside tool-result contents —
    /// string results are truncated directly; structured results are
    /// serialized, truncated and replaced by the (marked) text so they
    /// can't slip past the budget un-measured. CallId is untouched so
    /// call/result pairing survives.</summary>
    private void TruncateToolResults(ChatMessage toolMessage, int? capOverride = null)
    {
        var cap = capOverride ?? options.MaxBodyPairBytes;
        foreach (var result in toolMessage.Contents.OfType<FunctionResultContent>())
        {
            if (result.Result is not string text)
            {
                if (result.Result is null)
                    continue;
                var json = System.Text.Json.JsonSerializer.Serialize(result.Result);
                if (Encoding.UTF8.GetByteCount(json) <= cap)
                    continue;
                result.Result = CutToBytes(json, cap)
                    + $"\n…[truncated structured→{cap}B]";
                continue;
            }
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes <= cap)
                continue;
            result.Result = CutToBytes(text, cap)
                + $"\n…[truncated {bytes}B→{cap}B]";
        }
    }

    /// <summary>Character-bounded cut approximating a byte cap (avoids
    /// splitting a surrogate/UTF-8 sequence).</summary>
    private static string CutToBytes(string text, int byteCap)
    {
        var chars = Math.Max(0, byteCap / 4);
        return text.Length <= chars ? text : text[..chars];
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
            // SPEC-20260929 RF-005: tool payloads are quoted as `tool output`
            // — never merged into assistant speech (prompt-injection hygiene).
            var toolPreview = s.Body
                .SelectMany(p => p.ToolMessages)
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Select(r => r.Result?.ToString())
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (toolPreview is not null)
                sb.Append("  tool output: ").AppendLine(Trim(toolPreview));
            if (!string.IsNullOrWhiteSpace(answer))
                sb.Append("  answered: ").AppendLine(Trim(answer));
        }
        var text = sb.ToString();
        return text.Length <= 2048 ? text : text[..2048] + "…";

        // SPEC-20260929 RF-005: collapse EVERY line-breaking character — not
        // just CRLF — so quoted tool output can never forge a fresh
        // "answered:"/"user asked:" line inside the summarized block.
        static string Trim(string s)
        {
            var flat = string.Concat(s.Select(ch =>
                ch is '\r' or '\n' or '\v' or '\f' or '\u0085'
                or '\u2028' or '\u2029' ? ' ' : ch));
            return flat.Length <= 300 ? flat : flat[..300] + "…";
        }
    }
}
