using NeonSidekick.Oracle;

namespace NeonSidekick.Tests;

/// <summary>
/// The Oracle read-only gate (2026-09-30): what one SELECT may be, the bypasses a first-word check lets through — each
/// refused here by the lexer, with the sentence the model reads — and the keywords a literal, a comment or a quoted name
/// may hold without tripping it. The server's own layers are <see cref="LiveOracleTests"/>.
/// </summary>
public sealed class OracleReadOnlyGateTests
{
    [Theory]
    [InlineData("SELECT * FROM hr.employees FETCH FIRST 5 ROWS ONLY")]
    [InlineData("select 1 from dual")]
    [InlineData("WITH c AS (SELECT 1 AS x FROM dual) SELECT x FROM c")]
    [InlineData("WITH a AS (SELECT 1 x FROM dual), b AS (SELECT x FROM a) SELECT * FROM b ORDER BY x")]
    [InlineData("-- the top earners\nSELECT last_name FROM hr.employees ORDER BY salary DESC")]
    [InlineData("SELECT /*+ INDEX(e emp_name_ix) */ e.last_name FROM hr.employees e")]
    [InlineData("SELECT 'a;b' AS s FROM dual")]
    [InlineData("SELECT 'DELETE FROM t; DROP TABLE x' AS looks_bad FROM dual")]
    [InlineData("SELECT q'[it's; DELETE FROM t]' FROM dual")]
    [InlineData("SELECT q'{UPDATE}' , Q'!INSERT!' , nq'<MERGE>' FROM dual")]
    [InlineData("SELECT n'ünï' FROM dual")]
    [InlineData("SELECT \"update\", \"For\" FROM \"Odd Table\"")]   // quoted names keep their case and are names, not words
    [InlineData("SELECT 1 FROM dual;")]
    [InlineData("SELECT 1 FROM dual;\n/")]
    [InlineData("SELECT 1 FROM dual\n/")]
    [InlineData("SELECT * FROM hr.employees WHERE employee_id = :id AND last_name LIKE :name")]
    [InlineData("(SELECT 1 FROM dual) UNION ALL (SELECT 2 FROM dual)")]
    [InlineData("SELECT * FROM hr.employees AS OF TIMESTAMP SYSTIMESTAMP - INTERVAL '1' HOUR")]   // a flashback read
    [InlineData("SELECT employee_id, LEVEL FROM hr.employees START WITH manager_id IS NULL CONNECT BY PRIOR employee_id = manager_id")]
    [InlineData("SELECT jt.* FROM JSON_TABLE('[1,2]', '$[*]' COLUMNS (v NUMBER PATH '$')) jt")]
    [InlineData("SELECT s.CURRVAL FROM dual")]
    [InlineData("SELECT 1.5e3, .5, 2d, 3f FROM dual")]
    [InlineData("SELECT x FROM t WHERE a = 'x' -- trailing ; DELETE\n")]
    [InlineData("SELECT updated_at, inserted_by, created_on, deleted FROM audit")]   // words that start like the reserved ones
    public void OneReadOnlySelect_Passes(string sql) => Assert.Null(OracleReadOnlyGate.Check(sql));

