using System.Text;
using KnowledgeHub.Review.Signals;

namespace KnowledgeHub.Review.Review;

/// <summary>
/// Splits the per-file diff into chunks bounded by <c>REVIEW_MAX_DIFF_KB</c>
/// for map-reduce analysis. Files without patches (binary, too large) are
/// listed as metadata-only so the LLM still sees the file list.
/// </summary>
public static class DiffChunker
{
    public sealed record Chunk(IReadOnlyList<ChangedFile> Files, int TotalFiles, int Index);

    public static IReadOnlyList<Chunk> Split(IReadOnlyList<ChangedFile> files, int maxKb)
    {
        var chunks = new List<Chunk>();
        var current = new List<ChangedFile>();
        var budget = maxKb * 1024;
        var used = 0;

        foreach (var file in files)
        {
            var size = Encoding.UTF8.GetByteCount(file.Patch ?? "") + file.Filename.Length + 128;
            if (used + size > budget && current.Count > 0)
            {
                chunks.Add(new Chunk(current, files.Count, chunks.Count));
                current = [];
                used = 0;
            }
            current.Add(file);
            used += size;
        }
        if (current.Count > 0)
            chunks.Add(new Chunk(current, files.Count, chunks.Count));

        return chunks;
    }

    /// <summary>True when the whole diff is too large — summary-only mode
    /// (overview + highest-risk files, flagged in the summary).</summary>
    public static bool IsOversized(IReadOnlyList<ChangedFile> files, int maxKb) =>
        files.Sum(f => Encoding.UTF8.GetByteCount(f.Patch ?? "") + 128) > maxKb * 1024 * 4;

    /// <summary>Renders a chunk as a unified-diff-like prompt block.</summary>
    public static string Render(Chunk chunk)
    {
        var sb = new StringBuilder();
        foreach (var f in chunk.Files)
        {
            sb.AppendLine($"### {f.Filename} ({f.Status}, +{f.Additions}/-{f.Deletions})");
            if (f.PreviousFilename is { } prev)
                sb.AppendLine($"renamed from: {prev}");
            sb.AppendLine(string.IsNullOrWhiteSpace(f.Patch)
                ? "(patch unavailable — binary/large file; review metadata only)"
                : f.Patch);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
