using System.Threading.Channels;
using KnowledgeHub.Server.Evaluation;

namespace KnowledgeHub.Tests.Unit.Fakes;

/// <summary>Enqueuer stub that accepts nothing — for tests that don't exercise
/// the evaluation pipeline itself.</summary>
public sealed class NoopRagEvaluationEnqueuer : IRagEvaluationEnqueuer
{
    public static readonly NoopRagEvaluationEnqueuer Instance = new();

    public ChannelReader<RagEvaluationTask> Reader { get; } =
        Channel.CreateUnbounded<RagEvaluationTask>().Reader;

    public bool TryEnqueue(string queryId, string question, IReadOnlyList<string> contextChunks, string answer) => false;
}
