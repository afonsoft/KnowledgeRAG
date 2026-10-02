using System.Net;
using System.Text.Json;
using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;

// SonarQube S1075: 'https://api.notion.com' é o default configurável via connector config 'apiBaseUrl' — intencional.
#pragma warning disable S1075

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// Notion connector (SPEC-20260919-notion-connector): ingests pages and database
/// rows shared with an internal integration via the Notion REST API.
/// Discovery runs over <c>POST /search</c> (everything shared) or is restricted
/// to configured <c>rootPageIds</c>/<c>rootDatabaseIds</c> via tree traversal.
/// Incremental sync keys on <c>notion:{last_edited_time}</c> fingerprints —
/// unchanged pages skip the block fetch (search mode) and emit empty content so
/// the pipeline dedups without re-chunking. In roots mode the tree is always
/// walked because it doubles as child discovery, but the fingerprint still
/// skips re-processing. Per-item failures land in warnings, never abort the sync.
/// </summary>
public sealed class NotionConnector(
    IHttpClientFactory httpClientFactory,
    IIntegrationSecretStore secrets,
    ILogger<NotionConnector> logger) : IIncrementalSourceConnector, IItemFetchConnector
{
    public SourceType Type => SourceType.Notion;

    /// <summary>Secret store slug for this source's integration token.</summary>
    public static string SecretKey(Guid sourceId) => $"notion:{sourceId}";

    /// <summary>Builds the authenticated v1 client shared by FetchAsync and the
    /// on-demand item fetch (RF-001).</summary>
    private async Task<NotionApiClient> BuildClientAsync(
        KnowledgeSource source, ConnectorConfig config, CancellationToken cancellationToken)
    {
        var token = await secrets.GetAsync(SecretKey(source.Id), cancellationToken)
            ?? throw new InvalidOperationException(
                $"Notion source '{source.Name}': token Notion não configurado — salve a source com o integration token");

        var apiBaseUrl = config.String("apiBaseUrl") ?? "https://api.notion.com";
        var apiVersion = config.String("apiVersion") ?? "2022-06-28";

        var http = httpClientFactory.CreateClient("notion");
        http.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
        var client = new NotionApiClient(http, token, apiVersion);

        try
        {
            await client.ProbeAsync(cancellationToken);
        }
        catch (NotionApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("token Notion inválido", ex);
        }
        return client;
    }

    /// <summary>RF-001 (SPEC-20260926-ingestion-connector-integrity): on-demand
    /// single-page fetch for stub docs that need reprocessing without stored
    /// RawContent. Returns null when the page is unreachable.</summary>
    public async Task<RawDocument?> FetchItemAsync(
        KnowledgeSource source, string uriReference, CancellationToken cancellationToken)
    {
        const string prefix = "notion://page/";
        if (!uriReference.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var pageId = uriReference[prefix.Length..];
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var client = await BuildClientAsync(source, config, cancellationToken);
        var ctx = new FetchContext(client, new Dictionary<string, string>(),
            config.Int("maxPages", 200, 1, 1000),
            config.Int("maxBlockDepth", 10, 1, 50),
            config.Int("maxBlocksPerPage", 500, 10, 5000));
        try
        {
            var page = await client.GetPageAsync(pageId, cancellationToken);
            ctx.ResetPageBudget(pageId);
            var tree = await BuildTreeAsync(pageId, 0, ctx, cancellationToken);
            var title = NotionBlockRenderer.ExtractPageTitle(page);
            // RF-204: database rows are pages too — their searchable content
            // lives in properties; rendering without them would silently
            // unindex fields during on-demand reindex.
            return new RawDocument(
                uriReference, title, Render(page, tree, includeProperties: true),
                $"notion:{ReadEditedTime(page)}");
        }
        catch (NotionApiException ex)
        {
            logger.LogWarning(ex, "Notion on-demand fetch for {PageId} failed", pageId);
            return null;
        }
    }

    public Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken) =>
        FetchAsync(source, new Dictionary<string, string>(), cancellationToken);

    public async Task<FetchResult> FetchAsync(
        KnowledgeSource source,
        IReadOnlyDictionary<string, string> existingFingerprints,
        CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var maxPages = config.Int("maxPages", 200, 1, 1000);
        var maxBlockDepth = config.Int("maxBlockDepth", 10, 1, 50);
        var maxBlocksPerPage = config.Int("maxBlocksPerPage", 500, 10, 5000);
        var rootPageIds = config.StringArray("rootPageIds");
        var rootDatabaseIds = config.StringArray("rootDatabaseIds");

        var client = await BuildClientAsync(source, config, cancellationToken);

        var ctx = new FetchContext(client, existingFingerprints, maxPages, maxBlockDepth, maxBlocksPerPage);

        if (rootPageIds.Length > 0 || rootDatabaseIds.Length > 0)
            await FetchViaTraversalAsync(rootPageIds, rootDatabaseIds, ctx, cancellationToken);
        else
            await FetchViaSearchAsync(client, ctx, cancellationToken);

        if (ctx.Truncated)
            ctx.Warnings.Add($"fetch truncated at maxPages={maxPages}");

        logger.LogInformation("Notion fetch for source {SourceId}: {Docs} documents, {Warnings} warnings",
            source.Id, ctx.Documents.Count, ctx.Warnings.Count);
        return new FetchResult(ctx.Documents, ctx.Warnings,
            ctx.FailedUris.Count > 0 ? ctx.FailedUris : null, ctx.Truncated);
    }

    /// <summary>Roots-mode fetch: BFS over configured rootPageIds/rootDatabaseIds —
    /// child_page/child_database ids collected while building trees become new
    /// traversal roots (unseen ones only — Seen* guards cycles).</summary>
    private async Task FetchViaTraversalAsync(
        string[] rootPageIds, string[] rootDatabaseIds, FetchContext ctx, CancellationToken cancellationToken)
    {
        ctx.TraversalMode = true;
        var queue = new Queue<(string Id, bool IsDatabase)>();
        foreach (var id in rootPageIds) queue.Enqueue((id, false));
        foreach (var id in rootDatabaseIds) queue.Enqueue((id, true));

        while (queue.Count > 0 && !ctx.Truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (id, isDatabase) = queue.Dequeue();
            if (isDatabase)
                await FetchDatabaseRowsAsync(id, ctx, cancellationToken);
            else
                await FetchPageByIdAsync(id, ctx, cancellationToken);

            foreach (var p in ctx.DiscoveredPages)
                queue.Enqueue((p, false));
            ctx.DiscoveredPages.Clear();
            foreach (var d in ctx.DiscoveredDatabases)
                queue.Enqueue((d, true));
            ctx.DiscoveredDatabases.Clear();
        }
    }

    /// <summary>Search-mode fetch: every page/database shared with the
    /// integration via <c>POST /search</c>.</summary>
    private async Task FetchViaSearchAsync(
        NotionApiClient client, FetchContext ctx, CancellationToken cancellationToken)
    {
        await foreach (var obj in client.SearchAsync(cancellationToken))
        {
            if (ctx.Truncated)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            var objectType = obj.ValueKind == JsonValueKind.Object
                && obj.TryGetProperty("object", out var o) && o.ValueKind == JsonValueKind.String
                ? o.GetString()
                : null;
            if (objectType == "page")
                await ProcessPageAsync(obj, ctx, cancellationToken);
            else if (objectType == "database"
                     && obj.TryGetProperty("id", out var dbId) && dbId.ValueKind == JsonValueKind.String)
                await FetchDatabaseRowsAsync(dbId.GetString()!, ctx, cancellationToken);
        }
    }

    /// <summary>Traversal-mode page fetch: resolves metadata, walks the tree for
    /// rendering AND child discovery (child_page/child_database enqueue).</summary>
    private async Task FetchPageByIdAsync(
        string pageId, FetchContext ctx, CancellationToken cancellationToken)
    {
        if (ctx.SeenPages.Contains(pageId))
            return;
        try
        {
            var page = await ctx.Client.GetPageAsync(pageId, cancellationToken);
            await ProcessPageAsync(page, ctx, cancellationToken);
        }
        catch (NotionApiException ex)
        {
            logger.LogWarning(ex, "Notion page {PageId} fetch failed", pageId);
            ctx.Warnings.Add($"{pageId}: {ex.Message}");
            ctx.FailedUris.Add($"notion://page/{pageId}"); // exists upstream, fetch failed — keep doc (RF-002)
        }
    }

    private async Task FetchDatabaseRowsAsync(
        string databaseId, FetchContext ctx, CancellationToken cancellationToken)
    {
        if (!ctx.SeenDatabases.Add(databaseId))
            return;
        try
        {
            await foreach (var row in ctx.Client.QueryDatabaseAsync(databaseId, cancellationToken))
            {
                if (ctx.Truncated)
                    break;
                await ProcessPageAsync(row, ctx, cancellationToken, includeProperties: true);
            }
        }
        catch (NotionApiException ex)
        {
            logger.LogWarning(ex, "Notion database {DatabaseId} query failed", databaseId);
            ctx.Warnings.Add($"database {databaseId}: {ex.Message}");
            // RF-203: a failed/partial query hides rows the pipeline would then
            // DELETE as "remotely removed" — mark the listing incomplete so
            // reconciliation skips the unseen-deletion pass for this fetch.
            ctx.Truncated = true;
        }
    }

    /// <summary>Emits one RawDocument per page/row. When the stored fingerprint
    /// still matches, emits empty content (pipeline dedup skips re-chunking); in
    /// traversal mode the tree is still fetched for child discovery.</summary>
    private async Task ProcessPageAsync(
        JsonElement page, FetchContext ctx,
        CancellationToken cancellationToken, bool includeProperties = false)
    {
        var id = page.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()!
            : null;
        if (id is null || !ctx.SeenPages.Add(id))
            return;
        if (ctx.Documents.Count >= ctx.MaxPages)
        {
            ctx.Truncated = true;
            return;
        }

        var title = NotionBlockRenderer.ExtractPageTitle(page);
        var uri = $"notion://page/{id}";
        var fingerprint = $"notion:{ReadEditedTime(page)}";
        var unchanged = ctx.Existing.TryGetValue(uri, out var stored) && stored == fingerprint;

        if (unchanged && !ctx.TraversalMode)
        {
            ctx.Documents.Add(new RawDocument(uri, title, "", fingerprint));
            return;
        }

        List<NotionBlock> tree;
        try
        {
            ctx.ResetPageBudget(id);
            tree = await BuildTreeAsync(id, 0, ctx, cancellationToken);
        }
        catch (NotionApiException ex)
        {
            logger.LogWarning(ex, "Notion blocks for {PageId} failed", id);
            ctx.Warnings.Add($"{title} ({id}): {ex.Message}");
            ctx.FailedUris.Add(uri); // page exists, blocks failed — keep doc (RF-002)
            return;
        }

        var text = unchanged ? "" : Render(page, tree, includeProperties);
        ctx.Documents.Add(new RawDocument(uri, title, text, fingerprint));
    }

    private static string Render(JsonElement page, List<NotionBlock> tree, bool includeProperties)
    {
        var body = NotionBlockRenderer.Render(tree);
        if (!includeProperties
            || !page.TryGetProperty("properties", out var props)
            || props.ValueKind != JsonValueKind.Object)
            return body;
        var header = string.Join('\n', NotionBlockRenderer.SerializeProperties(props));
        return body.Length == 0 ? header : header + "\n\n" + body;
    }

    /// <summary>Recursively materializes a page's block tree with hard bounds
    /// (maxBlockDepth, maxBlocksPerPage). child_page/child_database ids are
    /// collected for traversal discovery — their content lives in their own
    /// documents, so they are rendered as markers, not expanded.</summary>
    private static async Task<List<NotionBlock>> BuildTreeAsync(
        string blockId, int depth, FetchContext ctx, CancellationToken cancellationToken)
    {
        var nodes = new List<NotionBlock>();
        await foreach (var block in ctx.Client.GetBlockChildrenAsync(blockId, cancellationToken))
        {
            if (++ctx.BlockCount > ctx.MaxBlocksPerPage)
            {
                ctx.WarnOnce($"{ctx.CurrentPageId}: block tree truncated at maxBlocksPerPage={ctx.MaxBlocksPerPage}");
                break;
            }
            nodes.Add(await BuildNodeAsync(block, depth, ctx, cancellationToken));
        }
        return nodes;
    }

    /// <summary>Materializes one block: collects child_page/child_database ids
    /// for traversal discovery and recurses into children within maxBlockDepth.</summary>
    private static async Task<NotionBlock> BuildNodeAsync(
        JsonElement block, int depth, FetchContext ctx, CancellationToken cancellationToken)
    {
        var type = StringProp(block, "type");
        var id = StringProp(block, "id");

        if (type == "child_page" && id is not null)
            ctx.DiscoveredPages.Add(id);
        else if (type == "child_database" && id is not null)
            ctx.DiscoveredDatabases.Add(id);

        var hasChildren = block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty("has_children", out var h) && h.ValueKind == JsonValueKind.True;
        IReadOnlyList<NotionBlock> children = [];
        if (hasChildren && type is not ("child_page" or "child_database") && id is not null)
        {
            if (depth + 1 > ctx.MaxBlockDepth)
            {
                ctx.WarnOnce($"{ctx.CurrentPageId}: block tree truncated at maxBlockDepth={ctx.MaxBlockDepth}");
            }
            else
            {
                children = await BuildTreeAsync(id, depth + 1, ctx, cancellationToken);
            }
        }
        return new NotionBlock(block, children);
    }

    private static string? StringProp(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string ReadEditedTime(JsonElement obj) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty("last_edited_time", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? ""
            : "";

    private sealed class FetchContext(
        NotionApiClient client,
        IReadOnlyDictionary<string, string> existing,
        int maxPages,
        int maxBlockDepth,
        int maxBlocksPerPage)
    {
        public NotionApiClient Client { get; } = client;
        public IReadOnlyDictionary<string, string> Existing { get; } = existing;
        public int MaxPages { get; } = maxPages;
        public int MaxBlockDepth { get; } = maxBlockDepth;
        public int MaxBlocksPerPage { get; } = maxBlocksPerPage;
        public bool TraversalMode { get; set; }
        public bool Truncated { get; set; }
        public int BlockCount { get; set; }
        public string CurrentPageId { get; private set; } = "";
        public List<RawDocument> Documents { get; } = [];
        public List<string> Warnings { get; } = [];
        /// <summary>Page URIs that exist upstream but failed mid-fetch —
        /// reconciliation keeps their documents (RF-002).</summary>
        public List<string> FailedUris { get; } = [];
        public HashSet<string> SeenPages { get; } = new();
        public HashSet<string> SeenDatabases { get; } = new();
        public List<string> DiscoveredPages { get; } = [];
        public List<string> DiscoveredDatabases { get; } = [];

        public void ResetPageBudget(string pageId)
        {
            BlockCount = 0;
            CurrentPageId = pageId;
        }

        public void WarnOnce(string message)
        {
            if (!Warnings.Contains(message))
                Warnings.Add(message);
        }
    }
}
