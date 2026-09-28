using System.Text;
using KnowledgeHub.Server.Ingestion.Connectors.Clients;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// Renders a <see cref="TranscriptionResult"/> as searchable Markdown
/// (SPEC-20260927 RF-002): chapter summary up top, then the diarized dialog.
/// Consecutive utterances from the same speaker within 10s are merged into
/// one block so chunking keeps speaker turns intact.
/// </summary>
public static class AudioTranscriptionRenderer
{
    private const long MergeGapMs = 10_000;

    public static string Render(string title, TranscriptionResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# Transcrição: ").AppendLine(title);
        sb.AppendLine();

        if (result.Chapters.Count > 0)
        {
            sb.AppendLine("## Capítulos");
            foreach (var c in result.Chapters)
                sb.Append("- [").Append(Stamp(c.StartMs)).Append("] ").AppendLine(c.Headline);
            sb.AppendLine();
        }

        sb.AppendLine("## Diálogo");
        var merged = Merge(result.Utterances);
        if (merged.Count == 0)
            sb.AppendLine("Nenhuma fala detectada no áudio.");
        foreach (var m in merged)
            sb.Append("**[Speaker ").Append(m.Speaker).Append(" - ")
                .Append(Stamp(m.StartMs)).Append("]**: ").AppendLine(m.Text);

        return sb.ToString();
    }

    /// <summary>Same-speaker utterances within 10s of the previous end merge.</summary>
    internal static List<TranscriptUtterance> Merge(IReadOnlyList<TranscriptUtterance> utterances)
    {
        var merged = new List<TranscriptUtterance>();
        foreach (var u in utterances)
        {
            var last = merged.Count > 0 ? merged[^1] : null;
            if (last is not null && u.Speaker == last.Speaker && u.StartMs - last.EndMs <= MergeGapMs)
                merged[^1] = last with { EndMs = Math.Max(last.EndMs, u.EndMs), Text = $"{last.Text} {u.Text}".Trim() };
            else
                merged.Add(u);
        }
        return merged;
    }

    private static string Stamp(long ms) =>
        TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss");
}
