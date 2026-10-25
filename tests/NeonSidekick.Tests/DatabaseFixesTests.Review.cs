using NeonSidekick.Computers;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Shell;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// The review of the database fixes (2026-10-24, DB8's <c>/code-review high</c> over the branch): each finding it raised that was fixed, held
/// by the smallest case that shows it.
/// </summary>
public sealed class DatabaseFixesReviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-dbfix-review-").FullName;

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    private static readonly ServerDatabaseGuard Servers = new(
    [
        new("PostgreSQL", "postgres_query", "postgres_execute", ServerDatabasePolice.PostgresWords, []),
    ]);

    [Fact]
    public async Task ADatabaseHold_Lapses()
    {
        using var scope = SignInGuard.Scope();
        var call = new SignInCall(SignInGuard.HoldKey("sql:x", "u", "p"), "sql:x", _ => true, TimeSpan.Zero);
        var first = await SignInGuard.OpenAsync<bool>(call, _ => throw new InvalidOperationException("login failed"), CancellationToken.None);
        Assert.NotNull(first.Failure);
        var second = await SignInGuard.OpenAsync(call, _ => Task.FromResult(true), CancellationToken.None);   // lapsed at once: tried again
        Assert.Equal(SignInStop.None, second.Stop);

        var held = call with { Lapse = null };
        await SignInGuard.OpenAsync<bool>(held, _ => throw new InvalidOperationException("login failed"), CancellationToken.None);
        Assert.Equal(SignInStop.Held, (await SignInGuard.OpenAsync(held, _ => Task.FromResult(true), CancellationToken.None)).Stop);   // the admin families' hold stays
        Assert.Equal(TimeSpan.FromMinutes(15), DatabaseSignIn.HoldFor);
    }

    [Theory]
    [InlineData("git commit -m \"$(psql -c 'DROP TABLE t')\"")]          // a command substitution is no plain message
    [InlineData("git commit -m 'x & psql -c q'")]                        // cmd reads & inside single quotes
    [InlineData("node -e \"require('pg')\"")]
    [InlineData("import pg from \"pg\"")]
    public void TheServerPolice_Refuses(string line) => Assert.NotNull(ServerDatabasePolice.Judge(line, _dir, _dir, Servers));

    [Theory]
    [InlineData("git commit -m \"psql: quote the name\"")]
    [InlineData("gcc -pg main.c")]                                       // pg is a package only as code names it, quoted
    [InlineData("ls src/pg/")]
    [InlineData("cat docs/pg-notes.md")]
    public void TheServerPolice_Passes(string line) => Assert.Null(ServerDatabasePolice.Judge(line, _dir, _dir, Servers));

    [Fact]
    public void AScriptAfterAValueOption_OrAsItsValue_IsRead()
    {
        File.WriteAllText(Path.Combine(_dir, "evil.py"), "x");
        File.WriteAllText(Path.Combine(_dir, "evil.js"), "x");
        Assert.Equal("evil.py", Assert.Single(SqlitePolice.ScriptFiles("python -I evil.py", _dir, _dir)).Name);
        Assert.Equal("evil.js", Assert.Single(SqlitePolice.ScriptFiles("node --require ./evil.js -e 1", _dir, _dir)).Name);
    }

    [Fact]
    public void Oracle_ADeniedMember_UnderAQuotedPackage_IsRefused()
    {
        Assert.NotNull(OracleWriteGate.Check("BEGIN \"DBMS_UTILITY\".EXEC_DDL_STATEMENT('DROP TABLE t'); END;", Enum.GetValues<ServerStatementKind>()));
        Assert.NotNull(OracleReadOnlyGate.Check("SELECT \"DBMS_UTILITY\".EXEC_DDL_STATEMENT('x') FROM dual"));
    }

    [Fact]
    public async Task AReadTheReadGateRefuses_NeedsChangingData()
    {
        var hooks = new ServerExecuteHooks(
            PostgresWriteGate.Check, PostgresWriteGate.Kinds, PostgresReadOnlyGate.Check, s => s,
            () => (new ExecuteTarget("c", true, "db", "h"), null),
            (_, _) => throw new InvalidOperationException("no read"),
            (_, _) => throw new InvalidOperationException("no run"),
            PostgresText.Error);
        string answer = await ServerExecute.RunAsync(PostgresStatementKinds.Family, "read-write", ["read"], "SELECT setval('s', 1)", [], 10, 1000, hooks, null, CancellationToken.None);
        Assert.StartsWith("Error: the SQL is changing data", answer);
    }

    [Fact]
    public void TheSmallFixes()
    {
        // A clear-text request is named with its way through; a refusal before the statement is audited as not run.
        Assert.EndsWith(PostgresText.CleartextRefused, PostgresText.WithCleartextHint("'Password' authentication method is not allowed"));
        Assert.Equal("timeout", PostgresText.WithCleartextHint("timeout"));
        Assert.Equal(DatabaseAuditOutcome.NotRun, DatabaseAudit.OutcomeOf(SqlRun.Refused(SqlOutcome.Failed, "the session did not keep it", "c")));

        // Oracle before 12.2 has no LISTAGG overflow clause.
        string describe = string.Join("\n", OracleCatalogQueries.Describe);
        Assert.Contains(OracleCatalogQueries.OverflowClause, describe, StringComparison.Ordinal);
        Assert.DoesNotContain(OracleCatalogQueries.OverflowClause, OracleCatalogQueries.ForServer(describe, "12.1.0.2.0"), StringComparison.Ordinal);
        Assert.Equal(describe, OracleCatalogQueries.ForServer(describe, "19.3.0.0.0"));

        // A name the names list writes as a cell (a schema named NULL) still matches.
        Assert.Equal(PostgresText.OtherSchema("NULL", "sales"), PostgresBoundary.Check("SELECT * FROM \"NULL\".t", "sales", ["sales", "'NULL'"]));

        // A partial database list leaves the alias's head for the server to settle.
        const string call = "SELECT payroll.dbo.f() FROM dbo.t AS payroll";
        Assert.Equal(["payroll"], SqlBoundary.UnsettledHeads(call, "scratch", ["scratch", ""]));
        Assert.Empty(SqlBoundary.UnsettledHeads(call, "scratch", ["scratch"]));
        Assert.Null(SqlBoundary.Check(call, "scratch", ["scratch", "master"]));

        // The folder walk reads no file too small to be a database.
        string folder = Path.Combine(_dir, "tiny");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "x"), [.. "SQLite format 3\0"u8.ToArray()]);
        Assert.Null(WorkingDirectory.HoldsGuarded(folder, git: false, DatabaseGuard.ByExtension, CancellationToken.None));
    }
}
