using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace KnowledgeHub.Server.Ingestion.Connectors.GitProviders;

/// <summary>
/// Unified read-only REST client for the three supported providers
/// (SPEC-20260927-git-repository-source-connector RF-002/RF-003):
/// <list type="bullet">
///   <item>github / gitea — GitHub-shaped API (<c>/repos/{o}/{r}/git/trees</c>,
///   <c>/contents</c>, <c>/media</c>), gitea differs only in base path and the
///   commit payload field (<c>commit.id</c> vs <c>commit.sha</c>).</item>
///   <item>gitlab — v4 API (<c>/projects/{url-encoded o/r}/repository/...</c>),
///   <c>PRIVATE-TOKEN</c> auth, paginated tree.</item>
/// </list>
/// </summary>
public sealed class GitApiClient(IHttpClientFactory httpFactory) : IGitApiClient
{
    public async Task<string?> GetBranchCommitShaAsync(
        GitRepositoryRef repo, string? token, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("git");
        var url = repo.Provider switch
        {
            "gitlab" => $"{repo.ApiBase}/projects/{Uri.EscapeDataString($"{repo.Owner}/{repo.Name}")}"
                + $"/repository/commits/{Uri.EscapeDataString(repo.Branch)}",
            "gitea" => $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Name}/branches/{Uri.EscapeDataString(repo.Branch)}",
            _ => $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Name}/branches/{Uri.EscapeDataString(repo.Branch)}"
        };

        using var response = await SendAsync(client, repo, url, token, acceptRaw: false, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccess(response, repo, "branch lookup", ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        // github/gitlab: {commit:{sha}} / {id}; gitea: {commit:{id}}.
        var root = doc.RootElement;
        if (repo.Provider == "gitlab")
            return root.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (root.TryGetProperty("commit", out var commit))
            return commit.TryGetProperty("sha", out var sha) ? sha.GetString()
                : commit.TryGetProperty("id", out var id) ? id.GetString() : null;
        return null;
    }

    public async Task<IReadOnlyList<GitTreeEntry>> GetTreeAsync(
        GitRepositoryRef repo, string? token, CancellationToken ct)
    {
        return repo.Provider == "gitlab"
            ? await GetGitLabTreeAsync(repo, token, ct)
            : await GetGitHubShapedTreeAsync(repo, token, ct);
    }

    private async Task<IReadOnlyList<GitTreeEntry>> GetGitHubShapedTreeAsync(
        GitRepositoryRef repo, string? token, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("git");
        var url = $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Name}"
            + $"/git/trees/{Uri.EscapeDataString(repo.Branch)}?recursive=1";
        using var response = await SendAsync(client, repo, url, token, acceptRaw: false, ct);
        // Tree 404/409 after a successful branch lookup = empty repository.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            return [];
        await EnsureSuccess(response, repo, "tree listing", ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tree", out var tree))
            return [];
        var entries = new List<GitTreeEntry>();
        foreach (var e in tree.EnumerateArray().Where(e =>
            !e.TryGetProperty("type", out var type) || type.GetString() == "blob"))
        {
            var path = e.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(path))
                continue;
            var size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
            var sha = e.TryGetProperty("sha", out var h) ? h.GetString() ?? "" : "";
            entries.Add(new GitTreeEntry(path, size, sha));
        }
        return entries;
    }

    private async Task<IReadOnlyList<GitTreeEntry>> GetGitLabTreeAsync(
        GitRepositoryRef repo, string? token, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("git");
        var project = Uri.EscapeDataString($"{repo.Owner}/{repo.Name}");
        var entries = new List<GitTreeEntry>();
        for (var page = 1; ; page++)
        {
            var url = $"{repo.ApiBase}/projects/{project}/repository/tree"
                + $"?ref={Uri.EscapeDataString(repo.Branch)}&recursive=true&per_page=100&page={page}";
            using var response = await SendAsync(client, repo, url, token, acceptRaw: false, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return entries.Count == 0 ? [] : entries;
            await EnsureSuccess(response, repo, "tree listing", ct);

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var count = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                count++;
                if (e.TryGetProperty("type", out var type) && type.GetString() != "blob")
                    continue;
                var path = e.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(path))
                    continue;
                var sha = e.TryGetProperty("id", out var h) ? h.GetString() ?? "" : "";
                entries.Add(new GitTreeEntry(path, 0, sha));
            }
            if (count < 100)
                return entries;
        }
    }

    public async Task<string> GetFileTextAsync(
        GitRepositoryRef repo, string path, string? token, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("git");
        var url = repo.Provider switch
        {
            "gitlab" => $"{repo.ApiBase}/projects/{Uri.EscapeDataString($"{repo.Owner}/{repo.Name}")}"
                + $"/repository/files/{Uri.EscapeDataString(path)}/raw"
                + $"?ref={Uri.EscapeDataString(repo.Branch)}",
            "gitea" => $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Name}/media/{path}"
                + $"?ref={Uri.EscapeDataString(repo.Branch)}",
            _ => $"{repo.ApiBase}/repos/{repo.Owner}/{repo.Name}/contents/{path}"
                + $"?ref={Uri.EscapeDataString(repo.Branch)}"
        };
        var raw = repo.Provider is "gitlab" or "gitea";
        using var response = await SendAsync(client, repo, url, token, acceptRaw: raw, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"file '{path}' not found at {repo.Branch}");
        await EnsureSuccess(response, repo, $"download of '{path}'", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, GitRepositoryRef repo, string url,
        string? token, bool acceptRaw, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KnowledgeHub", "1.0"));
        if (acceptRaw)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw"));
        if (!string.IsNullOrEmpty(token))
        {
            if (repo.Provider == "gitlab")
                request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
            else
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    repo.Provider == "gitea" ? "token" : "Bearer", token);
        }
        return await client.SendAsync(request, ct);
    }

    private static async Task EnsureSuccess(
        HttpResponseMessage response, GitRepositoryRef repo, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var status = (int)response.StatusCode;
        var message = status switch
        {
            401 or 403 => $"git {action}: authentication failed ({status}) — check the stored PAT for "
                + $"{repo.Owner}/{repo.Name}",
            404 => $"git {action}: not found (404) — {repo.Owner}/{repo.Name}@{repo.Branch}",
            429 => $"git {action}: rate limited (429) — retry the sync later",
            >= 500 => $"git {action}: remote error ({status}) — retry the sync later",
            _ => $"git {action}: unexpected status {status}"
        };
        // Body is drained so keep-alive can recycle the connection.
        _ = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(message);
    }
}
