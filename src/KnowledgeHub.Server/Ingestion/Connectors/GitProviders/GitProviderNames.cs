namespace KnowledgeHub.Server.Ingestion.Connectors.GitProviders;

/// <summary>Canonical <c>provider</c> config values shared by the Git
/// connector and its REST API clients.</summary>
internal static class GitProviderNames
{
    public const string GitHub = "github";
    public const string GitLab = "gitlab";
    public const string Gitea = "gitea";
}
