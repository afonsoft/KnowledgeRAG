namespace KnowledgeHub.Server.Ingestion.Connectors.GitProviders;

/// <summary>A resolved git remote: provider family + API base + coordinates.</summary>
public sealed record GitRepositoryRef(
    string Provider, string ApiBase, string Owner, string Name, string Branch);

/// <summary>One file entry discovered via the repository tree API.</summary>
public sealed record GitTreeEntry(string Path, long Size, string BlobSha);

/// <summary>
/// Read-only REST surface for git hosting providers (SPEC-20260927
/// git-repository-source-connector). No CLI, no write operations — trees and
/// blobs over HTTP only.
/// </summary>
public interface IGitApiClient
{
    /// <summary>Current commit SHA of the branch; null when the ref doesn't exist.</summary>
    Task<string?> GetBranchCommitShaAsync(
        GitRepositoryRef repo, string? token, CancellationToken ct);

    /// <summary>All blob entries under the branch (recursive tree).</summary>
    Task<IReadOnlyList<GitTreeEntry>> GetTreeAsync(
        GitRepositoryRef repo, string? token, CancellationToken ct);

    /// <summary>Raw text content of one file at the branch ref.</summary>
    Task<string> GetFileTextAsync(
        GitRepositoryRef repo, string path, string? token, CancellationToken ct);
}
