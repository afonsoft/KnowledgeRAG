using System.Text.RegularExpressions;

namespace KnowledgeHub.Tests.Unit.Conventions;

/// <summary>
/// SPEC-20261111-agent-rules-hardening RF-003: parity lint between the SQLite
/// and Postgres migration trees. A migration added to
/// <c>src/KnowledgeHub.Server/Migrations/</c> must have a same-named
/// counterpart under <c>Migrations/Postgres/</c> (name-base = file name
/// without the 14-digit timestamp prefix), unless the file body contains an
/// explicit opt-out marker <c>// dialect: sqlite-only</c> or
/// <c>// dialect: postgres-only</c>.
///
/// Baseline: the Postgres tree was bootstrapped by
/// <c>{ts}_PostgresInitial</c>; SQLite migrations older than that timestamp
/// are grandfathered and need no counterpart.
/// </summary>
public partial class MigrationParityTests
{
    private const string OptOutMarker = "// dialect:";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "KnowledgeHub.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    [Fact]
    public void Migrations_SqliteAndPostgres_ArePairedOrMarked()
    {
        var root = Path.Join(RepoRoot(), "src", "KnowledgeHub.Server", "Migrations");
        var sqlite = Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(f => (NameBase(Path.GetFileName(f)!), Path.GetFileName(f)!, f))
            .Where(t => !t.Item2.EndsWith(".Designer.cs") && !t.Item2.Contains("ModelSnapshot"))
            .ToList();
        var postgres = Directory.EnumerateFiles(Path.Join(root, "Postgres"), "*.cs")
            .Select(f => (NameBase(Path.GetFileName(f)!), Path.GetFileName(f)!, f))
            .Where(t => !t.Item2.EndsWith(".Designer.cs") && !t.Item2.Contains("ModelSnapshot"))
            .ToList();

        // Baseline: Postgres tree bootstrap timestamp — older SQLite migrations predate it.
        var pgBootstrap = postgres
            .Select(t => TimestampPrefix().Match(t.Item2))
            .Where(m => m.Success)
            .Select(m => m.Value.TrimEnd('_'))
            .Min() ?? "99999999999999";

        var pgBases = postgres.Select(t => t.Item1).ToHashSet();
        var sqliteBases = sqlite.Select(t => t.Item1).ToHashSet();

        var failures = new List<string>();

        foreach (var (nameBase, file, path) in sqlite)
        {
            var ts = TimestampPrefix().Match(file);
            var grandfathered = ts.Success && string.CompareOrdinal(ts.Value.TrimEnd('_'), pgBootstrap) < 0;
            if (grandfathered || pgBases.Contains(nameBase)) continue;
            if (File.ReadAllText(path).Contains(OptOutMarker)) continue;
            failures.Add($"SQLite migration '{file}' lacks a Postgres counterpart (or a '{OptOutMarker}' marker)");
        }

        foreach (var (nameBase, file, path) in postgres)
        {
            if (nameBase == "PostgresInitial") continue;
            if (sqliteBases.Contains(nameBase)) continue;
            if (File.ReadAllText(path).Contains(OptOutMarker)) continue;
            failures.Add($"Postgres migration '{file}' lacks a SQLite counterpart (or a '{OptOutMarker}' marker)");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string NameBase(string fileName)
    {
        var m = TimestampPrefix().Match(fileName);
        return (m.Success ? fileName[m.Length..] : fileName)[..^".cs".Length];
    }

    [GeneratedRegex(@"^\d{14}_")]
    private static partial Regex TimestampPrefix();
}