    [Fact]
    public void AnythingButASelect_IsNamed()
    {
        Assert.Equal(OracleText.NotASelect("DELETE"), OracleReadOnlyGate.Check("DELETE FROM t"));
        Assert.Equal(OracleText.NotASelect("UPDATE"), OracleReadOnlyGate.Check("UPDATE t SET x = 1"));
        Assert.Equal(OracleText.NotASelect("INSERT"), OracleReadOnlyGate.Check("INSERT INTO t VALUES (1)"));
        Assert.Equal(OracleText.NotASelect("MERGE"), OracleReadOnlyGate.Check("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.x = s.x"));
        Assert.Equal(OracleText.NotASelect("BEGIN"), OracleReadOnlyGate.Check("BEGIN DELETE FROM t; END;"));
        Assert.Equal(OracleText.NotASelect("DECLARE"), OracleReadOnlyGate.Check("DECLARE x NUMBER; BEGIN NULL; END;"));
        Assert.Equal(OracleText.NotASelect("CALL"), OracleReadOnlyGate.Check("CALL dbms_output.put_line('x')"));
        Assert.Equal(OracleText.NotASelect("EXEC"), OracleReadOnlyGate.Check("EXEC dbms_lock.sleep(10)"));
        Assert.Equal(OracleText.NotASelect("ALTER"), OracleReadOnlyGate.Check("ALTER SESSION SET READ_ONLY = FALSE"));
        Assert.Equal(OracleText.NotASelect("CREATE"), OracleReadOnlyGate.Check("CREATE TABLE t (x NUMBER)"));
        Assert.Equal(OracleText.NotASelect("TRUNCATE"), OracleReadOnlyGate.Check("TRUNCATE TABLE t"));
        Assert.Equal(OracleText.NotASelect("LOCK"), OracleReadOnlyGate.Check("LOCK TABLE t IN EXCLUSIVE MODE"));
        Assert.Equal(OracleText.NotASelect("SET"), OracleReadOnlyGate.Check("SET TRANSACTION READ WRITE"));
        Assert.Equal(OracleText.NotASelect("COMMIT"), OracleReadOnlyGate.Check("COMMIT"));
        Assert.Equal(OracleText.NotASelect("'@'"), OracleReadOnlyGate.Check("@script.sql"));
    }

    [Fact]
    public void ASecondStatement_IsRefused_WhereverItHides()
    {
        Assert.Equal(OracleText.NotOneStatement(2), OracleReadOnlyGate.Check("SELECT 1 FROM dual; SELECT 2 FROM dual"));
        Assert.Equal(OracleText.NotOneStatement(2), OracleReadOnlyGate.Check("SELECT 1 FROM dual; DELETE FROM t"));
        Assert.Equal(OracleText.NotOneStatement(2), OracleReadOnlyGate.Check("SELECT 1 FROM dual;; "));   // one trailing ; is dropped, the other ends an empty second
        Assert.Equal(OracleText.NotOneStatement(2), OracleReadOnlyGate.Check("SELECT ';' FROM dual; DROP TABLE t"));   // the ; in the literal counts for nothing, the one after it does
    }

    [Fact]
    public void PlSqlInsideTheQuery_IsRefused()
    {
        // 12c's WITH FUNCTION: an autonomous transaction there commits whatever the read-only transaction says.
        string with = OracleText.Forbidden("WITH FUNCTION (PL/SQL inside the query)");
        Assert.Equal(with, OracleReadOnlyGate.Check("WITH FUNCTION f RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN DELETE FROM t; COMMIT; RETURN 1; END; SELECT f FROM dual"));
        Assert.Equal(OracleText.Forbidden("WITH PROCEDURE (PL/SQL inside the query)"), OracleReadOnlyGate.Check("WITH PROCEDURE p IS BEGIN NULL; END; SELECT 1 FROM dual"));
        Assert.Equal(with, OracleReadOnlyGate.Check("SELECT /*+ WITH_PLSQL */ * FROM (WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; SELECT f FROM dual)"));
        Assert.Equal(with, OracleReadOnlyGate.Check("with\n  function f return number is begin return 1; end;\nselect f from dual"));
    }

