using NeonSidekick.MySql;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 7 of the database review's fixes (2026-10-24): the read gates, mechanism 6 — each hand-written lexer held to its server's documented
/// token forms by a table, one harmful and one harmless line per rule, the smallest that shows it.
/// </summary>
public sealed class DatabaseReadGateTests
{
    // ── PostgreSQL ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1 --x\rDELETE FROM t")]                 // A1: a -- comment ends at a lone CR too
    [InlineData("SELECT 1 --x\nDELETE FROM t")]
    [InlineData("SELECT * FROM t LIMIT 1FOR SHARE")]             // A15: a number ends at its last digit (PG 12–14 read FOR SHARE)
    [InlineData("SELECT 1INTO t")]
    [InlineData("SELECT x $a$ DELETE $a$")]                 // F1: a no-break space is a name's, so no dollar quote opens there
    [InlineData("SELECT @a$b$ DELETE $b$")]                      // F1: a placeholder run into a $
    [InlineData("SELECT @a.b FROM t")]                            // G11: a placeholder run into a dot
    [InlineData("SELECT brin_summarize_new_values('i')")]        // C10
    [InlineData("SELECT gin_clean_pending_list('i')")]
    [InlineData("SELECT pg_background_launch('x')")]             // F9
    [InlineData("SELECT pg_log_backend_memory_contexts(1)")]
    [InlineData("SELECT pg_logdir_ls()")]
    [InlineData("SELECT http_get('http://x')")]                  // L1
    public void Postgres_Refuses(string sql) => Assert.NotNull(PostgresReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("SELECT 1 /* nested /* */ DELETE */")]           // nested comments: what is inside stays inside
    [InlineData("SELECT 'a''b'")]
    [InlineData("SELECT E'a\\'DELETE'")]                         // a backslash escape inside E'…'
    [InlineData("SELECT $q$ DELETE $q$")]
    [InlineData("SELECT 0x1F, 0o17, 0b101, 1_000, 1e5, 1.5e-3, .5")]
    [InlineData("SELECT a$b FROM t")]                            // a $ inside a name opens nothing
    [InlineData("SELECT * FROM t WHERE id = @id")]
    public void Postgres_Allows(string sql) => Assert.Null(PostgresReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("1FOR", 1)]
    [InlineData("1.5e-3x", 6)]
    [InlineData("0x1Fz", 4)]
    [InlineData("1_000", 5)]
    [InlineData("1..2", 1)]
    public void Postgres_ANumberEndsWhereTheServersDoes(string text, int end) => Assert.Equal(end, PostgresReadOnlyGate.NumberEnd(text, 0));

    // ── MySQL / MariaDB ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1 -- x\nDELETE FROM t")]
    [InlineData("SELECT 1 -- x DELETE FROM t")]             // F2: '--' before a no-break space is no comment to the server
    [InlineData("SELECT 1 --\u0085x DELETE FROM t")]             // F2: nor before a C1 control
    [InlineData("SELECT /*+ MAX_EXECUTION_TIME(0) */ 1")]        // A12
    [InlineData("SELECT /*+ SET_VAR(max_execution_time = 0) */ 1")]
    [InlineData("SELECT MASTER_GTID_WAIT('0-1-1')")]             // F10
    [InlineData("SELECT service_get_write_locks('ns', 'l', 0)")]
    [InlineData("SELECT version_tokens_set('a=1')")]
    [InlineData("SELECT group_replication_set_as_primary('x')")]
    public void MySql_Refuses(string sql) => Assert.NotNull(MySqlReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("SELECT 1 -- x")]
    [InlineData("SELECT 1 #x")]
    [InlineData("SELECT 0x1e+5")]                                // F6: 0x1e, then + 5
    [InlineData("SELECT 1e10x")]                                 // 1e10, then the alias x
    [InlineData("SELECT 123abc FROM t")]                         // A2: a digit-led name
    [InlineData("SELECT 'it''s', \"a\\\"b\"")]
    public void MySql_Allows(string sql) => Assert.Null(MySqlReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("2024sales.orders", "2024SALES", true)]
    [InlineData("123abc", "123ABC", true)]
    [InlineData("1e10x", "1e10", false)]
    [InlineData("0x1Fz", "0X1FZ", true)]
    [InlineData("0x1e+5", "0x1e", false)]
    [InlineData("1.5abc", "1.5", false)]
    public void MySql_ADigitLedRun_IsANameWhereTheServerMakesOne(string text, string first, bool name)
    {
        var token = MySqlReadOnlyGate.Tokenize(text, out _)![0];
        Assert.Equal(first, token.Text);
        Assert.Equal(name ? MySqlReadOnlyGate.TokenKind.Word : MySqlReadOnlyGate.TokenKind.Number, token.Kind);
    }

    /// <summary>A2: a digit-led database name reaches the boundary as a qualifier.</summary>
    [Fact]
    public void MySql_ADigitLedDatabase_IsAQualifier() =>
        Assert.Equal(MySqlText.OtherDatabase("2024sales", "shop"), MySqlBoundary.Check("SELECT * FROM 2024sales.orders", "shop", ["shop", "2024sales"]));

    // ── Oracle ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1 FROM dual --x\rDELETE FROM t")]                      // G10
    [InlineData("SELECT 1q'[x]' FROM dual")]                                   // F7
    [InlineData("SELECT DBMS_XSLPROCESSOR.READ2CLOB('D', 'f') FROM dual")]     // C5
    [InlineData("SELECT URIFACTORY.GETURI('x') FROM dual")]                    // F8
    [InlineData("SELECT XDBURITYPE('/x').GETCLOB() FROM dual")]
    [InlineData("SELECT DBMS_SQLHASH.GETHASH('SELECT 1 FROM dual', 2) FROM dual")]
    [InlineData("SELECT APEX_WEB_SERVICE.MAKE_REST_REQUEST('http://x', 'GET') FROM dual")]   // L2
    [InlineData("SELECT DBMS_UTILITY.EXEC_DDL_STATEMENT('x') FROM dual")]      // A5
    [InlineData("SELECT * FROM t EXTERNAL MODIFY (LOCATION ('x'))")]          // I3
    public void Oracle_Refuses(string sql) => Assert.NotNull(OracleReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("SELECT q'[it's]' FROM dual")]
    [InlineData("SELECT nq'{x}', 'a''b' FROM dual")]
    [InlineData("SELECT 1 q FROM dual")]
    [InlineData("SELECT DBMS_UTILITY.FORMAT_ERROR_STACK FROM dual")]
    public void Oracle_Allows(string sql) => Assert.Null(OracleReadOnlyGate.Check(sql));

    // ── SQLite and SQL Server ───────────────────────────────────────────────

    [Fact]
    public void Sqlite_RefusesTheRawPages_WithOrWithoutParentheses()
    {
        Assert.NotNull(SqliteReadOnlyGate.Check("SELECT * FROM sqlite_dbpage"));   // L3
        Assert.Null(SqliteReadOnlyGate.Check("SELECT * FROM pages"));
    }

    [Theory]
    [InlineData("SELECT * FROM t WITH (TABLOCKX)")]                   // B1
    [InlineData("SELECT * FROM t WITH (UPDLOCK, HOLDLOCK)")]
    [InlineData("SELECT * FROM t WITH (XLOCK)")]
    public void SqlServer_RefusesALockingHint(string sql) => Assert.NotNull(SqlReadOnlyGate.Check(sql));

    [Fact]
    public void SqlServer_AllowsAReadingHint() => Assert.Null(SqlReadOnlyGate.Check("SELECT * FROM t WITH (NOLOCK)"));
}
