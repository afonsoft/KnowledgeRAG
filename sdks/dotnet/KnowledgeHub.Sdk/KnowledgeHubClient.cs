using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KnowledgeHub.Sdk;

/// <summary>
/// Client for the Knowledge MCP Hub. Connects over Streamable HTTP MCP
/// (<c>/mcp</c>) with a Bearer <c>aft_*</c> key and exposes the tool catalog
/// both as a typed facade and as <see cref="AIFunction"/>s for
/// Microsoft.Extensions.AI / Semantic Kernel.
/// </summary>
public sealed class KnowledgeHubClient : IAsyncDisposable
{
    private readonly McpClient _mcp;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly KnowledgeHubClientOptions _options;

    private KnowledgeHubClient(McpClient mcp, HttpClient http, bool ownsHttp, KnowledgeHubClientOptions options)
    {
        _mcp = mcp;
        _http = http;
        _ownsHttp = ownsHttp;
        _options = options;
    }

    /// <summary>Underlying MCP client — full escape hatch for resources, prompts,
    /// notifications and any tool not covered by the typed facade.</summary>
    public McpClient Mcp => _mcp;

    /// <summary>Connects to the hub and completes the MCP initialize handshake.</summary>
    public static async Task<KnowledgeHubClient> ConnectAsync(
        string baseUrl, string apiKey, CancellationToken cancellationToken = default)
        => await ConnectAsync(new KnowledgeHubClientOptions { BaseUrl = new Uri(baseUrl), ApiKey = apiKey }, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Connects to the hub and completes the MCP initialize handshake.</summary>
    public static async Task<KnowledgeHubClient> ConnectAsync(
        KnowledgeHubClientOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options.ApiKey);

        var http = new HttpClient { BaseAddress = options.BaseUrl };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(options.BaseUrl, options.McpPath),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {options.ApiKey}"
            }
        });

        var mcp = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = options.ClientName, Version = "0.1.0" }
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new KnowledgeHubClient(mcp, http, ownsHttp: true, options);
    }

    /// <summary>Live MCP tool catalog (dynamic — reflects the key's scopes and
    /// the sources/upstream providers registered on the hub). Each entry is an
    /// <see cref="AIFunction"/> ready for chat-client tool calling.</summary>
    public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
        => await _mcp.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>The tool catalog as <see cref="AIFunction"/>s — pass to
    /// <c>ChatOptions.Tools</c> of Microsoft.Extensions.AI or Semantic Kernel.</summary>
    public async Task<IReadOnlyList<AIFunction>> AsAIToolsAsync(CancellationToken cancellationToken = default)
        => (await ListToolsAsync(cancellationToken).ConfigureAwait(false))
            .Select(t => (AIFunction)t).ToList();

    /// <summary>Calls any tool by name with raw arguments; normalizes the result.</summary>
    public async Task<HubToolResult> CallToolAsync(
        string toolName, IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.CallTimeout);
        CallToolResult result;
        try
        {
            result = await _mcp.CallToolAsync(toolName,
                arguments ?? new Dictionary<string, object?>(), cancellationToken: cts.Token)
                .ConfigureAwait(false);
        }
        catch (McpProtocolException ex)
        {
            // Remote JSON-RPC error (unknown tool, missing/invalid args).
            throw new KnowledgeHubToolException($"tool '{toolName}' failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            // The hub may also answer JSON-RPC errors with non-2xx + an
            // SSE-framed error body — surface that, not the raw status.
            var detail = ex.Data["ResponseBody"] as string ?? ex.Message;
            throw new KnowledgeHubToolException(
                $"tool '{toolName}' failed: {ExtractRpcError(detail)}", ex);
        }

        var text = string.Join("\n",
            result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return new HubToolResult
        {
            Text = text,
            Structured = result.StructuredContent is { ValueKind: JsonValueKind.Undefined } ? null
                : result.StructuredContent,
            IsError = result.IsError ?? false
        };
    }

    private static string ExtractRpcError(string body)
    {
        foreach (var l in body.Split('\n')
                     .Select(l => l.TrimStart())
                     .Where(l => l.StartsWith("data:", StringComparison.Ordinal)))
        {
            try
            {
                using var doc = JsonDocument.Parse(l[5..].Trim());
                if (doc.RootElement.TryGetProperty("error", out var err)
                    && err.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? body;
            }
            catch (JsonException) { /* keep scanning */ }
        }
        return body;
    }

    /// <summary><c>search_knowledge</c>: hybrid retrieval (FTS5 + vector + graph
    /// arms fused by RRF) with corrective grading.</summary>
    public async Task<HubSearchResult> SearchAsync(
        string query, int topK = 10, Guid? sourceId = null, string? mode = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["query"] = query, ["topK"] = topK };
        if (sourceId is { } sid) args["sourceId"] = sid.ToString();
        if (mode is not null) args["mode"] = mode;

        var result = await CallToolAsync("search_knowledge", args, cancellationToken).ConfigureAwait(false);
        result.ThrowIfError();
        if (result.Structured is { } s)
            return s.Deserialize<HubSearchResult>(HubJson.Options)!;
        return new HubSearchResult { Results = [] };
    }

    /// <summary><c>ask_knowledge</c>: retrieval + grounded synthesis with
    /// citations; abstains honestly when evidence is insufficient.</summary>
    public async Task<HubAskAnswer> AskAsync(
        string question, int topK = 5, Guid? sourceId = null, string? mode = null,
        bool? generate = null, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["question"] = question, ["topK"] = topK };
        if (sourceId is { } sid) args["sourceId"] = sid.ToString();
        if (mode is not null) args["mode"] = mode;
        if (generate is { } g) args["generate"] = g;

        var result = await CallToolAsync("ask_knowledge", args, cancellationToken).ConfigureAwait(false);
        result.ThrowIfError();
        if (result.Structured is { } s)
            return s.Deserialize<HubAskAnswer>(HubJson.Options)!;
        // Raw-context fallback (generate=false or no provider configured).
        return new HubAskAnswer { Answer = result.Text, Generated = false };
    }

    /// <summary><c>agent_chat</c>: the tool-calling agent loop (search, graph,
    /// upstream proxies, live actions). May pause for HITL approval —
    /// <see cref="HubAgentResult.AwaitingApprovalId"/> is then set.</summary>
    public async Task<HubAgentResult> AgentChatAsync(
        string prompt, IReadOnlyList<string>? tools = null, int? maxIterations = null,
        bool allowWrite = false, Guid? threadId = null, bool persist = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["prompt"] = prompt };
        if (tools is not null) args["tools"] = tools;
        if (maxIterations is { } mi) args["maxIterations"] = mi;
        if (allowWrite) args["allowWrite"] = true;
        if (threadId is { } tid) args["threadId"] = tid.ToString();
        if (persist) args["persist"] = true;

        var result = await CallToolAsync("agent_chat", args, cancellationToken).ConfigureAwait(false);
        result.ThrowIfError();
        if (result.Structured is { } s)
            return s.Deserialize<HubAgentResult>(HubJson.Options)!;
        return new HubAgentResult { Answer = result.Text };
    }

    /// <summary><c>read_document</c>: full text of a document (use a citation's
    /// <see cref="HubCitation.Path"/> or URI).</summary>
    public async Task<string> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await CallToolAsync("read_document",
            new Dictionary<string, object?> { ["path"] = path }, cancellationToken).ConfigureAwait(false);
        result.ThrowIfError();
        return result.Text;
    }

    /// <summary><c>write_knowledge</c>: register a new document in the connected
    /// vault. Requires a key with write scope; may trigger an HITL approval.</summary>
    public Task<HubToolResult> WriteKnowledgeAsync(
        string title, string content, CancellationToken cancellationToken = default)
        => CallToolAsync("write_knowledge",
            new Dictionary<string, object?> { ["title"] = title, ["content"] = content }, cancellationToken);

    /// <summary><c>write_note</c>: append a note into the vault at an optional path.</summary>
    public Task<HubToolResult> WriteNoteAsync(
        string title, string content, string? path = null, CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["title"] = title, ["content"] = content };
        if (path is not null) args["path"] = path;
        return CallToolAsync("write_note", args, cancellationToken);
    }

    /// <summary><c>set_chat_settings</c>: pins a chat LLM endpoint/model for
    /// this key's sessions (per-key settings).</summary>
    public Task<HubToolResult> SetChatSettingsAsync(
        string? endpoint = null, string? model = null, string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>();
        if (endpoint is not null) args["endpoint"] = endpoint;
        if (model is not null) args["model"] = model;
        if (apiKey is not null) args["apiKey"] = apiKey;
        return CallToolAsync("set_chat_settings", args, cancellationToken);
    }

    /// <summary><c>set_api_key_settings</c>: configures an upstream integration
    /// key (<c>firecrawl</c>|<c>deepwiki</c>|<c>tavily</c>|<c>context7</c>) for
    /// this key's sessions.</summary>
    public Task<HubToolResult> SetApiKeySettingsAsync(
        string provider, string apiKey, CancellationToken cancellationToken = default)
        => CallToolAsync("set_api_key_settings",
            new Dictionary<string, object?> { ["provider"] = provider, ["apiKey"] = apiKey }, cancellationToken);

    /// <summary>Streams <c>POST /api/ask/stream</c> — meta/token/abstain/done/error events.</summary>
    public IAsyncEnumerable<HubStreamEvent> StreamAskAsync(
        string question, int? topK = null, string? mode = null,
        CancellationToken cancellationToken = default)
        => StreamAsync("/api/ask/stream",
            new SdkAskRequest { Question = question, TopK = topK, Mode = mode }, cancellationToken);

    /// <summary>Streams <c>POST /api/agent/stream</c> — meta/token/tool_start/
    /// tool_end/awaiting_approval/done/error events.</summary>
    public IAsyncEnumerable<HubStreamEvent> StreamAgentAsync(
        string prompt, HubAgentMessage[]? messages = null, int? maxIterations = null,
        bool allowWrite = false, CancellationToken cancellationToken = default)
        => StreamAsync("/api/agent/stream",
            new SdkAgentRequest
            {
                Prompt = prompt,
                Messages = messages,
                MaxIterations = maxIterations,
                AllowWrite = allowWrite
            }, cancellationToken);

    private async IAsyncEnumerable<HubStreamEvent> StreamAsync<T>(
        string path, T body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: HubJson.Options)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new KnowledgeHubToolException(
                $"{path} returned {(int)response.StatusCode}: {errorBody}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var (eventType, data) in SseReader.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            // Wire shape: data: {"seq": n, "data": {...}} — unwrap the payload.
            var payload = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner)
                ? inner : data;
            yield return new HubStreamEvent(eventType, payload.Clone());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _mcp.DisposeAsync().ConfigureAwait(false);
        if (_ownsHttp) _http.Dispose();
    }
}

internal static class HubToolResultExtensions
{
    public static void ThrowIfError(this HubToolResult result)
    {
        if (result.IsError)
            throw new KnowledgeHubToolException(result.Text);
    }
}

/// <summary>Raised when a tool call returns <c>isError: true</c>.</summary>
public sealed class KnowledgeHubToolException : Exception
{
    public KnowledgeHubToolException(string message) : base(message) { }
    public KnowledgeHubToolException(string message, Exception inner) : base(message, inner) { }
}
