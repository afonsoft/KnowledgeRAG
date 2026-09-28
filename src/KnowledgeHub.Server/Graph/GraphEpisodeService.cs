using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.Graph;

/// <summary>
/// Episode lifecycle for the temporal graph
/// (SPEC-20260927-temporal-episodic-knowledge-graph RF-001): every ingestion
/// run or agent session that produces graph facts opens an episode so nodes
/// and edges can be attributed and later queried per episode.
/// </summary>
public sealed class GraphEpisodeService(KnowledgeHubDbContext db)
{
    /// <summary>Opens an episode for one ingestion run that produced facts.</summary>
    public async Task<KgEpisode> StartIngestionEpisodeAsync(
        Guid sourceId, string summary, CancellationToken ct)
    {
        var episode = new KgEpisode
        {
            Kind = "ingestion",
            KnowledgeSourceId = sourceId,
            Summary = summary.Length > 500 ? summary[..500] : summary
        };
        db.KgEpisodes.Add(episode);
        await db.SaveChangesAsync(ct);
        return episode;
    }

    /// <summary>Opens an episode for an agent session (thread-linked).</summary>
    public async Task<KgEpisode> StartSessionEpisodeAsync(
        Guid threadId, string? summary, CancellationToken ct)
    {
        var episode = new KgEpisode
        {
            Kind = "agent-session",
            ThreadId = threadId,
            Summary = summary is { Length: > 500 } ? summary[..500] : summary
        };
        db.KgEpisodes.Add(episode);
        await db.SaveChangesAsync(ct);
        return episode;
    }

    public Task<KgEpisode?> FindAsync(Guid id, CancellationToken ct) =>
        db.KgEpisodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
}
