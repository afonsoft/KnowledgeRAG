using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Skills;

/// <summary>
/// SPEC-20261111-knowledge-hub-agent-skill RF-004/RF-005: published skills
/// stay synchronized with canonical docs, wiring snippets are valid JSON
/// with key placeholders, and no real secrets leak into skill files.
/// </summary>
public sealed class SkillsFreshnessTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KnowledgeHub.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Generated_docs_skill_matches_sources()
    {
        // xunit v2 has no runtime skip — when bash is unavailable (Windows
        // dev box) the check degrades to verifying the file exists.
        var bash = FindOnPath("bash");
        if (bash is null)
        {
            Assert.True(File.Exists(Path.Combine(Root, "skills/knowledge-hub-docs/SKILL.md")));
            return;
        }

        var psi = new ProcessStartInfo(bash, "scripts/generate-skills.sh --check")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0,
            $"docs skill is stale — run scripts/generate-skills.sh\n{stderr}");
    }

    [Theory]
    [InlineData("knowledge-hub")]
    [InlineData("knowledge-hub-docs")]
    [InlineData("knowledge-hub-architect")]
    public void Skill_frontmatter_is_valid(string skill)
    {
        var path = Path.Combine(Root, "skills", skill, "SKILL.md");
        Assert.True(File.Exists(path), $"missing {path}");
        var text = File.ReadAllText(path);
        Assert.StartsWith("---", text.TrimStart());
        Assert.Matches(new Regex(@"^name:\s*" + Regex.Escape(skill) + @"\s*$", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^description:\s*\S+", RegexOptions.Multiline), text);
    }

    [Fact]
    public void Skills_index_lists_every_skill_dir()
    {
        var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Root, "skills/index.json")));
        var names = manifest.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToHashSet();
        var dirs = Directory.GetDirectories(Path.Combine(Root, "skills"))
            .Select(Path.GetFileName).ToHashSet();
        Assert.Subset(dirs, names!); // every manifest entry is a real dir
        Assert.Contains("knowledge-hub", names);
        Assert.Contains("knowledge-hub-docs", names);
        Assert.Contains("knowledge-hub-architect", names);
    }

    [Fact]
    public void Wiring_snippets_are_valid_json_with_placeholder()
    {
        var dir = Path.Combine(Root, "skills/knowledge-hub/wiring");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.True(files.Length >= 4, $"expected >=4 harness snippets, got {files.Length}");
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            using var doc = JsonDocument.Parse(text); // throws on invalid JSON
            Assert.Contains("AFT_KEY_HERE", text);
            Assert.Contains("/mcp", text);
        }
    }

    [Fact]
    public void Skill_files_contain_no_real_secrets()
    {
        var realKey = new Regex(@"aft_[A-Za-z0-9]{16,}|sk-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{20,}|-----BEGIN");
        foreach (var f in Directory.GetFiles(Path.Combine(Root, "skills"), "*", SearchOption.AllDirectories))
            Assert.DoesNotMatch(realKey, File.ReadAllText(f));
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exe + ext);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
