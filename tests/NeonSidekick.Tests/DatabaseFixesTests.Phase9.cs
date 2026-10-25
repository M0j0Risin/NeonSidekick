using NeonSidekick.MySql;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 9 of the database review's fixes (2026-10-24): the boundaries — DB2's allow-list under a PostgreSQL schema lock, each family's
/// server-wide lists swept, a qualifier after a <c>:</c> or before an operator seen, the names list failing closed when cut or empty, a
/// text the boundary cannot read refused, and names compared as each server folds them.
/// </summary>
public sealed class DatabaseBoundaryTests
{
    // ── SQL Server ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT * FROM ::fn_virtualfilestats(1, 1)")]                                // A3
    [InlineData("SELECT * FROM sys.fn_get_sql(0x01)")]                                        // M2
    [InlineData("SELECT * FROM sys.fn_trace_geteventinfo(1)")]                                // R11
    [InlineData("SELECT * FROM sys.tcp_endpoints")]
    [InlineData("SELECT * FROM sys.resource_governor_configuration")]
    [InlineData("SELECT OBJECT_ID(@name)")]                                                   // R12: a name not written out
    [InlineData("SELECT OBJECT_ID('dbo.' + 't')")]
    [InlineData("not sql at all (")]                                                          // mechanism 7
    public void SqlServer_Refuses(string sql) => Assert.NotNull(SqlBoundary.Check(sql, "scratch"));

    [Theory]
    [InlineData("SELECT COL_LENGTH('payroll.dbo.t', 'c')", "payroll")]                         // C11
    [InlineData("SELECT TYPE_ID('payroll.dbo.money2')", "payroll")]
    [InlineData("SELECT OBJECT_ID((('payroll.dbo.t')))", "payroll")]                           // R12
    public void SqlServer_RefusesANameInAString(string sql, string other) =>
        Assert.Equal(SqlText.OtherDatabase(other, "scratch"), SqlBoundary.Check(sql, "scratch"));

    [Fact]
    public void SqlServer_TheConnectionsOwnInAnotherCase_IsSettledByTheNames()
    {
        // D4: a case-sensitive server holding both is refused; one holding only the connection's own passes.
        Assert.Equal(SqlText.OtherDatabase("SCRATCH", "scratch"), SqlBoundary.Check("SELECT * FROM SCRATCH.dbo.t", "scratch", ["scratch", "SCRATCH"]));
        Assert.Null(SqlBoundary.Check("SELECT * FROM SCRATCH.dbo.t", "scratch", ["scratch", "master"]));
        Assert.True(SqlBoundary.IsCaseTwin("SCRATCH", "scratch"));
        Assert.False(SqlBoundary.IsOther("SCRATCH", "scratch"));
    }

    [Fact]
    public void SqlServer_AnAliasHead_WithAListTheLoginCannotSeeWhole_IsRefused()
    {
        // D5: without VIEW ANY DATABASE the names read carries an empty row; a head the list does not hold may still be a database.
        const string sql = "SELECT payroll.dbo.f() FROM dbo.t AS payroll";
        Assert.Equal(SqlText.HeadUnsettled("payroll", "scratch"), SqlBoundary.Check(sql, "scratch", ["scratch", "master", ""]));
        Assert.Null(SqlBoundary.Check(sql, "scratch", ["scratch", "master"]));
        Assert.Contains("VIEW ANY DATABASE", SqlCatalogQueries.DatabaseNames, StringComparison.Ordinal);
    }

    // ── The names cache ─────────────────────────────────────────────────────

    private static SqlRun Names(bool more, params string[] names) =>
        new(SqlOutcome.Ok, "", "shop", "shop", [new SqlGrid(["name"], [.. names.Select(n => new[] { n })], more)], TimeSpan.Zero);

