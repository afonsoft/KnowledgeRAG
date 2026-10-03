package io.github.afonsoft.knowledgehub;

import java.time.Duration;

/** Options for {@link KnowledgeHubClient}. */
public final class KnowledgeHubClientOptions {

    private final String baseUrl;
    private final String apiKey;
    private final String mcpPath;
    private final Duration requestTimeout;

    private KnowledgeHubClientOptions(Builder builder) {
        this.baseUrl = stripTrailingSlash(builder.baseUrl);
        this.apiKey = builder.apiKey;
        this.mcpPath = builder.mcpPath;
        this.requestTimeout = builder.requestTimeout;
    }

    /** Base URL of the hub, e.g. {@code "http://localhost:5009"}. */
    public String baseUrl() {
        return baseUrl;
    }

    /** API key ({@code aft_*}), minted on the hub's /api-keys page. */
    public String apiKey() {
        return apiKey;
    }

    /** MCP Streamable-HTTP path (default {@code "/mcp"}). */
    public String mcpPath() {
        return mcpPath;
    }

    /** Per-call timeout (default 5 minutes — agent loops can be slow). */
    public Duration requestTimeout() {
        return requestTimeout;
    }

    private static String stripTrailingSlash(String url) {
        return url != null && url.endsWith("/") ? url.substring(0, url.length() - 1) : url;
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String baseUrl;
        private String apiKey;
        private String mcpPath = "/mcp";
        private Duration requestTimeout = Duration.ofMinutes(5);

        public Builder baseUrl(String baseUrl) {
            this.baseUrl = baseUrl;
            return this;
        }

        public Builder apiKey(String apiKey) {
            this.apiKey = apiKey;
            return this;
        }

        public Builder mcpPath(String mcpPath) {
            this.mcpPath = mcpPath;
            return this;
        }

        public Builder requestTimeout(Duration requestTimeout) {
            this.requestTimeout = requestTimeout;
            return this;
        }

        public KnowledgeHubClientOptions build() {
            if (baseUrl == null || baseUrl.isBlank()) {
                throw new IllegalStateException("baseUrl is required");
            }
            if (apiKey == null || apiKey.isBlank()) {
                throw new IllegalStateException("apiKey is required (aft_* from /api-keys)");
            }
            return new KnowledgeHubClientOptions(this);
        }
    }
}
