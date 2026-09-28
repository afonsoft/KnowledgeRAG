namespace KnowledgeHub.Server.Evaluation;

/// <summary>RAG Quality Triad evaluation result (SPEC-20260927-rag-evaluation-triad-metrics RF-002).</summary>
public sealed record RagEvaluationResult
{
    /// <summary>0.00–1.00: share of retrieved context sentences relevant to the query.</summary>
    public double ContextRelevance { get; init; }

    /// <summary>0.00–1.00: share of answer claims supported by the context (hallucination detector).</summary>
    public double Groundedness { get; init; }

    /// <summary>0.00–1.00: semantic similarity of the answer to the question.</summary>
    public double AnswerRelevance { get; init; }

    /// <summary>Harmonic mean of the three scores (SPEC-20260927 RF-002).</summary>
    public double OverallScore { get; init; }

    /// <summary>True when Groundedness drops below the hard floor.</summary>
    public bool FlaggedAsHallucination { get; init; }
}

/// <summary>
/// Computes the RAG Quality Triad (Context Relevance, Groundedness, Answer Relevance)
/// and the overall score. Deterministic, dependency-free scoring (token/lexical +
/// length heuristics) so it is cheap, testable and never blocks the user (no LLM call).
/// </summary>
public interface IRagTriadEvaluator
{
    RagEvaluationResult Evaluate(
        string question, IReadOnlyList<string> contextChunks, string answer,
        double hallucinationFloor = 0.60);
}