    [Fact]
    public async Task TheNames_CutAtTheCapOrEmpty_AreAFailedRead_NeverKept()
    {
        var cache = new BoundaryNames();
        int reads = 0;
        var (rows, failed) = await cache.GetAsync("k", _ => { reads++; return Task.FromResult(Names(true, "a", "b")); }, CancellationToken.None);   // E8
        Assert.Null(rows);
        Assert.Equal(SqlText.NamesUnread, failed!.Detail);
        (rows, failed) = await cache.GetAsync("k", _ => { reads++; return Task.FromResult(Names(false)); }, CancellationToken.None);
        Assert.Null(rows);
        Assert.Equal(SqlOutcome.Failed, failed!.Outcome);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task TheNames_AreForgottenAfterAChange_AndASetBuiltOnce()
    {
        var cache = new BoundaryNames();
        int reads = 0;
        Task<SqlRun> Read(CancellationToken _)
        {
            reads++;
            return Task.FromResult(Names(false, "sales"));
        }

        var (first, _) = await cache.GetAsync("k", Read, CancellationToken.None);
        await cache.GetAsync("k", Read, CancellationToken.None);
        Assert.Equal(1, reads);
        cache.Forget();                                                                         // A11
        await cache.GetAsync("k", Read, CancellationToken.None);
        Assert.Equal(2, reads);
        Assert.Same(cache.Built(first!, PostgresBoundary.NameSet), cache.Built(first!, PostgresBoundary.NameSet));   // R14
    }

    // ── PostgreSQL ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT * FROM pg_lock_status()")]                                             // B2
    [InlineData("SELECT current_setting('data_directory')")]
    [InlineData("SELECT * FROM pg_stat_progress_vacuum")]                                      // R3
    [InlineData("SELECT pg_tablespace_location(1)")]
    [InlineData("SELECT * FROM information_schema.user_mapping_options")]                     // C6
    [InlineData("SELECT oid::regrole FROM generate_series(1, 100) AS oid")]                    // R1
    [InlineData("SELECT shobj_description(1, 'pg_database')")]
    public void Postgres_RefusesTheServer_WhateverTheLock(string sql) => Assert.NotNull(PostgresBoundary.Qualifiers(sql, null).Refused);

    [Theory]
    [InlineData("SELECT pg_relation_size('payroll.t')")]                                       // DB2, R2
    [InlineData("SELECT pg_get_partkeydef(1)")]                                                // F14
    [InlineData("SELECT pg_describe_object(1, 2, 0)")]                                         // R1
    [InlineData("SELECT * FROM pg_catalog.pg_operator")]                                       // R4
    [InlineData("SELECT obj_description(1)")]
    [InlineData("SELECT has_table_privilege('payroll.t', 'SELECT')")]
    [InlineData("SELECT to_regclass('payroll.t')")]
    [InlineData("SELECT 'payroll.t'::regclass")]
    [InlineData("SELECT * FROM pg_notes")]                                                     // unqualified: qualify it with the lock's schema
    [InlineData("SELECT setval('payroll.seq', 1)")]                                            // R2
    [InlineData("DROP SCHEMA payroll")]                                                        // G2
    [InlineData("ALTER TABLE sales.t SET SCHEMA payroll")]                                     // A11, G2
    [InlineData("COMMENT ON SCHEMA payroll IS 'x'")]
    [InlineData("CREATE SCHEMA scratch")]
    [InlineData("DROP TABLE sales.t CASCADE")]                                                 // H8
    [InlineData("TRUNCATE sales.t CASCADE")]
    [InlineData("VACUUM FULL")]                                                                // H8: every schema's tables
    [InlineData("VACUUM (FULL, ANALYZE)")]
    [InlineData("ANALYZE")]
    [InlineData("REINDEX DATABASE shop")]
    [InlineData("REINDEX SYSTEM shop")]
    [InlineData("SELECT 'x")]                                                                  // mechanism 7
    public void Postgres_UnderALock_Refuses(string sql) => Assert.NotNull(PostgresBoundary.Check(sql, "sales", ["sales", "payroll"]));

    [Theory]
    [InlineData("SELECT pg_typeof(1), pg_backend_pid(), lower('A') FROM sales.orders")]
    [InlineData("SELECT pg_catalog.lower('A'), pg_snapshot_xmin(pg_current_snapshot())")]
    [InlineData("SELECT * FROM sales.pg_notes")]
    [InlineData("SELECT nextval('seq'), nextval('sales.seq')")]
    [InlineData("CREATE TABLE sales.t (a int REFERENCES sales.p ON DELETE CASCADE)")]
    [InlineData("CREATE SCHEMA IF NOT EXISTS sales")]
    public void Postgres_UnderALock_Allows(string sql) => Assert.Null(PostgresBoundary.Check(sql, "sales", ["sales", "payroll"]));

    [Theory]
    [InlineData("SELECT x::payroll.money2 FROM sales.t")]                                      // F3
    [InlineData("SELECT a[1:payroll.f()] FROM sales.t")]
    [InlineData("SELECT 1 OPERATOR(payroll.+) 2")]                                             // F4
    [InlineData("CREATE FUNCTION sales.f() RETURNS int LANGUAGE sql AS 'SELECT count(*) FROM payroll.salaries'")]   // G1
    public void Postgres_UnderALock_SeesAHeadItMissed(string sql) =>
        Assert.Equal(PostgresText.OtherSchema("payroll", "sales"), PostgresBoundary.Check(sql, "sales", ["sales", "payroll"]));

    [Fact]
    public void Postgres_FoldsAsTheServerDoes()
    {
        // B3: only A to Z fold, so Ärger stays Ärger; F5: an unbound @name after an operator is a bare name, folded.
        Assert.Equal(PostgresText.OtherSchema("Ärger", "sales"), PostgresBoundary.Check("SELECT * FROM Ärger.t", "sales", ["sales", "Ärger"]));
        Assert.Null(PostgresBoundary.Check("SELECT * FROM Ärger.t", "sales", ["sales", "ärger"]));
        Assert.Equal(GateTokenKind.Word, PostgresWriteGate.Lex("SELECT a <@Payroll", out _)!.Last().Kind);
        Assert.Equal("PAYROLL", PostgresWriteGate.Lex("SELECT a <@Payroll", out _)!.Last().Text);
    }

    [Fact]
    public void Postgres_TheCatalogQueries_KeepToTheLock()
    {
        // C7, C8: the keys pointing in and the relationships keep to the locked schema, and a table is found in it by its exact spelling.
        Assert.Contains("@lock", PostgresCatalogQueries.Describe[2], StringComparison.Ordinal);
        Assert.Contains("@lock", PostgresCatalogQueries.Relationships, StringComparison.Ordinal);
        Assert.Contains("@lock::text IS NULL AND lower(n.nspname)", PostgresCatalogQueries.Resolve, StringComparison.Ordinal);
    }

    // ── MySQL / MariaDB ─────────────────────────────────────────────────────

    [Fact]
    public void MySql_ABareWord_IsNeverTheConnectionsOwnUnchecked()
    {
        // R8: with an own SHOP on a case-sensitive server, a bare shop.t (upper-cased by the lexer) is the database shop.
        Assert.Equal(MySqlText.OtherDatabase("shop", "SHOP"), MySqlBoundary.Check("SELECT * FROM shop.t", "SHOP", ["SHOP", "shop"]));
        Assert.Null(MySqlBoundary.Check("SELECT * FROM `SHOP`.t", "SHOP", ["SHOP", "shop"]));
        Assert.Null(MySqlBoundary.Check("SELECT * FROM shop.t", "shop", ["shop"]));
        Assert.Equal(MySqlText.BoundaryUnread, MySqlBoundary.Check("SELECT 'x", "shop", ["shop"]));   // mechanism 7
        Assert.Contains("@db", MySqlCatalogQueries.Resolve, StringComparison.Ordinal);                 // P5: the server's own name rule
    }

    // ── Oracle ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT DBMS_METADATA.GET_DDL('TABLE', 'T', 'HR') FROM dual")]                 // C4
    [InlineData("SELECT * FROM XMLTABLE('fn:collection(\"oradb:/HR/T\")')")]
    [InlineData("SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR('abc'))")]                      // R6
    [InlineData("SELECT * FROM UNIFIED_AUDIT_TRAIL")]
    [InlineData("SELECT * FROM CONTAINERS(t)")]                                                // R13
    [InlineData("SELECT 'x FROM dual")]                                                        // mechanism 7
    public void Oracle_Refuses(string sql) => Assert.NotNull(OracleBoundary.Refusal(sql, "APP"));

    [Fact]
    public void Oracle_AllowsThePlanTable_AndSeesAHeadAfterAColon_AndListsEveryPublicDictionarySynonym()
    {
        Assert.Null(OracleBoundary.Refusal("SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY())", "APP"));
        Assert.Contains("HR", OracleBoundary.Qualifiers("SELECT JSON_OBJECT('k' : HR.f()) FROM dual", "APP").Heads);   // R7
        Assert.DoesNotContain("table_owner", OracleBoundary.Names, StringComparison.Ordinal);                           // R5
        Assert.Null(OracleBoundary.Check("SELECT :NEW.x FROM dual", "APP", ["HR", "NEW"], []));                        // a bind is never a head
    }
}
