using System.Diagnostics.Metrics;

namespace KnowledgeHub.Server.Evaluation;

/// <summary>
/// OpenTelemetry instruments for the RAG Quality Triad
/// (SPEC-20260927-rag-evaluation-triad-metrics RF-003). Gauges publish the latest
/// evaluation scores; no query text or PII is ever tagged.
/// </summary>
public static class RagEvaluationMetrics
{
    /// <summary>Registered in WithMetrics so Prometheus/OTLP exporters see the gauges.</summary>
    public const string MeterName = "KnowledgeHub.Server.RagEvaluation";

    private static readonly Meter Meter = new(MeterName, "0.1.0");

    /// <summary>Share of retrieved context relevant to the query (0–1).</summary>
    public static readonly Gauge<double> ContextRelevance =
        Meter.CreateGauge<double>("rag_context_relevance");

    /// <summary>Share of answer claims supported by the context (0–1).</summary>
    public static readonly Gauge<double> Groundedness =
        Meter.CreateGauge<double>("rag_groundedness");

    /// <summary>Semantic similarity of the answer to the question (0–1).</summary>
    public static readonly Gauge<double> AnswerRelevance =
        Meter.CreateGauge<double>("rag_answer_relevance");

    public static void Record(RagEvaluationResult result)
    {
        ContextRelevance.Record(result.ContextRelevance);
        Groundedness.Record(result.Groundedness);
        AnswerRelevance.Record(result.AnswerRelevance);
    }
}
