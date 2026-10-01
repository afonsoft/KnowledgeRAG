using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KnowledgeHub.McpEngine.Activity;

/// <summary>
/// SDK filter factories that feed <see cref="IMcpActivityFeed"/> (SPEC-01 RF-003/RF-004).
/// </summary>
public static class McpActivityFilters
{
    private const string ToolsCallMethod = "tools/call";

    /// <summary>
    /// Request filter for <c>tools/call</c>: measures latency, captures outcome
    /// (<c>isError</c> on the result or a thrown <see cref="McpProtocolException"/>),
    /// and enforces the per-session concurrency gate.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateToolCallFilter(
        IMcpActivityFeed feed, SessionCallGate gate, IMcpRequestMetrics? metrics = null) =>
        next => async (request, cancellationToken) =>
    {
        var sessionId = request.Server.SessionId;
        var toolName = request.Params?.Name;
        var stopwatch = Stopwatch.StartNew();

        await gate.AcquireAsync(sessionId, cancellationToken);
        try
        {
            var result = await next(request, cancellationToken);
            var succeeded = result.IsError != true;
            metrics?.Record(ToolsCallMethod, sessionId is null ? "stateless" : "stateful", succeeded);
            feed.Record(new McpActivityEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Kind = McpActivityKind.ToolCall,
                SessionId = sessionId,
                Method = ToolsCallMethod,
                ToolName = toolName,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Succeeded = succeeded,
                Error = succeeded ? null : TryReadError(result),
                Caller = CallerResolver.Resolve(request.Server.Services)
            });
            return result;
        }
        catch (Exception ex)
        {
            feed.Record(new McpActivityEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Kind = McpActivityKind.ToolCall,
                SessionId = sessionId,
                Method = ToolsCallMethod,
                ToolName = toolName,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Succeeded = false,
                Error = ex.Message,
                Caller = CallerResolver.Resolve(request.Server.Services)
            });
            throw;
        }
        finally
        {
            gate.Release(sessionId);
        }
    };

    /// <summary>
    /// Incoming message filter: records non-<c>tools/call</c> JSON-RPC requests
    /// (initialize, tools/list, resources/*, ping) with method + latency.
    /// </summary>
    public static McpMessageFilter CreateRequestTelemetryFilter(
        IMcpActivityFeed feed, McpSessionRegistry registry, IMcpRequestMetrics? metrics = null) =>
        next => async (context, cancellationToken) =>
    {
        // Track the session so tools/list_changed can broadcast to live clients.
        registry.Register(context.Server);

        if (context.JsonRpcMessage is not JsonRpcRequest request || request.Method == ToolsCallMethod)
        {
            await next(context, cancellationToken);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var succeeded = true;
        string? error = null;
        try
        {
            await next(context, cancellationToken);
        }
        catch (Exception ex)
        {
            succeeded = false;
            error = ex.Message;
            throw;
        }
        finally
        {
            metrics?.Record(request.Method,
                context.Server.SessionId is null ? "stateless" : "stateful", succeeded);
            feed.Record(new McpActivityEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Kind = McpActivityKind.Request,
                SessionId = context.Server.SessionId,
                Method = request.Method,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Succeeded = succeeded,
                Error = error,
                Caller = CallerResolver.Resolve(context.Server.Services)
            });
        }
    };

    private static string? TryReadError(CallToolResult result)
    {
        var block = result.Content?.FirstOrDefault();
        if (block is TextContentBlock text)
            return text.Text;
        return block is null ? null : JsonSerializer.Serialize(block);
    }
}
