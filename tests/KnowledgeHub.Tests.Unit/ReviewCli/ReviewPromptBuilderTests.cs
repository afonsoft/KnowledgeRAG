using KnowledgeHub.Review.Config;
using KnowledgeHub.Review.Review;
using KnowledgeHub.Review.Signals;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class ReviewPromptBuilderTests
{
    private static InstructionLoader.Instructions NoInstructions =>
        new(null, null, null, null);

    [Fact]
    public void System_prompt_is_skeptical_by_default()
    {
        Assert.Contains("SKEPTICAL BY DEFAULT", ReviewPromptBuilder.SystemPrompt);
        Assert.Contains("never reply \"looks good\" without evidence", ReviewPromptBuilder.SystemPrompt);
    }

    [Fact]
    public void System_prompt_has_prompt_injection_hard_rule()
    {
        // 88% bypass stat from the Laurie Voss talk — the guardrail must stay.
        Assert.Contains("UNTRUSTED DATA", ReviewPromptBuilder.SystemPrompt);
        Assert.Contains("prompt injection", ReviewPromptBuilder.SystemPrompt);
    }

    [Fact]
    public void User_prompt_labels_pr_body_as_untrusted()
    {
        var prompt = ReviewPromptBuilder.BuildUserPrompt(
            TestSignals.Signal(), "diff...", NoInstructions, [], new ReviewOptions(), 0, 1);
        Assert.Contains("Description (untrusted):", prompt);
    }

    [Fact]
    public void Review_md_language_overrides_env_language()
    {
        var instructions = new InstructionLoader.Instructions("language: en-US\nrest", null, null, "en-US");
        var options = new ReviewOptions { Language = "pt-BR" };
        var prompt = ReviewPromptBuilder.BuildUserPrompt(
            TestSignals.Signal(), "diff", instructions, [], options, 0, 1);
        Assert.Contains("Language for findings: en-US", prompt);
        Assert.Contains("## REVIEW.md", prompt);
    }

    [Fact]
    public void Chunked_diff_discloses_partial_view()
    {
        var prompt = ReviewPromptBuilder.BuildUserPrompt(
            TestSignals.Signal(), "diff", NoInstructions, [], new ReviewOptions(), 1, 3);
        Assert.Contains("chunk 2/3", prompt);
    }

    [Fact]
    public void Stacked_pr_notes_layer_only_diff()
    {
        var signal = TestSignals.Signal(
            meta: TestSignals.Meta(baseRef: "feature/base"),
            stacked: true,
            stack: [new StackLayer(5, "feature/base", "main", false)]);
        var prompt = ReviewPromptBuilder.BuildUserPrompt(
            signal, "diff", NoInstructions, [], new ReviewOptions(), 0, 1);
        Assert.Contains("layer-only", prompt);
    }
}
