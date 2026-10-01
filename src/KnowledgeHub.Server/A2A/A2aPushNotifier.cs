using System.Text;
using System.Text.Json;
using A2A;
using KnowledgeHub.Server.Audit.Evidence;

namespace KnowledgeHub.Server.A2A;

/// <summary>
/// SPEC-20261001-a2a-task-durability RF-003: fans out one signed webhook POST
/// per registered push config when a task reaches a terminal state.
/// Singleton — the evidence signing service is scoped, so it is resolved per
/// dispatch through <see cref="IServiceScopeFactory"/>.
/// </summary>
public interface IA2aPushNotifier
{
    /// <summary>POST the serialized task to every configured webhook. Never
    /// throws — per-endpoint failures retry and are logged.</summary>
    Task DispatchTerminalAsync(
        AgentTask task, IReadOnlyList<TaskPushNotificationConfig> configs,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IA2aPushNotifier"/>
public sealed class A2aPushNotifier(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<A2aPushNotifier> logger) : IA2aPushNotifier
{
    /// <summary>Signature header the receiver verifies against the request body.</summary>
    public const string SignatureHeader = "X-KH-Signature";
    /// <summary>Caller-supplied opaque token echoed back so the receiver can
    /// correlate the notification with the registration (A2A `token`).</summary>
    public const string TokenHeader = "X-A2A-Notification-Token";

    private const int MaxAttempts = 3;

    public async Task DispatchTerminalAsync(
        AgentTask task, IReadOnlyList<TaskPushNotificationConfig> configs,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(task, A2AJsonUtilities.DefaultOptions);
        await using var scope = scopeFactory.CreateAsyncScope();
        var evidence = scope.ServiceProvider.GetService<IEvidenceChainService>();
        var signature = evidence is null
            ? null
            : await evidence.SignPayloadAsync(body, cancellationToken);

        var http = httpClientFactory.CreateClient("a2a-push");
        foreach (var cfg in configs)
            await DeliverAsync(http, task.Id, cfg, body, signature, cancellationToken);
    }

    private async Task DeliverAsync(
        HttpClient http, string taskId, TaskPushNotificationConfig config,
        string body, string? signature, CancellationToken ct)
    {
        var url = config.PushNotificationConfig.Url;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                if (signature is not null)
                    request.Headers.TryAddWithoutValidation(SignatureHeader, signature);
                if (config.PushNotificationConfig.Token is { } token)
                    request.Headers.TryAddWithoutValidation(TokenHeader, token);

                using var response = await http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "a2a push delivered task {TaskId} → {Url} (attempt {Attempt})",
                        taskId, url, attempt);
                    return;
                }
                logger.LogDebug(
                    "a2a push to {Url} for task {TaskId} returned {Status} (attempt {Attempt}/{Max})",
                    url, taskId, (int)response.StatusCode, attempt, MaxAttempts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex,
                    "a2a push to {Url} for task {TaskId} failed (attempt {Attempt}/{Max})",
                    url, taskId, attempt, MaxAttempts);
            }

            if (attempt < MaxAttempts)
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), ct);
        }

        // SPEC AC-3: bounded retries — after 3 failures give up with a warning.
        logger.LogWarning(
            "a2a push to {Url} for task {TaskId} gave up after {Max} attempts",
            url, taskId, MaxAttempts);
    }
}
