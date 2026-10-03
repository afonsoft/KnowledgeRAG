package io.github.afonsoft.knowledgehub.langchain4j;

import com.fasterxml.jackson.databind.ObjectMapper;
import dev.langchain4j.agent.tool.ToolSpecification;
import dev.langchain4j.model.chat.request.json.JsonArraySchema;
import dev.langchain4j.model.chat.request.json.JsonBooleanSchema;
import dev.langchain4j.model.chat.request.json.JsonEnumSchema;
import dev.langchain4j.model.chat.request.json.JsonIntegerSchema;
import dev.langchain4j.model.chat.request.json.JsonNumberSchema;
import dev.langchain4j.model.chat.request.json.JsonObjectSchema;
import dev.langchain4j.model.chat.request.json.JsonSchemaElement;
import dev.langchain4j.model.chat.request.json.JsonStringSchema;
import dev.langchain4j.service.tool.ToolExecutor;
import io.github.afonsoft.knowledgehub.KnowledgeHubClient;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * LangChain4j adapter: exposes the hub's MCP tool catalog as
 * {@link ToolSpecification}/{@link ToolExecutor} pairs, so any LangChain4j
 * {@code AiService} can call KnowledgeHub tools natively.
 *
 * <pre>{@code
 * var hubTools = KnowledgeHubLangChain4j.asTools(hub);
 * var assistant = AiServices.builder(MyAssistant.class)
 *     .chatModel(model)
 *     .tools(hubTools)   // Map<ToolSpecification, ToolExecutor>
 *     .build();
 * }</pre>
 */
public final class KnowledgeHubLangChain4j {

    private static final ObjectMapper MAPPER = new ObjectMapper();

    private KnowledgeHubLangChain4j() {
    }

    /**
     * Maps every tool in the hub's dynamic catalog (for this API key) to a
     * ToolSpecification + executor that routes through {@code tools/call}.
     */
    public static Map<ToolSpecification, ToolExecutor> asTools(KnowledgeHubClient client) {
        var out = new LinkedHashMap<ToolSpecification, ToolExecutor>();
        for (var tool : client.listTools()) {
            var spec = ToolSpecification.builder()
                    .name(tool.name())
                    .description(tool.description() == null ? "" : tool.description())
                    .parameters(toJsonObjectSchema(tool.inputSchema()))
                    .build();
            out.put(spec, executor(client, tool.name()));
        }
        return out;
    }

    /** Executor that calls the hub tool with the LLM-provided JSON arguments. */
    private static ToolExecutor executor(KnowledgeHubClient client, String toolName) {
        return (request, memoryId) -> {
            Map<String, Object> args;
            try {
                @SuppressWarnings("unchecked")
                Map<String, Object> parsed = request.arguments() == null
                        || request.arguments().isBlank()
                        ? Map.of()
                        : MAPPER.readValue(request.arguments(), Map.class);
                args = parsed;
            } catch (Exception ex) {
                return "invalid arguments: " + ex.getMessage();
            }
            var result = client.callTool(toolName, args);
            if (result.isError()) {
                return "tool error: " + result.text();
            }
            return result.text();
        };
    }

    // ----------------------------------------------------- schema conversion

    /** Converts a JSON-Schema map (from the MCP tool catalog) to JsonObjectSchema. */
    @SuppressWarnings("unchecked")
    public static JsonObjectSchema toJsonObjectSchema(Map<String, Object> schema) {
        var builder = JsonObjectSchema.builder();
        Object properties = schema == null ? null : schema.get("properties");
        if (properties instanceof Map) {
            ((Map<String, Object>) properties).forEach((name, spec) -> {
                var element = toElement(spec instanceof Map ? (Map<String, Object>) spec : Map.of());
                builder.addProperty(name, element);
            });
        }
        Object required = schema == null ? null : schema.get("required");
        if (required instanceof List) {
            builder.required((List<String>) required);
        }
        return builder.build();
    }

    @SuppressWarnings("unchecked")
    private static JsonSchemaElement toElement(Map<String, Object> spec) {
        String description = spec.get("description") instanceof String d ? d : null;
        Object enumValues = spec.get("enum");
        if (enumValues instanceof List) {
            var b = JsonEnumSchema.builder();
            if (description != null) {
                b.description(description);
            }
            b.enumValues((List<String>) enumValues);
            return b.build();
        }
        Object type = spec.get("type");
        // Union types like ["string","null"] → take the first non-null member.
        if (type instanceof List) {
            type = ((List<Object>) type).stream()
                    .filter(t -> !"null".equals(t)).findFirst().orElse("string");
        }
        return switch (type instanceof String t ? t : "string") {
            case "integer" -> {
                var b = JsonIntegerSchema.builder();
                if (description != null) {
                    b.description(description);
                }
                yield b.build();
            }
            case "number" -> {
                var b = JsonNumberSchema.builder();
                if (description != null) {
                    b.description(description);
                }
                yield b.build();
            }
            case "boolean" -> {
                var b = JsonBooleanSchema.builder();
                if (description != null) {
                    b.description(description);
                }
                yield b.build();
            }
            case "array" -> {
                var items = spec.get("items");
                var b = JsonArraySchema.builder();
                if (description != null) {
                    b.description(description);
                }
                if (items instanceof Map) {
                    b.items(toElement((Map<String, Object>) items));
                }
                yield b.build();
            }
            case "object" -> toJsonObjectSchema(spec);
            default -> {
                var b = JsonStringSchema.builder();
                if (description != null) {
                    b.description(description);
                }
                yield b.build();
            }
        };
    }
}
