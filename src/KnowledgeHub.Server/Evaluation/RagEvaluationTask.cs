namespace KnowledgeHub.Server.Evaluation;

/// <summary>One evaluation request queued for background processing (SPEC-20260927 RF-001).</summary>
public sealed record RagEvaluationTask(
    string QueryId, string Question, IReadOnlyList<string> ContextChunks, string Answer, DateTimeOffset QueuedAt);
