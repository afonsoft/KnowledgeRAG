using KnowledgeHub.Shared;
using KnowledgeHub.Shared.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace KnowledgeHub.Client.Services;

/// <summary>
/// Wraps the SignalR connection to /hubs/mcp (SPEC-05 RF-003).
/// Auto-reconnect with backoff; caller registers handlers before StartAsync.
/// SPEC-20260914-signalr-hub-resilience: WS|LongPolling only (SSE is never
/// supported in WASM), OperationCanceledException on StartAsync is benign
/// (dispose/navigation mid-connect), LastError surfaces the failure detail.
/// </summary>
public sealed class McpMonitorClient(NavigationManager nav) : IAsyncDisposable
{
    private readonly HubConnection _connection = new HubConnectionBuilder()
        .WithUrl(nav.ToAbsoluteUri("/hubs/mcp"),
            HttpTransportType.WebSockets | HttpTransportType.LongPolling)
        // SharedJson.Options: hub payload DTOs resolve via source-gen metadata —
        // reflection-based deserialization is off in trimmed WASM builds.
        .AddJsonProtocol(o => o.PayloadSerializerOptions = SharedJson.Options)
        .WithAutomaticReconnect()
        .Build();

    // Registered once — a post-failure "Reconectar" calls StartAsync again and
    // re-subscribing On(...) would duplicate every event invocation.
    private bool _handlersRegistered;

    public HubConnectionState State => _connection.State;

    /// <summary>Last connect failure (per-transport aggregate); null after a successful connect.</summary>
    public string? LastError { get; private set; }

    public event Action? StateChanged;
    public event Action<SessionOpenedEvent>? SessionOpened;
    public event Action<SessionClosedEvent>? SessionClosed;
    public event Action<McpMonitorEventDto>? Activity;
    public event Action<IReadOnlyList<McpMonitorEventDto>>? Snapshot;
    /// <summary>SPEC-20260925-job-progress-feed RF-002: pushed job progress.</summary>
    public event Action<IngestionProgressEventDto>? IngestionProgress;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_connection.State != HubConnectionState.Disconnected)
            return;
        if (!_handlersRegistered)
        {
            _connection.On<SessionOpenedEvent>("SessionOpened", e => SessionOpened?.Invoke(e));
            _connection.On<SessionClosedEvent>("SessionClosed", e => SessionClosed?.Invoke(e));
            _connection.On<McpMonitorEventDto>("Activity", e => Activity?.Invoke(e));
            _connection.On<IReadOnlyList<McpMonitorEventDto>>("Snapshot", s => Snapshot?.Invoke(s));
            _connection.On<IngestionProgressEventDto>("IngestionProgress", e => IngestionProgress?.Invoke(e));
            // SPEC-20260929-observability-and-tests-residual RF-001: replay the
            // per-job snapshot through the same event path as live progress.
            _connection.On<List<IngestionProgressEventDto>>("IngestionProgressSnapshot",
                list =>
                {
                    foreach (var e in list)
                        IngestionProgress?.Invoke(e);
                });
            _connection.Reconnecting += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };
            _connection.Reconnected += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };
            _connection.Closed += _ => { StateChanged?.Invoke(); return Task.CompletedTask; };
            _handlersRegistered = true;
        }

        try
        {
            await _connection.StartAsync(ct);
            LastError = null;
        }
        catch (OperationCanceledException)
        {
            // RF-001: dispose or navigation mid-connect is benign.
            return;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
