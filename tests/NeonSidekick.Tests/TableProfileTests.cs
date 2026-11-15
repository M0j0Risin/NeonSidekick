using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.DuckDb;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Oracle;
using NeonSidekick.Plans;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The <c>*_profile</c> tools (2026-11-14, the database-tools plan's #14): each dialect's SQL (quoting a hostile name, the sample, the measures
/// a type takes), the renderer (the header's forms, the cells, the cap), the engine's passes on a scripted runner (one past the cap, the short
/// or failed sample, the timeouts), the arguments, SQLite and DuckDB end to end in process, and the classing (plan mode, advisors, help, the
/// factories).
/// </summary>
public sealed class TableProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "profile-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new() { SqliteTools = true, SqliteSandboxFiles = true, DuckDbTools = true, DuckDbSandboxFiles = true };

    public TableProfileTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "shop.db"), Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT, city TEXT, score REAL);
            INSERT INTO people VALUES (1, 'Ann', 'Oslo', 3.5), (2, 'Bo', 'Oslo', NULL), (3, 'Cy', 'Rome', 9), (4, 'Di', 'Oslo', 1), (5, 'Ed', NULL, 2), (6, 'Fi', 'Rome', 4);
            CREATE VIEW oslo AS SELECT * FROM people WHERE city = 'Oslo';
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<string> Invoke(AIFunction tool, params (string Key, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in pairs)
        {
            arguments[key] = value;
        }

        return (string)(await tool.InvokeAsync(arguments))!;
    }

    private SqliteProfileTool SqliteTool(long sampleAbove = TableProfile.DefaultSampleAbove) =>
        new(new SqliteAccess(() => SqliteCatalog.Empty, () => _files), () => _settings) { SampleAbove = sampleAbove };

    private DuckDbProfileTool DuckDbTool(long sampleAbove = TableProfile.DefaultSampleAbove) =>
        new(new DuckDbAccess(() => DuckDbCatalog.Empty, () => _files), () => _settings) { SampleAbove = sampleAbove };

    private static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement;

    // ── the dialects ────────────────────────────────────────────────────────

    [Fact]
    public void EachDialect_QuotesAHostileName_ByDoublingItsQuote()
    {
        Assert.Equal("[a]]; DROP TABLE x;--]", new SqlServerProfile(false).Quote("a]; DROP TABLE x;--"));
        Assert.Equal("\"a\"\" OR 1=1\"", OracleProfile.Instance.Quote("a\" OR 1=1"));
        Assert.Equal("`a``b`", MySqlProfile.Instance.Quote("a`b"));
        Assert.Equal("\"a\"\"b\"", PostgresProfile.Instance.Quote("a\"b"));
        Assert.Equal("\"a\"\"b\"", SqliteProfile.Instance.Quote("a\"b"));
        Assert.Equal("\"a\"\"b\"", DuckDbProfile.Instance.Quote("a\"b"));
    }

    [Fact]
    public void ThePassOneAggregate_HasFourSlotsAColumn_NullWhereTheTypeTakesNone()
    {
        var columns = new[] { new ProfileColumn("a", "int", ProfileMeasure.Full), new ProfileColumn("b", "blob", ProfileMeasure.NullsOnly), new ProfileColumn("c", "long", ProfileMeasure.None), new ProfileColumn("d", "bit", ProfileMeasure.Distinct) };
        Assert.Equal(
            "SELECT COUNT(*), COUNT(\"a\"), COUNT(DISTINCT \"a\"), MIN(\"a\"), MAX(\"a\"), COUNT(CASE WHEN \"b\" IS NOT NULL THEN 1 END), NULL, NULL, NULL, NULL, NULL, NULL, NULL, COUNT(\"d\"), COUNT(DISTINCT \"d\"), NULL, NULL FROM \"t\" p",
            Assert.Single(SqliteProfile.Instance.Aggregate(columns, SqliteProfile.Instance.Whole("\"t\""))));
    }

    [Fact]
    public void SqlServer_CountsBig_AndTakesTheApproximateDistinct_FromLevel150()
    {
        var exact = SqlServerProfile.For(140);
        Assert.Equal("SELECT COUNT_BIG(*), COUNT_BIG([a]), COUNT_BIG(DISTINCT [a]), MIN([a]), MAX([a]) FROM [s].[t] p",
            Assert.Single(exact.Aggregate([new ProfileColumn("a", "int", ProfileMeasure.Full)], exact.Whole("[s].[t]"))));
        Assert.False(SqlServerProfile.For(null).ApproximateDistinct);
        Assert.True(SqlServerProfile.For(160).ApproximateDistinct);
        Assert.Equal("APPROX_COUNT_DISTINCT([a])", SqlServerProfile.For(150).Distinct("[a]"));
        Assert.Equal("SELECT TOP (5) [a], COUNT_BIG(*) FROM [s].[t] p WHERE [a] IS NOT NULL GROUP BY [a] ORDER BY 2 DESC, 1", exact.Top("[a]", exact.Whole("[s].[t]"), 5));
    }

    [Fact]
    public void EachDialect_SamplesWhatItCan_AndCapsTheFirstRows_SelectingThePickedColumns()
    {
        Assert.Equal("(SELECT TOP (100000) [a], [b] FROM [s].[t] TABLESAMPLE (100000 ROWS) REPEATABLE (42)) p", new SqlServerProfile(false).Sample("[s].[t]", "[a], [b]", "table", 100_000, 2_000_000));
        Assert.Null(new SqlServerProfile(false).Sample("[s].[v]", "[a]", "view", 100_000, null));
        Assert.Null(new SqlServerProfile(false).Sample("[s].[m]", "[a]", SqlServerProfile.MemoryOptimizedKind, 100_000, 2_000_000));
        Assert.Equal("(SELECT \"A\" FROM \"O\".\"T\" SAMPLE (10) SEED (42) WHERE ROWNUM <= 100000) p", OracleProfile.Instance.Sample("\"O\".\"T\"", "\"A\"", "table", 100_000, 1_000_000));
        Assert.Null(OracleProfile.Instance.Sample("\"O\".\"V\"", "\"A\"", "view", 100_000, 1_000_000));
        Assert.Equal("(SELECT \"a\" FROM \"s\".\"t\" TABLESAMPLE SYSTEM (10) REPEATABLE (42) LIMIT 100000) p", PostgresProfile.Instance.Sample("\"s\".\"t\"", "\"a\"", "table", 100_000, 1_000_000));
        Assert.NotNull(PostgresProfile.Instance.Sample("\"s\".\"m\"", "\"a\"", "materialized view", 100_000, 1_000_000));
        Assert.NotNull(PostgresProfile.Instance.Sample("\"s\".\"p\"", "\"a\"", "partitioned table", 100_000, 1_000_000));
        Assert.Null(PostgresProfile.Instance.Sample("\"s\".\"v\"", "\"a\"", "view", 100_000, 1_000_000));
        Assert.Null(PostgresProfile.Instance.Sample("\"s\".\"f\"", "\"a\"", "foreign table", 100_000, 1_000_000));
        Assert.Null(PostgresProfile.Instance.Sample("\"s\".\"t\"", "\"a\"", "table", 100_000, null));
        Assert.Null(MySqlProfile.Instance.Sample("`d`.`t`", "`a`", "table", 100_000, 1_000_000));
        Assert.Null(SqliteProfile.Instance.Sample("\"t\"", "\"a\"", "table", 100_000, null));
        Assert.Equal("(SELECT \"a\" FROM \"t\" USING SAMPLE reservoir(100000 ROWS) REPEATABLE (42)) p", DuckDbProfile.Instance.Sample("\"t\"", "\"a\"", "file", 100_000, null));

        Assert.Equal("(SELECT \"A\" FROM \"O\".\"T\" WHERE ROWNUM <= 100001) p", OracleProfile.Instance.FirstRows("\"O\".\"T\"", "\"A\"", 100_001));
        Assert.Equal("(SELECT TOP (100001) [a] FROM [s].[t]) p", new SqlServerProfile(false).FirstRows("[s].[t]", "[a]", 100_001));
        Assert.Equal("(SELECT `a` FROM `d`.`t` LIMIT 100001) p", MySqlProfile.Instance.FirstRows("`d`.`t`", "`a`", 100_001));
        Assert.Equal("SELECT \"A\", COUNT(*) FROM \"O\".\"T\" p WHERE \"A\" IS NOT NULL GROUP BY \"A\" ORDER BY 2 DESC, 1 FETCH FIRST 3 ROWS ONLY", OracleProfile.Instance.Top("\"A\"", OracleProfile.Instance.Whole("\"O\".\"T\""), 3));
    }

    [Fact]
    public void TheSelectList_NamesThePickedColumns_LeavingOutWhatIsNeverMeasured()
    {
        var columns = new[] { new ProfileColumn("a", "NUMBER", ProfileMeasure.Full), new ProfileColumn("BODY", "LONG", ProfileMeasure.None), new ProfileColumn("x\"y", "CLOB", ProfileMeasure.NullsOnly) };
        Assert.Equal("\"a\", \"x\"\"y\"", OracleProfile.Instance.SelectList(columns));
        Assert.Equal("1 AS \"_\"", OracleProfile.Instance.SelectList([columns[1]]));
        Assert.Equal("1 AS [_]", new SqlServerProfile(false).SelectList([columns[1]]));
    }

    [Theory]
    [InlineData("[Order Date]", "Order Date", true)]
    [InlineData("[a]]b]", "a]b", true)]
    [InlineData("\"Mixed\"", "Mixed", true)]
    [InlineData(" plain ", "plain", false)]
    public void SqlServer_TakesABracketedName_AsWritten(string written, string name, bool quoted) =>
        Assert.Equal((name, quoted), new SqlServerProfile(false).Unquote(written));

    [Theory]
    [InlineData("`order date`", "order date", true)]
    [InlineData("`a``b`", "a`b", true)]
    [InlineData("\"x\"", "x", true)]
    [InlineData("[x]", "[x]", false)]
    public void MySql_TakesABacktickedName_AsWritten(string written, string name, bool quoted) =>
        Assert.Equal((name, quoted), MySqlProfile.Instance.Unquote(written));

    [Fact]
    public void PostgresAndDuckDb_ReadTheirSourceOnce_ForEveryColumnsTopValues()
    {
        var columns = new[] { new ProfileColumn("a", "int", ProfileMeasure.Full), new ProfileColumn("b\"c", "text", ProfileMeasure.Full) };
        var postgres = PostgresProfile.Instance.Tops(columns, "\"s\".\"t\" p", 3);
        Assert.Equal(PostgresProfile.TextStyles, postgres[0]);
        string one = Assert.Single(postgres.Skip(1));
        Assert.StartsWith("WITH s AS (SELECT * FROM \"s\".\"t\" p)", one);
        Assert.Contains("SELECT 1 AS i, CAST(r.v AS text) AS v, r.n, row_number() OVER (ORDER BY r.n DESC, r.v) AS k FROM (SELECT \"b\"\"c\" AS v, COUNT(*) AS n FROM s WHERE \"b\"\"c\" IS NOT NULL GROUP BY \"b\"\"c\" ORDER BY 2 DESC, 1 LIMIT 3) r", one);
        Assert.Contains("\nUNION ALL\n", one);
        Assert.EndsWith(") t ORDER BY i, k", one);
        Assert.StartsWith("WITH s AS MATERIALIZED (", Assert.Single(DuckDbProfile.Instance.Tops(columns, "\"t\" p", 3)));
        Assert.Contains("CAST(r.v AS VARCHAR)", DuckDbProfile.Instance.Tops(columns, "\"t\" p", 3)[0]);

        // UNION ALL keeps no order: the rows are put back by column, then rank; a column with no value has none.
        var grid = new SqlGrid(["i", "v", "n", "k"], [["1", "y", "2", "1"], ["0", "z", "1", "2"], ["0", "x", "4", "1"]], false);
        var read = PostgresProfile.Instance.ReadTops([new SqlGrid(["a", "b"], [["ISO, MDY", "postgres"]], false), grid], 3);
        Assert.Equal([("x", 4L), ("z", 1L)], read[0]);
        Assert.Equal([("y", 2L)], read[1]);
        Assert.Empty(read[2]!);
        Assert.All(PostgresProfile.Instance.ReadTops([new SqlGrid(["a", "b"], [["ISO, MDY", "postgres"]], false)], 2), Assert.Null);
    }

    [Fact]
    public void SqlServer_LimitsWithTop_AfterTheLeadingSelect()
    {
        var dialect = new SqlServerProfile(false);
        Assert.Equal("SELECT TOP (4) [a], COUNT_BIG(*) FROM [t] p WHERE [a] IS NOT NULL GROUP BY [a] ORDER BY 2 DESC, 1", dialect.Top("[a]", dialect.Whole("[t]"), 4));
    }

    [Fact]
    public void Postgres_ABooleansRange_IsBoolAndAndBoolOr()
    {
        var sql = Assert.Single(PostgresProfile.Instance.Aggregate([new ProfileColumn("ok", "boolean", ProfileMeasure.Bool)], "\"s\".\"t\" p"));
        Assert.Contains("bool_and(\"ok\"), bool_or(\"ok\")", sql);
        Assert.DoesNotContain("MIN(", sql);
    }

    [Theory]
    [InlineData("int", 4L, ProfileMeasure.Full)]
    [InlineData("datetime2", 8L, ProfileMeasure.Full)]
    [InlineData("nvarchar", 100L, ProfileMeasure.Full)]
    [InlineData("nvarchar", 8000L, ProfileMeasure.Full)]
    [InlineData("varchar", 4000L, ProfileMeasure.Full)]
    [InlineData("varchar", 8000L, ProfileMeasure.NullsOnly)]
    [InlineData("nvarchar", -1L, ProfileMeasure.NullsOnly)]
    [InlineData("varbinary", 16L, ProfileMeasure.NullsOnly)]
    [InlineData("bit", 1L, ProfileMeasure.Distinct)]
    [InlineData("uniqueidentifier", 16L, ProfileMeasure.Distinct)]
    [InlineData("xml", -1L, ProfileMeasure.NullsOnly)]
    [InlineData("geography", -1L, ProfileMeasure.NullsOnly)]
    public void SqlServer_TakesTheMeasuresItsAllowListNames(string type, long length, ProfileMeasure expected) => Assert.Equal(expected, SqlServerProfile.Measure(type, length));

    [Theory]
    [InlineData("NUMBER", null, ProfileMeasure.Full)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", null, ProfileMeasure.Full)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", null, ProfileMeasure.Full)]
    [InlineData("VARCHAR2", 4000L, ProfileMeasure.Full)]
    [InlineData("VARCHAR2", 32767L, ProfileMeasure.NullsOnly)]
    [InlineData("NCHAR", null, ProfileMeasure.Full)]
    [InlineData("RAW", null, ProfileMeasure.Distinct)]
    [InlineData("BOOLEAN", null, ProfileMeasure.Distinct)]
    [InlineData("ROWID", null, ProfileMeasure.Distinct)]
    [InlineData("UROWID", null, ProfileMeasure.Distinct)]
    [InlineData("CLOB", null, ProfileMeasure.NullsOnly)]
    [InlineData("SDO_GEOMETRY", null, ProfileMeasure.NullsOnly)]
    [InlineData("LONG", null, ProfileMeasure.None)]
    public void Oracle_TakesTheMeasuresItsAllowListNames(string type, long? charLength, ProfileMeasure expected) => Assert.Equal(expected, OracleProfile.Measure(type, charLength));

    [Theory]
    [InlineData("INTEGER", ProfileMeasure.Full)]
    [InlineData("", ProfileMeasure.Full)]
    [InlineData("BLOB", ProfileMeasure.NullsOnly)]
    [InlineData("blob", ProfileMeasure.NullsOnly)]
    public void Sqlite_MeasuresEveryColumnButABlob(string declared, ProfileMeasure expected) => Assert.Equal(expected, SqliteProfile.Measure(declared));

    [Theory]
    [InlineData("INTEGER", ProfileMeasure.Full)]
    [InlineData("VARCHAR", ProfileMeasure.Full)]
    [InlineData("TIMESTAMP WITH TIME ZONE", ProfileMeasure.Full)]
    [InlineData("VARCHAR[]", ProfileMeasure.Distinct)]
    [InlineData("STRUCT(a INTEGER)", ProfileMeasure.Distinct)]
    [InlineData("MAP(VARCHAR, INTEGER)", ProfileMeasure.Distinct)]
    [InlineData("BLOB", ProfileMeasure.NullsOnly)]
    [InlineData("BIT", ProfileMeasure.NullsOnly)]
    public void DuckDb_TakesTheMeasuresItsTypesName(string type, ProfileMeasure expected)
    {
        Assert.Equal(expected, DuckDbProfile.Measure(type));
        Assert.Equal(!DuckDbProfile.Nested(type), DuckDbProfile.Instance.Groupable(new ProfileColumn("c", type, expected)));
    }

    [Theory]
    [InlineData("int", null, ProfileMeasure.Full)]
    [InlineData("varchar", 255L, ProfileMeasure.Full)]
    [InlineData("varchar", 16000L, ProfileMeasure.NullsOnly)]
    [InlineData("text", 65535L, ProfileMeasure.NullsOnly)]
    [InlineData("enum", 4L, ProfileMeasure.Distinct)]
    [InlineData("bit", null, ProfileMeasure.Distinct)]
    [InlineData("json", null, ProfileMeasure.NullsOnly)]
    [InlineData("geometry", null, ProfileMeasure.NullsOnly)]
    public void MySql_TakesTheMeasuresItsAllowListNames(string type, long? length, ProfileMeasure expected) => Assert.Equal(expected, MySqlProfile.Measure(type, length));

    [Theory]
    [InlineData("N", "int4", ProfileMeasure.Full)]
    [InlineData("S", "text", ProfileMeasure.Full)]
    [InlineData("D", "timestamptz", ProfileMeasure.Full)]
    [InlineData("B", "bool", ProfileMeasure.Bool)]
    [InlineData("U", "uuid", ProfileMeasure.Distinct)]
    [InlineData("I", "inet", ProfileMeasure.Distinct)]
    [InlineData("N", "regclass", ProfileMeasure.Distinct)]
    [InlineData("U", "jsonb", ProfileMeasure.NullsOnly)]
    [InlineData("A", "_int4", ProfileMeasure.NullsOnly)]
    [InlineData("U", "bytea", ProfileMeasure.NullsOnly)]
    public void Postgres_TakesTheMeasuresItsCategoryNames(string category, string type, ProfileMeasure expected) => Assert.Equal(expected, PostgresProfile.Measure(category, type));

    // ── the renderer ────────────────────────────────────────────────────────

    private static ProfileResult Result(ProfileScope scope, long rows, long? about = null, long? moreThan = null, bool exact = true, params ColumnStats[] columns) =>
        new("orders", scope, rows, about, moreThan, columns.Length, columns, [], "shop/sales", TimeSpan.FromMilliseconds(12), exact);

    [Fact]
    public void TheHeader_SaysHowManyRowsAndHow()
    {
        Assert.Equal("orders: 1204 rows (exact) · 0 columns · shop/sales (12 ms).", TableProfileText.Header(Result(ProfileScope.Whole, 1204)));
        Assert.Equal("orders: 1 row (exact) · 0 columns · shop/sales (12 ms).", TableProfileText.Header(Result(ProfileScope.Whole, 1)));
        Assert.Equal("orders: from a sample of ~99812 of about 3000000000 rows · 0 columns · shop/sales (12 ms).", TableProfileText.Header(Result(ProfileScope.Sample, 99_812, about: 3_000_000_000)));
        Assert.Equal("orders: from the first 100000 of more than 100000 rows · 0 columns · shop/sales (12 ms).", TableProfileText.Header(Result(ProfileScope.FirstRows, 100_000, moreThan: 100_000)));
    }

    [Fact]
    public void ARow_GivesNullsDistinctRangeAndTop_AndMarksWhatIsntMeasured()
    {
        var full = new ColumnStats(new ProfileColumn("city", "text", ProfileMeasure.Full), 5, 2, false, "Oslo", "Rome") { Top = [("Oslo", 3), ("Rome", 2)] };
        var id = new ColumnStats(new ProfileColumn("id", "int", ProfileMeasure.Full), 6, 6, false, "1", "6");
        var blob = new ColumnStats(new ProfileColumn("photo", "blob", ProfileMeasure.NullsOnly), 6, null, false, null, null);
        var result = Result(ProfileScope.Whole, 6, columns: [full, id, blob]);
        Assert.Equal("| city | text | 1 (17%) | 2 | Oslo | Rome | Oslo (3), Rome (2) |", TableProfileText.Row(result, full));
        Assert.Equal("| id | int | 0 | 6 (unique) | 1 | 6 |  |", TableProfileText.Row(result, id));
        Assert.Equal("| photo | blob | 0 | — | — | — | — |", TableProfileText.Row(result, blob));
        Assert.Contains(TableProfileText.NotMeasuredNote, TableProfileText.Answer(result, [], 6000));
        Assert.Equal("| id | int | 0 | ~6 | 1 | 6 |  |", TableProfileText.Row(result with { Scope = ProfileScope.Sample, ExactDistinct = false }, id));
    }

    [Fact]
    public void AValue_IsClipped_AndAName_Scrubbed_AndAPipe_Escaped()
    {
        var stats = new ColumnStats(new ProfileColumn("na‮me|x", "text", ProfileMeasure.Full), 1, 1, false, new string('a', 60), "b|c");
        string row = TableProfileText.Row(Result(ProfileScope.Whole, 1, columns: [stats]), stats);
        Assert.DoesNotContain("‮", row, StringComparison.Ordinal);
        Assert.Contains(new string('a', TableProfileText.ValueChars) + "…", row);
        Assert.DoesNotContain(new string('a', TableProfileText.ValueChars + 1), row);
        Assert.Contains("b\\|c", row);
    }

    [Fact]
    public void ALongAnswer_KeepsTheFirstColumns_AndNamesTheRest()
    {
        var columns = Enumerable.Range(1, 40).Select(i => new ColumnStats(new ProfileColumn("column_" + i, "varchar", ProfileMeasure.Full), 10, 10, false, new string('x', 40), new string('y', 40))).ToArray();
        string answer = TableProfileText.Answer(Result(ProfileScope.Whole, 10, columns: columns), [], 2000);
        Assert.True(answer.Length <= 2000, answer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("| column_1 |", answer);
        Assert.Contains("more columns left out to keep the answer short: ", answer);
        Assert.Contains("column_40", answer);
        Assert.DoesNotContain("| column_40 |", answer);
    }

    [Fact]
    public void TheNullShare_NeverRoundsToAllOrNone_WhileOneValueIsThere()
    {
        var stats = new ColumnStats(new ProfileColumn("a", "int", ProfileMeasure.Full), 3, 3, false, "1", "3");
        Assert.Contains("| 997 (>99%) |", TableProfileText.Row(Result(ProfileScope.Whole, 1000, columns: [stats]), stats));
        Assert.Contains("| 1 (<1%) |", TableProfileText.Row(Result(ProfileScope.Whole, 1000, columns: [stats]), stats with { NonNull = 999 }));
        Assert.Contains("| 1000 (100%) |", TableProfileText.Row(Result(ProfileScope.Whole, 1000, columns: [stats]), stats with { NonNull = 0 }));
    }

    [Fact]
    public void AnEmptyValue_ReadsAsTwoQuotes_NotABlankCell()
    {
        var stats = new ColumnStats(new ProfileColumn("a", "text", ProfileMeasure.Full), 3, 2, false, "", "x") { Top = [("", 2), ("x", 1)] };
        Assert.Equal("| a | text | 0 | 2 | '' | x | '' (2), x (1) |", TableProfileText.Row(Result(ProfileScope.Whole, 3, columns: [stats]), stats));
    }

    [Fact]
    public void ADistinctOnlyColumn_GetsItsOwnNote_AndAnUngroupableOne_NoTopCell()
    {
        var flag = new ColumnStats(new ProfileColumn("ok", "bit", ProfileMeasure.Distinct), 3, 2, false, null, null);
        var list = new ColumnStats(new ProfileColumn("tags", "VARCHAR[]", ProfileMeasure.Distinct), 3, 3, true, null, null) { Groupable = false };
        var result = Result(ProfileScope.Whole, 3, columns: [flag, list]);
        string answer = TableProfileText.Answer(result, [], 6000);
        Assert.Contains(TableProfileText.NoRangeNote, answer);
        Assert.DoesNotContain(TableProfileText.NotMeasuredNote, answer);
        Assert.EndsWith(" | — | — | — |", TableProfileText.Row(result, list));
    }

    [Fact]
    public void EveryValueSeenOnce_IsUnique_OnAWholeRead_AndAllDistinctInTheRowsRead_OnAPartOne()
    {
        var once = new ColumnStats(new ProfileColumn("email", "text", ProfileMeasure.Full), 50, 48, true, "a", "z") { AllDistinct = true };
        Assert.Contains("| 50 (unique) |", TableProfileText.Row(Result(ProfileScope.Whole, 50, exact: false, columns: [once]), once));
        Assert.Contains("| 50" + TableProfileText.AllDistinctRead + " |", TableProfileText.Row(Result(ProfileScope.Sample, 50, exact: false, columns: [once]), once));

        // An exact count over the rows read that equals the values read (#169).
        var exact = new ColumnStats(new ProfileColumn("id", "int", ProfileMeasure.Full), 100, 100, false, "1", "100");
        Assert.Contains("| 100" + TableProfileText.AllDistinctRead + " |", TableProfileText.Row(Result(ProfileScope.FirstRows, 100, moreThan: 99, columns: [exact]), exact));
        Assert.Contains("| ~40 |", TableProfileText.Row(Result(ProfileScope.FirstRows, 100, moreThan: 99, columns: [exact]), exact with { Distinct = 40 }));
    }

    [Fact]
    public void TheNotProfiledList_NamesTwenty_AndCountsTheRest()
    {
        string line = TableProfileText.NotProfiled([.. Enumerable.Range(41, 960).Select(i => "c" + i)]);
        Assert.Contains("c60 and 940 more; name them in columns.", line);
        Assert.DoesNotContain("c61", line);
    }

    [Fact]
    public void UnderALowCap_TheNotesStayWhole_AndRowsGiveWay()
    {
        var columns = Enumerable.Range(1, 5).Select(i => new ColumnStats(new ProfileColumn("column_" + i, "varchar", ProfileMeasure.Full), 10, 10, false, new string('x', 40), new string('y', 40))).ToArray();
        string note = TableProfileText.TopStopped(30);
        string answer = TableProfileText.Answer(Result(ProfileScope.Whole, 10, columns: columns), [note], 500);
        Assert.True(answer.Length <= 500, answer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith("\n" + note, answer);
    }

    // ── the engine, on a scripted runner ────────────────────────────────────

    private static SqlRun Ok(params string[][] rows) => new(SqlOutcome.Ok, "", "conn", "db", [new SqlGrid(["x"], rows, false)], TimeSpan.Zero);

    private static SqlRun OkGrids(params SqlGrid[] grids) => new(SqlOutcome.Ok, "", "conn", "db", grids, TimeSpan.Zero);

    private static SqlRun Timeout() => new(SqlOutcome.Timeout, "30", "conn", "db", [], TimeSpan.Zero);

    private static TableProfile.Request Request(long? estimate = null, string kind = "table", int top = 5, IReadOnlyList<string>? asked = null, long sampleAbove = TableProfile.DefaultSampleAbove, int timeout = 30) =>
        new("t", "\"t\"", kind, [new ProfileColumn("a", "int", ProfileMeasure.Full), new ProfileColumn("B", "text", ProfileMeasure.Full)], asked, top, estimate, timeout, 6000, "x_describe",
            System.Diagnostics.Stopwatch.StartNew()) { SampleAbove = sampleAbove };

    private static SqlRun Failed() => new(SqlOutcome.Failed, "TABLESAMPLE is not supported", "conn", "db", [], TimeSpan.Zero);

    private sealed class Script(params SqlRun[] runs)
    {
        public List<IReadOnlyList<string>> Seen { get; } = [];

        public Task<SqlRun> Run(IReadOnlyList<string> statements, int seconds, CancellationToken token)
        {
            Seen.Add(statements);
            return Task.FromResult(runs[Seen.Count - 1]);
        }
    }

    [Fact]
    public async Task NoEstimate_ReadsOnePastTheCap_AndFewerBackIsTheWholeTable()
    {
        var script = new Script(Ok(["6", "6", "6", "1", "6", "5", "2", "a", "b"]), OkGrids(new SqlGrid(["v", "n"], [["a", "3"], ["b", "2"]], false)));
        string answer = await TableProfile.RunAsync(Request(), SqliteProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.EndsWith("FROM (SELECT \"a\", \"B\" FROM \"t\" LIMIT 100001) p", Assert.Single(script.Seen[0]));
        Assert.StartsWith("t: 6 rows (exact) · 2 columns · conn/db (", answer);
        Assert.Contains("| B | text | 1 (17%) | 2 | a | b | a (3), b (2) |", answer);
        Assert.Contains("| a | int | 0 | 6 (unique) | 1 | 6 |  |", answer);
        Assert.Equal(2, script.Seen.Count);
    }

    [Fact]
    public async Task AllRowsOnePastTheCap_SayTheTableIsBigger_WithoutASecondRead()
    {
        var script = new Script(Ok(["6", "6", "6", "1", "6", "5", "2", "a", "b"]));
        string answer = await TableProfile.RunAsync(Request(top: 0, sampleAbove: 5, estimate: 4), SqliteProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.EndsWith("LIMIT 6) p", Assert.Single(script.Seen[0]));
        Assert.StartsWith("t: from the first 6 of more than 5 rows · 2 columns", answer);
        Assert.Single(script.Seen);
    }

    [Fact]
    public async Task ABigTable_IsSampled_AndAShortSample_FallsBackToTheFirstRows()
    {
        var script = new Script(Ok(["3", "3", "3", "1", "3", "3", "3", "a", "c"]), Ok(["100", "100", "100", "1", "100", "100", "100", "a", "z"]));
        string answer = await TableProfile.RunAsync(Request(estimate: 5000, top: 0, sampleAbove: 100), PostgresProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.Contains("(SELECT \"a\", \"B\" FROM \"t\" TABLESAMPLE SYSTEM (2) REPEATABLE (42) LIMIT 100) p", Assert.Single(script.Seen[0]));
        Assert.Contains("(SELECT \"a\", \"B\" FROM \"t\" LIMIT 100) p", Assert.Single(script.Seen[1]));
        Assert.StartsWith("t: from the first 100 of about 5000 rows · 2 columns", answer);
        Assert.Equal(2, script.Seen.Count);
    }

    [Fact]
    public async Task AFailedSample_IsReadAgainAsTheFirstRows_UnderTheRerunsLabels()
    {
        var rerun = new SqlRun(SqlOutcome.Ok, "", "conn2", "db2", [new SqlGrid(["x"], [["100", "100", "100", "1", "100", "100", "100", "a", "z"]], false)], TimeSpan.Zero);
        var script = new Script(Failed(), rerun);
        string answer = await TableProfile.RunAsync(Request(estimate: 5000, top: 0, sampleAbove: 100), PostgresProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.Contains("LIMIT 100) p", Assert.Single(script.Seen[1]));
        Assert.StartsWith("t: from the first 100 of about 5000 rows · 2 columns · conn2/db2 (", answer);
    }

    [Fact]
    public async Task NoTimeLeft_NeverReachesTheRunner()
    {
        // The deadline spent before pass 1 (the resolve and the catalog share it): a 0 would read as "no limit" on SQL Server and MySQL.
        var request = Request(timeout: 1);
        await Task.Delay(1100);
        var script = new Script();
        Assert.Equal(TableProfileText.Stopped("t", "", 1), await TableProfile.RunAsync(request, SqliteProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None));
        Assert.Empty(script.Seen);
    }

    [Fact]
    public async Task PassOneStopped_SaysSo_WithTheWayToNarrowIt()
    {
        var script = new Script(Timeout());
        string answer = await TableProfile.RunAsync(Request(), SqliteProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.Equal(TableProfileText.Stopped("t", "conn", 30), answer);
    }

    [Fact]
    public async Task PassTwoStopped_AnswersTheTable_WithoutItsTopValues()
    {
        var script = new Script(Ok(["6", "6", "2", "1", "6", "5", "2", "a", "b"]), Timeout());
        string answer = await TableProfile.RunAsync(Request(), SqliteProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.StartsWith("t: 6 rows (exact)", answer);
        Assert.Contains(TableProfileText.TopStopped(30), answer);
        Assert.Equal(2, script.Seen[1].Count);
    }

    [Fact]
    public async Task TopsOfOneEach_DropTheTop_AndMarkTheColumnUnique()
    {
        // Oracle's distinct is approximate: 5 under 6 values sends the column to pass 2, which finds every value once.
        var script = new Script(Ok(["6", "6", "5", "1", "6", "6", "2", "a", "b"]), OkGrids(new SqlGrid(["v", "n"], [["1", "1"], ["2", "1"]], false), new SqlGrid(["v", "n"], [["a", "4"], ["b", "2"]], false)));
        string answer = await TableProfile.RunAsync(Request(), OracleProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.Contains("| a | int | 0 | 6 (unique) | 1 | 6 |  |", answer);
        Assert.Contains("| B | text | 0 | ~2 | a | b | a (4), b (2) |", answer);
    }

    [Fact]
    public async Task TheColumns_AreMatchedAsTheFamilyMatches_AndAnUnknownOneRefused()
    {
        var script = new Script(Ok(["6", "5", "2", "a", "b"]));
        string answer = await TableProfile.RunAsync(Request(top: 0, asked: ["b", "b"]), SqliteProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.Contains("| B | text |", answer);
        Assert.Contains("· 2 columns ·", answer);
        Assert.Equal(TableProfileText.NoSuchColumn("nope", "t", "x_describe"),
            await TableProfile.RunAsync(Request(asked: ["nope"]), SqliteProfile.Instance, script.Run, r => "", CancellationToken.None));
        Assert.Equal(TableProfileText.NoSuchColumn("b", "t", "x_describe"),
            await TableProfile.RunAsync(Request(asked: ["\"b\""]), SqliteProfile.Instance, script.Run, r => "", CancellationToken.None));
    }

    [Fact]
    public void CaseTwins_AreRefusedAsAmbiguous_UnlessQuotedOrExact()
    {
        var twins = Request() with { Columns = [new ProfileColumn("Id", "int", ProfileMeasure.Full), new ProfileColumn("ID", "int", ProfileMeasure.Full), new ProfileColumn("", "int", ProfileMeasure.Full)] };
        Assert.Null(TableProfile.Pick(twins with { Asked = ["id"] }, PostgresProfile.Instance, out _, out string? refusal));
        Assert.Equal(TableProfileText.Ambiguous("id", "t", ["Id", "ID"]), refusal);
        Assert.Equal("ID", Assert.Single(TableProfile.Pick(twins with { Asked = ["ID"] }, PostgresProfile.Instance, out _, out _)!).Name);
        Assert.Equal("Id", Assert.Single(TableProfile.Pick(twins with { Asked = ["\"Id\""] }, PostgresProfile.Instance, out _, out _)!).Name);
        Assert.Equal(["Id", "ID"], TableProfile.Pick(twins, PostgresProfile.Instance, out _, out _)!.Select(c => c.Name));
        Assert.Equal("Id", Assert.Single(TableProfile.Pick(twins with { Asked = ["[Id]"] }, new SqlServerProfile(false), out _, out _)!).Name);
    }

    [Fact]
    public async Task ACutColumnList_IsNoted()
    {
        var script = new Script(Ok(["6", "6", "6", "1", "6", "5", "2", "a", "b"]));
        string answer = await TableProfile.RunAsync(Request(top: 0) with { ColumnsCut = true }, SqliteProfile.Instance, script.Run, r => "Error: " + r.Detail, CancellationToken.None);
        Assert.EndsWith(TableProfileText.ColumnsCut(2), answer);
    }

    [Fact]
    public void PastFortyColumns_TheRestAreNamed()
    {
        var all = Enumerable.Range(1, 45).Select(i => new ProfileColumn("c" + i, "int", ProfileMeasure.Full)).ToList();
        var picked = TableProfile.Pick(Request() with { Columns = all }, SqliteProfile.Instance, out var rest, out _);
        Assert.Equal(TableProfile.MaxColumns, picked!.Count);
        Assert.Equal(["c41", "c42", "c43", "c44", "c45"], rest);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(-1)]
    public async Task ATopOutOfRange_IsRefused(int top)
    {
        Assert.Equal(TableProfileText.BadTop(TableProfile.MaxTop), await Invoke(SqliteTool(), ("table", "people"), ("database", "shop.db"), ("top", top)));
    }

    [Fact]
    public async Task AColumnsListOfNumbers_IsRefused()
    {
        Assert.StartsWith("Error:", await Invoke(SqliteTool(), ("table", "people"), ("database", "shop.db"), ("columns", Json("[1, 2]"))));
    }

    // ── SQLite and DuckDB end to end ────────────────────────────────────────

    [Fact]
    public async Task Sqlite_ProfilesATable_InOneAnswer()
    {
        string answer = await Invoke(SqliteTool(), ("table", "PEOPLE"), ("database", "shop.db"));
        Assert.StartsWith("people: 6 rows (exact) · 4 columns · shop.db (", answer);
        Assert.Contains("| id | INTEGER | 0 | 6 (unique) | 1 | 6 |  |", answer);
        Assert.Contains("| city | TEXT | 1 (17%) | 2 | Oslo | Rome | Oslo (3), Rome (2) |", answer);
        Assert.Contains("| score | REAL | 1 (17%) | 5 (unique) | 1 | 9 |  |", answer);
    }

    [Fact]
    public async Task Sqlite_AView_AndASubsetOfColumns_AndNoTopValues()
    {
        string answer = await Invoke(SqliteTool(), ("table", "oslo"), ("database", "shop.db"), ("columns", Json("""["city"]""")), ("top", 0));
        Assert.StartsWith("oslo: 3 rows (exact) · 4 columns", answer);
        Assert.Contains("| city | TEXT | 0 | 1 | Oslo | Oslo |  |", answer);
        Assert.DoesNotContain("| id |", answer);
    }

    [Fact]
    public async Task Sqlite_ABigTable_IsProfiledByItsFirstRows_AndSaysSo()
    {
        string answer = await Invoke(SqliteTool(sampleAbove: 4), ("table", "people"), ("database", "shop.db"));
        Assert.StartsWith("people: from the first 5 of more than 4 rows · 4 columns", answer);
        Assert.Contains("| id | INTEGER | 0 | 5" + TableProfileText.AllDistinctRead + " | 1 | 5 |  |", answer);
    }

    [Fact]
    public async Task Sqlite_ABlob_IsMeasuredForItsNullsAlone()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "pics.db"), Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE pics (id INTEGER, photo BLOB); INSERT INTO pics VALUES (1, x'00ff'), (2, NULL);";
            command.ExecuteNonQuery();
        }

        string answer = await Invoke(SqliteTool(), ("table", "pics"), ("database", "pics.db"));
        Assert.Contains("| photo | BLOB | 1 (50%) | — | — | — | — |", answer);
    }

    [Fact]
    public async Task Sqlite_AnUnknownTableOrColumn_IsRefused()
    {
        Assert.StartsWith("Error:", await Invoke(SqliteTool(), ("table", "nope"), ("database", "shop.db")));
        Assert.Equal(TableProfileText.NoSuchColumn("zip", "people", SqliteDescribeTool.ToolName),
            await Invoke(SqliteTool(), ("table", "people"), ("database", "shop.db"), ("columns", Json("""["zip"]"""))));
    }

    [Fact]
    public async Task DuckDb_ProfilesACsvFile_ThroughTheFileRules()
    {
        File.WriteAllText(Path.Combine(_root, "sales.csv"), "id,region,total\n1,north,2.5\n2,south,7\n3,north,\n4,north,1\n");
        string answer = await Invoke(DuckDbTool(), ("table", "sales.csv"));
        Assert.StartsWith("sales.csv: 4 rows (exact) · 3 columns · ", answer);
        Assert.Contains("| region | VARCHAR | 0 | ~2 | north | south | north (3), south (1) |", answer);
        Assert.Contains("| total | DOUBLE | 1 (25%) | ~3 | 1.0 | 7.0 |  |", answer);
        Assert.DoesNotContain(_root, answer);
        Assert.Equal(TableProfileText.NoSuchColumn("zip", "sales.csv", DuckDbText.DescribeFile), await Invoke(DuckDbTool(), ("table", "sales.csv"), ("columns", Json("""["zip"]"""))));
        Assert.Equal(DuckDbText.NoProfileSource, await Invoke(DuckDbTool(), ("table", " ")));
    }

    [Fact]
    public async Task DuckDb_ABigFile_IsSampled_AndSaysSo()
    {
        // The reservoir asks for one past the cap: four of five rows back is more than the cap.
        File.WriteAllText(Path.Combine(_root, "sales.csv"), "id,region\n1,north\n2,south\n3,north\n4,north\n5,west\n");
        string answer = await Invoke(DuckDbTool(sampleAbove: 3), ("table", "sales.csv"));
        Assert.StartsWith("sales.csv: from a sample of ~4 of more than 3 rows · 2 columns", answer);
    }

    [Fact]
    public async Task DuckDb_AReservoirBiggerThanTheSource_ReturnsEveryRow()
    {
        // The rule the one-pass read stands on: a source under the reservoir comes back whole, so its count is exact.
        File.WriteAllText(Path.Combine(_root, "ten.csv"), "n\n" + string.Join("\n", Enumerable.Range(1, 10)) + "\n");
        string answer = await Invoke(DuckDbTool(), ("table", "ten.csv"));
        Assert.StartsWith("ten.csv: 10 rows (exact) · 1 column", answer);
    }

    [Fact]
    public async Task DuckDb_ProfilesATableOfADatabaseFile()
    {
        DuckDbToolsTests.Make(Path.Combine(_root, "shop.duckdb"), "CREATE TABLE things (a INTEGER, tags VARCHAR[]); INSERT INTO things VALUES (7, ['x']), (7, ['y']), (8, NULL);");
        string answer = await Invoke(DuckDbTool(), ("table", "THINGS"), ("database", "shop.duckdb"));
        Assert.StartsWith("things: 3 rows (exact) · 2 columns", answer);
        Assert.Contains("| a | INTEGER | 0 | ~2 | 7 | 8 | 7 (2), 8 (1) |", answer);
        Assert.Contains("| tags | VARCHAR[] | 1 (33%) | ~2 | — | — | — |", answer);
    }

    [Fact]
    public async Task DuckDb_RefusesAFileOutsideTheWorkingDirectory()
    {
        Assert.StartsWith("Error:", await Invoke(DuckDbTool(), ("table", "../../outside.csv")));
    }

    // ── classing ────────────────────────────────────────────────────────────

    public static TheoryData<string> ProfileNames() =>
        [SqlProfileTool.ToolName, OracleProfileTool.ToolName, MySqlProfileTool.ToolName, PostgresProfileTool.ToolName, SqliteProfileTool.ToolName, DuckDbProfileTool.ToolName];

    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void EachProfile_IsAPlanModeRead_KeptFromAdvisors_AndInHelp(string name)
    {
        Assert.True(PlanTools.Allowed(name), name);
        Assert.Contains(name, ChatScreen.DatabaseToolNames);
        Assert.Contains(NeonSidekick.Help.HelpToolCatalog.All, t => t.Name == name);
    }

    [Fact]
    public void AnAdvisorsRecentLines_WithholdAProfile()
    {
        var call = new FunctionCallContent("c1", SqliteProfileTool.ToolName, new Dictionary<string, object?> { ["table"] = "people", ["database"] = "shop.db" });
        Assert.StartsWith("(" + SqliteProfileTool.ToolName + " on ", Advisors.AdvisorRecent.StandIn(call));
    }

    [Fact]
    public void TheFactories_OfferProfile_AfterExplain()
    {
        var sqlite = ChatScreen.SqliteTools(new SqliteAccess(() => SqliteCatalog.Empty, () => _files), () => _settings, allow: null).Select(t => t.Name).ToList();
        Assert.Equal(sqlite.IndexOf(SqliteExplainTool.ToolName) + 1, sqlite.IndexOf(SqliteProfileTool.ToolName));
        var duck = ChatScreen.DuckDbTools(new DuckDbAccess(() => DuckDbCatalog.Empty, () => _files), () => _settings, allow: null).Select(t => t.Name).ToList();
        Assert.Equal(duck.IndexOf(DuckDbExplainTool.ToolName) + 1, duck.IndexOf(DuckDbProfileTool.ToolName));
    }
}
