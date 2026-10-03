# KnowledgeHub SDK — Java

Client SDK for the Knowledge MCP Hub: wraps the hub's native MCP endpoint
(Streamable HTTP, `/mcp`) via the official MCP Java SDK
(`io.modelcontextprotocol.sdk:mcp`), with a typed facade plus a LangChain4j
adapter that exposes the hub's dynamic tool catalog as
`ToolSpecification`/`ToolExecutor` — LangChain-style tool injection for Java.

## Install

```xml
<dependency>
    <groupId>io.github.afonsoft</groupId>
    <artifactId>knowledgehub-sdk</artifactId>
    <version>0.1.0</version>
</dependency>
<!-- LangChain4j adapter (optional dep): -->
<dependency>
    <groupId>dev.langchain4j</groupId>
    <artifactId>langchain4j</artifactId>
    <version>1.0.0</version>
</dependency>
```

Java 17+. Until Maven Central publishing is set up, build locally:
`mvn -f sdks/java install`.

## Usage

```java
try (var hub = KnowledgeHubClient.connect("http://localhost:5009", "aft_...")) {
    var tools  = hub.listTools();                       // dynamic catalog
    var search = hub.search("deployment options", 10, null, null);
    var answer = hub.ask("how do I configure postgres?");  // citations + grade
    var agent  = hub.agentChat("summarize the runbook");   // agent loop

    for (var ev : hub.streamAsk("status?", null, null))    // SSE tokens
        System.out.println(ev.type() + " " + ev.data());
}
```

LangChain4j:

```java
var hubTools = KnowledgeHubLangChain4j.asTools(hub);   // Map<ToolSpecification, ToolExecutor>
var assistant = AiServices.builder(MyAssistant.class)
        .chatModel(model)
        .tools(hubTools)      // AiServices accepts Map<ToolSpecification, ToolExecutor>
        .build();
```

Notes:

- **Dynamic catalog** — `listTools()` is the source of truth;
  `read_document`/`write_note` exist only with an Obsidian source;
  upstream tools (deepwiki/tavily/…) only with configured integrations.
- **Errors** — remote JSON-RPC errors, `isError` results and non-2xx SSE
  responses surface as `KnowledgeHubToolException`.
- **Timeouts** — default 5 min per call (agent loops are slow);
  `KnowledgeHubClientOptions.builder().requestTimeout(...)`.

## Tests

`mvn test` — unit tests always run; live E2E activates with
`KNOWLEDGEHUB_URL` + `KNOWLEDGEHUB_KEY` env vars.
