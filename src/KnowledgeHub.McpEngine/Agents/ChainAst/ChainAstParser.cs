using Microsoft.Extensions.AI;

namespace KnowledgeHub.McpEngine.Agents.ChainAst;

/// <summary>
/// Builds a <see cref="ChainAST"/> from a flat message list (RF-001).
/// Section boundaries: a <see cref="ChatRole.System"/>/<see cref="ChatRole.User"/>
/// message after body pairs starts a new section; consecutive header messages
/// join the open section's headers. Tool-role messages pair by
/// <see cref="FunctionResultContent.CallId"/> with the open
/// <see cref="BodyPair"/>'s calls; unpaired tool messages are captured in
/// <see cref="Orphans"/> instead of crashing (RF-002).
/// </summary>
public static class ChainAstParser
{
    /// <summary>Parses <paramref name="messages"/>; when
    /// <paramref name="forceRepair"/> is set the result is passed through
    /// <see cref="ChainAstRepair.Repair"/> so every call has a response.</summary>
    public static ChainAST Parse(IReadOnlyList<ChatMessage> messages, bool forceRepair = false)
    {
        var ast = new ChainAST();
        var orphans = new List<ChatMessage>();
        ChainSection? section = null;
        BodyPair? open = null;

        void EnsureSection()
        {
            section ??= new ChainSection();
            if (!ast.Sections.Contains(section))
                ast.Sections.Add(section);
        }

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System || m.Role == ChatRole.User)
            {
                EnsureSection();
                // A header turn after body pairs opens a fresh section.
                if (section!.Body.Count > 0)
                {
                    section = new ChainSection();
                    ast.Sections.Add(section);
                }
                section.Headers.Add(m);
                open = null;
                continue;
            }

            if (m.Role == ChatRole.Assistant)
            {
                EnsureSection();
                open = new BodyPair
                {
                    AiMessage = m,
                    Type = BodyPairType.Completion
                };
                section!.Body.Add(open);
                foreach (var call in m.Contents.OfType<FunctionCallContent>())
                {
                    open.Type = BodyPairType.RequestResponse;
                    open.Calls.Add(new ToolCallPair { Call = call });
                }
                continue;
            }

            if (m.Role == ChatRole.Tool)
            {
                var matched = false;
                if (open is not null)
                    foreach (var result in m.Contents.OfType<FunctionResultContent>())
                    {
                        var pair = open.Calls
                            .FirstOrDefault(c => c.Call.CallId == result.CallId);
                        if (pair is not null && pair.Result is null)
                        {
                            pair.Result = result;
                            matched = true;
                        }
                    }
                if (matched)
                    open!.ToolMessages.Add(m);
                else
                    orphans.Add(m); // tool message with no matching call (edge case)
                continue;
            }

            // Unknown/future roles — keep attached to the open pair so the
            // round-trip never silently drops content.
            EnsureSection();
            if (open is not null)
                open.ToolMessages.Add(m);
            else
                section!.Headers.Add(m);
        }

        ast.Orphans = orphans;
        return forceRepair ? ChainAstRepair.Repair(ast) : ast;
    }
}
