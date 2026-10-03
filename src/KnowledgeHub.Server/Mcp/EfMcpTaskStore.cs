using System.Text.Json;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Server.Mcp;

/// <summary>
/// SPEC-20260926-mcp-sdk-alignment RF-004: durable <see cref="IMcpTaskStore"/>
/// backed by the catalog database. <c>WithTasks</c> captures the instance at
/// registration time (before the DI provider exists), so the store constructs
/// short-lived contexts itself from <see cref="CatalogDatabase"/> + the same
/// provider selection the pooled registration uses. Stateless-safe: every
/// request sees the same persisted state.
/// </summary>
public sealed class EfMcpTaskStore(CatalogDatabase catalog, IConfiguration configuration) : IMcpTaskStore
{
    /// <summary>Suggested client poll cadence.</summary>
    public long? DefaultPollIntervalMs { get; } = 2_000;
    /// <summary>Handles expire one hour after creation.</summary>
    public TimeSpan? DefaultTimeToLive { get; } = TimeSpan.FromHours(1);

    /// <summary>Raised once per resolved input response.</summary>
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    private KnowledgeHubDbContext Open() => catalog.IsPostgres
        ? new PostgresKnowledgeHubDbContext(
            new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
                .UseNpgsql(catalog.PostgresConnectionString!).Options)
        : new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>()
                .UseSqlite($"Data Source={DatabasePath.Resolve(configuration)}").Options);

    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        var row = new McpTask
        {
            TaskId = $"mt_{Guid.NewGuid():N}",
            Status = "working",
            PollIntervalMs = DefaultPollIntervalMs,
            TtlMs = DefaultTimeToLive is { } ttl ? (long)ttl.TotalMilliseconds : null
        };
        await using var db = Open();
        db.McpTasks.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        var row = await db.McpTasks.FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        return row is null || Expired(row) ? null : Map(row);
    }

    public async Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default) =>
        await MutateAsync(taskId, cancellationToken, row =>
        {
            row.Status = "completed";
            row.ResultJson = result.GetRawText();
        });

    public async Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default) =>
        await MutateAsync(taskId, cancellationToken, row =>
        {
            row.Status = "failed";
            row.ErrorJson = error.GetRawText();
        });

    /// <summary>Cancellation is idempotent — terminal rows return false,
    /// anything pending flips to cancelled.</summary>
    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        var row = await db.McpTasks.FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        if (row is null || row.Status is "completed" or "cancelled" or "failed")
            return false;
        row.Status = "cancelled";
        row.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SetInputRequestsAsync(
        string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken = default) =>
        await MutateAsync(taskId, cancellationToken, row =>
        {
            row.Status = "input_required"; // parsed back leniently in Map
            row.InputRequestsJson = JsonSerializer.Serialize(
                inputRequests, McpTasksJsonContext.Default.IDictionaryStringInputRequest);
        });

    /// <summary>Applies client input responses: clears the pending requests and
    /// raises <see cref="InputResponseReceived"/> per entry so the blocked
    /// execution can resume.</summary>
    public async Task ResolveInputRequestsAsync(
        string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
    {
        await MutateAsync(taskId, cancellationToken, row =>
        {
            row.Status = "working";
            row.InputRequestsJson = null;
        });
        foreach (var (requestId, response) in inputResponses)
            InputResponseReceived?.Invoke(
                new InputResponseReceivedEventArgs { TaskId = taskId, RequestId = requestId, Response = response });
    }

    /// <summary>Audit 2026-10-03: TTL stops reads, but rows still accumulated
    /// forever — A2A tasks already have a retention purge, MCP tasks were
    /// missing theirs. Purges rows created before the cutoff (terminal or
    /// stale-running alike). Called by the maintenance loop.</summary>
    public async Task<int> PurgeOlderThanAsync(DateTimeOffset createdBefore, CancellationToken ct = default)
    {
        await using var db = Open();
        // Client-side timestamp compare — SQLite can't translate it; ids
        // project small before the batch delete (same pattern as EfA2aTaskStore).
        var staleIds = (await db.McpTasks
                .Select(t => new { t.TaskId, t.CreatedAt })
                .ToListAsync(ct))
            .Where(t => t.CreatedAt < createdBefore)
            .Select(t => t.TaskId).ToList();
        return staleIds.Count == 0 ? 0
            : await db.McpTasks.Where(t => staleIds.Contains(t.TaskId))
                .ExecuteDeleteAsync(ct);
    }

    private async Task MutateAsync(string taskId, CancellationToken ct, Action<McpTask> mutate)
    {
        await using var db = Open();
        var row = await db.McpTasks.FirstOrDefaultAsync(t => t.TaskId == taskId, ct);
        if (row is null)
            return;
        mutate(row);
        row.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static bool Expired(McpTask row) =>
        row.TtlMs is { } ttl && row.CreatedAt + TimeSpan.FromMilliseconds(ttl) < DateTimeOffset.UtcNow;

    // Stored as snake_case ("input_required"); ignoreCase alone can't bridge
    // the underscore — strip it before parsing.
    private static McpTaskStatus ParseStatus(string status) =>
        Enum.Parse<McpTaskStatus>(status.Replace("_", string.Empty), ignoreCase: true);

    private static McpTaskInfo Map(McpTask row) => new(
        TaskId: row.TaskId,
        Status: ParseStatus(row.Status),
        CreatedAt: row.CreatedAt,
        LastUpdatedAt: row.LastUpdatedAt,
        TimeToLive: row.TtlMs is { } ttl ? TimeSpan.FromMilliseconds(ttl) : null,
        PollIntervalMs: row.PollIntervalMs,
        StatusMessage: row.StatusMessage,
        Result: row.ResultJson is { } r ? JsonSerializer.Deserialize<JsonElement>(r) : null,
        Error: row.ErrorJson is { } e ? JsonSerializer.Deserialize<JsonElement>(e) : null,
        InputRequests: row.InputRequestsJson is { } ir
            ? JsonSerializer.Deserialize(ir, McpTasksJsonContext.Default.IDictionaryStringInputRequest)
                ?.ToDictionary(kv => kv.Key, kv => kv.Value)
            : null);
}
