using KnowledgeHub.Review.Review;
using KnowledgeHub.Review.Signals;
using Xunit;

namespace KnowledgeHub.Tests.Unit.ReviewCli;

public sealed class DiffChunkerTests
{
    [Fact]
    public void Small_diff_stays_one_chunk()
    {
        var chunks = DiffChunker.Split([TestSignals.File(), TestSignals.File("src/Bar.cs")], maxKb: 256);
        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].Files.Count);
        Assert.Equal(2, chunks[0].TotalFiles);
    }

    [Fact]
    public void Splits_at_budget_and_preserves_order()
    {
        var files = Enumerable.Range(0, 5)
            .Select(i => TestSignals.File($"src/F{i}.cs", patch: new string('x', 1200)))
            .ToList();
        var chunks = DiffChunker.Split(files, maxKb: 2); // 2KB budget
        Assert.True(chunks.Count > 1);
        Assert.Equal(files.Count, chunks.Sum(c => c.Files.Count));
        Assert.Equal(files.Select(f => f.Filename), chunks.SelectMany(c => c.Files).Select(f => f.Filename));
    }

    [Fact]
    public void Oversized_single_file_gets_own_chunk()
    {
        var files = new List<ChangedFile>
        {
            TestSignals.File("big.cs", patch: new string('x', 10 * 1024)),
            TestSignals.File("small.cs"),
        };
        var chunks = DiffChunker.Split(files, maxKb: 1);
        Assert.True(chunks.Count >= 2);
        Assert.Equal("big.cs", chunks[0].Files[0].Filename);
    }

    [Fact]
    public void IsOversized_flags_huge_diffs()
    {
        var files = new[] { TestSignals.File("a.cs", patch: new string('x', 600 * 1024)) };
        Assert.True(DiffChunker.IsOversized(files, maxKb: 128));
        Assert.False(DiffChunker.IsOversized([TestSignals.File()], maxKb: 128));
    }

    [Fact]
    public void Render_includes_metadata_and_patch()
    {
        var chunk = new DiffChunker.Chunk(
            [TestSignals.File("src/Foo.cs"), new ChangedFile("bin.png", "added", null, 0, 0, null)], 2, 0);
        var text = DiffChunker.Render(chunk);
        Assert.Contains("### src/Foo.cs", text);
        Assert.Contains("patch unavailable", text);   // binary file noted, not silently dropped
        Assert.Contains("+new call", text);
    }
}