    [Fact]
    public void WhatLocks_BumpsASequence_OrReachesOutside_IsRefused()
    {
        Assert.Equal(OracleText.Forbidden("FOR UPDATE (it locks rows)"), OracleReadOnlyGate.Check("SELECT * FROM t FOR UPDATE"));
        Assert.Equal(OracleText.Forbidden("FOR UPDATE (it locks rows)"), OracleReadOnlyGate.Check("SELECT * FROM t FOR UPDATE OF x NOWAIT"));
        Assert.Equal(OracleText.Forbidden("NEXTVAL (a sequence moves on, and no rollback moves it back)"), OracleReadOnlyGate.Check("SELECT hr.seq.NEXTVAL FROM dual"));
        Assert.Equal(OracleText.Forbidden("a database link (@name: another server)"), OracleReadOnlyGate.Check("SELECT * FROM employees@remote_db"));
        Assert.Equal(OracleText.Forbidden("a database link (@name: another server)"), OracleReadOnlyGate.Check("SELECT * FROM \"Emp\"@\"Remote\""));
        Assert.Equal(OracleText.Forbidden("EXTERNAL(…) (an inline external table: files on the server)"), OracleReadOnlyGate.Check("SELECT * FROM EXTERNAL ((x VARCHAR2(10)) TYPE oracle_loader DEFAULT DIRECTORY d LOCATION ('secret.txt'))"));
        Assert.Equal(OracleText.Forbidden("BFILENAME (files on the server)"), OracleReadOnlyGate.Check("SELECT DBMS_LOB.GETLENGTH(BFILENAME('DATA_PUMP_DIR', 'x.dmp')) FROM dual"));
        Assert.Equal(OracleText.SelectInto, OracleReadOnlyGate.Check("SELECT x INTO v FROM t"));
        Assert.Equal(OracleText.Forbidden("NEXTVAL (a sequence moves on, and no rollback moves it back)"), OracleReadOnlyGate.Check("SELECT hr.seq.\"NEXTVAL\" FROM dual"));
        Assert.Equal(OracleText.Forbidden("BFILENAME (files on the server)"), OracleReadOnlyGate.Check("SELECT \"BFILENAME\"('DATA_PUMP_DIR', 'x.dmp') FROM dual"));
    }

    [Theory]
    [InlineData("SELECT UTL_HTTP.REQUEST('http://evil.example/') FROM dual", "UTL_HTTP")]
    [InlineData("SELECT sys.utl_inaddr.get_host_address('x') FROM dual", "UTL_INADDR")]
    [InlineData("SELECT HTTPURITYPE('http://x').getclob() FROM dual", "HTTPURITYPE")]
    [InlineData("SELECT DBMS_PIPE.RECEIVE_MESSAGE('p', 10) FROM dual", "DBMS_PIPE")]
    [InlineData("SELECT DBMS_XMLGEN.GETXML('SELECT 1 FROM dual') FROM dual", "DBMS_XMLGEN")]
    [InlineData("SELECT dbms_lock.request(1) FROM dual", "DBMS_LOCK")]
    [InlineData("SELECT \"DBMS_LOCK\".SLEEP(5) FROM dual", "DBMS_LOCK")]
    [InlineData("SELECT \"SYS\".\"UTL_HTTP\".REQUEST('http://evil.example/') FROM dual", "UTL_HTTP")]
    [InlineData("SELECT x FROM t WHERE DBMS_SCHEDULER.x = 1", "DBMS_SCHEDULER")]
    public void ThePackagesThatReachOut_AreRefused(string sql, string package) =>
        Assert.Equal(OracleText.Forbidden(package), OracleReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("SELECT * FROM (DELETE FROM t)", "DELETE")]
    [InlineData("SELECT * FROM t WHERE EXISTS (UPDATE t SET x = 1)", "UPDATE")]
    [InlineData("SELECT 1 FROM dual WHERE 1 = (INSERT INTO t VALUES (1))", "INSERT")]
    [InlineData("SELECT 1 FROM dual CREATE TABLE t (x NUMBER)", "CREATE")]
    public void TheReservedChangingWords_AreRefused_AnywhereBare(string sql, string word) =>
        Assert.Equal(OracleText.Forbidden(word), OracleReadOnlyGate.Check(sql));

