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
    : ISourceConnector, IIncrementalSourceConnector
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

        var sha = await api.GetBranchCommitShaAsync(repo, token, cancellationToken);
        if (sha is null && repo.Branch == "main")
        {
            // Default-branch fallback (RF-001: "main ou master").
            repo = repo with { Branch = "master" };
            sha = await api.GetBranchCommitShaAsync(repo, token, cancellationToken);
        }
        if (sha is null)
            throw new InvalidOperationException(
                $"git: branch '{repo.Branch}' not found in {repo.Owner}/{repo.Name} "
                + "(private repo without a PAT also returns 404)");

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
        if (oversized > 0)
            warnings.Add($"{oversized} file(s) skipped — over maxFileSizeBytes");
        var truncatedByCap = eligible.Count > maxFiles;
        if (truncatedByCap)
        {
            warnings.Add($"file list truncated at maxFiles={maxFiles} ({eligible.Count} eligible)");
            eligible = eligible.Take(maxFiles).ToList();
        }

        var documents = new List<RawDocument>();
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
                var text = await api.GetFileTextAsync(repo, e.Path, token, cancellationToken);
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

        return new FetchResult(documents, warnings,
            FailedUris: failed.Count > 0 ? failed : null,
            Truncated: truncatedByCap);
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

        if (config.String("repoUrl") is { Length: > 0 } url
            && Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var segs = u.AbsolutePath.Trim('/').Split('/');
            if (segs.Length < 2)
                throw new InvalidOperationException("repoUrl must look like https://host/owner/name");
            // SPEC-20260929 RF-007: GitLab subgroups — owner is every segment
            // before the repo (group/sub/...), not just the parent.
            owner ??= string.Join('/', segs[..^1]);
            name ??= segs[^1].TrimEnd('/');
            if (name!.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (provider.Length == 0)
                provider = u.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) ? "gitlab"
                    : u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "github"
                    : "gitea";
            // Non-default host → self-hosted instance.
            if (!u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && !u.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase))
                instance ??= $"{u.Scheme}://{u.Host}";
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "GitRepository requires 'repoUrl' or both 'owner' and 'name'");

        if (provider.Length == 0)
            provider = "github";
        if (provider is not ("github" or "gitlab" or "gitea"))
            throw new InvalidOperationException(
                $"unsupported provider '{provider}' — github|gitlab|gitea");

        string apiBase;
        if (!string.IsNullOrWhiteSpace(instance))
        {
            if (!Uri.TryCreate(instance, UriKind.Absolute, out var inst)
                || inst.Scheme is not ("https" or "http"))
                throw new InvalidOperationException("instanceUrl must be an absolute http(s) URL");
            if (inst.Scheme != "https" && !config.Bool("allowPrivateHosts"))
                throw new InvalidOperationException(
                    "instanceUrl must use https — set 'allowPrivateHosts': true for local instances");
            if (!config.Bool("allowPrivateHosts"))
                await WebPageConnector.GuardPublicAsync(inst, ct);
            apiBase = provider switch
            {
                "gitlab" => $"{inst.GetLeftPart(UriPartial.Authority)}/api/v4",
                "gitea" => $"{inst.GetLeftPart(UriPartial.Authority)}/api/v1",
                _ => $"{inst.GetLeftPart(UriPartial.Authority)}/api/v3" // GitHub Enterprise
            };
        }
        else
        {
            apiBase = provider == "gitlab" ? "https://gitlab.com/api/v4"
                : provider == "gitea"
                    ? throw new InvalidOperationException(
                        "provider 'gitea' requires 'instanceUrl' (no hosted default)")
                    : "https://api.github.com";
        }

        var branch = config.String("branch") is { Length: > 0 } b ? b : "main";
        return new GitRepositoryRef(provider, apiBase.TrimEnd('/'), owner!, name!, branch,
            config.Bool("allowPrivateHosts"));
    }
}
