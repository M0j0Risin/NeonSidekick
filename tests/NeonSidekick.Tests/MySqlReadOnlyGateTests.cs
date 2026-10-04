using NeonSidekick.MySql;

namespace NeonSidekick.Tests;

/// <summary>
/// The MySQL read-only gate (2026-09-30): what one SELECT may be, MySQL's own ways to hide a second statement or a write
/// (executable comments, backslash-escaped quotes, <c>#</c> comments, <c>INTO OUTFILE</c>), and the keywords a literal, a comment
/// or a quoted name may hold without tripping it. The server's layers are <see cref="LiveMySqlTests"/>.
/// </summary>
public sealed class MySqlReadOnlyGateTests
{
    [Theory]
    [InlineData("SELECT * FROM shop.orders LIMIT 5")]
    [InlineData("select 1")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT x FROM c")]
    [InlineData("WITH RECURSIVE n AS (SELECT 1 AS i UNION ALL SELECT i + 1 FROM n WHERE i < 5) SELECT * FROM n")]
    [InlineData("# the newest first\nSELECT order_id FROM orders ORDER BY ordered_at DESC")]
    [InlineData("-- a comment ; DELETE FROM t\nSELECT 1")]
    [InlineData("SELECT /*+ MAX_EXECUTION_TIME(1000) */ * FROM orders")]
    [InlineData("SELECT 'a;b', \"DROP TABLE t\" FROM dual")]
    [InlineData("SELECT 'it\\'s; DELETE FROM t' AS s")]
    [InlineData("SELECT 'it''s' AS s")]
    [InlineData("SELECT `update`, `delete` FROM `odd table`")]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT * FROM orders WHERE order_id = @id AND status = @status")]
    [InlineData("(SELECT 1) UNION ALL (SELECT 2)")]
    [InlineData("SELECT INSERT('abcdef', 2, 3, 'X'), REPLACE(name, 'a', 'b'), TRUNCATE(1.256, 2) FROM customers")]
    [InlineData("SELECT @@version, @@SESSION.sql_mode")]
    [InlineData("SELECT 1--1")]   // minus minus one, not a comment
    [InlineData("SELECT JSON_EXTRACT(specs, '$.color') FROM products")]
    [InlineData("SELECT updated_at, deleted, created_on FROM audit")]   // words that start like the changing ones
    [InlineData("SELECT x'DEADBEEF', b'101', 0x1F, 1.5e3")]
    public void OneReadOnlySelect_Passes(string sql) => Assert.Null(MySqlReadOnlyGate.Check(sql));

    [Fact]
    public void AnythingButASelect_IsNamed()
    {
        Assert.Equal(MySqlText.NotASelect("DELETE"), MySqlReadOnlyGate.Check("DELETE FROM t"));
        Assert.Equal(MySqlText.NotASelect("UPDATE"), MySqlReadOnlyGate.Check("UPDATE t SET x = 1"));
        Assert.Equal(MySqlText.NotASelect("INSERT"), MySqlReadOnlyGate.Check("INSERT INTO t VALUES (1)"));
        Assert.Equal(MySqlText.NotASelect("REPLACE"), MySqlReadOnlyGate.Check("REPLACE INTO t VALUES (1)"));
        Assert.Equal(MySqlText.NotASelect("CALL"), MySqlReadOnlyGate.Check("CALL p()"));
        Assert.Equal(MySqlText.NotASelect("SET"), MySqlReadOnlyGate.Check("SET SESSION sql_mode = ''"));
        Assert.Equal(MySqlText.NotASelect("DO"), MySqlReadOnlyGate.Check("DO SLEEP(10)"));
        Assert.Equal(MySqlText.NotASelect("SHOW"), MySqlReadOnlyGate.Check("SHOW TABLES"));
        Assert.Equal(MySqlText.NotASelect("LOAD"), MySqlReadOnlyGate.Check("LOAD DATA INFILE '/etc/passwd' INTO TABLE t"));
        Assert.Equal(MySqlText.NotASelect("HANDLER"), MySqlReadOnlyGate.Check("HANDLER t OPEN"));
        Assert.Equal(MySqlText.NotASelect("TABLE"), MySqlReadOnlyGate.Check("TABLE t"));
    }

    [Fact]
    public void ASecondStatement_IsRefused_WhereverItHides()
    {
        Assert.Equal(MySqlText.NotOneStatement(2), MySqlReadOnlyGate.Check("SELECT 1; DROP TABLE t"));
        Assert.Equal(MySqlText.NotOneStatement(2), MySqlReadOnlyGate.Check("SELECT 'a\\''; DROP TABLE t"));   // 'a\'' is one string to MySQL (the backslash escapes the first quote, the second ends it): the DROP after it is a second statement
        Assert.Equal(MySqlText.NotOneStatement(2), MySqlReadOnlyGate.Check("SELECT 1 # x\n; DROP TABLE t"));
        Assert.Equal(MySqlText.NotOneStatement(2), MySqlReadOnlyGate.Check("SELECT 1;; "));
    }

    [Fact]
    public void AnExecutableComment_IsRefused_ForTheServerRunsIt()
    {
        Assert.Equal(MySqlText.ExecutableComment(1, 8), MySqlReadOnlyGate.Check("SELECT /*! 1; DROP TABLE t */ 1"));
        Assert.Equal(MySqlText.ExecutableComment(1, 8), MySqlReadOnlyGate.Check("SELECT /*!50000 1 */ 1"));
        Assert.Equal(MySqlText.ExecutableComment(1, 8), MySqlReadOnlyGate.Check("SELECT /*M! 1 */ 1"));   // MariaDB's
        Assert.Null(MySqlReadOnlyGate.Check("SELECT /* not ! executable */ 1"));
    }

    [Fact]
    public void WhatWritesLocksOrReachesOutside_IsRefused()
    {
        Assert.Equal(MySqlText.SelectInto, MySqlReadOnlyGate.Check("SELECT * FROM t INTO OUTFILE '/tmp/t.csv'"));
        Assert.Equal(MySqlText.SelectInto, MySqlReadOnlyGate.Check("SELECT 1 INTO DUMPFILE '/var/lib/x'"));
        Assert.Equal(MySqlText.SelectInto, MySqlReadOnlyGate.Check("SELECT 1 INTO @x"));
        Assert.Equal(MySqlText.Forbidden("FOR UPDATE (it locks rows)"), MySqlReadOnlyGate.Check("SELECT * FROM t FOR UPDATE"));
        Assert.Equal(MySqlText.Forbidden("FOR SHARE (it locks rows)"), MySqlReadOnlyGate.Check("SELECT * FROM t FOR SHARE"));
        Assert.Equal(MySqlText.Forbidden("LOCK"), MySqlReadOnlyGate.Check("SELECT * FROM t LOCK IN SHARE MODE"));
        Assert.Equal(MySqlText.Forbidden("LOAD_FILE"), MySqlReadOnlyGate.Check("SELECT LOAD_FILE('/etc/passwd')"));
        Assert.Equal(MySqlText.Forbidden("GET_LOCK"), MySqlReadOnlyGate.Check("SELECT GET_LOCK('x', 10)"));
        Assert.Equal(MySqlText.Forbidden("LOAD_FILE"), MySqlReadOnlyGate.Check("SELECT `load_file`('/etc/passwd')"));
        Assert.Null(MySqlReadOnlyGate.Check("SELECT `load_file` FROM t"));
        Assert.Equal(MySqlText.Forbidden("NEXTVAL"), MySqlReadOnlyGate.Check("SELECT NEXTVAL(s)"));
        Assert.Equal(MySqlText.Forbidden("NEXT VALUE FOR (a sequence moves on, and no rollback moves it back)"), MySqlReadOnlyGate.Check("SELECT NEXT VALUE FOR s"));
        Assert.Equal(MySqlText.Forbidden("SYS_EXEC"), MySqlReadOnlyGate.Check("SELECT sys_exec('rm -rf /')"));
        Assert.Equal(MySqlText.Forbidden("DELETE"), MySqlReadOnlyGate.Check("SELECT * FROM (DELETE FROM t) x"));
        Assert.Equal(MySqlText.Forbidden("INSERT"), MySqlReadOnlyGate.Check("SELECT 1 FROM dual WHERE 1 = (INSERT INTO t VALUES (1))"));
    }

    [Fact]
    public void AnEmptyText_OrOneThatNeverEnds_IsSaidSo()
    {
        Assert.Equal(MySqlText.NoSql, MySqlReadOnlyGate.Check(""));
        Assert.Equal(MySqlText.NoSql, MySqlReadOnlyGate.Check("# only a comment"));
        Assert.Equal(MySqlText.NoSql, MySqlReadOnlyGate.Check(";"));
        Assert.Equal(MySqlText.Unterminated("string literal", 1, 8), MySqlReadOnlyGate.Check("SELECT 'never closed\\'"));
        Assert.Equal(MySqlText.Unterminated("/* comment", 1, 10), MySqlReadOnlyGate.Check("SELECT 1 /* DELETE"));
        Assert.Equal(MySqlText.Unterminated("quoted name", 1, 8), MySqlReadOnlyGate.Check("SELECT `x FROM t"));
    }

    [Fact]
    public void TheLexer_SeesWhatTheServerSees()
    {
        var tokens = MySqlReadOnlyGate.Tokenize("SELECT 'a\\'b', \"c\", `my``col`, @id, @@version FROM t # end", out var error)!;
        Assert.Null(error);
        Assert.Equal("SELECT 'a\\'b' , \"c\" , `my`col` , @id , @@VERSION FROM T", MySqlReadOnlyGate.Show(tokens));
        Assert.Equal(["id", "Name"], MySqlReadOnlyGate.Binds("SELECT @id, @Name, @ID, @@version, '@skip' -- @also"));
        Assert.Equal("SELECT 1", MySqlReadOnlyGate.Body("SELECT 1; # done"));
        Assert.Equal("/*+ BKA(t) */ SELECT 1", MySqlReadOnlyGate.Body("/*+ BKA(t) */ SELECT 1 /* tail */"));
    }

    [Fact]
    public void Sentences_ArePinned()
    {
        Assert.Equal("Error: the SQL is 2 statements; send exactly one SELECT per call (a WITH clause may lead it)", MySqlText.NotOneStatement(2));
        Assert.Equal("Error: the SQL starts with DELETE; the MySQL tools only read — send one SELECT (a WITH clause may lead it)", MySqlText.NotASelect("DELETE"));
        Assert.Equal("Error: the SQL holds an executable comment (/*! … */, line 1, column 8), which the server runs as code; write it as plain SQL", MySqlText.ExecutableComment(1, 8));
    }
}
