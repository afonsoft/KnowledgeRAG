using Microsoft.Extensions.AI;

namespace KnowledgeHub.McpEngine.Agents.ChainAst;

/// <summary>
/// Integrity repair pass (RF-002): providers hard-fail on
/// <c>unmatched tool_call_id</c> — every assistant tool call must end with a
/// Tool-role result carrying the same CallId.
/// <list type="bullet">
///   <item>Pending call (interrupted run, approval timeout) → a synthetic
///   <see cref="FunctionResultContent"/> with the interrupt notice is paired
///   into the same tool message; if no tool message exists one is
///   synthesized and appended to the pair.</item>
///   <item>Orphan tool messages (CallId without a call) stay out of the
///   flattened stream — <see cref="ChainAst.Orphans"/> retains them.</item>
///   <item>Duplicate CallIds in one assistant message collapse to the first.</item>
/// </list>
/// </summary>
public static class ChainAstRepair
{
    /// <summary>Standard defensive text for a tool call that never resolved.</summary>
    public const string InterruptedNotice =
        "The tool call was interrupted or unhandled; continuing session.";

    public static ChainAst Repair(ChainAst ast)
    {
        foreach (var section in ast.Sections)
            foreach (var pair in section.Body)
                RepairPair(pair);
        return ast;
    }

    /// <summary>Repairs one call/result pair: collapses duplicate CallIds,
    /// then slots a synthetic interrupted-result stub into an existing tool
    /// message (or a fresh one) so the pair stays contiguous.</summary>
    private static void RepairPair(BodyPair pair)
    {
        // Collapse duplicate CallIds within the same AI message.
        if (pair.Calls.Count > 1)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            pair.Calls.RemoveAll(c => !seen.Add(c.Call.CallId));
        }

        var pending = pair.Calls.Where(c => c.Result is null).ToList();
        if (pending.Count == 0)
            return;

        var carrier = pair.ToolMessages
            .FirstOrDefault(m => m.Contents.OfType<FunctionResultContent>().Any());
        foreach (var call in pending)
        {
            var stub = new FunctionResultContent(
                call.Call.CallId, InterruptedNotice);
            call.Result = stub;
            if (carrier is null)
            {
                carrier = new ChatMessage(ChatRole.Tool, [stub]);
                pair.ToolMessages.Add(carrier);
            }
            else
                carrier.Contents.Add(stub);
        }
    }

    /// <summary>Quick integrity check — every call answered, no orphans in
    /// the outgoing stream.</summary>
    public static bool IsValid(ChainAst ast) =>
        ast.Sections.SelectMany(s => s.Body)
            .SelectMany(p => p.Calls)
            .All(c => c.Result is not null);
}