    [Fact]
    public void AnEmptyText_OrOneThatNeverEnds_IsSaidSo()
    {
        Assert.Equal(OracleText.NoSql, OracleReadOnlyGate.Check(""));
        Assert.Equal(OracleText.NoSql, OracleReadOnlyGate.Check("   \n"));
        Assert.Equal(OracleText.NoSql, OracleReadOnlyGate.Check("-- nothing but a comment"));
        Assert.Equal(OracleText.NoSql, OracleReadOnlyGate.Check(";"));
        Assert.Equal(OracleText.NoSql, OracleReadOnlyGate.Check("((("));
        Assert.Equal(OracleText.Unterminated("string literal", 2, 8), OracleReadOnlyGate.Check("SELECT 1\nSELECT 'unclosed; DELETE FROM t"));
        Assert.Equal(OracleText.Unterminated("/* comment", 1, 10), OracleReadOnlyGate.Check("SELECT 1 /* DELETE FROM t"));
        Assert.Equal(OracleText.Unterminated("quoted name", 1, 8), OracleReadOnlyGate.Check("SELECT \"x FROM t"));
        Assert.Equal(OracleText.Unterminated("string literal", 1, 8), OracleReadOnlyGate.Check("SELECT q'[never closed' FROM dual"));
    }

    [Fact]
    public void TheLexer_SeesLiteralsCommentsAndNames_AsTheServerDoes()
    {
        var tokens = OracleReadOnlyGate.Tokenize("SELECT q'[a ' b]', 'it''s', \"Mixed Case\", :id, 1.5e3 /* c */ FROM t -- end", out var error)!;
        Assert.Null(error);
        Assert.Equal("SELECT q'[a ' b]' , 'it''s' , \"Mixed Case\" , :id , 1.5e3 FROM T", OracleReadOnlyGate.Show(tokens));
        Assert.Equal(OracleReadOnlyGate.TokenKind.String, tokens[1].Kind);
        Assert.Equal(OracleReadOnlyGate.TokenKind.Quoted, tokens[5].Kind);
        Assert.Equal(OracleReadOnlyGate.TokenKind.Bind, tokens[7].Kind);
        Assert.Equal(OracleReadOnlyGate.TokenKind.Number, tokens[9].Kind);
    }

    [Fact]
    public void Binds_AreTheNamesUsed_OnceEach_OutsideLiterals()
    {
        Assert.Equal(["id", "Name"], OracleReadOnlyGate.Binds("SELECT * FROM t WHERE a = :id AND b = :Name AND c = :ID AND d = ':skip' -- :also"));
        Assert.Empty(OracleReadOnlyGate.Binds("SELECT 1 FROM dual"));
        Assert.Empty(OracleReadOnlyGate.Binds("SELECT 'never closed"));
    }

    [Theory]
    [InlineData("SELECT 1 FROM dual", "SELECT 1 FROM dual")]
    [InlineData("SELECT 1 FROM dual;", "SELECT 1 FROM dual")]
    [InlineData("SELECT 1 FROM dual ;  -- done\n", "SELECT 1 FROM dual")]
    [InlineData("SELECT 1 FROM dual;\n/\n", "SELECT 1 FROM dual")]
    [InlineData("/*+ hint */ SELECT 1 FROM dual /* tail */", "/*+ hint */ SELECT 1 FROM dual")]
    [InlineData("SELECT 10 / 2 FROM dual", "SELECT 10 / 2 FROM dual")]
    public void Body_IsTheStatement_WithoutItsTerminators(string sql, string body) => Assert.Equal(body, OracleReadOnlyGate.Body(sql));

    [Fact]
    public void Sentences_ArePinned()
    {
        Assert.Equal("Error: the SQL is 2 statements; send exactly one SELECT per call (a WITH clause may lead it)", OracleText.NotOneStatement(2));
        Assert.Equal("Error: the SQL starts with DELETE; the Oracle tools only read — send one SELECT (a WITH clause may lead it)", OracleText.NotASelect("DELETE"));
        Assert.Equal("Error: the SELECT uses UTL_HTTP, which the Oracle tools refuse (it reaches outside this database or changes state a rollback cannot undo)", OracleText.Forbidden("UTL_HTTP"));
        Assert.Equal("Error: the SQL does not parse (line 1, column 8): a string literal never ends", OracleText.Unterminated("string literal", 1, 8));
    }
}
