package io.github.afonsoft.knowledgehub.model;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;
import java.util.List;
import java.util.Map;

/** DTOs for the hub's MCP tool results (structuredContent payloads). */
public final class HubModels {

    private HubModels() {
    }

    /** Normalized result of an MCP tools/call. */
    public record HubToolResult(
            /** Concatenated text content blocks (what LLMs consume). */
            String text,
            /** Raw structuredContent payload when the tool emitted one. */
            Map<String, Object> structured,
            /** True when the tool reported an execution error in-band. */
            boolean isError) {
    }

    /** A discovered MCP tool (name + JSON-Schema inputSchema). */
    public record HubToolDescriptor(
            String name,
            String description,
            Map<String, Object> inputSchema) {
    }

    /** One citation linking an answer marker [n] to a retrieved chunk. */
    @JsonIgnoreProperties(ignoreUnknown = true)
    public record HubCitation(
            int index,
            String source,
            String title,
            String uri,
            String path,
            double score,
            String suspicionFlags,
            List<String> components) {
    }

    /** One ranked chunk hit from search_knowledge. */
    @JsonIgnoreProperties(ignoreUnknown = true)
    public record HubSearchHit(
            String chunkText,
            String documentTitle,
            String sourceName,
            String sourceId,
            double score,
            String uriReference,
            String suspicionFlags,
            String sectionPath,
            String context,
            List<String> components,
            boolean isRelaxed) {
    }

    /** Result of search_knowledge (hybrid RRF retrieval + corrective grade). */
    @JsonIgnoreProperties(ignoreUnknown = true)
    public record HubSearchResult(
            List<HubSearchHit> results,
            /** Corrective-RAG grade: sufficient | weak | insufficient (null when grading is off). */
            String grade,
            boolean retried,
            int totalMatches,
            boolean filterRelaxed,
            boolean truncatedByTokens,
            List<String> warnings) {
    }

    /** Result of ask_knowledge (retrieval + grounded synthesis). */
    @JsonIgnoreProperties(ignoreUnknown = true)
    public record HubAskAnswer(
            String answer,
            List<HubCitation> citations,
            String model,
            boolean generated,
            /** True when the retriever graded evidence insufficient and abstained. */
            boolean insufficientEvidence,
            String retrievalGrade,
            boolean retried,
            boolean cached,
            boolean truncatedByTokens) {
    }

    /** One tool-call step taken by the agent loop. */
    @JsonIgnoreProperties(ignoreUnknown = true)
    public record HubAgentStep(
            int iteration,
            String tool,
            String argsSummary,
            boolean isError,
            double elapsedMs) {
    }

    /** Result of agent_chat — the completed (or HITL-paused) run. */
    @JsonIgnoreProperties(ignoreUnknown = true)
    public record HubAgentResult(
            String answer,
            List<HubAgentStep> steps,
            List<String> toolCalls,
            int iterations,
            double latencyMs,
            boolean limitReached,
            /** Set when the run paused for human approval of a write tool. */
            String awaitingApprovalId,
            String pendingTool,
            String threadId) {
    }

    /**
     * One Server-Sent-Events frame from /api/ask/stream or /api/agent/stream.
     * Type is one of meta | token | tool_start | tool_end | awaiting_approval |
     * abstain | done | error.
     */
    public record HubStreamEvent(String type, Map<String, Object> data) {
    }

    /** A chat message for the agent REST streaming endpoint. */
    public record HubAgentMessage(String role, String content) {
    }
}
