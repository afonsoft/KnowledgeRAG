namespace KnowledgeHub.Shared.Contracts;

/// <summary>GET /api/v1/evaluation/stats payload — RAG triad aggregates.</summary>
public class RagEvaluationStats
{
    public string Period { get; set; } = "";
    public int TotalEvaluations { get; set; }
    public double AverageContextRelevance { get; set; }
    public double AverageGroundedness { get; set; }
    public double AverageAnswerRelevance { get; set; }
    public double HallucinationRatePercent { get; set; }
    public List<FlaggedQuery> RecentFlaggedQueries { get; set; } = new();
}

public class FlaggedQuery
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public double GroundednessScore { get; set; }
    public DateTimeOffset FlaggedAt { get; set; }
}
