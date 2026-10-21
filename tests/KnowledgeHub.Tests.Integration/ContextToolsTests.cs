using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using static KnowledgeHub.Tests.Integration.TestMcp;

namespace KnowledgeHub.Tests.Integration;

/// <summary>
/// SPEC-20261004-context-snapshots — end-to-end coverage for
/// context_save / context_recovery / context_compact over MCP + REST on SQLite.
/// </summary>
public class ContextToolsTests : IClassFixture<ContextToolsTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DbPath { get; } = Path.Join(Path.GetTempPath(), $"kh-ctx-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = DbPath,
                    ["DeepWiki:Enabled"] = "false",
                    ["Context:MaxSnapshotsPerRepo"] = "3"
                }));
        }
    }

    private readonly Fixture _factory;

    public ContextToolsTests(Fixture factory) => _factory = factory;

    private static JsonElement Structured(JsonElement callResult) =>
        callResult.GetProperty("structuredContent");

    private static string Repo() => $"owner-{Guid.NewGuid():N}/repo";

    [Fact]
    public async Task ToolsList_Exposes_ContextTools()
    {
        var mcp = await ConnectAsync(_factory);
        var names = ToolNames(await mcp.SendAsync("tools/list"));
        Assert.Contains("context_save", names);
        Assert.Contains("context_recovery", names);
        Assert.Contains("context_compact", names);
    }

    [Fact]
    public async Task SaveAndRecover_SameRepo_VersionMonotonic()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        for (var i = 0; i < 3; i++)
        {
            var save = await mcp.SendAsync("tools/call", new
            {
                name = "context_save",
                arguments = new
                {
                    repo,
                    items = new[] { new { kind = "decision", content = $"d{i}" } }
                }
            });
            Assert.Equal(i + 1, Structured(save).GetProperty("version").GetInt32());
        }
    }

    [Fact]
    public async Task Recovery_NormalizesRepoCase()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        await mcp.SendAsync("tools/call", new
        {
            name = "context_save",
            arguments = new { repo, items = new[] { new { kind = "note", content = "n" } } }
        });
        var parts = repo.Split('/');
        var mixed = $"{parts[0].ToUpperInvariant()}/{parts[1].ToUpperInvariant()}";
        var rec = await mcp.SendAsync("tools/call", new
        {
            name = "context_recovery",
            arguments = new { repo = mixed }
        });
        Assert.False(rec.GetProperty("isError").GetBoolean());
        Assert.Equal(repo, Structured(rec).GetProperty("repo").GetString());
    }

    [Fact]
    public async Task Recovery_OtherRepo_NotFound()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        await mcp.SendAsync("tools/call", new
        {
            name = "context_save",
            arguments = new { repo, items = new[] { new { kind = "note", content = "x" } } }
        });
        var rec = await mcp.SendAsync("tools/call", new
        {
            name = "context_recovery",
            arguments = new { repo = Repo() }
        });
        Assert.True(rec.GetProperty("isError").GetBoolean());
        Assert.Contains("context_not_found",
            rec.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task InvalidRepo_Rejected()
    {
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new
            {
                name = "context_save",
                arguments = new { repo = "noslash", items = new[] { new { kind = "note", content = "c" } } }
            }));
        Assert.Contains("invalid_repo", ex.Message);
    }

    [Fact]
    public async Task Secret_InPayload_Rejected()
    {
        var mcp = await ConnectAsync(_factory);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mcp.SendAsync("tools/call", new
            {
                name = "context_save",
                arguments = new
                {
                    repo = Repo(),
                    items = new[] { new { kind = "note", content = "token = sk-abc123" } }
                }
            }));
        Assert.Contains("secret_detected", ex.Message);
    }

    [Fact]
    public async Task Retention_PrunesOldest()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        int pruned = 0;
        for (var i = 0; i < 5; i++)
        {
            var save = await mcp.SendAsync("tools/call", new
            {
                name = "context_save",
                arguments = new { repo, items = new[] { new { kind = "note", content = $"n{i}" } } }
            });
            pruned = Structured(save).GetProperty("pruned").GetInt32();
        }
        Assert.Equal(1, pruned); // MaxSnapshotsPerRepo=3 → 5th save prunes to 3

        var http = await TestAuth.LoginAsync(_factory);
        var list = await http.GetFromJsonAsync<JsonElement>(
            $"/api/context/snapshots?repo={Uri.EscapeDataString(repo)}");
        Assert.Equal(3, list.GetArrayLength());
    }

    [Fact]
    public async Task Recovery_Elision_StubsOldToolOutput()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        var big = new string('y', 4000);
        await mcp.SendAsync("tools/call", new
        {
            name = "context_save",
            arguments = new
            {
                repo,
                items = new object[]
                {
                    new { kind = "tool_output", tool = "search_knowledge", content = big },
                    new { kind = "decision", content = "keep me" },
                    new { kind = "note", content = "tail1" },
                    new { kind = "note", content = "tail2" },
                    new { kind = "note", content = "tail3" }
                }
            }
        });
        var rec = await mcp.SendAsync("tools/call", new
        {
            name = "context_recovery",
            arguments = new { repo, strategy = "elision", tokenBudget = 8192 }
        });
        var items = Structured(rec).GetProperty("items");
        var first = items[0];
        Assert.StartsWith("[Output elided:", first.GetProperty("content").GetString());
        Assert.Contains("search_knowledge", first.GetProperty("content").GetString());
        // stored snapshot unchanged — recover strategy=none returns raw content
        var raw = await mcp.SendAsync("tools/call", new
        {
            name = "context_recovery",
            arguments = new { repo, strategy = "none", tokenBudget = 32768 }
        });
        Assert.Equal(big, Structured(raw).GetProperty("items")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Cache_SecondRecovery_ReportsHit_AndSaveInvalidates()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        await mcp.SendAsync("tools/call", new
        {
            name = "context_save",
            arguments = new { repo, items = new[] { new { kind = "note", content = "v1" } } }
        });
        var args = new { repo, strategy = "none", tokenBudget = 4096 };
        var first = await mcp.SendAsync("tools/call", new { name = "context_recovery", arguments = args });
        Assert.False(Structured(first).GetProperty("stats").GetProperty("cacheHit").GetBoolean());
        var second = await mcp.SendAsync("tools/call", new { name = "context_recovery", arguments = args });
        Assert.True(Structured(second).GetProperty("stats").GetProperty("cacheHit").GetBoolean());

        // save invalida: próximo recovery é miss e vê a nova versão
        await mcp.SendAsync("tools/call", new
        {
            name = "context_save",
            arguments = new { repo, items = new[] { new { kind = "note", content = "v2" } } }
        });
        var third = await mcp.SendAsync("tools/call", new { name = "context_recovery", arguments = args });
        var s = Structured(third);
        Assert.False(s.GetProperty("stats").GetProperty("cacheHit").GetBoolean());
        Assert.Equal(2, s.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task IdempotencyKey_SameKeyReturnsSameVersion()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        var args = new
        {
            repo,
            idempotencyKey = "k-1",
            items = new[] { new { kind = "note", content = "n" } }
        };
        var a = Structured(await mcp.SendAsync("tools/call", new { name = "context_save", arguments = args }));
        var b = Structured(await mcp.SendAsync("tools/call", new { name = "context_save", arguments = args }));
        Assert.Equal(a.GetProperty("version").GetInt32(), b.GetProperty("version").GetInt32());
        Assert.True(b.GetProperty("idempotentHit").GetBoolean());
    }

    [Fact]
    public async Task Compact_RequiresExactlyOneSource()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        // nem items nem fromVersion
        var none = await mcp.SendAsync("tools/call", new
        {
            name = "context_compact",
            arguments = new { repo }
        });
        Assert.True(none.GetProperty("isError").GetBoolean());
        // ambos
        var both = await mcp.SendAsync("tools/call", new
        {
            name = "context_compact",
            arguments = new
            {
                repo,
                fromVersion = 1,
                items = new[] { new { kind = "note", content = "x" } }
            }
        });
        Assert.True(both.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Compact_FromVersion_SavePersistsNewVersion()
    {
        var mcp = await ConnectAsync(_factory);
        var repo = Repo();
        var big = new string('z', 3000);
        await mcp.SendAsync("tools/call", new
        {
            name = "context_save",
            arguments = new
            {
                repo,
                items = new object[]
                {
                    new { kind = "tool_output", tool = "t", content = big },
                    new { kind = "decision", content = "d" },
                    new { kind = "note", content = "tail1" },
                    new { kind = "note", content = "tail2" },
                    new { kind = "note", content = "tail3" }
                }
            }
        });
        // keep-tail=3 → adiciono 3 notes após o tool_output para ele ser elidido
        var cmp = await mcp.SendAsync("tools/call", new
        {
            name = "context_compact",
            arguments = new { repo, fromVersion = 1, strategy = "elision", save = true, label = "compactado" }
        });
        var s = Structured(cmp);
        Assert.Equal(2, s.GetProperty("savedVersion").GetInt32());
        Assert.True(s.GetProperty("elidedCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task Compact_FromVersionMissing_404()
    {
        var mcp = await ConnectAsync(_factory);
        var res = await mcp.SendAsync("tools/call", new
        {
            name = "context_compact",
            arguments = new { repo = Repo(), fromVersion = 99 }
        });
        Assert.True(res.GetProperty("isError").GetBoolean());
        Assert.Contains("context_not_found",
            res.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Rest_SaveAndLatest_Roundtrip()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var repo = Repo();
        var save = await http.PostAsJsonAsync("/api/context/snapshots", new
        {
            repo,
            items = new[] { new { kind = "decision", content = "d1" } }
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var latest = await http.GetFromJsonAsync<JsonElement>(
            $"/api/context/snapshots/latest?repo={Uri.EscapeDataString(repo)}");
        Assert.Equal(1, latest.GetProperty("version").GetInt32());
        Assert.Equal(repo, latest.GetProperty("repo").GetString());
    }

    [Fact]
    public async Task Rest_Latest_UnknownRepo_404()
    {
        var http = await TestAuth.LoginAsync(_factory);
        var res = await http.GetAsync(
            $"/api/context/snapshots/latest?repo={Uri.EscapeDataString(Repo())}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
