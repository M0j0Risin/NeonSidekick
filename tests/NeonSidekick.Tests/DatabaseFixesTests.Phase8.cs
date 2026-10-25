using NeonSidekick.MySql;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 8 of the database review's fixes (2026-10-24): the write gates — DB3's readable bodies (a routine's body meets the top level's
/// refusals, one in another language is refused whatever is ticked, who it runs as is never changed), and each kind a statement needs
/// beyond its verb's. Every gate is checked with every kind allowed (null), so a refusal here is one no tick lifts.
/// </summary>
public sealed class DatabaseWriteGateTests
{
    private static readonly ServerStatementKind[] All = Enum.GetValues<ServerStatementKind>();

    // ── SQL Server ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CREATE TRIGGER t ON ALL SERVER FOR LOGON AS BEGIN SELECT 1 END")]                      // A4
    [InlineData("DISABLE TRIGGER ALL ON ALL SERVER")]
    [InlineData("CREATE PROCEDURE p AS BEGIN GRANT SELECT ON t TO u END")]                               // A18
    [InlineData("CREATE PROCEDURE p AS BEGIN CREATE LOGIN x WITH PASSWORD = 'p@ss1234!' END")]
    [InlineData("CREATE PROCEDURE p AS BEGIN EXECUTE AS LOGIN = 'sa' END")]
    [InlineData("CREATE PROCEDURE p AS BEGIN ALTER ROLE db_owner ADD MEMBER u END")]
    [InlineData("CREATE SCHEMA s GRANT SELECT ON SCHEMA::s TO u")]                                      // C3
    [InlineData("CREATE SCHEMA s AUTHORIZATION other")]                                                 // G17
    [InlineData("CREATE SYNONYM dbo.x FOR sys.sp_executesql")]                                           // I1
    [InlineData("CREATE SYNONYM dbo.x FOR master.dbo.xp_cmdshell")]
    [InlineData("CREATE PROCEDURE p WITH EXECUTE AS OWNER AS SELECT 1")]                                 // I6
    [InlineData("CREATE FUNCTION f() RETURNS INT WITH EXECUTE AS 'u' AS BEGIN RETURN 1 END")]
    [InlineData("EXEC OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.p")]                             // M1
    [InlineData("CREATE PROCEDURE p AS EXTERNAL NAME a.b.c")]                                            // M3
    [InlineData("EXEC sp_rename 'shop', 'shop2', 'DATABASE'")]                                           // T1
    [InlineData("EXEC sp_rename @objname = 'shop', @newname = 'x', @objtype = 'DATABASE'")]
    public void SqlServer_Refuses(string sql) => Assert.NotNull(SqlWriteGate.Check(sql, All));

    [Theory]
    [InlineData("CREATE PROCEDURE p AS BEGIN SET NOCOUNT ON; SELECT 1 END")]
    [InlineData("EXEC sp_rename 'dbo.t.a', 'b', 'COLUMN'")]
    [InlineData("EXEC sp_rename 'dbo.t', 't2'")]
    [InlineData("CREATE SYNONYM dbo.x FOR dbo.orders")]
    [InlineData("CREATE SCHEMA s")]
    public void SqlServer_Allows(string sql) => Assert.Null(SqlWriteGate.Check(sql, All));

    [Theory]
    [InlineData("ALTER TABLE t DROP COLUMN c", ServerStatementKind.Drop)]                               // B4
    [InlineData("ALTER TABLE t SWITCH PARTITION 1 TO t2", ServerStatementKind.Delete)]
    [InlineData("ALTER TABLE t DISABLE TRIGGER tr", ServerStatementKind.Procedures)]                    // G13
    [InlineData("ALTER INDEX ix ON t DISABLE", ServerStatementKind.Drop)]                                // G14
    [InlineData("CREATE INDEX ix ON t (a) WITH (DROP_EXISTING = ON)", ServerStatementKind.Drop)]        // M5
    [InlineData("DELETE FROM t OUTPUT deleted.a INTO t2", ServerStatementKind.Data)]                     // T2
    public void SqlServer_NeedsTheKind(string sql, ServerStatementKind kind) => Assert.Contains(kind, SqlWriteGate.Kinds(sql)!);

    [Fact]
    public void SqlServer_AnIndexRebuild_IsUpkeepAlone() => Assert.Equal([ServerStatementKind.Upkeep], SqlWriteGate.Kinds("ALTER INDEX ix ON t REBUILD"));

    // ── MySQL / MariaDB ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("CREATE PROCEDURE p() BEGIN PREPARE s FROM @q; EXECUTE s; END")]                         // A6
    [InlineData("CREATE PROCEDURE p() BEGIN GRANT ALL ON *.* TO u; END")]                                // G4
    [InlineData("CREATE PROCEDURE p() BEGIN DROP DATABASE prod; END")]
    [InlineData("CREATE PROCEDURE p() BEGIN SET GLOBAL max_connections = 1; END")]
    [InlineData("CREATE PROCEDURE p() BEGIN SET sql_mode = ''; END")]
    [InlineData("CREATE PROCEDURE p() BEGIN DECLARE EXIT HANDLER FOR SQLEXCEPTION GRANT ALL ON *.* TO u; SELECT 1; END")]
    [InlineData("CREATE TABLE t (a INT) ENGINE = FEDERATED CONNECTION = 'mysql://u@h/db/t'")]           // A13
    [InlineData("CREATE TABLE t (a INT) CONNECTION = 'mysql://u@h/db/t'")]
    [InlineData("CREATE TABLE t (a INT) DATA DIRECTORY = '/tmp'")]                                       // F19
    [InlineData("CREATE AGGREGATE FUNCTION f RETURNS STRING SONAME 'udf.so'")]                           // G5
    [InlineData("ALTER TABLE t IMPORT TABLESPACE")]                                                      // R10
    [InlineData("ALTER TABLE t DISCARD TABLESPACE")]
    [InlineData("ALTER PROCEDURE p SQL SECURITY DEFINER")]                                               // T5
    public void MySql_Refuses(string sql) => Assert.NotNull(MySqlWriteGate.Check(sql, All));

    [Theory]
    [InlineData("CREATE PROCEDURE p(IN a INT, OUT b INT) BEGIN DECLARE c, d INT; SET b = a, c = 1; SET @x = 2; END")]
    [InlineData("CREATE TRIGGER tr BEFORE INSERT ON t FOR EACH ROW BEGIN SET NEW.a = 1; END")]
    [InlineData("CREATE PROCEDURE p() BEGIN START TRANSACTION; UPDATE t SET a = 1; COMMIT; END")]
    [InlineData("CREATE TABLE t (a INT) ENGINE = InnoDB")]
    public void MySql_Allows(string sql) => Assert.Null(MySqlWriteGate.Check(sql, All));

    [Theory]
    [InlineData("REPLACE INTO t VALUES (1)", ServerStatementKind.Delete)]                               // A7
    [InlineData("ANALYZE UPDATE t SET a = 1", ServerStatementKind.Data)]                                 // G3
    [InlineData("ANALYZE DELETE FROM t", ServerStatementKind.Delete)]
    [InlineData("ALTER TABLE t TRUNCATE PARTITION p0", ServerStatementKind.Delete)]                      // B4
    [InlineData("ALTER TABLE t DROP PARTITION p0", ServerStatementKind.Drop)]
    [InlineData("ALTER TABLE t DROP COLUMN c", ServerStatementKind.Drop)]
    public void MySql_NeedsTheKind(string sql, ServerStatementKind kind) => Assert.Contains(kind, MySqlWriteGate.Kinds(sql)!);

    [Fact]
    public void MySql_AnalyzeTable_IsUpkeepAlone_AndTheReplaceFunction_IsNoDelete()
    {
        Assert.Equal([ServerStatementKind.Upkeep], MySqlWriteGate.Kinds("ANALYZE TABLE t"));
        Assert.Equal([ServerStatementKind.Data], MySqlWriteGate.Kinds("UPDATE t SET a = REPLACE(a, 'x', 'y')"));
    }

    // ── PostgreSQL ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DO $$ BEGIN EXECUTE 'DROP TABLE t'; END $$")]                                           // A6
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_read_file('x'); END $$")]   // G1
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE sql AS 'ALTER ROLE r SUPERUSER'")]
    [InlineData("DO $$ BEGIN SET search_path = x; END $$")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE c AS 'lib.so', 'f'")]                          // G6
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE plpython3u AS $$ return 1 $$")]
    [InlineData("DO LANGUAGE plperlu $$ system('x') $$")]
    [InlineData("CREATE FUNCTION f() RETURNS int SECURITY DEFINER LANGUAGE sql AS 'SELECT 1'")]         // DB3
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS E'SELECT 1'")]                          // unreadable
    [InlineData("CREATE SCHEMA s AUTHORIZATION r")]                                                      // C2
    [InlineData("CREATE SCHEMA s CREATE TABLE t (a int) GRANT SELECT ON t TO r")]
    [InlineData("ALTER TABLE t DISABLE ROW LEVEL SECURITY")]                                             // F12
    [InlineData("ALTER TABLE t NO FORCE ROW LEVEL SECURITY")]
    [InlineData("COMMENT ON DATABASE shop IS 'x'")]                                                      // F13
    [InlineData("COMMENT ON ROLE r IS 'x'")]
    [InlineData("ALTER TABLE t SET TABLESPACE fast")]                                                    // H7
    [InlineData("ALTER TABLE ALL IN TABLESPACE a SET TABLESPACE b")]
    public void Postgres_Refuses(string sql) => Assert.NotNull(PostgresWriteGate.Check(sql, All));

    [Theory]
    [InlineData("CREATE FUNCTION f(a int) RETURNS int LANGUAGE sql AS 'SELECT $1 + 1'")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$ DECLARE x int; BEGIN UPDATE t SET a = 1; x := 2; RETURN x; END $$")]
    [InlineData("DO $$ BEGIN INSERT INTO t VALUES (1); END $$")]
    [InlineData("VACUUM FULL t")]
    [InlineData("VACUUM FULL")]                         // whole-database upkeep: refused under a schema lock alone (PostgresBoundary)
    [InlineData("REINDEX DATABASE shop")]
    [InlineData("ANALYZE VERBOSE t (a)")]
    [InlineData("COMMENT ON TABLE t IS 'x'")]
    public void Postgres_Allows(string sql) => Assert.Null(PostgresWriteGate.Check(sql, All));

    [Theory]
    [InlineData("SELECT * INTO t2 FROM t", ServerStatementKind.Create)]                                 // T7
    [InlineData("ALTER TABLE t DISABLE TRIGGER tr", ServerStatementKind.Procedures)]                    // T6
    [InlineData("ALTER TABLE t ENABLE ALWAYS RULE r", ServerStatementKind.Procedures)]
    [InlineData("ALTER TABLE t DROP COLUMN c", ServerStatementKind.Drop)]                               // B4
    [InlineData("CREATE SCHEMA s CREATE TABLE t (a int) CREATE TRIGGER tr AFTER INSERT ON t FOR EACH ROW EXECUTE FUNCTION f()", ServerStatementKind.Procedures)]   // C2
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE WHEN NOT MATCHED THEN INSERT (id) VALUES (s.id)", ServerStatementKind.Data)]          // R9
    public void Postgres_NeedsTheKind(string sql, ServerStatementKind kind) => Assert.Contains(kind, PostgresWriteGate.Kinds(sql)!);

    [Fact]
    public void Postgres_SelectInto_NeverTakesTheReadPath_AndDropDefault_LosesNothing()
    {
        Assert.False(ServerWriteGate.IsRead(PostgresWriteGate.Kinds("SELECT * INTO t2 FROM t")));
        Assert.True(ServerWriteGate.IsRead(PostgresWriteGate.Kinds("SELECT * FROM t")));
        Assert.Equal([ServerStatementKind.Alter], PostgresWriteGate.Kinds("ALTER TABLE t ALTER COLUMN a DROP DEFAULT"));
    }

    [Theory]
    [InlineData("'a''b'", "a'b")]
    [InlineData("$$ x $$", " x ")]
    [InlineData("$f$ $$ $f$", " $$ ")]
    [InlineData("E'x'", null)]
    public void Postgres_ABodyString_IsReadAsTheServerDoes(string source, string? value) => Assert.Equal(value, PostgresWriteGate.StringValue(source));

    // ── Oracle ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CREATE TRIGGER t AFTER LOGON ON DATABASE BEGIN NULL; END;")]                           // G7
    [InlineData("CREATE TRIGGER t BEFORE SHUTDOWN ON DATABASE BEGIN NULL; END;")]
    [InlineData("CREATE TRIGGER t AFTER LOGON ON SCHEMA BEGIN NULL; END;")]
    [InlineData("CREATE PROCEDURE p AS LANGUAGE C LIBRARY l NAME \"f\";")]                              // G8
    [InlineData("CREATE FUNCTION f RETURN NUMBER AS LANGUAGE JAVA NAME 'A.b() return int';")]
    [InlineData("CREATE PROCEDURE p AS EXTERNAL LIBRARY l;")]
    [InlineData("CREATE FUNCTION f RETURN NUMBER AS MLE MODULE m SIGNATURE 'f()';")]                   // M4
    [InlineData("BEGIN OPEN c FOR 'SELECT * FROM t'; END;")]                                             // I2
    [InlineData("BEGIN OPEN c FOR v_sql; END;")]
    [InlineData("ALTER TABLE ext DEFAULT DIRECTORY d2")]                                                 // T3
    [InlineData("ALTER TABLE ext ACCESS PARAMETERS (FIELDS TERMINATED BY ',')")]
    [InlineData("ALTER TABLE ext LOCATION ('other.csv')")]
    [InlineData("UPDATE t SET a = 1 RETURN a INTO :x")]                                                  // T4
    [InlineData("BEGIN DBMS_UTILITY.EXEC_DDL_STATEMENT('DROP TABLE t'); END;")]                          // A5's sibling
    public void Oracle_Refuses(string sql) => Assert.NotNull(OracleWriteGate.Check(sql, All));

    [Theory]
    [InlineData("BEGIN OPEN c FOR SELECT * FROM t; END;")]
    [InlineData("CREATE TRIGGER t BEFORE INSERT ON orders FOR EACH ROW BEGIN :NEW.a := 1; END;")]
    [InlineData("CREATE FUNCTION f RETURN NUMBER AS BEGIN RETURN 1; END;")]
    public void Oracle_Allows(string sql) => Assert.Null(OracleWriteGate.Check(sql, All));

    [Theory]
    [InlineData("ALTER TABLE t DROP COLUMN c", ServerStatementKind.Drop)]                               // B4
    [InlineData("ALTER TABLE t TRUNCATE PARTITION p0", ServerStatementKind.Delete)]
    [InlineData("ALTER TABLE t EXCHANGE PARTITION p0 WITH TABLE t2", ServerStatementKind.Delete)]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.a = s.a DELETE WHERE t.a = 0 WHEN NOT MATCHED THEN INSERT (id) VALUES (s.id)", ServerStatementKind.Data)]
    public void Oracle_NeedsTheKind(string sql, ServerStatementKind kind) => Assert.Contains(kind, OracleWriteGate.Kinds(sql)!);

    // ── SQLite ──────────────────────────────────────────────────────────────

    private static readonly SqliteStatementKind[] AllSqlite = Enum.GetValues<SqliteStatementKind>();

    [Theory]
    [InlineData("PRAGMA schema_version = 5")]                                                            // C9
    [InlineData("PRAGMA main.schema_version = 5")]
    [InlineData("PRAGMA hard_heap_limit = 1")]                                                           // G9
    [InlineData("PRAGMA soft_heap_limit(1)")]
    [InlineData("PRAGMA temp_store_directory = 'C:\\elsewhere'")]
    [InlineData("PRAGMA data_store_directory = 'C:\\elsewhere'")]
    [InlineData("SELECT * FROM pragma_soft_heap_limit(1)")]
    [InlineData("PRAGMA 'writable_schema' = 1")]                                                         // I4
    [InlineData("SELECT * FROM 'sqlite_dbpage'")]
    [InlineData("WITH replace AS (SELECT 1) DELETE FROM users")]                                         // C1, with only changing data ticked below
    public void Sqlite_Refuses(string sql) => Assert.NotNull(SqliteWriteGate.Check(sql, sql.StartsWith("WITH", StringComparison.Ordinal) ? [SqliteStatementKind.Data] : AllSqlite));

    [Theory]
    [InlineData("PRAGMA schema_version")]
    [InlineData("PRAGMA soft_heap_limit")]
    [InlineData("PRAGMA journal_mode = WAL")]
    [InlineData("INSERT INTO notes VALUES ('a note')")]
    public void Sqlite_Allows(string sql) => Assert.Null(SqliteWriteGate.Check(sql, AllSqlite));

    [Theory]
    [InlineData("WITH replace AS (SELECT 1) DELETE FROM users", SqliteStatementKind.Delete)]            // C1
    [InlineData("WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c WHERE n < 3), d AS MATERIALIZED (SELECT 2) INSERT INTO t SELECT n FROM c", SqliteStatementKind.Data)]
    [InlineData("REPLACE INTO t VALUES (1)", SqliteStatementKind.Delete)]                               // A7
    [InlineData("INSERT OR REPLACE INTO t VALUES (1)", SqliteStatementKind.Delete)]
    [InlineData("UPDATE OR REPLACE t SET a = 1", SqliteStatementKind.Delete)]
    [InlineData("CREATE TABLE t (a INT UNIQUE ON CONFLICT REPLACE)", SqliteStatementKind.Delete)]
    public void Sqlite_NeedsTheKind(string sql, SqliteStatementKind kind)
    {
        Assert.Null(SqliteWriteGate.Check(sql, AllSqlite));
        Assert.NotNull(SqliteWriteGate.Check(sql, AllSqlite.Where(k => k != kind).ToArray()));
    }

    [Fact]
    public void Sqlite_TheReplaceFunction_IsNoDelete_AndTheReadGate_RefusesTheRawPagesAsAString()
    {
        Assert.Null(SqliteWriteGate.Check("UPDATE t SET a = replace(a, 'x', 'y')", [SqliteStatementKind.Data]));
        Assert.NotNull(SqliteReadOnlyGate.Check("SELECT * FROM 'sqlite_dbpage'"));
        Assert.NotNull(SqliteReadOnlyGate.Check("SELECT * FROM \"sqlite_dbpage\""));
        Assert.Null(SqliteReadOnlyGate.Check("SELECT 'a note'"));
    }
}
