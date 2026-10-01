using System.Text.Json;
using A2A;
using KnowledgeHub.Server.Auth;
using KnowledgeHub.Server.Data;
using KnowledgeHub.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Server.A2A;

/// <summary>
/// SPEC-20261001-a2a-task-durability RF-001: durable <see cref="ITaskStore"/>
/// backed by the catalog database — the same pattern as
/// <see cref="Mcp.EfMcpTaskStore"/>: singleton, constructs short-lived
/// contexts from <see cref="CatalogDatabase"/> + the same provider selection
/// the pooled registration uses. Every event the SDK applies to a task is
/// persisted via <see cref="SaveTaskAsync"/>, so <c>tasks/get</c> survives
/// restarts.
///
/// Terminal-transition detection lives here: when a save flips the row to a
/// terminal state with push configs registered (RF-003), the webhook dispatch
/// is fired once (<see cref="A2aTask.PushDispatched"/>) and never repeated.
/// </summary>
public sealed class EfA2aTaskStore(
    CatalogDatabase catalog,
    IConfiguration configuration,
    IHttpContextAccessor http,
    IA2aPushNotifier pushNotifier,
    ILogger<EfA2aTaskStore> logger) : ITaskStore
{
    private KnowledgeHubDbContext Open() => catalog.IsPostgres
        ? new PostgresKnowledgeHubDbContext(
            new DbContextOptionsBuilder<PostgresKnowledgeHubDbContext>()
                .UseNpgsql(catalog.PostgresConnectionString!).Options)
        : new KnowledgeHubDbContext(
            new DbContextOptionsBuilder<KnowledgeHubDbContext>()
                .UseSqlite($"Data Source={DatabasePath.Resolve(configuration)}").Options);

    /// <inheritdoc/>
    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        var row = await db.A2aTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        return row is null ? null : DeserializeTask(row.TaskJson);
    }

    /// <inheritdoc/>
    public async Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken cancellationToken = default)
    {
        var taskJson = SerializeTask(task);
        var state = task.Status.State.ToString();
        IReadOnlyList<TaskPushNotificationConfig>? notify = null;

        await using var db = Open();
        var row = await db.A2aTasks.FirstOrDefaultAsync(t => t.TaskId == taskId, cancellationToken);
        if (row is null)
        {
            row = new A2aTask
            {
                TaskId = taskId,
                ContextId = task.ContextId,
                State = state,
                TaskJson = taskJson,
                CallerKeyId = http.HttpContext?.User
                    .FindFirst(ApiKeyAuthenticationHandler.KeyIdClaim)?.Value
            };
            db.A2aTasks.Add(row);
        }
        else
        {
            // Terminal transition + undispatched configs → notify exactly once.
            if (task.Status.State.IsTerminal() && !IsTerminalStateName(row.State)
                && !row.PushDispatched && row.PushConfigsJson is not null)
            {
                notify = DeserializeConfigs(row.PushConfigsJson);
                if (notify.Count > 0)
                    row.PushDispatched = true;
            }
            row.ContextId = task.ContextId;
            row.State = state;
            row.TaskJson = taskJson;
        }
        row.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (notify is { Count: > 0 })
            _ = DispatchSafelyAsync(task, notify);
    }

    /// <summary>Fire-and-forget: webhook delivery must not add latency (or
    /// failure modes) to task saves; delivery errors are logged inside.</summary>
    private async Task DispatchSafelyAsync(
        AgentTask task, IReadOnlyList<TaskPushNotificationConfig> configs)
    {
        try
        {
            await pushNotifier.DispatchTerminalAsync(task, configs, CancellationToken.None);
        }
        // codeql[cs/catch-of-all-exceptions] fire-and-forget push dispatch —
        // any notifier failure is logged; the task is already terminal.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "a2a push dispatch failed for task {TaskId}", task.Id);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        await db.A2aTasks.Where(t => t.TaskId == taskId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ListTasksResponse> ListTasksAsync(
        ListTasksRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = Open();
        var query = db.A2aTasks.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(request.ContextId))
            query = query.Where(t => t.ContextId == request.ContextId);
        if (request.Status is { } status)
        {
            var name = status.ToString();
            query = query.Where(t => t.State == name);
        }

        // Timestamp filter + ordering run client-side: SQLite cannot translate
        // DateTimeOffset comparisons/ORDER BY (same convention as evidence
        // receipts). Task volume is bounded by the retention purge.
        var candidates = await query
            .Select(t => new { t.TaskId, t.LastUpdatedAt })
            .ToListAsync(cancellationToken);
        if (request.StatusTimestampAfter is { } after)
            candidates = candidates.Where(c => c.LastUpdatedAt > after).ToList();

        var total = candidates.Count;
        var pageSize = Math.Clamp(request.PageSize ?? 50, 1, 100);
        var offset = int.TryParse(request.PageToken, out var parsed) ? Math.Max(0, parsed) : 0;
        var pageIds = candidates
            .OrderByDescending(c => c.LastUpdatedAt)
            .Skip(offset).Take(pageSize)
            .Select(c => c.TaskId).ToList();

        var rows = pageIds.Count == 0 ? [] : await db.A2aTasks.AsNoTracking()
            .Where(t => pageIds.Contains(t.TaskId))
            .ToListAsync(cancellationToken);
        var order = pageIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var tasks = rows
            .OrderBy(r => order[r.TaskId])
            .Select(r => ApplyListOptions(DeserializeTask(r.TaskJson), request))
            .ToList();

        return new ListTasksResponse
        {
            Tasks = tasks,
            TotalSize = total,
            PageSize = pageSize,
            NextPageToken = offset + tasks.Count < total
                ? (offset + tasks.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty
        };
    }

    /// <summary>RF-001 TTL purge — removes tasks older than
    /// <c>A2a:TaskRetentionHours</c> (default 72h). Called by the maintenance
    /// loop; age is measured from creation so a stale non-terminal task is
    /// also reclaimed.</summary>
    public async Task<int> PurgeOlderThanAsync(DateTimeOffset createdBefore, CancellationToken ct = default)
    {
        await using var db = Open();
        // Client-side timestamp compare — SQLite can't translate it (see
        // ListTasksAsync); ids project small before the batch delete.
        var staleIds = (await db.A2aTasks
                .Select(t => new { t.TaskId, t.CreatedAt })
                .ToListAsync(ct))
            .Where(t => t.CreatedAt < createdBefore)
            .Select(t => t.TaskId).ToList();
        return staleIds.Count == 0 ? 0
            : await db.A2aTasks.Where(t => staleIds.Contains(t.TaskId))
                .ExecuteDeleteAsync(ct);
    }

    // ---- Push notification configuration (RF-003) — stored on the task row
    // so a terminal transition inside SaveTaskAsync can fan out without a
    // second query. Called by KnowledgeHubA2AServer's overrides.

    public async Task<IReadOnlyList<TaskPushNotificationConfig>> GetPushConfigsAsync(
        string taskId, CancellationToken ct = default)
    {
        await using var db = Open();
        var row = await db.A2aTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TaskId == taskId, ct);
        return row is null ? [] : DeserializeConfigs(row.PushConfigsJson);
    }

    /// <summary>Adds or replaces a config (matched by id); returns the stored
    /// entry. Throws <see cref="A2AException"/> TaskNotFound when the task is
    /// unknown — the caller must create the task first.</summary>
    public async Task<TaskPushNotificationConfig> UpsertPushConfigAsync(
        string taskId, TaskPushNotificationConfig config, CancellationToken ct = default)
    {
        await using var db = Open();
        var row = await db.A2aTasks.FirstOrDefaultAsync(t => t.TaskId == taskId, ct)
            ?? throw new A2AException($"Task '{taskId}' not found.", A2AErrorCode.TaskNotFound);

        var configs = DeserializeConfigs(row.PushConfigsJson).ToList();
        var existing = configs.FindIndex(c => c.Id == config.Id);
        if (existing >= 0)
            configs[existing] = config;
        else
            configs.Add(config);
        row.PushConfigsJson = JsonSerializer.Serialize(configs, A2AJsonUtilities.DefaultOptions);
        row.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return config;
    }

    public async Task DeletePushConfigAsync(string taskId, string configId, CancellationToken ct = default)
    {
        await using var db = Open();
        var row = await db.A2aTasks.FirstOrDefaultAsync(t => t.TaskId == taskId, ct)
            ?? throw new A2AException($"Task '{taskId}' not found.", A2AErrorCode.TaskNotFound);
        var configs = DeserializeConfigs(row.PushConfigsJson);
        if (configs.All(c => c.Id != configId))
            throw new A2AException(
                $"Push notification config '{configId}' not found on task '{taskId}'.",
                A2AErrorCode.TaskNotFound);
        row.PushConfigsJson = configs.Count == 1
            ? null
            : JsonSerializer.Serialize(
                configs.Where(c => c.Id != configId).ToList(), A2AJsonUtilities.DefaultOptions);
        row.LastUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static AgentTask ApplyListOptions(AgentTask task, ListTasksRequest request)
    {
        if (request.HistoryLength is { } historyLength
            && task.History is { } history && history.Count > historyLength)
            task.History = history.Skip(history.Count - historyLength).ToList();
        if (request.IncludeArtifacts == false)
            task.Artifacts = null;
        return task;
    }

    private static bool IsTerminalStateName(string state) =>
        Enum.TryParse<TaskState>(state, ignoreCase: true, out var parsed) && parsed.IsTerminal();

    private static string SerializeTask(AgentTask task) =>
        JsonSerializer.Serialize(task, A2AJsonUtilities.DefaultOptions);

    private static AgentTask DeserializeTask(string json) =>
        JsonSerializer.Deserialize<AgentTask>(json, A2AJsonUtilities.DefaultOptions)!;

    internal static List<TaskPushNotificationConfig> DeserializeConfigs(string? json) =>
        json is null ? []
            : JsonSerializer.Deserialize<List<TaskPushNotificationConfig>>(
                json, A2AJsonUtilities.DefaultOptions) ?? [];
}
