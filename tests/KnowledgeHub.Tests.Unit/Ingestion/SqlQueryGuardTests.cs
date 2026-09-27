using KnowledgeHub.Server.Ingestion.Connectors;

namespace KnowledgeHub.Tests.Unit.Ingestion;

// Covers SPEC-20260927-restapi-sqldatabase-connectors RF-004: SELECT-only
// validation — first token SELECT/WITH, single terminal semicolon, write
// keywords rejected as whole words outside literals and comments.
public class SqlQueryGuardTests
{
    [Theory]
    [InlineData("SELECT id, title FROM notes")]
    [InlineData("select * from t where x = 1")]
    [InlineData("WITH cte AS (SELECT 1 AS n) SELECT n FROM cte")]
    [InlineData("  SELECT 1  ")]                // leading/trailing whitespace
    [InlineData("SELECT 1;")]                    // single terminal semicolon
    [InlineData("SELECT 1 ;")]                   // semicolon after trailing space
    public void Valid_SelectOrWith_Passes(string query)
    {
        var (ok, reason) = SqlQueryGuard.Validate(query);
        Assert.True(ok, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DELETE FROM x")]
    [InlineData("insert into t values (1)")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DROP TABLE y")]
    [InlineData("SELECT 1; DROP TABLE y")]       // intermediate semicolon
    [InlineData("SELECT 1; SELECT 2")]            // two statements
    [InlineData("SELECT 1; ;")]                   // stray semicolon
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("PRAGMA journal_mode=WAL")]
    [InlineData("ATTACH DATABASE 'x' AS y")]
    [InlineData("CREATE TABLE t (id int)")]
    [InlineData("ALTER TABLE t ADD c int")]
    [InlineData("GRANT SELECT ON t TO u")]
    [InlineData("COPY t TO '/tmp/x'")]
    [InlineData("CALL proc()")]
    [InlineData("VACUUM")]
    [InlineData("MERGE INTO t USING s ON 1=1")]
    [InlineData("REPLACE INTO t VALUES (1)")]
    [InlineData("EXEC sp")]
    [InlineData("EXECUTE stmt")]
    [InlineData("DETACH x")]
    [InlineData("REVOKE SELECT ON t FROM u")]
    public void Invalid_Queries_Are_Rejected(string query)
    {
        var (ok, reason) = SqlQueryGuard.Validate(query);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void WriteKeyword_InsideSingleQuoteLiteral_IsAllowed()
    {
        // Edge case (SPEC §6): keywords only count outside literals.
        var (ok, reason) = SqlQueryGuard.Validate("SELECT 'DROP TABLE' AS v FROM t");
        Assert.True(ok, reason);
    }

    [Fact]
    public void WriteKeyword_InsideDoubleQuotedIdentifier_IsAllowed()
    {
        var (ok, reason) = SqlQueryGuard.Validate("SELECT \"delete\" AS v FROM t");
        Assert.True(ok, reason);
    }

    [Fact]
    public void WriteKeyword_InLineComment_IsAllowed()
    {
        var (ok, reason) = SqlQueryGuard.Validate("""
            -- housekeeping note: never DELETE rows here
            SELECT 1
            """);
        Assert.True(ok, reason);
    }

    [Fact]
    public void WriteKeyword_InBlockComment_IsAllowed()
    {
        var (ok, reason) = SqlQueryGuard.Validate("/* UPDATE stats nightly */ SELECT 1");
        Assert.True(ok, reason);
    }

    [Fact]
    public void WriteKeyword_InTrailingComment_IsAllowed()
    {
        // SPEC §6: keywords only count outside literals AND comments —
        // a trailing "DROP" inside a line comment stays a comment.
        var (ok, reason) = SqlQueryGuard.Validate("SELECT 1 WHERE x = 'a' -- DROP TABLE y");
        Assert.True(ok, reason);
    }

    [Theory]
    [InlineData("SELECT created_at FROM logs")]     // CREATE inside identifier
    [InlineData("SELECT updated_at FROM logs")]     // UPDATE inside identifier
    [InlineData("SELECT deleted_flag FROM logs")]   // DELETE inside identifier
    [InlineData("SELECT replacements FROM logs")]   // REPLACE inside identifier
    public void KeywordAsPartOfIdentifier_IsAllowed(string query)
    {
        var (ok, reason) = SqlQueryGuard.Validate(query);
        Assert.True(ok, reason);
    }
}
