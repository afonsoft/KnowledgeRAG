using KnowledgeHub.Server.Domain.Entities;
using KnowledgeHub.Server.Ingestion.Connectors.GitProviders;
using KnowledgeHub.Server.Settings;
using KnowledgeHub.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Server.Ingestion.Connectors;

/// <summary>
/// <see cref="SourceType.GitRepository"/> connector (SPEC-20260927
/// git-repository-source-connector). Read-only HTTP REST integration —
/// never shells out to <c>git</c>.
/// <list type="bullet">
///   <item>RF-001: <c>repoUrl</c> or owner/name; provider github|gitlab|gitea
///   (inferred from well-known hosts, overridable; self-hosted via
///   <c>instanceUrl</c> behind an SSRF guard).</item>
///   <item>RF-002: one recursive tree call → include/exclude glob filtering,
///   size and count caps.</item>
///   <item>RF-003: per-blob raw download; per-file failures land in warnings +
///   <see cref="FetchResult.FailedUris"/>.</item>
///   <item>RF-004: commit-SHA fast path — when every stored fingerprint still
///   carries the current <c>{commitSha}</c> the sync returns stubs without a
///   single blob download. Per-file fingerprint
///   <c>git:{provider}:{owner}/{name}:{branch}:{commitSha}:{path}:{blobSha}</c>
///   additionally skips re-downloading files whose blob is unchanged.</item>
/// </list>
/// PATs live in <see cref="IIntegrationSecretStore"/> under
/// <c>git:{sourceId}</c> — public repositories need none.
/// </summary>
public sealed class GitRepositoryConnector(
    IHttpClientFactory httpFactory,
    IIntegrationSecretStore secrets,
    ILogger<GitRepositoryConnector> logger)
    : IIncrementalSourceConnector
{
    private static readonly string[] DefaultIncludes = ["**/*.md", "**/README*", "**/*.txt"];
    private static readonly string[] DefaultExcludes =
        [".git/**", "**/node_modules/**", "**/bin/**", "**/obj/**", "**/*.min.js"];

    public SourceType Type => SourceType.GitRepository;

    internal static string SecretKey(Guid sourceId) => $"git:{sourceId}";

    public Task<FetchResult> FetchAsync(KnowledgeSource source, CancellationToken cancellationToken) =>
        FetchAsync(source, new Dictionary<string, string>(), cancellationToken);

    public async Task<FetchResult> FetchAsync(
        KnowledgeSource source,
        IReadOnlyDictionary<string, string> existingFingerprints,
        CancellationToken cancellationToken)
    {
        var config = ConnectorConfig.Parse(source.ConfigurationJson);
        var repo = await ResolveRepoAsync(config, cancellationToken);
        var token = await secrets.GetAsync(SecretKey(source.Id), cancellationToken);
        var api = new GitApiClient(httpFactory);
        var warnings = new List<string>();
        var failed = new List<string>();

        var prefix = $"git:{repo.Provider}:{repo.Owner}/{repo.Name}:{repo.Branch}";

        (repo, var sha) = await ResolveBranchShaAsync(api, repo, token, cancellationToken);

        // RF-004 fast path: unchanged commit → NoChanges stubs, zero downloads.
        var commitPrefix = $"{prefix}:{sha}:";
        if (existingFingerprints.Count > 0
            && existingFingerprints.Values.All(v => v.StartsWith(commitPrefix, StringComparison.Ordinal)))
        {
            logger.LogDebug("git {Owner}/{Name}@{Branch} unchanged at {Sha}", repo.Owner, repo.Name, repo.Branch, sha);
            return new FetchResult(
                existingFingerprints.Select(kv => new RawDocument(kv.Key, "", "", kv.Value)).ToList(),
                warnings);
        }

        var includes = config.StringArray("includePatterns");
        var excludes = config.StringArray("excludePatterns");
        var includeMatchers = (includes.Length > 0 ? includes : DefaultIncludes)
            .Select(DocumentFileConnector.GlobMatcher.Compile).ToArray();
        var excludeMatchers = (excludes.Length > 0 ? excludes : DefaultExcludes)
            .Select(DocumentFileConnector.GlobMatcher.Compile).ToArray();
        var pathPrefix = (config.String("path") ?? "").Trim('/');
        var maxFiles = config.Int("maxFiles", 200, 1, 1000);
        var maxBytes = config.Int("maxFileSizeBytes", 500 * 1024, 10 * 1024, 5 * 1024 * 1024);

        var tree = await api.GetTreeAsync(repo, token, cancellationToken);
        var (eligible, oversized) = FilterTree(
            tree, includeMatchers, excludeMatchers, pathPrefix, maxBytes, repo, failed);
        if (oversized > 0)
            warnings.Add($"{oversized} file(s) skipped — over maxFileSizeBytes");
        var truncatedByCap = eligible.Count > maxFiles;
        if (truncatedByCap)
        {
            warnings.Add($"file list truncated at maxFiles={maxFiles} ({eligible.Count} eligible)");
            eligible = eligible.Take(maxFiles).ToList();
        }

        var documents = await DownloadFilesAsync(
            api, repo, token, eligible, existingFingerprints, commitPrefix,
            maxBytes, warnings, failed, cancellationToken);

        return new FetchResult(documents, warnings,
            FailedUris: failed.Count > 0 ? failed : null,
            Truncated: truncatedByCap);
    }

    /// <summary>Resolves the branch commit SHA with the default-branch
    /// fallback (RF-001: "main ou master").</summary>
    private async Task<(GitRepositoryRef Repo, string Sha)> ResolveBranchShaAsync(
        GitApiClient api, GitRepositoryRef repo, string? token, CancellationToken ct)
    {
        var sha = await api.GetBranchCommitShaAsync(repo, token, ct);
        if (sha is null && repo.Branch == "main")
        {
            // Default-branch fallback (RF-001: "main ou master").
            repo = repo with { Branch = "master" };
            sha = await api.GetBranchCommitShaAsync(repo, token, ct);
        }
        if (sha is null)
            throw new InvalidOperationException(
                $"git: branch '{repo.Branch}' not found in {repo.Owner}/{repo.Name} "
                + "(private repo without a PAT also returns 404)");
        return (repo, sha);
    }

    /// <summary>Tree filter result: eligible entries + oversized count.</summary>
    private sealed record TreeFilterResult(List<GitTreeEntry> Eligible, int Oversized);

    /// <summary>Applies path prefix, glob include/exclude and the size gate;
    /// oversized-but-present files enter FailedUris (RF-007) so reconciliation
    /// keeps the already-indexed document.</summary>
    private TreeFilterResult FilterTree(
        IReadOnlyList<GitTreeEntry> tree,
        Func<string, bool>[] includeMatchers, Func<string, bool>[] excludeMatchers,
        string pathPrefix, long maxBytes, GitRepositoryRef repo, List<string> failed)
    {
        var eligible = new List<GitTreeEntry>();
        var oversized = 0;
        foreach (var e in tree)
        {
            var path = e.Path;
            if (pathPrefix.Length > 0 && !path.StartsWith(pathPrefix + "/", StringComparison.Ordinal))
                continue;
            if (!includeMatchers.Any(m => m(path)) || excludeMatchers.Any(m => m(path)))
                continue;
            // RF-007: an oversized-but-present file must enter FailedUris —
            // otherwise reconciliation treats the URI as deleted and drops the
            // already-indexed document even though the remote file exists.
            if (e.Size > maxBytes)
            {
                oversized++;
                failed.Add($"git://{repo.Provider}/{repo.Owner}/{repo.Name}@{repo.Branch}:{e.Path}");
                continue;
            }
            eligible.Add(e);
        }
        return new TreeFilterResult(eligible, oversized);
    }

    /// <summary>Downloads eligible files with fingerprint skip + post-download
    /// size enforcement (SPEC-20260929 RF-007 — GitLab Size=0 entries).</summary>
    private async Task<List<RawDocument>> DownloadFilesAsync(
        GitApiClient api, GitRepositoryRef repo, string? token,
        List<GitTreeEntry> eligible,
        IReadOnlyDictionary<string, string> existingFingerprints,
        string commitPrefix, long maxBytes,
        List<string> warnings, List<string> failed, CancellationToken ct)
    {
        var documents = new List<RawDocument>();
        var oversized = 0;
        foreach (var e in eligible)
        {
            var fingerprint = $"{commitPrefix}{e.Path}:{e.BlobSha}";
            var uri = $"git://{repo.Provider}/{repo.Owner}/{repo.Name}@{repo.Branch}:{e.Path}";

            if (existingFingerprints.TryGetValue(uri, out var prev) && prev == fingerprint)
            {
                documents.Add(new RawDocument(uri, "", "", prev));
                continue;
            }

            try
            {
                var text = await api.GetFileTextAsync(repo, e.Path, token, ct);
                // SPEC-20260929 RF-007: GitLab tree entries carry Size=0 — the
                // pre-download gate can't fire; enforce the limit on content.
                // FailedUris keeps the previously-indexed document alive.
                if (e.Size <= 0 && System.Text.Encoding.UTF8.GetByteCount(text) > maxBytes)
                {
                    oversized++;
                    warnings.Add($"{e.Path}: skipped — over maxFileSizeBytes (post-download check)");
                    failed.Add(uri);
                    continue;
                }
                documents.Add(new RawDocument(uri, Path.GetFileName(e.Path), text, fingerprint));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"{e.Path}: {ex.Message}");
                failed.Add(uri);
            }
        }
        if (oversized > 0)
            warnings.Add($"{oversized} file(s) skipped — over maxFileSizeBytes (post-download)");
        return documents;
    }

    /// <summary>RF-001 + SSRF: resolves provider/owner/name/apiBase —
    /// <c>repoUrl</c> wins over the owner+name tuple; <c>instanceUrl</c> must
    /// be https and non-private unless <c>allowPrivateHosts</c>.</summary>
    internal static async Task<GitRepositoryRef> ResolveRepoAsync(
        ConnectorConfig config, CancellationToken ct)
    {
        var provider = (config.String("provider") ?? "").Trim().ToLowerInvariant();
        string? owner = config.String("owner"), name = config.String("name");
        var instance = config.String("instanceUrl");

        (provider, owner, name, instance) = ApplyRepoUrl(
            config.String("repoUrl"), provider, owner, name, instance);
        provider = ValidateOwnerAndProvider(provider, owner, name);
        var apiBase = await ResolveApiBaseAsync(config, provider, instance, ct);

        var branch = config.String("branch") is { Length: > 0 } b ? b : "main";
        return new GitRepositoryRef(provider, apiBase.TrimEnd('/'), owner, name, branch,
            config.Bool("allowPrivateHosts"));
    }

    /// <summary>repoUrl wins over the owner+name tuple: derives owner/name
    /// (GitLab subgroups — owner is every segment before the repo, RF-007),
    /// the provider from the host, and a self-hosted instance for
    /// non-default hosts.</summary>
    private static (string Provider, string? Owner, string? Name, string? Instance) ApplyRepoUrl(
        string? repoUrl, string provider, string? owner, string? name, string? instance)
    {
        if (string.IsNullOrEmpty(repoUrl)
            || !Uri.TryCreate(repoUrl, UriKind.Absolute, out var u))
            return (provider, owner, name, instance);

        var segs = u.AbsolutePath.Trim('/').Split('/');
        if (segs.Length < 2)
            throw new InvalidOperationException("repoUrl must look like https://host/owner/name");
        // SPEC-20260929 RF-007: GitLab subgroups — owner is every segment
        // before the repo (group/sub/...), not just the parent.
        owner ??= string.Join('/', segs[..^1]);
        name ??= segs[^1].TrimEnd('/');
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (provider.Length == 0)
            provider = ProviderFromHost(u.Host);
        // Non-default host → self-hosted instance.
        if (!u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !u.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase))
            instance ??= $"{u.Scheme}://{u.Host}";
        return (provider, owner, name, instance);
    }

    private static string ProviderFromHost(string host) =>
        host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) ? GitProviderNames.GitLab
        : host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? GitProviderNames.GitHub
        : GitProviderNames.Gitea;

    /// <summary>Owner/name must be present; provider defaults to GitHub and
    /// must be one of the supported ones.</summary>
    private static string ValidateOwnerAndProvider(string provider, string? owner, string? name)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "GitRepository requires 'repoUrl' or both 'owner' and 'name'");

        if (provider.Length == 0)
            provider = GitProviderNames.GitHub;
        if (provider is not (GitProviderNames.GitHub or GitProviderNames.GitLab or GitProviderNames.Gitea))
            throw new InvalidOperationException(
                $"unsupported provider '{provider}' — github|gitlab|gitea");
        return provider;
    }

    /// <summary>Instance URL must be https and non-private unless
    /// <c>allowPrivateHosts</c>; hosted providers get their default API base.</summary>
    private static async Task<string> ResolveApiBaseAsync(
        ConnectorConfig config, string provider, string? instance, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instance))
        {
            if (provider == GitProviderNames.GitLab)
                return "https://gitlab.com/api/v4";
            if (provider == GitProviderNames.Gitea)
                throw new InvalidOperationException(
                    "provider 'gitea' requires 'instanceUrl' (no hosted default)");
            return "https://api.github.com";
        }

        if (!Uri.TryCreate(instance, UriKind.Absolute, out var inst)
            || inst.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("instanceUrl must be an absolute http(s) URL");
        if (inst.Scheme != "https" && !config.Bool("allowPrivateHosts"))
            throw new InvalidOperationException(
                "instanceUrl must use https — set 'allowPrivateHosts': true for local instances");
        if (!config.Bool("allowPrivateHosts"))
            await WebPageConnector.GuardPublicAsync(inst, ct);
        var authority = inst.GetLeftPart(UriPartial.Authority);
        return provider switch
        {
            GitProviderNames.GitLab => $"{authority}/api/v4",
            GitProviderNames.Gitea => $"{authority}/api/v1",
            _ => $"{authority}/api/v3" // GitHub Enterprise
        };
    }
}
