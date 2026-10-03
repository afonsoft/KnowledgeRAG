package io.github.afonsoft.knowledgehub;

import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import io.github.afonsoft.knowledgehub.model.HubModels.HubAgentMessage;
import io.github.afonsoft.knowledgehub.model.HubModels.HubAgentResult;
import io.github.afonsoft.knowledgehub.model.HubModels.HubAskAnswer;
import io.github.afonsoft.knowledgehub.model.HubModels.HubSearchResult;
import io.github.afonsoft.knowledgehub.model.HubModels.HubStreamEvent;
import io.github.afonsoft.knowledgehub.model.HubModels.HubToolDescriptor;
import io.github.afonsoft.knowledgehub.model.HubModels.HubToolResult;
import io.modelcontextprotocol.client.McpClient;
import io.modelcontextprotocol.client.McpSyncClient;
import io.modelcontextprotocol.client.transport.HttpClientStreamableHttpTransport;
import io.modelcontextprotocol.spec.McpSchema;
import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/**
 * Client for the Knowledge MCP Hub. Wraps the hub's native MCP endpoint
 * (Streamable HTTP, JSON-RPC 2.0) via the official MCP Java SDK, adds a
 * typed facade over the stable tools ({@code search_knowledge},
 * {@code ask_knowledge}, {@code agent_chat}, {@code read_document},
 * {@code write_knowledge}, {@code write_note}, per-key settings) and
 * REST SSE streaming for token-level output.
 *
 * <p>Usage:
 * <pre>{@code
 * try (var hub = KnowledgeHubClient.connect("http://localhost:5009", "aft_...")) {
 *     var answer = hub.ask("what does the spec say about retries?");
 *     System.out.println(answer.answer());
 * }
 * }</pre>
 *
 * <p>Generate an {@code aft_*} key on the hub's /api-keys page. The tool
 * catalog is dynamic per key scope and registered sources —
 * {@link #listTools()} is the source of truth; {@code read_document} /
 * {@code write_note} only exist when an Obsidian source is registered.
 */
public final class KnowledgeHubClient implements AutoCloseable {

