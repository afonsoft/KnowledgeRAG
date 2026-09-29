using KnowledgeHub.McpEngine.Activity;
using KnowledgeHub.Server.Ingestion;
using Microsoft.AspNetCore.SignalR;

namespace KnowledgeHub.Server.Hubs;

/// <summary>
/// SignalR hub feeding the MCP Monitor page (SPEC-05 RF-003).
/// On connect the client receives the buffered snapshot; afterwards
/// <see cref="McpActivityBroadcastService"/> pushes live events.
/// </summary>
public sealed class McpMonitorHub(
    IMcpActivityFeed feed, IIngestionProgressFeed ingestionFeed) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // SPEC-20260915-mcp-monitor-activity RF-002: snapshot uses the same wire
        // DTO as the live broadcast so the client replays it with one code path.
        await Clients.Caller.SendAsync("Snapshot",
            feed.Snapshot().Select(McpMonitorEventMapper.Map).ToList());
        // SPEC-20260929-observability-and-tests-residual RF-001: replay the
        // last per-job ingestion state — a client connecting after an
        // orphaned-job terminal event still sees it.
        await Clients.Caller.SendAsync("IngestionProgressSnapshot",
            ingestionFeed.Snapshot().Select(e => new
            {
                jobId = e.JobId,
                sourceId = e.SourceId,
                status = e.Status,
                processed = e.Processed,
                skipped = e.Skipped,
                failed = e.Failed,
                chunksCreated = e.ChunksCreated
            }).ToList());
        await base.OnConnectedAsync();
    }
}
