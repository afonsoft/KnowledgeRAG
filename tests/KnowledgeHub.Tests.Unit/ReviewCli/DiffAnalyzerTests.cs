using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Review;
using Microsoft.Extensions.AI;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class DiffAnalyzerTests
{
    /// <summary>Hand-rolled IChatClient fake (repo convention — no mocking libs).
    /// Returns queued JSON answers; counts calls to verify REVIEW_PASSES.</summary>
    private sealed class FakeChatClient(params string[] answers) : IChatClient
    {
        private int _index;
        public int Calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var text = answers[_index++ % answers.Length];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static InstructionLoader.Instructions NoInstructions => new(null, null, null, null);
    private const string Clean = """{"verdict":"approved","summary":"looks clean","findings":[]}""";

    [Fact]
    public async Task Clean_llm_answer_yields_no_findings()
    {
        var chat = new FakeChatClient(Clean);
        var result = await new DiffAnalyzer(chat, new ReviewOptions())
            .AnalyzeAsync(TestSignals.Signal(), NoInstructions, [], CancellationToken.None);
        Assert.Empty(result.Findings);
        Assert.Equal(1, chat.Calls); // REVIEW_PASSES default = 1
    }

    [Fact]
    public async Task Passes_runs_llm_per_pass_and_consensus_filters()
    {
        var flaky = """
            {"verdict":"comment","summary":"",
             "findings":[{"kind":"bug","severity":"severe","file":"src/Foo.cs","line":11,"confidence":0.9,"rationale":"npe"}]}
            """;
        // Passes=2: finding appears in both passes → survives consensus.
        var chat = new FakeChatClient(flaky, Clean, flaky);
        var options = new ReviewOptions { Passes = 2 };
        var result = await new DiffAnalyzer(chat, options)
            .AnalyzeAsync(TestSignals.Signal(), NoInstructions, [], CancellationToken.None);
        Assert.Equal(2, chat.Calls);
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task Single_pass_minority_finding_is_dropped()
    {
        var flaky = """
            {"verdict":"comment","summary":"",
             "findings":[{"kind":"bug","severity":"severe","file":"src/Foo.cs","line":11,"confidence":0.9,"rationale":"npe"}]}
            """;
        // Passes=3: finding in 1/3 → below ceil(3/2)=2 threshold.
        var chat = new FakeChatClient(flaky, Clean, Clean);
        var options = new ReviewOptions { Passes = 3 };
        var result = await new DiffAnalyzer(chat, options)
            .AnalyzeAsync(TestSignals.Signal(), NoInstructions, [], CancellationToken.None);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Oversized_diff_sets_summary_only()
    {
        var files = new[] { TestSignals.File("big.cs", patch: new string('x', 600 * 1024)) };
        var chat = new FakeChatClient(Clean);
        var options = new ReviewOptions { MaxDiffKb = 128 };
        var result = await new DiffAnalyzer(chat, options)
            .AnalyzeAsync(TestSignals.Signal(files: files), NoInstructions, [], CancellationToken.None);
        Assert.True(result.SummaryOnly);
        Assert.Contains("summary mode", result.Summary);
    }

    [Fact]
    public async Task Empty_diff_approves_without_llm_call()
    {
        var chat = new FakeChatClient(Clean);
        var result = await new DiffAnalyzer(chat, new ReviewOptions())
            .AnalyzeAsync(TestSignals.Signal(files: []), NoInstructions, [], CancellationToken.None);
        Assert.Equal("approved", result.Verdict);
        Assert.Equal(0, chat.Calls);
    }
}
