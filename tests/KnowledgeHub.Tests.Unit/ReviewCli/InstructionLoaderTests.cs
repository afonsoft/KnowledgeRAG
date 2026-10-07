using KnowledgeHub.Review.Review;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class InstructionLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kr-{Guid.NewGuid():N}");

    public InstructionLoaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_files_yield_null_sections()
    {
        var i = InstructionLoader.Load(_dir);
        Assert.Null(i.ReviewMd);
        Assert.Null(i.AgentsMd);
        Assert.Null(i.ClaudeMd);
        Assert.Null(i.LanguageOverride);
    }

    [Fact]
    public void Review_md_front_line_sets_language_override()
    {
        File.WriteAllText(Path.Combine(_dir, "REVIEW.md"), "language: en-US\nBe strict about nullability.\n");
        var i = InstructionLoader.Load(_dir);
        Assert.Equal("en-US", i.LanguageOverride);
        Assert.Contains("nullability", i.ReviewMd);
    }

    [Fact]
    public void Agents_and_claude_md_are_loaded()
    {
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "agents rules");
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "claude rules");
        var i = InstructionLoader.Load(_dir);
        Assert.Equal("agents rules", i.AgentsMd);
        Assert.Equal("claude rules", i.ClaudeMd);
    }
}
