using System.Text.RegularExpressions;

namespace KnowledgeHub.Tests.Unit.Conventions;

/// <summary>
/// SPEC-20261111-agent-rules-hardening RF-005: <c>.env.example</c> stays in
/// sync with the code. Every env var read directly via
/// <c>Environment.GetEnvironmentVariable("NAME")</c> (or
/// <c>Environment.GetEnvironmentVariable(KnownConstant)</c> literals) in
/// <c>src/</c> must be declared in <c>.env.example</c> — commented or not —
/// unless the call site carries a <c>// env:optional</c> marker.
/// Configuration keys bound through <c>IConfiguration</c>
/// (<c>Section__Key</c> env convention) are intentionally out of scope: they
/// are documented in appsettings comments; this gate covers direct reads.
/// </summary>
public partial class EnvExampleTests
{
    [Fact]
    public void DirectEnvReads_AreDeclaredInEnvExample()
    {
        var root = RepoRoot();
        var envFile = Path.Join(root, ".env.example");
        Assert.True(File.Exists(envFile), ".env.example is missing at the repo root");

        var declared = File.ReadAllLines(envFile)
            .Select(l => EnvDecl().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Join(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("// env:optional")) continue;
                foreach (Match m in EnvRead().Matches(lines[i]))
                {
                    var name = m.Groups[1].Value;
                    if (!declared.Contains(name))
                        missing.Add($"{Path.GetRelativePath(root, file)}:{i + 1} reads env '{name}' not declared in .env.example");
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "KnowledgeHub.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    // VAR_NAME= or # VAR_NAME= (declaration lines)
    [GeneratedRegex(@"^\s*#?\s*([A-Z][A-Z0-9_]{2,})\s*=")]
    private static partial Regex EnvDecl();

    // Environment.GetEnvironmentVariable("VAR_NAME")
    [GeneratedRegex(@"Environment\.GetEnvironmentVariable\(\s*""([A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex EnvRead();
}
