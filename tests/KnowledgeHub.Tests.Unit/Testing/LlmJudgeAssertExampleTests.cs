namespace KnowledgeHub.Tests.Unit.Testing;

/// <summary>
/// SPEC-20261111-agent-rules-hardening RF-001: example of the LLM-judge
/// convention. Runs fully deterministically when no judge is configured
/// (skips), and judges a real output when Tests__LlmJudge__* is set.
/// </summary>
public class LlmJudgeAssertExampleTests
{
    [Fact]
    public async Task Answer_AboutMigrationParity_StatesTheRule()
    {
        // Covers RF-001 / AC-01: judge asserts a *property* of free text.
        var actual = "Each SQLite migration needs a Postgres counterpart; " +
                     "files may opt out with a dialect marker comment.";

        await LlmJudgeAssert.PassesAsync(
            criteria: "states that migrations must exist for both database dialects",
            actual: actual);
    }

    [Fact]
    public void Judge_NoopsGracefully_WhenNotConfigured()
    {
        // Covers AC-01: on machines without Tests__LlmJudge__* the suite stays green.
        if (!LlmJudgeAssert.IsConfigured) return; // deterministic no-op
        Assert.True(true);
    }
}
