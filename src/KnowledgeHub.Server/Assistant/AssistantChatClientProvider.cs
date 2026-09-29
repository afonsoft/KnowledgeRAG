using A2A;
using KnowledgeHub.Server.Chat;
using KnowledgeHub.Server.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Server.Assistant;

/// <summary>
/// Singleton resolving the assistant <see cref="IChatClient"/> from effective
/// settings (store → env) with a cached snapshot (SPEC-20260929-a2a-assistant-
/// delegation RF-001/RF-003). Local mode builds an OpenAI-compatible client via
/// <see cref="ChatClientFactory"/>; remote mode resolves the A2A Agent Card and
/// delegates through <see cref="A2AClient"/>.
/// </summary>
public sealed class AssistantChatClientProvider(
    IOptions<AssistantOptions> envOptions,
    IIntegrationSecretStore secrets,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpFactory,
    ILogger<AssistantChatClientProvider> logger) : IAssistantChatClientProvider
{
    private readonly object _gate = new();
    private volatile Snapshot? _snapshot;

    private sealed record Snapshot(bool Enabled, string Mode, HashSet<string> Route,
        IChatClient? Client, TimeSpan Timeout);

    public IChatClient? ForSubtask(string subtask, IChatClient? main)
    {
        var snap = Current();
        if (!snap.Enabled || snap.Client is null || !snap.Route.Contains(subtask))
            return main;
        if (main is null)
            return snap.Client;
        return new AssistantFallbackChatClient(snap.Client, main, subtask, snap.Timeout, logger);
    }

    public void Invalidate()
    {
        lock (_gate)
            _snapshot = null;
    }

    private Snapshot Current()
    {
        var snap = _snapshot;
        if (snap is not null)
            return snap;
        lock (_gate)
        {
            snap ??= LoadSnapshot();
            _snapshot = snap;
            return snap;
        }
    }

    private Snapshot LoadSnapshot()
    {
        var env = envOptions.Value;
        var route = new HashSet<string>(env.Route, StringComparer.OrdinalIgnoreCase);
        var enabled = env.Enabled;
        var mode = env.Mode;
        var endpoint = env.Endpoint;
        var model = env.Model;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(env.TimeoutSeconds, 1, 300));

        var row = FindRow();
        string? key = null;
        if (row is not null)
        {
            enabled = row.Enabled;
            mode = row.Mode;
            endpoint = row.Endpoint;
            model = row.Model;
            timeout = TimeSpan.FromSeconds(Math.Clamp(row.TimeoutSeconds, 1, 300));
            if (row.RouteJson is { } json)
            {
                try
                {
                    route = new HashSet<string>(
                        System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [],
                        StringComparer.OrdinalIgnoreCase);
                }
                catch (System.Text.Json.JsonException) { /* keep env routes */ }
            }
        }
        key = secrets.GetAsync("assistant").GetAwaiter().GetResult() ?? env.ApiKey;

        IChatClient? client = null;
        if (enabled && !string.IsNullOrWhiteSpace(endpoint))
        {
            try
            {
                client = mode.Equals("remote", StringComparison.OrdinalIgnoreCase)
                    ? BuildRemoteClient(endpoint!, key)
                    : ChatClientFactory.Create(new ChatProviderOptions
                    {
                        Provider = "openai",
                        Endpoint = endpoint,
                        Model = model,
                        ApiKey = key,
                        TimeoutSeconds = (int)timeout.TotalSeconds
                    }, httpFactory);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "assistant client build failed ({Mode}) — sub-tasks use main model", mode);
            }
        }
        return new Snapshot(enabled, mode, route, client, timeout);
    }

    private IChatClient? BuildRemoteClient(string baseUrl, string? key)
    {
        var http = httpFactory.CreateClient("assistant-a2a");
        if (!string.IsNullOrEmpty(key))
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var resolver = new A2ACardResolver(new Uri(baseUrl), http,
            "/.well-known/agent-card.json", logger);
        var card = resolver.GetAgentCardAsync().GetAwaiter().GetResult();
        var url = card.SupportedInterfaces
            .FirstOrDefault(i => i.ProtocolBinding == ProtocolBindingNames.JsonRpc)?.Url
            ?? card.SupportedInterfaces.FirstOrDefault()?.Url;
        if (url is null)
        {
            logger.LogWarning("remote assistant agent card at {BaseUrl} has no interfaces", baseUrl);
            return null;
        }
        return new A2AChatClient(new A2AClient(new Uri(url), http), "agent_chat");
    }

    private Domain.Entities.AssistantSettings? FindRow()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.KnowledgeHubDbContext>();
        return db.AssistantSettings.AsNoTracking().SingleOrDefault();
    }
}
