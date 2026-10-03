package io.github.afonsoft.knowledgehub;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.fasterxml.jackson.databind.ObjectMapper;
import io.github.afonsoft.knowledgehub.langchain4j.KnowledgeHubLangChain4j;
import io.github.afonsoft.knowledgehub.model.HubModels.HubAskAnswer;
import io.github.afonsoft.knowledgehub.model.HubModels.HubSearchResult;
import io.github.afonsoft.knowledgehub.model.HubModels.HubToolResult;
import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;

class KnowledgeHubSdkTest {

    private static final ObjectMapper MAPPER = new ObjectMapper();

    @Test
    void convert_parsesSearchStructuredContent() {
        var structured = Map.<String, Object>of(
                "results", List.of(Map.of(
                        "chunkText", "hello",
                        "documentTitle", "doc",
                        "sourceName", "src",
                        "sourceId", "11111111-1111-1111-1111-111111111111",
                        "score", 0.9,
                        "uriReference", "obsidian://x")),
                "grade", "sufficient",
                "retried", false,
                "totalMatches", 3,
                "filterRelaxed", false,
                "truncatedByTokens", false);
        var result = new HubToolResult("text", structured, false);

        var parsed = KnowledgeHubClient.convert(result, HubSearchResult.class, () -> null);

        assertNotNull(parsed);
        assertEquals(1, parsed.results().size());
        assertEquals("hello", parsed.results().get(0).chunkText());
        assertEquals("sufficient", parsed.grade());
        assertEquals(3, parsed.totalMatches());
    }

    @Test
    void convert_fallsBackWhenNoStructured() {
        var result = new HubToolResult("plain", null, false);
        var parsed = KnowledgeHubClient.convert(result, HubAskAnswer.class,
                () -> new HubAskAnswer(result.text(), List.of(), null, false, false, null,
                        false, false, false));
        assertEquals("plain", parsed.answer());
        assertFalse(parsed.generated());
    }

    @Test
    void throwIfError_raisesOnToolError() {
        var result = new HubToolResult("boom: unknown tool", null, true);
        var ex = assertThrows(KnowledgeHubToolException.class,
                () -> KnowledgeHubClient.throwIfError(result));
        assertTrue(ex.getMessage().contains("unknown tool"));
    }

    @Test
    void sseReader_parsesEnvelopeAndEvents() throws Exception {
        var sse = """
                event: meta
                data: {"seq":0,"data":{"model":"m"}}

                event: token
                data: {"seq":1,"data":{"text":"hello"}}

                event: done
                data: {"seq":2,"data":{"answer":"hello world"}}

                """;
        var frames = SseReader.readAll(
                new ByteArrayInputStream(sse.getBytes(StandardCharsets.UTF_8)), MAPPER);

        assertEquals(3, frames.size());
        assertEquals("meta", frames.get(0).event());
        assertEquals("m", frames.get(0).data().get("model"));
        assertEquals("hello", frames.get(1).data().get("text"));
        assertEquals("done", frames.get(2).event());
    }

    @Test
    void sseReader_passesThroughBarePayload() throws Exception {
        var sse = "event: token\ndata: {\"text\":\"x\"}\n\n";
        var frames = SseReader.readAll(
                new ByteArrayInputStream(sse.getBytes(StandardCharsets.UTF_8)), MAPPER);
        assertEquals(1, frames.size());
        assertEquals("x", frames.get(0).data().get("text"));
    }

    @Test
    void langchainSchema_mapsJsonSchemaTypes() {
        var schema = Map.<String, Object>of(
                "type", "object",
                "properties", Map.of(
                        "query", Map.of("type", "string", "description", "the query"),
                        "topK", Map.of("type", "integer"),
                        "exact", Map.of("type", "boolean"),
                        "mode", Map.of("type", "string", "enum", List.of("hybrid", "lexical")),
                        "window", Map.of("type", List.of("integer", "null"))),
                "required", List.of("query"));

        var converted = KnowledgeHubLangChain4j.toJsonObjectSchema(schema);

        assertNotNull(converted);
    }

    @Test
    void options_requiresBaseUrlAndKey() {
        assertThrows(IllegalStateException.class,
                () -> KnowledgeHubClientOptions.builder().build());
        assertThrows(IllegalStateException.class,
                () -> KnowledgeHubClientOptions.builder().baseUrl("http://x").build());
        var opts = KnowledgeHubClientOptions.builder()
                .baseUrl("http://x/").apiKey("aft_k").build();
        assertEquals("http://x", opts.baseUrl());
    }
}
