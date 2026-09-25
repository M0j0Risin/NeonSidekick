using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// The read-only gate (2026-09-23): what one SELECT may be, and every bypass the <c>mcp-mssql-read</c> server's
/// first-word check let through — each one refused here by the parse, with the sentence the model reads.
/// </summary>
public sealed class SqlReadOnlyGateTests
{
    [Theory]
    [InlineData("SELECT TOP 5 * FROM Person.Person")]
    [InlineData("select 1")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT x FROM c")]
    [InlineData("WITH a AS (SELECT 1 AS x), b AS (SELECT x FROM a) SELECT * FROM b ORDER BY x")]
    [InlineData("-- the top customers\nSELECT TOP 3 CustomerID FROM Sales.Customer")]
    [InlineData("/* a block\n comment */ SELECT 1")]
    [InlineData("SELECT 'a;b' AS s")]
    [InlineData("SELECT 'DELETE FROM t' AS looks_bad")]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT * FROM Sales.SalesOrderHeader WHERE SalesOrderID = @id")]
    [InlineData("(SELECT 1) UNION ALL (SELECT 2)")]
    [InlineData("SELECT name FROM sys.tables FOR XML PATH('')")]
    [InlineData("SELECT value FROM OPENJSON('[1,2]')")]
    [InlineData("SELECT [Order Details].x FROM [dbo].[Order Details]")]
    [InlineData("SELECT * FROM AdventureWorks2022.Sales.Customer")]   // three parts: another database on the same server
    public void OneReadOnlySelect_Passes(string sql) => Assert.Null(SqlReadOnlyGate.Check(sql));

    [Fact]
    public void TheMcpServersBypasses_AreRefused()
    {
        // T-SQL needs no ';' between statements: a SELECT then a DELETE is two statements.
        Assert.Equal(SqlText.NotOneStatement(2), SqlReadOnlyGate.Check("SELECT 1 DELETE FROM t"));
        Assert.Equal(SqlText.NotOneStatement(2), SqlReadOnlyGate.Check("SELECT 1 EXEC xp_cmdshell 'dir'"));
        Assert.Equal(SqlText.NotOneStatement(2), SqlReadOnlyGate.Check("SELECT 1 DROP TABLE t"));
        Assert.Equal(SqlText.NotOneStatement(2), SqlReadOnlyGate.Check("WITH c AS (SELECT 1 AS x) SELECT * FROM c DELETE FROM t"));
        Assert.Equal(SqlText.SelectInto, SqlReadOnlyGate.Check("SELECT * INTO copy FROM t"));
    }

    [Fact]
    public void AnythingButASelect_IsNamed()
    {
        Assert.Equal(SqlText.NotASelect("DELETE"), SqlReadOnlyGate.Check("DELETE FROM t"));
        Assert.Equal(SqlText.NotASelect("UPDATE"), SqlReadOnlyGate.Check("UPDATE t SET x = 1"));
        Assert.Equal(SqlText.NotASelect("INSERT"), SqlReadOnlyGate.Check("INSERT INTO t VALUES (1)"));
        Assert.Equal(SqlText.NotASelect("MERGE"), SqlReadOnlyGate.Check("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN DELETE;"));
        Assert.Equal(SqlText.NotASelect("DELETE"), SqlReadOnlyGate.Check("WITH c AS (SELECT 1 AS x) DELETE FROM t"));   // a CTE ahead of a DML statement
        Assert.Equal(SqlText.NotASelect("EXECUTE"), SqlReadOnlyGate.Check("EXEC sp_who"));
        Assert.Equal(SqlText.NotASelect("DROP TABLE"), SqlReadOnlyGate.Check("DROP TABLE t"));
        Assert.Equal(SqlText.NotASelect("CREATE TABLE"), SqlReadOnlyGate.Check("CREATE TABLE t (x int)"));
        Assert.Equal(SqlText.NotASelect("TRUNCATE TABLE"), SqlReadOnlyGate.Check("TRUNCATE TABLE t"));
        Assert.Equal(SqlText.NotASelect("WAIT FOR"), SqlReadOnlyGate.Check("WAITFOR DELAY '00:01:00'"));
        Assert.Equal(SqlText.NotASelect("DECLARE VARIABLE"), SqlReadOnlyGate.Check("DECLARE @x int"));
        Assert.Equal(SqlText.NotASelect("BEGIN TRANSACTION"), SqlReadOnlyGate.Check("BEGIN TRAN"));
    }

    [Fact]
    public void TwoSelects_OrTwoBatches_AreRefused()
    {
        Assert.Equal(SqlText.NotOneStatement(2), SqlReadOnlyGate.Check("SELECT 1; SELECT 2"));
        Assert.Equal(SqlText.NotOneStatement(2), SqlReadOnlyGate.Check("SELECT 1\nGO\nSELECT 2"));
        Assert.Equal(SqlText.NotOneStatement(2), SqlReadOnlyGate.Check("DECLARE @x int = 1 SELECT @x"));
    }

    [Fact]
    public void WhatReachesOutsideTheServer_OrPastTheRollback_IsRefused()
    {
        Assert.Equal(SqlText.Forbidden("OPENROWSET"), SqlReadOnlyGate.Check("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;Trusted_Connection=yes', 'SELECT 1')"));
        Assert.Equal(SqlText.Forbidden("OPENROWSET(BULK …)"), SqlReadOnlyGate.Check("SELECT * FROM OPENROWSET(BULK 'C:\\secret.txt', SINGLE_CLOB) AS f"));
        Assert.Equal(SqlText.Forbidden("OPENQUERY"), SqlReadOnlyGate.Check("SELECT * FROM OPENQUERY(linked, 'SELECT 1')"));
        Assert.Equal(SqlText.Forbidden("OPENDATASOURCE"), SqlReadOnlyGate.Check("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t"));
        Assert.Equal(SqlText.Forbidden("a linked server (a four-part name)"), SqlReadOnlyGate.Check("SELECT * FROM linked.db.dbo.t"));
        Assert.Equal(SqlText.Forbidden("NEXT VALUE FOR"), SqlReadOnlyGate.Check("SELECT NEXT VALUE FOR dbo.seq"));
        Assert.Equal(SqlText.Forbidden("OPENROWSET"), SqlReadOnlyGate.Check("WITH c AS (SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')) SELECT * FROM c"));   // inside a CTE too
    }

    [Fact]
    public void AnEmptyText_OrOneThatDoesNotParse_IsSaidSo()
    {
        Assert.Equal(SqlText.NoSql, SqlReadOnlyGate.Check(""));
        Assert.Equal(SqlText.NoSql, SqlReadOnlyGate.Check("   \n"));
        Assert.Equal(SqlText.NoSql, SqlReadOnlyGate.Check("-- nothing but a comment"));
        string? error = SqlReadOnlyGate.Check("SELECT FROM WHERE");
        Assert.NotNull(error);
        Assert.StartsWith("Error: the SQL does not parse (line 1, column ", error);
        Assert.StartsWith("Error: the SQL does not parse (line 2, ", SqlReadOnlyGate.Check("SELECT 1\nSELECT 'unclosed"));
    }

    [Fact]
    public void Sentences_ArePinned()
    {
        Assert.Equal("Error: the SQL is 2 statements; send exactly one SELECT per call (a CTE may lead it)", SqlText.NotOneStatement(2));
        Assert.Equal("Error: the SQL is a DELETE statement; the SQL tools only read — send one SELECT (a CTE may lead it)", SqlText.NotASelect("DELETE"));
        Assert.Equal("Error: the SELECT uses OPENQUERY, which the SQL tools refuse (it reaches outside this server or changes state a rollback cannot undo)", SqlText.Forbidden("OPENQUERY"));
    }
}
