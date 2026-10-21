using KnowledgeHub.Server.Context;
using Xunit;

namespace KnowledgeHub.Tests.Unit.Context;

/// <summary>SPEC-20261004-context-snapshots RF-007 — key shapes.</summary>
public class ContextCacheKeysTests
{
    [Fact]
    public void Recovery_KeyShape()
    {
        var key = ContextCacheKeys.Recovery("a/b", "latest", "elision", 2048);
        Assert.Equal("ctx:v1:a/b:latest:elision:2048", key);
    }

    [Fact]
    public void Compact_HashesContent_NoRawData()
    {
        var key = ContextCacheKeys.Compact("a/b", "{\"items\":[{\"kind\":\"note\",\"content\":\"secret-body\"}]}", "none", 100);
        Assert.StartsWith("ctx:v1:a/b:cmp:", key);
        Assert.DoesNotContain("secret-body", key);
        // ctx:v1:a/b:cmp:{hash32}:none:100 — hash prefix has 32 chars
        Assert.Equal(32, key.Split(':')[4].Length);
    }

    [Fact]
    public void RepoTag_Isolation()
    {
        Assert.Equal("ctx:a/b", ContextCacheKeys.RepoTag("a/b"));
        Assert.NotEqual(ContextCacheKeys.RepoTag("a/b"), ContextCacheKeys.RepoTag("a/c"));
    }

    [Fact]
    public void LatestVersionPointer_Shape()
    {
        Assert.Equal("ctx:v1:a/b:latestVersion", ContextCacheKeys.LatestVersionPointer("a/b"));
    }
}