    private static final ObjectMapper MAPPER = new ObjectMapper()
            .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);

    private final KnowledgeHubClientOptions options;
    private final HttpClient http;
    private final McpSyncClient mcp;

    private KnowledgeHubClient(KnowledgeHubClientOptions options, McpSyncClient mcp) {
        this.options = options;
        this.http = HttpClient.newHttpClient();
        this.mcp = mcp;
    }

    /** Connects and performs the MCP initialize handshake. */
    public static KnowledgeHubClient connect(String baseUrl, String apiKey) {
        return connect(KnowledgeHubClientOptions.builder()
                .baseUrl(baseUrl).apiKey(apiKey).build());
    }

    /** Connects and performs the MCP initialize handshake. */
    public static KnowledgeHubClient connect(KnowledgeHubClientOptions options) {
        var transport = HttpClientStreamableHttpTransport.builder(options.baseUrl())
                .endpoint(options.mcpPath())
                .requestBuilder(HttpRequest.newBuilder()
                        .header("Authorization", "Bearer " + options.apiKey()))
                .build();

        var mcp = McpClient.sync(transport)
                .requestTimeout(options.requestTimeout())
                .clientInfo(new McpSchema.Implementation("knowledgehub-java-sdk", "0.1.0"))
                .build();
        mcp.initialize();
        return new KnowledgeHubClient(options, mcp);
    }

    /** Raw MCP session — escape hatch for resources, prompts, notifications. */
    public McpSyncClient rawSession() {
        return mcp;
    }

    // -------------------------------------------------------------- tools

    /** tools/list — the full catalog for this key (dynamic per scope/sources). */
    public List<HubToolDescriptor> listTools() {
        ListToolsResultMapper mapper = new ListToolsResultMapper();
        var result = mcp.listTools();
        var out = new ArrayList<HubToolDescriptor>(result.tools().size());
        for (var tool : result.tools()) {
            out.add(new HubToolDescriptor(tool.name(), tool.description(), mapper.schema(tool)));
        }
        return out;
    }

    /** tools/call — generic invocation returning text + structuredContent. */
    @SuppressWarnings("unchecked")
    public HubToolResult callTool(String name, Map<String, Object> arguments) {
        McpSchema.CallToolResult result;
        try {
            result = mcp.callTool(CallToolRequestFactory.create(name,
                    arguments == null ? Map.of() : arguments));
        } catch (RuntimeException ex) {
            throw new KnowledgeHubToolException("tool '" + name + "' failed: "
                    + RpcErrorMapper.message(ex), ex);
        }

        var text = new StringBuilder();
        for (var content : result.content()) {
            if (content instanceof McpSchema.TextContent tc) {
                if (text.length() > 0) {
                    text.append('\n');
                }
                text.append(tc.text());
            }
        }
        var structured = result.structuredContent() instanceof Map
                ? (Map<String, Object>) result.structuredContent() : null;
        return new HubToolResult(text.toString(), structured, Boolean.TRUE.equals(result.isError()));
    }

    /**
     * {@code search_knowledge}: hybrid retrieval (FTS5 + vector + graph arms
     * fused by RRF) with corrective grading.
     */
    public HubSearchResult search(String query) {
        return search(query, 10, null, null);
    }

    public HubSearchResult search(String query, int topK, UUID sourceId, String mode) {
        var args = new LinkedHashMap<String, Object>();
        args.put("query", query);
        args.put("topK", topK);
        if (sourceId != null) {
            args.put("sourceId", sourceId.toString());
        }
        if (mode != null) {
            args.put("mode", mode);
        }
        var result = callTool("search_knowledge", args);
        throwIfError(result);
        return convert(result, HubSearchResult.class,
                () -> new HubSearchResult(List.of(), null, false, 0, false, false, null));
    }

    /**
     * {@code ask_knowledge}: retrieval + grounded synthesis with citations;
     * abstains honestly when evidence is insufficient.
     */
    public HubAskAnswer ask(String question) {
        return ask(question, 5, null, null, null);
    }

    public HubAskAnswer ask(String question, int topK, UUID sourceId, String mode,
            Boolean generate) {
        var args = new LinkedHashMap<String, Object>();
        args.put("question", question);
        args.put("topK", topK);
        if (sourceId != null) {
            args.put("sourceId", sourceId.toString());
        }
        if (mode != null) {
            args.put("mode", mode);
        }
        if (generate != null) {
            args.put("generate", generate);
        }
        var result = callTool("ask_knowledge", args);
        throwIfError(result);
        if (result.structured() == null) {
            // Raw-context fallback (generate=false or no provider configured).
            return new HubAskAnswer(result.text(), List.of(), null, false, false, null,
                    false, false, false);
        }
        return convert(result, HubAskAnswer.class,
                () -> new HubAskAnswer(result.text(), List.of(), null, false, false, null,
                        false, false, false));
    }

    /**
     * {@code agent_chat}: the tool-calling agent loop. May pause for HITL
     * approval — {@code awaitingApprovalId} is then set on the result.
     */
    public HubAgentResult agentChat(String prompt) {
        return agentChat(prompt, null, null, false, null, false);
    }

    public HubAgentResult agentChat(String prompt, List<String> tools, Integer maxIterations,
            boolean allowWrite, UUID threadId, boolean persist) {
        var args = new LinkedHashMap<String, Object>();
        args.put("prompt", prompt);
        if (tools != null) {
            args.put("tools", tools);
        }
        if (maxIterations != null) {
            args.put("maxIterations", maxIterations);
        }
        if (allowWrite) {
            args.put("allowWrite", true);
        }
        if (threadId != null) {
            args.put("threadId", threadId.toString());
        }
        if (persist) {
            args.put("persist", true);
        }
        var result = callTool("agent_chat", args);
        throwIfError(result);
        return convert(result, HubAgentResult.class,
                () -> new HubAgentResult(result.text(), List.of(), List.of(), 0, 0,
                        false, null, null, null));
    }

    /**
     * {@code read_document}: full text of a document (a citation's
     * {@code path} or URI). Only in the catalog with an Obsidian source.
     */
    public String readDocument(String path) {
        var result = callTool("read_document", Map.of("path", path));
        throwIfError(result);
        return result.text();
    }

    /**
     * {@code write_knowledge}: register a new document. Requires a key with
     * write scope; may trigger an HITL approval.
     */
    public HubToolResult writeKnowledge(String title, String content) {
        return callTool("write_knowledge", Map.of("title", title, "content", content));
    }

    /** {@code write_note}: append a note into the vault at an optional path. */
    public HubToolResult writeNote(String title, String content, String path) {
        var args = new LinkedHashMap<String, Object>();
        args.put("title", title);
        args.put("content", content);
        if (path != null) {
            args.put("path", path);
        }
        return callTool("write_note", args);
    }

    /** {@code set_chat_settings}: pins a chat LLM endpoint/model for this key. */
    public HubToolResult setChatSettings(String endpoint, String model, String apiKey) {
        var args = new LinkedHashMap<String, Object>();
        if (endpoint != null) {
            args.put("endpoint", endpoint);
        }
        if (model != null) {
            args.put("model", model);
        }
        if (apiKey != null) {
            args.put("apiKey", apiKey);
        }
        return callTool("set_chat_settings", args);
    }

    /**
     * {@code set_api_key_settings}: configures an upstream integration key
     * (firecrawl|deepwiki|tavily|context7) for this key's sessions.
     */
    public HubToolResult setApiKeySettings(String provider, String apiKey) {
        return callTool("set_api_key_settings",
                Map.of("provider", provider, "apiKey", apiKey));
    }

    // ----------------------------------------------------------- streaming

    /** Streams {@code POST /api/ask/stream} — meta/token/abstain/done/error. */
    public List<HubStreamEvent> streamAsk(String question, Integer topK, String mode) {
        var body = new LinkedHashMap<String, Object>();
        body.put("question", question);
        if (topK != null) {
            body.put("topK", topK);
        }
        if (mode != null) {
            body.put("mode", mode);
        }
        return stream("/api/ask/stream", body);
    }

    /** Streams {@code POST /api/agent/stream} — meta/token/tool_start/done/error. */
    public List<HubStreamEvent> streamAgent(String prompt, List<HubAgentMessage> messages,
            Integer maxIterations, boolean allowWrite) {
        var body = new LinkedHashMap<String, Object>();
        body.put("prompt", prompt);
        if (messages != null) {
            body.put("messages", messages);
        }
        if (maxIterations != null) {
            body.put("maxIterations", maxIterations);
        }
        if (allowWrite) {
            body.put("allowWrite", true);
        }
        return stream("/api/agent/stream", body);
    }

    private List<HubStreamEvent> stream(String path, Map<String, Object> body) {
        HttpResponse<java.io.InputStream> response;
        try {
            var request = HttpRequest.newBuilder()
                    .uri(URI.create(options.baseUrl() + path))
                    .header("Authorization", "Bearer " + options.apiKey())
                    .header("Accept", "text/event-stream")
                    .header("Content-Type", "application/json")
                    .POST(HttpRequest.BodyPublishers.ofByteArray(MAPPER.writeValueAsBytes(body)))
                    .build();
            response = http.send(request, HttpResponse.BodyHandlers.ofInputStream());
        } catch (IOException | InterruptedException ex) {
            if (ex instanceof InterruptedException) {
                Thread.currentThread().interrupt();
            }
            throw new KnowledgeHubToolException(path + " request failed: " + ex.getMessage(), ex);
        }
        if (response.statusCode() >= 400) {
            String errorBody;
            try {
                errorBody = new String(response.body().readAllBytes(), java.nio.charset.StandardCharsets.UTF_8);
            } catch (IOException ex) {
                errorBody = "<unreadable: " + ex.getMessage() + ">";
            }
            throw new KnowledgeHubToolException(path + " returned " + response.statusCode()
                    + ": " + errorBody);
        }
        try {
            var events = new ArrayList<HubStreamEvent>();
            for (var frame : SseReader.readAll(response.body(), MAPPER)) {
                events.add(new HubStreamEvent(frame.event(), frame.data()));
            }
            return events;
        } catch (IOException ex) {
            throw new KnowledgeHubToolException(path + " stream failed: " + ex.getMessage(), ex);
        }
    }

    // ------------------------------------------------------------ internals

    static void throwIfError(HubToolResult result) {
        if (result.isError()) {
            throw new KnowledgeHubToolException(result.text());
        }
    }

    static <T> T convert(HubToolResult result, Class<T> type, java.util.function.Supplier<T> fallback) {
        if (result.structured() == null) {
            return fallback.get();
        }
        return MAPPER.convertValue(result.structured(), type);
    }

    @Override
    public void close() {
        mcp.closeGracefully();
    }

    /** Extracts a useful message from transport/JSON-RPC errors. */
    private static final class RpcErrorMapper {
        static String message(RuntimeException ex) {
            var detail = ex.getMessage();
            return detail != null && !detail.isBlank() ? detail : ex.getClass().getSimpleName();
        }
    }

    /** Maps {@code McpSchema.Tool} inputSchema records to plain JSON-Schema maps. */
    private static final class ListToolsResultMapper {
        Map<String, Object> schema(McpSchema.Tool tool) {
            Object schema;
            try {
                schema = MAPPER.convertValue(tool.inputSchema(),
                        new TypeReference<Map<String, Object>>() {
                        });
            } catch (IllegalArgumentException ex) {
                return Map.of();
            }
            return schema instanceof Map ? cast(schema) : Map.of();
        }

        @SuppressWarnings("unchecked")
        private Map<String, Object> cast(Object value) {
            return (Map<String, Object>) value;
        }
    }

    /** Builds a CallToolRequest across SDK API shapes. */
    private static final class CallToolRequestFactory {
        static McpSchema.CallToolRequest create(String name, Map<String, Object> args) {
            return McpSchema.CallToolRequest.builder(name).arguments(args).build();
        }
    }
}
