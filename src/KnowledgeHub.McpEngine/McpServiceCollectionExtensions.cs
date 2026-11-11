using KnowledgeHub.McpEngine.Activity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KnowledgeHub.McpEngine;

/// <summary>Composition root for the KnowledgeHub MCP server (SPEC-01).</summary>
public static class McpServiceCollectionExtensions
{
    public const int DefaultMaxConcurrentCallsPerSession = 8;

    /// <summary>
    /// Registers the official MCP server (dual transport: Streamable HTTP on /mcp,
    /// legacy SSE on /mcp/sse + /mcp/message) plus the activity feed used by the
    /// Monitor UI.
    /// </summary>
    public static IMcpServerBuilder AddKnowledgeHubMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMcpActivityFeed>(_ => new McpActivityFeed(
            configuration.GetValue("Mcp:ActivityFeedCapacity", 500)));

        services.AddSingleton(_ => new SessionCallGate(
            configuration.GetValue("Mcp:MaxConcurrentCallsPerSession", DefaultMaxConcurrentCallsPerSession)));

        services.AddSingleton<McpSessionRegistry>();
        // No-op default so the engine works standalone; the server registers a
        // real IMcpRequestMetrics backed by its Meter.
        services.TryAddSingleton<IMcpRequestMetrics>(NullMcpRequestMetrics.Instance);

        var sessionMode = ParseSessionMode(configuration["Mcp:SessionMode"]);

        var builder = services
            .AddMcpServer()
            .WithHttpTransport(transport =>
            {
                // Hybrid mode (SPEC-20260918-mcp-v2-hybrid-transport): clients on the
                // initialize handshake (2025-11-25 and earlier, incl. legacy SSE) keep
                // full stateful sessions — Mcp-Session-Id, GET stream, notifications —
                // while 2026-07-28+ clients are served statelessly on the same endpoint
                // instead of being refused with -32022 and forced to downgrade.
                transport.SessionMode = sessionMode;

                // Serves GET /mcp/sse + POST /mcp/message alongside Streamable HTTP on /mcp.
                // MCP9004: legacy SSE is obsolete upstream but required for Cursor/Claude
                // Desktop clients that only implement the HTTP+SSE transport.
                // Stateless mode rejects EnableLegacySse (SSE needs session state), so the
                // endpoints only map for the session-capable modes.
#pragma warning disable MCP9004
                transport.EnableLegacySse = sessionMode != HttpServerSessionMode.Stateless;
#pragma warning restore MCP9004
            });

        services.AddOptions<McpServerOptions>()
            .Configure<IMcpActivityFeed, SessionCallGate, McpSessionRegistry, IMcpRequestMetrics, IConfiguration>(
                (options, feed, gate, registry, metrics, cfg) =>
            {
                options.ServerInfo = new Implementation { Name = "knowledge", Version = "0.1.1" };
                // SPEC-20261111-mcp-ergonomics-wave2 RF-005: admins attach
                // deployment notes (conventions, default bank, usage hints)
                // that initialize answers and tool surfaces echo back.
                var instructions = cfg["Mcp:Instructions"];
                if (!string.IsNullOrWhiteSpace(instructions))
                    options.ServerInstructions = SanitizeInstructions(instructions);
                options.Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = true },
                    Resources = new ResourcesCapability { ListChanged = true }
                };

                options.Filters.Request.CallToolFilters.Add(McpActivityFilters.CreateToolCallFilter(feed, gate, metrics));
                options.Filters.Message.IncomingFilters.Add(McpActivityFilters.CreateRequestTelemetryFilter(feed, registry, metrics));
            });

        return builder;
    }

    /// <summary>SPEC-20261111-mcp-ergonomics-wave2 RF-005: deployment
    /// instructions are operator input echoed to clients — strip HTML-ish
    /// markup, collapse whitespace and cap at 2000 chars.</summary>
    public static string SanitizeInstructions(string raw)
    {
        var cleaned = new string(raw.Where(c => c is not ('<' or '>')).ToArray());
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return cleaned.Length > 2000 ? cleaned[..2000] : cleaned;
    }

    /// <summary>
    /// Resolves the <c>Mcp:SessionMode</c> configuration value into
    /// <see cref="HttpServerSessionMode"/>. Defaults to
    /// <see cref="HttpServerSessionMode.StatefulForInitializeClients"/> (hybrid).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown at startup when the configured value is not a valid mode.
    /// </exception>
    public static HttpServerSessionMode ParseSessionMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return HttpServerSessionMode.StatefulForInitializeClients;

        // TryParse also accepts numeric strings ("0" → Stateless); only named
        // values are supported so a stray integer fails loudly instead of
        // silently picking a mode.
        if (Enum.TryParse<HttpServerSessionMode>(value, ignoreCase: true, out var mode) &&
            char.IsLetter(value.TrimStart()[0]))
            return mode;

        throw new InvalidOperationException(
            $"Invalid Mcp:SessionMode value '{value}'. Valid values: " +
            $"{nameof(HttpServerSessionMode.Stateless)}, {nameof(HttpServerSessionMode.Stateful)}, " +
            $"{nameof(HttpServerSessionMode.StatefulForInitializeClients)}.");
    }
}
