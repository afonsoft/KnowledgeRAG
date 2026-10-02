namespace KnowledgeHub.Server;

/// <summary>
/// Resolves the SQLite database location (SPEC-06 RF-002):
/// <c>KnowledgeHub:DatabasePath</c> → <c>Database:Path</c> →
/// <c>{AppContext.BaseDirectory}/knowledgehub.db</c>. Relative paths resolve
/// against the executable directory so single-file installs are portable.
/// </summary>
public static class DatabasePath
{
    public const string DefaultFileName = "knowledgehub.db";

    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration["KnowledgeHub:DatabasePath"]
            ?? configuration.GetValue<string>("Database:Path");
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Join(AppContext.BaseDirectory, DefaultFileName);
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Join(AppContext.BaseDirectory, configured));
    }

    /// <summary>Ensures the parent directory exists; surfaces a clear error on read-only locations.</summary>
    public static void EnsureDirectory(IConfiguration configuration)
    {
        var path = Resolve(configuration);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot create database directory for '{path}'. " +
                "Set KnowledgeHub:DatabasePath to a writable location.", ex);
        }
    }
}
