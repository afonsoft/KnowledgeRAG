using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Evaluation;

/// <summary>
/// Enqueues synthesized answers for background triad evaluation. Honors
/// <see cref="RagEvaluationOptions"/> (enabled + sample rate) and is allocation-
/// and latency-light (&lt;1ms hot path, SPEC-20260927 RF-001) — no LLM call here.
/// </summary>
public interface IRagEvaluationEnqueuer
{
    bool TryEnqueue(string queryId, string question, IReadOnlyList<string> contextChunks, string answer);

    /// <summary>Reader consumed by <see cref="EvaluationWorker"/>.</summary>
    ChannelReader<RagEvaluationTask> Reader { get; }
}

/// <summary>Default <see cref="IRagEvaluationEnqueuer"/> over a bounded channel.</summary>
public sealed class RagEvaluationEnqueuer : IRagEvaluationEnqueuer, IDisposable
{
    private readonly Channel<RagEvaluationTask> _channel;
    private readonly RagEvaluationOptions _options;
    private readonly ILogger<RagEvaluationEnqueuer> _logger;

    public RagEvaluationEnqueuer(
        IOptions<RagEvaluationOptions> options, ILogger<RagEvaluationEnqueuer> logger)
    {
        _options = options.Value;
        _logger = logger;
        _channel = Channel.CreateBounded<RagEvaluationTask>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>Reader consumed by <see cref="EvaluationWorker"/>.</summary>
    public ChannelReader<RagEvaluationTask> Reader => _channel.Reader;

    public bool TryEnqueue(string queryId, string question, IReadOnlyList<string> contextChunks, string answer)
    {
        if (!_options.Enabled) return false;
        if (_options.SampleRate <= 0.0) return false;
        if (_options.SampleRate < 1.0 && Random.Shared.NextDouble() > _options.SampleRate) return false;

        var task = new RagEvaluationTask(queryId, question, contextChunks, answer, DateTimeOffset.UtcNow);
        return _channel.Writer.TryWrite(task);
    }

    public void Dispose() => _channel.Writer.TryComplete();
}
