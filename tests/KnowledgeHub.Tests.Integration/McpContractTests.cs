using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using static KnowledgeHub.Tests.Integration.TestMcp;

namespace KnowledgeHub.Tests.Integration;

// Covers SPEC-20260914-mcp-contract-tests RF-001/RF-003/RF-004.
//
// Contract update rule: the MCP tool catalog IS a public interface — clients
// (Cursor, Claude Desktop) hardcode these names/schemas. A deliberate breaking
// change is fine: update the pinned names/schemas in this file in the same
// commit so the diff is reviewable. Failures print expected vs actual JSON.
public class McpContractTests : IClassFixture<McpContractTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-contract-{Guid.NewGuid():N}.db");
        public string Vault { get; } = Path.Join(Path.GetTempPath(), $"vault-contract-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Vault);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["DeepWiki:Enabled"] = "true",
                    ["Graph:Enabled"] = "true"
                }));
        }
    }

    private readonly Fixture _factory;

    public McpContractTests(Fixture factory) => _factory = factory;

    private static readonly Dictionary<string, string> PinnedSchemas = new()
    {
        // SPEC-20260923-graphrag: graph tools ship in the default catalog
        // (Graph:Enabled defaults to true).
        ["find_dependencies"] = """{"type":"object","properties":{"component":{"type":"string","description":"Entity name as extracted from indexed text — case/variant-insensitive. Discover real names via search_knowledge: every hit carries a `components` field with the entity names found in that chunk.","examples":["OmniRoute","AgentRouter"]},"depth":{"type":"integer","description":"Traversal depth 1-3 (default 1, clamped to 3)"}},"required":["component"],"examples":[{"component":"OmniRoute","depth":2}]}""",
        ["find_dependents"] = """{"type":"object","properties":{"component":{"type":"string","description":"Entity name as extracted from indexed text — case/variant-insensitive. Discover real names via search_knowledge: every hit carries a `components` field with the entity names found in that chunk.","examples":["OmniRoute","AgentRouter"]},"depth":{"type":"integer","description":"Traversal depth 1-3 (default 1, clamped to 3)"}},"required":["component"],"examples":[{"component":"OmniRoute","depth":2}]}""",
        ["find_path"] = """{"type":"object","properties":{"a":{"type":"string","description":"Source entity name","examples":["OpenClaw"]},"b":{"type":"string","description":"Target entity name","examples":["OmniRoute"]},"depth":{"type":"integer","description":"Max path length 1-3 (default 3, clamped)"}},"required":["a","b"],"examples":[{"a":"OpenClaw","b":"OmniRoute","depth":3}]}""",
        ["analyze_impact"] = """{"type":"object","properties":{"component":{"type":"string","description":"Entity name whose dependents are analyzed — discover names via the `components` field of search_knowledge results","examples":["OmniRoute"]}},"required":["component"],"examples":[{"component":"OmniRoute"}]}""",
        // SPEC-20260927-temporal-episodic-knowledge-graph: temporal/episodic
        // graph tools (Graph:Enabled gate — same fixture flag).
        ["search_graph_temporal"] = """{"type":"object","properties":{"query":{"type":"string","description":"Free-text query — matched against entity names; empty browses the whole window","examples":["connector architecture"]},"start":{"type":"string","description":"Window start — RFC3339/ISO-8601, yyyy-MM-ddTHH:mm:ss, yyyy-MM-dd HH:mm:ss or yyyy-MM-dd (UTC assumed)","examples":["2026-09-01","2026-09-20T10:00:00Z"]},"end":{"type":"string","description":"Window end — same formats as start; must be after start","examples":["2026-09-27"]},"maxResults":{"type":"integer","description":"Max nodes returned (default 15, cap 100)"}},"required":[],"examples":[{"query":"arquitetura de conectores","start":"2026-09-01","end":"2026-09-27"}]}""",
        ["search_graph_recent"] = """{"type":"object","properties":{"query":{"type":"string","description":"Free-text query — matched against entity names; empty browses the whole window","examples":["arquitetura de conectores"]},"window":{"type":"string","description":"Sliding window — one of: 1h, 6h, 24h, 7d","enum":["1h","6h","24h","7d"]},"maxResults":{"type":"integer","description":"Max nodes returned (default 10, cap 100)"}},"required":["query","window"],"examples":[{"query":"arquitetura de conectores","window":"24h","maxResults":10}]}""",
        ["search_graph_diverse"] = """{"type":"object","properties":{"entity":{"type":"string","description":"Entity name to diversify around — discover names via the `components` field of search_knowledge results","examples":["OmniRoute"]},"diversityLevel":{"type":"string","description":"Cluster spread: low = up to 5 nodes/cluster, medium = 2, high = 1","enum":["low","medium","high"]},"maxResults":{"type":"integer","description":"Max nodes returned (default 10, cap 100)"}},"required":["entity"],"examples":[{"entity":"OmniRoute","diversityLevel":"high","maxResults":10}]}""",
        ["search_graph_relationships"] = """{"type":"object","properties":{"entity":{"type":"string","description":"Entity name to explore — discover names via the `components` field of search_knowledge results","examples":["OmniRoute"]},"depth":{"type":"integer","description":"Multi-hop depth 1-3 (default 2, clamped to 3)"},"maxResults":{"type":"integer","description":"Max nodes returned (default 15, cap 100)"}},"required":["entity"],"examples":[{"entity":"OmniRoute","depth":2}]}""",
        ["search_graph_episode"] = """{"type":"object","properties":{"episodeId":{"type":"string","description":"Episode GUID — surfaced as `episodeId` on temporal/diverse search results","examples":["3fa85f64-5717-4562-b3fc-2c963f66afa6"]},"maxResults":{"type":"integer","description":"Max nodes returned (default 10, cap 100)"}},"required":["episodeId"],"examples":[{"episodeId":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}]}""",
        ["search_knowledge"] = """{"type":"object","properties":{"query":{"type":"string","description":"Text or question to search for","examples":["what is RAG?"]},"topK":{"type":"integer","description":"Max results (default 5, max 50)"},"source":{"type":"string","description":"Source slug (default: all active sources)"},"mode":{"type":"string","enum":["hybrid","semantic","lexical"],"description":"Search mode (default: hybrid)"},"sourceType":{"type":"string","description":"Filter by connector type (e.g. DocumentFile, WebPage)"},"pathPrefix":{"type":"string","description":"Filter by document path/URI prefix"},"indexedAfter":{"type":"string","description":"ISO-8601 date — only documents indexed at/after it"},"language":{"type":"string","description":"BCP-47 language tag filter (matches document metadata when present)"},"expand":{"type":"string","enum":["off","multi","hyde","both"],"description":"Query expansion override (default: server config). multi rewrites N variants + fuses; hyde embeds a hypothetical doc on the vector arm; both combines them"},"contextExpand":{"type":"string","enum":["none","window","section"],"description":"Attach surrounding context to each hit: window=neighbouring chunks, section=whole parent section"},"useGraph":{"type":"boolean","description":"Enable the knowledge-graph retrieval arm: entity linking + 1-hop evidence chunks (default: server config)"},"windowSize":{"type":"integer","description":"Neighbour window breadth for context expansion, 0-3 (default: server config). >0 implies contextExpand=window; 0 disables expansion"},"limitMode":{"type":"string","enum":["fixed","autocut"],"description":"Result limit: fixed=topK, autocut=prunes the long tail at the score elbow (default: server config)"},"autocutSensitivity":{"type":"integer","description":"Autocut sensitivity 1-3 — cut at the N-th abrupt score drop (default 1)"},"subQueries":{"type":"array","items":{"type":"string"},"description":"Extra query variants searched in parallel and fused via RRF (max 4) — for multi-faceted questions"},"allowRelaxation":{"type":"boolean","description":"When a strict source/tag filter yields too few results, fall back to broader scopes (source→type→global) with relaxed hits flagged (default: server config)"},"budget":{"type":"string","enum":["low","mid","high"],"description":"Recall depth/cost (default high). low = half candidate pool per arm, no query expansion, no corrective retries; mid = default pool without expansion; high = full pipeline"},"maxTokens":{"type":"integer","description":"Response token budget (default 4096, clamped 256-32768) — truncates the result list by ~4 chars/token after ranking; sets truncatedByTokens when applied"},"minScores":{"type":"object","properties":{"semantic":{"type":"number"},"lexical":{"type":"number"},"final":{"type":"number"}},"description":"Per-stage score floors 0-1: semantic = cosine floor on each vector arm, lexical = fraction of the arm's best FTS score, final = post-fusion floor"},"temporalWindow":{"type":"object","properties":{"start":{"type":"string","description":"ISO-8601"},"end":{"type":"string","description":"ISO-8601"}},"description":"Explicit recency window — documents indexed inside it rank higher (boost, not filter)"}},"required":["query"],"examples":[{"query":"what is RAG?","topK":5,"mode":"hybrid"}]}""",
        ["ask_knowledge"] = """{"type":"object","properties":{"question":{"type":"string","description":"Natural-language question","examples":["How does synchronization work?"]},"topK":{"type":"integer","description":"Max passages used as context (default 5, max 50)"},"source":{"type":"string","description":"Source slug (default: all active sources)"},"mode":{"type":"string","enum":["hybrid","semantic","lexical"],"description":"Search mode (default: hybrid)"},"generate":{"type":"boolean","description":"Synthesize the answer via the server's configured chat provider (default: true when one is configured)"},"sourceType":{"type":"string","description":"Filter by connector type (e.g. DocumentFile, WebPage)"},"pathPrefix":{"type":"string","description":"Filter by document path/URI prefix"},"indexedAfter":{"type":"string","description":"ISO-8601 date — only documents indexed at/after it"},"language":{"type":"string","description":"BCP-47 language tag filter (matches document metadata when present)"},"expand":{"type":"string","enum":["off","multi","hyde","both"],"description":"Query expansion override (default: server config). multi rewrites N variants + fuses; hyde embeds a hypothetical doc on the vector arm; both combines them"},"contextExpand":{"type":"string","enum":["none","window","section"],"description":"Attach surrounding context to each hit: window=neighbouring chunks, section=whole parent section"},"useGraph":{"type":"boolean","description":"Enable the knowledge-graph retrieval arm: entity linking + 1-hop evidence chunks (default: server config)"},"windowSize":{"type":"integer","description":"Neighbour window breadth for context expansion, 0-3 (default: server config). >0 implies contextExpand=window; 0 disables expansion"},"limitMode":{"type":"string","enum":["fixed","autocut"],"description":"Result limit: fixed=topK, autocut=prunes the long tail at the score elbow (default: server config)"},"autocutSensitivity":{"type":"integer","description":"Autocut sensitivity 1-3 — cut at the N-th abrupt score drop (default 1)"},"subQueries":{"type":"array","items":{"type":"string"},"description":"Extra query variants searched in parallel and fused via RRF (max 4) — for multi-faceted questions"},"allowRelaxation":{"type":"boolean","description":"When a strict source/tag filter yields too few results, fall back to broader scopes (source→type→global) with relaxed hits flagged (default: server config)"},"budget":{"type":"string","enum":["low","mid","high"],"description":"Recall depth/cost (default high). low = half candidate pool per arm, no query expansion, no corrective retries; mid = default pool without expansion; high = full pipeline"},"maxTokens":{"type":"integer","description":"Context token budget (default 4096, clamped 256-32768) — truncates the passage list by ~4 chars/token after ranking; sets truncatedByTokens when applied"},"minScores":{"type":"object","properties":{"semantic":{"type":"number"},"lexical":{"type":"number"},"final":{"type":"number"}},"description":"Per-stage score floors 0-1: semantic = cosine floor on each vector arm, lexical = fraction of the arm's best FTS score, final = post-fusion floor (empty result → abstains instead of answering)"},"temporalWindow":{"type":"object","properties":{"start":{"type":"string","description":"ISO-8601"},"end":{"type":"string","description":"ISO-8601"}},"description":"Explicit recency window — documents indexed inside it rank higher (boost, not filter)"},"enableLiveActions":{"type":"boolean","description":"Action-Augmented RAG: execute live MCP tools nominated by the question itself — or by retrieved-chunk markers when the server opts in (Agent:AllowDocumentMarkers) — then fuse outputs with document citations (default: server config)"}},"required":["question"],"examples":[{"question":"How does synchronization work?","topK":5,"generate":true}]}""",
        ["agent_chat"] = """{"type":"object","properties":{"prompt":{"type":"string","description":"Natural-language question or task — the agent iterates tools until it can answer","examples":["Summarize this week's notes"]},"tools":{"type":"array","items":{"type":"string"},"description":"Allowlist of tools exposed to the model (default: all read-only tools)","examples":[["search_knowledge","ask_knowledge"]]},"maxIterations":{"type":"integer","description":"Max model→tools→model iterations (default 10)"},"allowWrite":{"type":"boolean","description":"Opt-in: exposes write tools (write_knowledge, write_note)"},"threadId":{"type":"string","description":"Existing thread GUID — continues the conversation with context"},"persist":{"type":"boolean","description":"Creates a new thread and persists this call's turns"}},"required":["prompt"],"examples":[{"prompt":"Summarize this week's notes","tools":["search_knowledge","ask_knowledge"],"maxIterations":10,"persist":true}]}""",
        ["write_knowledge"] = """{"type":"object","properties":{"title":{"type":"string","description":"Document title (becomes the file name in vault sources)","examples":["Example note"]},"content":{"type":"string","description":"Markdown or plain-text content","examples":["# Title\n\nMarkdown content."]},"source":{"type":"string","description":"Target source slug (default: first active source)"},"tags":{"type":"array","items":{"type":"string"},"description":"Tags (stored as frontmatter in vault sources)","examples":[["example"]]}},"required":["title","content"],"examples":[{"title":"Example note","content":"# Title\n\nMarkdown content.","tags":["example"]}]}""",
        ["read_document"] = """{"type":"object","properties":{"path":{"type":"string","description":"Vault-relative path of the document","examples":["folder/note.md"]},"source":{"type":"string","description":"Vault slug (default: first active vault)"}},"required":["path"],"examples":[{"path":"folder/note.md"}]}""",
        ["write_note"] = """{"type":"object","properties":{"path":{"type":"string","description":"Vault-relative note path (.md is appended when missing)","examples":["journal/2026-09-14"]},"content":{"type":"string","description":"Markdown content","examples":["# Note\n\nText."]},"tags":{"type":"array","items":{"type":"string"},"description":"Tags → frontmatter","examples":[["journal"]]},"source":{"type":"string","description":"Vault slug (default: first active vault)"}},"required":["path","content"],"examples":[{"path":"journal/2026-09-14","content":"# Note\n\nText.","tags":["journal"]}]}""",
        ["ask_question"] = """{"type":"object","properties":{"repoName":{"anyOf":[{"type":"string"},{"type":"array","items":{"type":"string"},"maxItems":10}],"description":"GitHub repo(s) in owner/repo format (max 10)","examples":["langchain-ai/langgraph"]},"question":{"type":"string","description":"Question about the repository","examples":["How does checkpointing work?"]}},"required":["repoName","question"],"examples":[{"repoName":"langchain-ai/langgraph","question":"How does checkpointing work?"},{"repoName":["langchain-ai/langgraph","afonsoft/skills"],"question":"Compare the architectures"}]}""",
        ["read_wiki_structure"] = """{"type":"object","properties":{"repoName":{"type":"string","description":"GitHub repo in owner/repo format","examples":["langchain-ai/langgraph"]}},"required":["repoName"],"examples":[{"repoName":"langchain-ai/langgraph"}]}""",
        ["read_wiki_contents"] = """{"type":"object","properties":{"repoName":{"type":"string","description":"GitHub repo in owner/repo format","examples":["langchain-ai/langgraph"]}},"required":["repoName"],"examples":[{"repoName":"langchain-ai/langgraph"}]}""",
        // SPEC-20260916-firecrawl-mcp-proxy: static Firecrawl core. With a real
        // key, upstream tools/list merges in dynamically — this fixture is
        // keyless, so only the static surface is pinned here.
        ["firecrawl_scrape"] = """{"type":"object","additionalProperties":true,"properties":{"url":{"type":"string","description":"URL to scrape","examples":["https://example.com"]},"formats":{"type":"array","items":{"type":"string"},"description":"Output formats (e.g. markdown, html, json)"},"onlyMainContent":{"type":"boolean","description":"Exclude nav/footer boilerplate"}},"required":["url"],"examples":[{"url":"https://example.com"},{"url":"https://docs.firecrawl.dev","formats":["markdown"],"onlyMainContent":true}]}""",
        ["firecrawl_search"] = """{"type":"object","additionalProperties":true,"properties":{"query":{"type":"string","description":"Search query","examples":["latest .NET 10 release notes"]},"limit":{"type":"integer","description":"Max results"}},"required":["query"],"examples":[{"query":"latest .NET 10 release notes"}]}""",
        ["firecrawl_map"] = """{"type":"object","additionalProperties":true,"properties":{"url":{"type":"string","description":"Site URL to map","examples":["https://docs.firecrawl.dev"]}},"required":["url"],"examples":[{"url":"https://docs.firecrawl.dev"}]}""",
        ["firecrawl_crawl"] = """{"type":"object","additionalProperties":true,"properties":{"url":{"type":"string","description":"Starting URL","examples":["https://docs.firecrawl.dev"]},"limit":{"type":"integer","description":"Max pages to crawl"}},"required":["url"],"examples":[{"url":"https://docs.firecrawl.dev","limit":5}]}""",
        ["firecrawl_check_crawl_status"] = """{"type":"object","additionalProperties":true,"properties":{"id":{"type":"string","description":"Crawl job id","examples":["550e8400-e29b-41d4-a716-446655440000"]}},"required":["id"],"examples":[{"id":"550e8400-e29b-41d4-a716-446655440000"}]}""",
        ["firecrawl_parse"] = """{"type":"object","additionalProperties":true,"properties":{"url":{"type":"string","description":"Public URL of the document to parse","examples":["https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf"]},"filePath":{"type":"string","description":"Local file path (two-phase upload flow)"}},"examples":[{"url":"https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf"}]}""",
        // SPEC-20260916-api-key-settings + SPEC-20260926-split-settings-tools:
        // per-API-key settings separated into integrations and chat.
        ["set_api_key_settings"] = """{"type":"object","properties":{"provider":{"type":"string","enum":["firecrawl","deepwiki","tavily","context7"],"description":"Which integration provider to configure"},"apiKey":{"type":["string","null"],"description":"API key override (null or empty = inherit from global)"}},"required":["provider"],"examples":[{"provider":"deepwiki","apiKey":"dw-secret-key"},{"provider":"firecrawl","apiKey":null}]}""",
        ["set_chat_settings"] = """{"type":"object","properties":{"endpoint":{"type":["string","null"],"description":"OpenAI-compatible base URL (null = inherit from global)"},"model":{"type":["string","null"],"description":"Model name (null = inherit from global)"},"apiKey":{"type":["string","null"],"description":"Chat provider API key override (null = inherit from global)"}},"examples":[{"endpoint":"http://localhost:11434","model":"llama3","apiKey":null}]}""",
        // SPEC-20260916-tavily-mcp-proxy: static Tavily core (keyless fixture —
        // only the static surface is pinned; upstream merge needs a real key).
        ["tavily_search"] = """{"type":"object","additionalProperties":true,"properties":{"query":{"type":"string","description":"Search query","examples":["latest .NET 10 release notes"]},"max_results":{"type":"integer","description":"Max results (default 5)"},"search_depth":{"type":"string","description":"basic | advanced"},"include_domains":{"type":"array","items":{"type":"string"}},"exclude_domains":{"type":"array","items":{"type":"string"}}},"required":["query"],"examples":[{"query":"latest .NET 10 release notes"},{"query":"site reliability best practices","max_results":3,"search_depth":"basic"}]}""",
        ["tavily_extract"] = """{"type":"object","additionalProperties":true,"properties":{"urls":{"type":"array","items":{"type":"string"},"description":"One or more URLs to extract content from","examples":[["https://example.com"]]}},"required":["urls"],"examples":[{"urls":["https://example.com"]}]}""",
        ["tavily_map"] = """{"type":"object","additionalProperties":true,"properties":{"url":{"type":"string","description":"Site URL to map","examples":["https://docs.tavily.com"]},"max_depth":{"type":"integer","description":"Max link depth"},"limit":{"type":"integer","description":"Max URLs returned"}},"required":["url"],"examples":[{"url":"https://docs.tavily.com","max_depth":1,"limit":20}]}""",
        ["tavily_crawl"] = """{"type":"object","additionalProperties":true,"properties":{"url":{"type":"string","description":"Starting URL","examples":["https://docs.tavily.com"]},"max_depth":{"type":"integer","description":"Max link depth"},"limit":{"type":"integer","description":"Max pages to crawl"},"instructions":{"type":"string","description":"Natural-language crawl guidance"}},"required":["url"],"examples":[{"url":"https://docs.tavily.com","max_depth":1,"limit":5}]}""",
        ["tavily_research"] = """{"type":"object","additionalProperties":true,"properties":{"input":{"type":"string","description":"Research question or task","examples":["Compare .NET 10 minimal APIs vs controllers for a small team"]},"model":{"type":"string","description":"Research model (e.g. mini | pro)"}},"required":["input"],"examples":[{"input":"Compare .NET 10 minimal APIs vs controllers for a small team"}]}""",
        // SPEC-20260922-context7-mcp-proxy: static Context7 core (keyless
        // fixture — only the static surface is pinned; upstream merge needs a
        // real key).
        ["resolve-library-id"] = """{"type":"object","additionalProperties":true,"properties":{"query":{"type":"string","description":"What to look up in the library's documentation — used to rank results by relevance","examples":["app router routing"]},"libraryName":{"type":"string","description":"Official library name (e.g. 'Next.js', 'MongoDB', 'Three.js')","examples":["Next.js"]}},"required":["query","libraryName"],"examples":[{"libraryName":"Next.js","query":"app router routing"},{"libraryName":"MongoDB","query":"connection pooling"}]}""",
        ["query-docs"] = """{"type":"object","additionalProperties":true,"properties":{"libraryId":{"type":"string","description":"Exact Context7 library ID (/org/project[/version]) — from resolve-library-id or the user query","examples":["/vercel/next.js"]},"query":{"type":"string","description":"Single-concept question to look up in the library docs","examples":["middleware authentication"]}},"required":["libraryId","query"],"examples":[{"libraryId":"/vercel/next.js","query":"middleware authentication"},{"libraryId":"/mongodb/docs","query":"connection string format"}]}""",
        // Agent flows (UI-defined): the two generic catalog tools; per-flow
        // flow_<slug> tools appear only when the fixture DB has enabled flows.
        ["list_flows"] = """{"type":"object","properties":{},"additionalProperties":false,"examples":[{}]}""",
        ["run_flow"] = """{"type":"object","properties":{"flow":{"type":"string","description":"Flow slug (flow_<slug>) or GUID","examples":["weekly-digest"]},"inputs":{"type":"object","description":"Input values matching the flow's declared inputs"}},"required":["flow"],"additionalProperties":false,"examples":[{"flow":"weekly-digest","inputs":{"since":"2026-09-20"}}]}""",
        // query_{slug} tools share this schema (SourceQueryToolsProvider).
        ["__query_source__"] = """{"type":"object","properties":{"query":{"type":"string","description":"Text or question to search within this source","examples":["search term"]},"topK":{"type":"integer","description":"Max results (default 5, max 50)"}},"required":["query"],"examples":[{"query":"search term","topK":5}]}""",
    };

    // SPEC-20260922-tool-descriptions-en-us RF-001/RF-002: catalog surface is
    // en-US and free of system references — regression markers below.
    private static readonly string[] ForbiddenMarkers =
    [
        "KnowledgeHub", "Chat:Provider", "Obsidian",
        "Busca", "busca", "Pergunta", "pergunta", "Conteúdo", "conteúdo",
        "fonte", "Fonte", "Slug da", "Slug do", "Máx.", "sincronização",
        "conhecimento", "nota"
    ];

    private static readonly HashSet<string> WriteTools =
        ["write_knowledge", "write_note", "firecrawl_crawl", "tavily_crawl", "tavily_research", "set_api_key_settings", "set_chat_settings", "run_flow"];

    [Fact]
    public async Task ToolCatalog_NamesSchemasAndHints_MatchPinnedContract()
    {
        var mcp = await ConnectAsync(_factory);
        var http = await TestAuth.LoginAsync(_factory);

        var sourceName = $"ContractVault{Guid.NewGuid():N}";
        var create = await http.PostAsJsonAsync("/api/sources", new
        {
            name = sourceName,
            type = "ObsidianVault",
            configuration = new { path = _factory.Vault },
            isActive = true
        });
        create.EnsureSuccessStatusCode();

        var result = await mcp.SendAsync("tools/list");
        var tools = result.GetProperty("tools").EnumerateArray().ToList();

        var slug = sourceName.ToLowerInvariant();
        var expectedNames = PinnedSchemas.Keys.Where(k => k != "__query_source__")
            .Append($"query_{slug}").Order().ToList();
        var actualNames = tools.Select(t => t.GetProperty("name").GetString()!).Order().ToList();
        Assert.Equal(string.Join(", ", expectedNames), string.Join(", ", actualNames));

        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            var key = name.StartsWith("query_") ? "__query_source__" : name;
            using var expectedDoc = JsonDocument.Parse(PinnedSchemas[key]);
            var expected = Canonicalize(expectedDoc.RootElement);
            var actual = Canonicalize(tool.GetProperty("inputSchema"));
            Assert.True(expected == actual,
                $"inputSchema drift for '{name}'\nexpected: {expected}\nactual:   {actual}");

            var expectedReadOnly = !WriteTools.Contains(name);
            var actualReadOnly = tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean();
            Assert.True(expectedReadOnly == actualReadOnly,
                $"annotations.readOnlyHint drift for '{name}': expected {expectedReadOnly}, got {actualReadOnly}");

            var surface = tool.GetProperty("description").GetString() + tool.GetProperty("inputSchema").GetRawText();
            foreach (var marker in ForbiddenMarkers)
                Assert.False(surface.Contains(marker, StringComparison.Ordinal),
                    $"tool '{name}' contains forbidden marker '{marker}'");
        }
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_IsMethodNotFound()
    {
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new { name = "removed_tool", arguments = new { } }));
        Assert.Contains("-32601", ex.Message);
    }

    // Canonical JSON: object keys sorted recursively so ordering doesn't
    // produce false-positive diffs.
    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, element);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    Write(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
