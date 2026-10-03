package io.github.afonsoft.knowledgehub;

/**
 * Raised when a hub tool call fails — either a remote JSON-RPC error
 * (unknown tool, invalid arguments) or a tool result with {@code isError: true},
 * or a non-2xx response from a REST streaming endpoint.
 */
public class KnowledgeHubToolException extends RuntimeException {

    public KnowledgeHubToolException(String message) {
        super(message);
    }

    public KnowledgeHubToolException(String message, Throwable cause) {
        super(message, cause);
    }
}
