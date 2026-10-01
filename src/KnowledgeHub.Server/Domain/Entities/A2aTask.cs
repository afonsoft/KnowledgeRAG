namespace KnowledgeHub.Server.Domain.Entities;

/// <summary>
/// SPEC-20261001-a2a-task-durability RF-001: durable row behind the A2A
/// <c>ITaskStore</c>. <see cref="TaskJson"/> carries the full serialized
/// <c>AgentTask</c> (status + history + artifacts); <see cref="State"/> and
/// <see cref="ContextId"/> are denormalized for list filters. Push webhook
/// registrations live on the same row (<see cref="PushConfigsJson"/>) so a
/// terminal transition can notify subscribers without a second lookup.
/// </summary>
public sealed class A2aTask
{
    /// <summary>A2A task id (server-generated <c>N</c>-format GUID).</summary>
    public required string TaskId { get; set; }
    /// <summary>A2A context id grouping related tasks.</summary>
    public required string ContextId { get; set; }
    /// <summary><c>TaskState</c> name — Submitted|Working|Completed|Failed|Canceled|InputRequired|Rejected.</summary>
    public required string State { get; set; }
    /// <summary>Serialized <c>AgentTask</c> — the SDK projects events into the
    /// task object; the store persists/returns it verbatim.</summary>
    public required string TaskJson { get; set; }
    /// <summary>Serialized <c>List&lt;TaskPushNotificationConfig&gt;</c> — webhook
    /// subscriptions registered via tasks/pushNotificationConfig/* (RF-003).</summary>
    public string? PushConfigsJson { get; set; }
    /// <summary>Terminal webhook delivery already fired — prevents double-send
    /// when a terminal task is saved again.</summary>
    public bool PushDispatched { get; set; }
    /// <summary><c>aft_*</c> key id of the caller that created the task
    /// (ambient at creation time — best-effort provenance).</summary>
    public string? CallerKeyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
