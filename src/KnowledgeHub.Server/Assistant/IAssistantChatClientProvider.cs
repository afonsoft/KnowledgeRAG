using Microsoft.Extensions.AI;

namespace KnowledgeHub.Server.Assistant;

/// <summary>
/// Resolves the chat client for a cheap sub-task (SPEC-20260929-a2a-assistant-
/// delegation RF-002). Returns the assistant client wrapped in a
/// main-model fallback when the sub-task is routed; otherwise the main client
/// unchanged. Never throws — misconfiguration yields <paramref name="main"/>.
/// </summary>
public interface IAssistantChatClientProvider
{
    /// <summary>Returns the effective client for <paramref name="subtask"/>
    /// (rewrite | grade | expand | summarize). <paramref name="main"/> is the
    /// caller's normal (scoped) client — used as fallback and returned when the
    /// assistant is disabled or the sub-task is not routed.</summary>
    IChatClient? ForSubtask(string subtask, IChatClient? main);

    /// <summary>Drops the cached assistant client — next request rebuilds it
    /// from the effective settings (store → env).</summary>
    void Invalidate();
}
