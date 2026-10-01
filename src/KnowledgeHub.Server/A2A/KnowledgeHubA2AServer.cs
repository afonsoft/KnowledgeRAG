using A2A;
using KnowledgeHub.Server.Security;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.A2A;

/// <summary>
/// SPEC-20261001-a2a-task-durability RF-003: <see cref="A2AServer"/> with the
/// push-notification config CRUD implemented — the SDK base stubs throw
/// <c>PushNotificationNotSupported</c>. Configs persist on the task row via
/// <see cref="EfA2aTaskStore"/>; webhook delivery happens inside the store on
/// terminal transition (or here immediately when the task already finished).
/// Gated by <c>A2a:PushNotifications:Enabled</c> — when off, the base stubs
/// (and the Agent Card) keep declaring the feature unsupported.
/// </summary>
public sealed class KnowledgeHubA2AServer(
    IAgentHandler handler,
    EfA2aTaskStore taskStore,
    ChannelEventNotifier notifier,
    ILogger<A2AServer> logger, // NOSONAR S6672 — repassado ao ctor do A2AServer (SDK), que exige ILogger<A2AServer>
    A2AServerOptions options,
    IConfiguration configuration,
    IA2aPushNotifier pushNotifier,
    ILogger<KnowledgeHubA2AServer> ownLogger)
    : A2AServer(handler, taskStore, notifier, logger, options)
{
    /// <summary>Card/config gate — mirrors <c>BuildAgentCard</c>'s flag.</summary>
    public const string EnabledConfigKey = "A2a:PushNotifications:Enabled";

    private bool PushEnabled => configuration.GetValue(EnabledConfigKey, true);

    /// <summary>Honors <c>SendMessageConfiguration.PushNotificationConfig</c>
    /// — the SDK base ignores the inline config field. Persisted after the
    /// call: for <c>returnImmediately</c> the task is still running so the
    /// terminal save dispatches; otherwise it fired already and the store's
    /// immediate-dispatch path covers it.</summary>
    public override async Task<SendMessageResponse> SendMessageAsync(
        SendMessageRequest request, CancellationToken cancellationToken = default)
    {
        var response = await base.SendMessageAsync(request, cancellationToken);
        await StoreInlineConfigAsync(request, response.Task?.Id, cancellationToken);
        return response;
    }

    /// <inheritdoc cref="SendMessageAsync"/>
    public override async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(
        SendMessageRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? taskId = null;
        await foreach (var response in base.SendStreamingMessageAsync(request, cancellationToken))
        {
            taskId ??= response.Task?.Id
                ?? response.StatusUpdate?.TaskId
                ?? response.ArtifactUpdate?.TaskId;
            yield return response;
        }
        await StoreInlineConfigAsync(request, taskId, cancellationToken);
    }

    private async Task StoreInlineConfigAsync(
        SendMessageRequest request, string? responseTaskId, CancellationToken ct)
    {
        if (!PushEnabled || request.Configuration?.PushNotificationConfig is not { } inline)
            return;
        var entry = new TaskPushNotificationConfig
        {
            Id = string.IsNullOrWhiteSpace(inline.Id) ? $"pn_{Guid.NewGuid():N}" : inline.Id,
            TaskId = request.Message.TaskId ?? responseTaskId ?? string.Empty,
            PushNotificationConfig = inline,
            Tenant = request.Tenant
        };
        if (string.IsNullOrEmpty(entry.TaskId))
            return;
        await ValidateWebhookAsync(inline.Url, ct);
        await taskStore.UpsertPushConfigAsync(entry.TaskId, entry, ct);
        var task = await taskStore.GetTaskAsync(entry.TaskId, ct);
        if (task?.Status.State.IsTerminal() == true)
            _ = DispatchSafelyAsync(task, [entry]);
    }

    /// <inheritdoc/>
    public override async Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
        CreateTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default)
    {
        if (!PushEnabled)
            return await base.CreateTaskPushNotificationConfigAsync(request, cancellationToken);

        await ValidateWebhookAsync(request.Config.Url, cancellationToken);

        var entry = new TaskPushNotificationConfig
        {
            Id = string.IsNullOrWhiteSpace(request.ConfigId)
                ? $"pn_{Guid.NewGuid():N}"
                : request.ConfigId,
            TaskId = request.TaskId,
            PushNotificationConfig = request.Config,
            Tenant = request.Tenant
        };
        await taskStore.UpsertPushConfigAsync(request.TaskId, entry, cancellationToken);

        // Registered after the task already finished — deliver immediately
        // instead of waiting for a transition that will never come.
        var task = await taskStore.GetTaskAsync(request.TaskId, cancellationToken);
        if (task?.Status.State.IsTerminal() == true)
            _ = DispatchSafelyAsync(task, [entry]);
        return entry;
    }

    /// <inheritdoc/>
    public override async Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default)
    {
        if (!PushEnabled)
            return await base.GetTaskPushNotificationConfigAsync(request, cancellationToken);

        var configs = await taskStore.GetPushConfigsAsync(request.TaskId, cancellationToken);
        return configs.FirstOrDefault(c => c.Id == request.Id)
            ?? throw new A2AException(
                $"Push notification config '{request.Id}' not found on task '{request.TaskId}'.",
                A2AErrorCode.TaskNotFound);
    }

    /// <inheritdoc/>
    public override async Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(
        ListTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default)
    {
        if (!PushEnabled)
            return await base.ListTaskPushNotificationConfigAsync(request, cancellationToken);

        var configs = await taskStore.GetPushConfigsAsync(request.TaskId, cancellationToken);
        var pageSize = Math.Clamp(request.PageSize ?? 50, 1, 100);
        var offset = int.TryParse(request.PageToken, out var parsed) ? Math.Max(0, parsed) : 0;
        var page = configs.Skip(offset).Take(pageSize).ToList();
        return new ListTaskPushNotificationConfigResponse
        {
            Configs = page,
            NextPageToken = offset + page.Count < configs.Count
                ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null
        };
    }

    /// <inheritdoc/>
    public override async Task DeleteTaskPushNotificationConfigAsync(
        DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default)
    {
        if (!PushEnabled)
        {
            await base.DeleteTaskPushNotificationConfigAsync(request, cancellationToken);
            return;
        }
        await taskStore.DeletePushConfigAsync(request.TaskId, request.Id, cancellationToken);
    }

    /// <summary>Webhook URLs pass the same egress guard as connector traffic:
    /// http(s) only, no private/loopback/metadata addresses — unless the
    /// deployment explicitly opted in via
    /// <c>Security:Egress:AllowPrivateNetworks</c>, which also relaxes the
    /// https requirement (LAN/dev targets).</summary>
    private async Task ValidateWebhookAsync(string url, CancellationToken ct)
    {
        var allowPrivate = configuration.GetValue("Security:Egress:AllowPrivateNetworks", false);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (!allowPrivate && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            || await EgressPolicyHandler.IsBlockedAsync(uri, allowPrivate, ct))
        {
            throw new A2AException(
                "push webhook URL rejected: must be an allowed https endpoint",
                A2AErrorCode.InvalidParams);
        }
    }

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
            ownLogger.LogWarning(ex, "a2a push dispatch failed for task {TaskId}", task.Id);
        }
    }
}
