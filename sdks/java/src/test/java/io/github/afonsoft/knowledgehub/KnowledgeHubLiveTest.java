package io.github.afonsoft.knowledgehub;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import io.github.afonsoft.knowledgehub.langchain4j.KnowledgeHubLangChain4j;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledIfEnvironmentVariable;

/**
 * Live E2E against a running hub. Enable with:
 *   KNOWLEDGEHUB_URL=http://localhost:5009 KNOWLEDGEHUB_KEY=aft_... mvn test
 */
@EnabledIfEnvironmentVariable(named = "KNOWLEDGEHUB_URL", matches = ".*")
class KnowledgeHubLiveTest {

    private static final String URL = System.getenv("KNOWLEDGEHUB_URL");
    private static final String KEY = System.getenv("KNOWLEDGEHUB_KEY");

    private KnowledgeHubClient connect() {
        return KnowledgeHubClient.connect(URL, KEY);
    }

    @Test
    void listTools_returnsDynamicCatalog() {
        try (var hub = connect()) {
            var tools = hub.listTools();
            assertFalse(tools.isEmpty());
            assertTrue(tools.stream().anyMatch(t -> t.name().equals("search_knowledge")),
                    "catalog should include search_knowledge");
            assertNotNull(tools.get(0).inputSchema());
        }
    }

    @Test
    void callTool_unknownTool_raisesCleanError() {
        try (var hub = connect()) {
            var ex = assertThrows(KnowledgeHubToolException.class,
                    () -> hub.callTool("no_such_tool_xyz", null));
            assertTrue(ex.getMessage().contains("no_such_tool_xyz"),
                    ex.getMessage());
        }
    }

    @Test
    void search_andAsk_returnTypedResults() {
        try (var hub = connect()) {
            var search = hub.search("knowledge hub");
            assertNotNull(search);
            assertNotNull(search.results());

            var answer = hub.ask("what is this knowledge base about?");
            assertNotNull(answer);
            assertNotNull(answer.answer());
        }
    }

    @Test
    void langchainAdapter_mapsCatalogToToolSpecs() {
        try (var hub = connect()) {
            var tools = KnowledgeHubLangChain4j.asTools(hub);
            assertFalse(tools.isEmpty());
        }
    }

    @Test
    void agentChat_completesRunOrCleanProviderError() {
        try (var hub = connect()) {
            try {
                var result = hub.agentChat("Say hello.");
                assertNotNull(result.answer());
            } catch (KnowledgeHubToolException ex) {
                // A hub without a configured chat provider rejects agent_chat —
                // the SDK must surface that as a clean typed error.
                assertFalse(ex.getMessage().isBlank());
            }
        }
    }

    @Test
    void streamAsk_emitsDoneOrCleanProviderError() {
        try (var hub = connect()) {
            try {
                var events = hub.streamAsk("what is this about?", null, null);
                assertFalse(events.isEmpty());
                assertEquals("done", events.get(events.size() - 1).type());
            } catch (KnowledgeHubToolException ex) {
                // Same as agentChat: no chat provider → 400 surfaced cleanly.
                assertTrue(ex.getMessage().contains("400"), ex.getMessage());
            }
        }
    }
}
