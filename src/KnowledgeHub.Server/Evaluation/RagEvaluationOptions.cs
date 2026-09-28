namespace KnowledgeHub.Server.Evaluation;

/// <summary>RAG evaluation tuning (SPEC-20260927-rag-evaluation-triad-metrics).</summary>
public sealed class RagEvaluationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RagEvaluation";

    /// <summary>Master switch — when false, no task is ever queued.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Share of generated answers sampled for evaluation (0.0–1.0).</summary>
    public double SampleRate { get; set; } = 0.20;

    /// <summary>Optional judge model id; unused by the deterministic evaluator.</summary>
    public string? JudgeModel { get; set; }
}
