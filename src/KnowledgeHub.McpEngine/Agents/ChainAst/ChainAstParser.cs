using Microsoft.Extensions.AI;

namespace KnowledgeHub.McpEngine.Agents.ChainAst;

/// <summary>
/// Builds a <see cref="ChainAst"/> from a flat message list (RF-001).
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
    public static ChainAst Parse(IReadOnlyList<ChatMessage> messages, bool forceRepair = false)
    {
        var state = new ParserState();
        foreach (var m in messages)
            ParseMessage(state, m);
        state.Ast.Orphans = state.Orphans;
        return forceRepair ? ChainAstRepair.Repair(state.Ast) : state.Ast;
    }

    /// <summary>Mutable parser state shared by the per-role handlers.</summary>
    private sealed class ParserState
    {
        public ChainAst Ast = new();
        public List<ChatMessage> Orphans = [];
        public ChainSection? Section;
        public BodyPair? Open;

        public void EnsureSection()
        {
            Section ??= new ChainSection();
            if (!Ast.Sections.Contains(Section))
                Ast.Sections.Add(Section);
        }
    }

    /// <summary>Dispatches one message to its role handler.</summary>
    private static void ParseMessage(ParserState s, ChatMessage m)
    {
        if (m.Role == ChatRole.System || m.Role == ChatRole.User)
        {
            ParseHeader(s, m);
            return;
        }
        if (m.Role == ChatRole.Assistant)
        {
            ParseAssistant(s, m);
            return;
        }
        if (m.Role == ChatRole.Tool)
        {
            ParseTool(s, m);
            return;
        }
        ParseOther(s, m);
    }

    /// <summary>System/User turns are section headers; a header after body
    /// pairs opens a fresh section.</summary>
    private static void ParseHeader(ParserState s, ChatMessage m)
    {
        s.EnsureSection();
        if (s.Section!.Body.Count > 0)
        {
            s.Section = new ChainSection();
            s.Ast.Sections.Add(s.Section);
        }
        s.Section.Headers.Add(m);
        s.Open = null;
    }

    /// <summary>Assistant turns open a body pair; function calls flip it to
    /// RequestResponse and register one ToolCallPair per call.</summary>
    private static void ParseAssistant(ParserState s, ChatMessage m)
    {
        s.EnsureSection();
        var open = new BodyPair
        {
            AiMessage = m,
            Type = BodyPairType.Completion
        };
        s.Section!.Body.Add(open);
        foreach (var call in m.Contents.OfType<FunctionCallContent>())
        {
            open.Type = BodyPairType.RequestResponse;
            open.Calls.Add(new ToolCallPair { Call = call });
        }
        s.Open = open;
    }

    /// <summary>Tool results attach to the open pair's matching call; unmatched
    /// results become orphans (edge case).</summary>
    private static void ParseTool(ParserState s, ChatMessage m)
    {
        var matched = false;
        if (s.Open is not null)
            foreach (var result in m.Contents.OfType<FunctionResultContent>())
            {
                var pair = s.Open.Calls
                    .FirstOrDefault(c => c.Call.CallId == result.CallId);
                if (pair is not null && pair.Result is null)
                {
                    pair.Result = result;
                    matched = true;
                }
            }
        if (matched)
            s.Open!.ToolMessages.Add(m);
        else
            s.Orphans.Add(m); // tool message with no matching call (edge case)
    }

    /// <summary>Unknown/future roles — keep attached to the open pair so the
    /// round-trip never silently drops content.</summary>
    private static void ParseOther(ParserState s, ChatMessage m)
    {
        s.EnsureSection();
        if (s.Open is not null)
            s.Open.ToolMessages.Add(m);
        else
            s.Section!.Headers.Add(m);
    }
}
