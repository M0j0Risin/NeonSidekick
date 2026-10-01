using NeonSidekick.Oracle;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>The Oracle tools' wording and formatting (2026-09-30), pure: the types, the describe and index pages, the headers, the outcomes.</summary>
public sealed class OracleTextTests
{
    private const string N = SqlText.Null;

    private static SqlRun Ok(params SqlGrid[] grids) => new(SqlOutcome.Ok, "", "free", "HR", grids, TimeSpan.FromMilliseconds(12));

    [Theory]
    [InlineData("VARCHAR2", "400", N, N, "100", "C", "VARCHAR2(100 CHAR)")]
    [InlineData("VARCHAR2", "100", N, N, "100", "B", "VARCHAR2(100)")]
    [InlineData("NVARCHAR2", "200", N, N, "100", "C", "NVARCHAR2(100)")]
    [InlineData("RAW", "16", N, N, "0", N, "RAW(16)")]
    [InlineData("NUMBER", "22", "12", "2", "0", N, "NUMBER(12,2)")]
    [InlineData("NUMBER", "22", "10", "0", "0", N, "NUMBER(10)")]
    [InlineData("NUMBER", "22", N, "0", "0", N, "INTEGER")]
    [InlineData("NUMBER", "22", N, N, "0", N, "NUMBER")]
    [InlineData("FLOAT", "22", "126", N, "0", N, "FLOAT(126)")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "13", N, "6", "0", N, "TIMESTAMP(6) WITH TIME ZONE")]
    [InlineData("CLOB", "4000", N, N, "0", N, "CLOB")]
    public void Types_ReadAsDeclared(string type, string length, string precision, string scale, string chars, string charUsed, string expected) =>
        Assert.Equal(expected, OracleText.TypeName(type, length, precision, scale, chars, charUsed));

    [Fact]
    public void Describe_ShowsColumnsKeysIndexesChecksAndTriggers_TheNotNullChecksLeftOut()
    {
        var columns = new SqlGrid(
            ["column", "type", "data_length", "data_precision", "data_scale", "char_length", "char_used", "nullable", "identity_column", "virtual_column", "default", "pk", "description"],
            [
                ["ID", "NUMBER", "22", "10", "0", "0", N, "N", "YES", "NO", "\"HR\".\"ISEQ$$_1\".nextval", "1", N],
                ["EMAIL", "VARCHAR2", "200", N, N, "200", "B", "Y", "NO", "NO", N, N, "where to write"],
                ["TOTAL_X2", "NUMBER", "22", N, N, "0", N, "Y", "NO", "YES", "\"TOTAL\"*2", N, N],
            ],
            false);
        var keys = new SqlGrid(["direction", "constraint", "from_table", "from_column", "to_table", "to_column"], [["out", "FK_C", "HR.ORDERS", "CUSTOMER_ID", "HR.CUSTOMERS", "ID"], ["in", "FK_L", "HR.LINES", "ORDER_ID", "HR.ORDERS", "ID"]], false);
        var indexes = new SqlGrid(["index", "kind", "uniqueness", "constraint", "keys", "status"], [["SYS_C1", "NORMAL", "UNIQUE", "P", "ID", "VALID"], ["ORD_IX", "NORMAL", "NONUNIQUE", N, "CUSTOMER_ID, PLACED DESC", "UNUSABLE"]], false);
        var comment = new SqlGrid(["description"], [["What people\nbought"]], false);
        var checks = new SqlGrid(["constraint", "definition", "status", "generated"], [["SYS_C9", "\"ID\" IS NOT NULL", "ENABLED", "GENERATED NAME"], ["CK_TOTAL", "total >= 0", "DISABLED", "USER NAME"]], false);
        var triggers = new SqlGrid(["trigger", "trigger_type", "triggering_event", "status"], [["ORD_TOUCH", "BEFORE EACH ROW", "UPDATE", "ENABLED"]], false);

        string text = OracleText.Describe("HR", "ORDERS", "table", Ok(columns, keys, indexes, comment, checks, triggers), 10_000);

        Assert.StartsWith("HR.ORDERS (table, 3 columns) in free\nWhat people bought\n", text);
        Assert.Contains("| ID | NUMBER(10) identity | no | PK | \"HR\".\"ISEQ$$_1\".nextval |  |\n", text);
        Assert.Contains("| EMAIL | VARCHAR2(200) | yes |  |  | where to write |\n", text);
        Assert.Contains("| TOTAL_X2 | NUMBER virtual | yes |  | \"TOTAL\"*2 |  |\n", text);
        Assert.Contains("Foreign keys:\n- out: HR.ORDERS.CUSTOMER_ID -> HR.CUSTOMERS.ID (FK_C)\n- in : HR.LINES.ORDER_ID -> HR.ORDERS.ID (FK_L)\n", text);
        Assert.Contains("Indexes:\n- SYS_C1 (primary key, normal): ID\n- ORD_IX (normal, unusable): CUSTOMER_ID, PLACED DESC\n", text);
        Assert.Contains("Check constraints:\n- CK_TOTAL: total >= 0 (disabled)\n", text);
        Assert.DoesNotContain("SYS_C9", text);
        Assert.EndsWith("Triggers:\n- ORD_TOUCH (before each row update)", text);
    }

    [Fact]
    public void Indexes_ShowUsage_OrWhyItIsMissing_AndMarkTheUnused()
    {
        string[] columns = ["table", "index", "kind", "uniqueness", "constraint", "keys", "status", "visibility", "num_rows", "distinct_keys", "last_analyzed", "owner"];
        var run = Ok(new SqlGrid(columns, [["HR.ORDERS", "SYS_C1", "NORMAL", "UNIQUE", "P", "ID", "VALID", "VISIBLE", "2", "2", "2026-09-30", "HR"], ["HR.ORDERS", "ORD_IX", "BITMAP", "NONUNIQUE", N, "STATUS", "VALID", "INVISIBLE", N, N, N, "HR"], ["HR.ORDERS", "USED_IX", "NORMAL", "NONUNIQUE", N, "PLACED", "VALID", "VISIBLE", "2", "2", N, "HR"]], false));
        var usage = new SqlGrid(["owner", "name", "total_access_count", "total_exec_count", "total_rows_returned", "last_used"], [["HR", "USED_IX", "41", "40", "90", "2026-09-29 10:00"]], false);

        string with = OracleText.Indexes("on HR.ORDERS", run, usage, null, 10_000);
        Assert.StartsWith("3 indexes on HR.ORDERS in free (usage as DBA_INDEX_USAGE tracked it)\n", with);
        Assert.Contains("| HR.ORDERS | SYS_C1 | normal PK | ID |  | 2 | 2 | 2026-09-30 | 0 |  |\n", with);   // a key's index is never marked
        Assert.Contains("| HR.ORDERS | ORD_IX | bitmap | STATUS | invisible |  |  |  | 0 " + OracleText.UnusedMarker + " |  |", with);
        Assert.Contains("| HR.ORDERS | USED_IX | normal | PLACED |  | 2 | 2 |  | 41 | 2026-09-29 10:00 |", with);

        string without = OracleText.Indexes("", run, null, "ORA-00942: table or view does not exist", 10_000);
        Assert.Contains(OracleText.UsageUnavailable("ORA-00942: table or view does not exist"), without);
        Assert.DoesNotContain("accesses", without);
    }

    [Fact]
    public void TheHeaders_SayWhereAndHowMuch()
    {
        var grid = new SqlGrid(["N"], [["1"], ["2"]], true);
        string query = OracleText.Query(Ok(grid), 2, 10_000);
        Assert.StartsWith("2 rows+ × 1 column from free/HR (12 ms) — the first 2 shown, more exist (narrow it with WHERE or FETCH FIRST, or raise max_rows)\n\n| N |", query);
        Assert.Equal("No schemas in free", OracleText.Listing("schema", "schemas", Ok(new SqlGrid(["schema"], [], false)) with { Database = "" }, 10_000));
        Assert.StartsWith("1 table or view in free/HR\n\n", OracleText.Listing("table or view", "tables and views", Ok(new SqlGrid(["schema", "name"], [["HR", "T"]], false)), 10_000));
        Assert.Equal("No foreign keys touching HR.T in free", OracleText.Relationships("HR.T", Ok(), 10_000));
        Assert.Equal("free/HR", OracleText.Where(Ok()));
    }

    [Fact]
    public void Outcomes_AreSentences_ThatStartWithError()
    {
        Assert.Equal(OracleText.NoConnections, OracleText.Error(SqlRun.Refused(SqlOutcome.NoConnections, "")));
        Assert.Equal("Error: no Oracle connection is named 'x'; the connections are a, b", OracleText.Error(SqlRun.Refused(SqlOutcome.UnknownConnection, "a, b", "x")));
        Assert.Equal("Error: could not connect to free: ORA-01017: invalid credential", OracleText.Error(SqlRun.Refused(SqlOutcome.ConnectFailed, "ORA-01017: invalid credential", "free")));
        Assert.Equal("Error: the query on free ran past 30 s and was stopped; narrow it (WHERE, FETCH FIRST n ROWS ONLY, fewer joins)", OracleText.Error(new SqlRun(SqlOutcome.Timeout, "30", "free", "HR", [], TimeSpan.Zero)));
        Assert.Equal("Error: the server refused it (free): ORA-00942", OracleText.Error(new SqlRun(SqlOutcome.Failed, "ORA-00942", "free", "HR", [], TimeSpan.Zero)));
        Assert.All(new[] { OracleText.NoSql, OracleText.NoTable, OracleText.NoPattern, OracleText.SelectInto, OracleText.NoConnections }, s => Assert.StartsWith("Error: ", s));
    }

    [Fact]
    public void TheWizardsLines_ArePinned()
    {
        Assert.Equal("Connected to 'free' as NEON (FREEPDB1), Oracle 23.0.0.0.0.", OracleText.TestOk("free", "NEON", "FREEPDB1", "23.0.0.0.0"));
        Assert.Equal("This account can change data (CREATE TABLE, owns 3 tables); the Oracle tools only read, but a read-only account is the real guard.", OracleText.CanWrite(["CREATE TABLE", "owns 3 tables"]));
        Assert.StartsWith("This account can change data (A, B, C, D, and 2 more);", OracleText.CanWrite(["A", "B", "C", "D", "E", "F"]));
        Assert.Equal("(SDO_GEOMETRY: select it converted to text — TO_CHAR, XMLSERIALIZE, JSON_SERIALIZE, SDO_UTIL.TO_WKTGEOMETRY)", OracleText.Unreadable("SDO_GEOMETRY"));
    }
}
