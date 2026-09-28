namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// Persisted RAG Quality Triad evaluation (SPEC-20260927-rag-evaluation-triad-metrics).
/// Stores the three normalized scores plus the auto hallucination flag for trend
/// dashboards and auditing. Only the question is persisted — never raw chunk or
/// answer content — to bound storage and avoid duplicating document data.
/// </summary>
public sealed class RagEvaluationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Correlates with the originating query/session.</summary>
    public string QueryId { get; set; } = "";

    /// <summary>Truncated question text for dashboards (cap enforced by caller).</summary>
    public string Question { get; set; } = "";

    /// <summary>0.00–1.00: share of retrieved context relevant to the query.</summary>
    public double ContextRelevance { get; set; }

    /// <summary>0.00–1.00: share of answer claims supported by context.</summary>
    public double Groundedness { get; set; }

    /// <summary>0.00–1.00: semantic similarity of answer to question.</summary>
    public double AnswerRelevance { get; set; }

    /// <summary>Harmonic mean of the three scores.</summary>
    public double OverallScore { get; set; }

    /// <summary>UTC timestamp of the evaluation.</summary>
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when Groundedness fell below the configured floor.</summary>
    public bool FlaggedAsHallucination { get; set; }
}
