using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Office;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The four server families' exports against the dev containers (2026-10-29, the Office writing plan; containers per docs/TESTCONTAINERS.md):
/// a decimal past 15 digits kept as text, dates typed, long text to Excel's cap, PostgreSQL's timestamptz said UTC and infinity as the word.
/// Each skips without its container.
/// </summary>
public sealed class LiveQueryExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "liveexport-" + Guid.NewGuid().ToString("N"));
    private readonly WorkingDirectory _files;

    public LiveQueryExportTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, TimeProvider.System);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Func<SaveRoot> Root => () => new SaveRoot(_files, null);

    private static async Task<string> Invoke<T>(IReadOnlyList<AIFunction> tools, params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await tools.OfType<T>().Single().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private string Sheet(string name)
    {
        byte[] book = File.ReadAllBytes(Path.Combine(_root, name));
        OfficeSchema.ValidWorkbook(book);
        return OfficeWriterTests.Part(book, "xl/worksheets/sheet1.xml");
    }

    [LiveSqlFact]
    public async Task SqlServer_TypesTheCells_AndReadsLongTextToTheCap()
    {
        var catalog = new SqlCatalog([new SqlNamedConnection("aw", LiveSql.Config!, "test")], []);
        var tools = ChatScreen.SqlTools(new SqlAccess(() => catalog), () => new AppSettingsData(), saveRoot: Root);
        string result = await Invoke<SqlQueryTool>(tools,
            ("sql", "SELECT TOP 3 SalesOrderID, OrderDate, TotalDue, CAST(12345678901234567.89 AS decimal(20,2)) AS big, CAST('2026-10-29' AS date) AS d, " +
                "CAST('13:45' AS time) AS t, REPLICATE(CAST(N'x' AS nvarchar(max)), 40000) AS long FROM Sales.SalesOrderHeader ORDER BY SalesOrderID"),
            ("to", "aw.xlsx"));
        Assert.StartsWith("Made aw.xlsx (", result);
        Assert.Contains("3 rows × 7 columns", result);
        string sheet = Sheet("aw.xlsx");
        Assert.Contains("12345678901234567.89", sheet);
        Assert.Contains(new string('x', XlsxValue.MaxChars - 1) + "…", sheet);
        Assert.DoesNotContain("2026-10-29", sheet);   // a date, not text
    }

    [LiveMySqlFact]
    public async Task MySql_TypesTheCells()
    {
        var catalog = new MySqlCatalog([new MySqlNamedConnection("live", LiveMySql.Config!, "test")], []);
        var tools = ChatScreen.MySqlTools(new MySqlAccess(() => catalog), () => new AppSettingsData { MySqlTools = true }, saveRoot: Root);
        string result = await Invoke<MySqlQueryTool>(tools,
            ("sql", "SELECT CAST('2026-10-29' AS DATE) AS d, CAST('2026-10-29 13:45:00' AS DATETIME) AS at, CAST(12345678901234567.89 AS DECIMAL(20,2)) AS big, CAST('30:00:00' AS TIME) AS t"),
            ("to", "my.xlsx"));
        Assert.StartsWith("Made my.xlsx (", result);
        string sheet = Sheet("my.xlsx");
        Assert.Contains("12345678901234567.89", sheet);
        Assert.DoesNotContain("2026-10-29", sheet);
    }

    [LiveOracleFact]
    public async Task Oracle_TypesTheCells_AndKeepsATimeZonesTimestampAsText()
    {
        var catalog = new OracleCatalog([new OracleNamedConnection("free", LiveOracle.Config!, "test")], []);
        var tools = ChatScreen.OracleTools(new OracleAccess(() => catalog), () => new AppSettingsData { OracleTools = true }, saveRoot: Root);
        string result = await Invoke<OracleQueryTool>(tools,
            ("sql", "SELECT DATE '2026-10-29' AS d, CAST(12345678901234567.89 AS NUMBER(20,2)) AS big, TIMESTAMP '2026-10-30 08:00:00 +02:00' AS tz, " +
                "TO_CLOB(RPAD('y', 3000, 'y')) AS c, INTERVAL '2 03:04:05' DAY TO SECOND AS ds FROM dual"),
            ("to", "ora.xlsx"));
        Assert.StartsWith("Made ora.xlsx (", result);
        string sheet = Sheet("ora.xlsx");
        Assert.Contains("12345678901234567.89", sheet);
        Assert.Contains("+02:00", sheet);
        Assert.Contains(new string('y', 3000), sheet);
        Assert.DoesNotContain("2026-10-29", sheet);
        Assert.Contains(">2.03:04:05<", Cell(sheet, "E"));   // a day interval as the chat writes it (2026-10-30, review F49)
    }

    [LivePostgresFact]
    public async Task Postgres_SaysUtc_AndInfinityIsTheWord()
    {
        var catalog = new PostgresCatalog([new PostgresNamedConnection("live", LivePostgres.Config!, "test")], []);
        var tools = ChatScreen.PostgresTools(new PostgresAccess(() => catalog), () => new AppSettingsData { PostgresTools = true }, saveRoot: Root);
        string result = await Invoke<PostgresQueryTool>(tools,
            ("sql", "SELECT TIMESTAMPTZ '2026-10-29 12:00:00+00' AS at, 12345678901234567.89::numeric AS big, 'infinity'::date AS forever, " +
                "interval '1 day 2 hours' AS i, DATE '2026-10-29' AS d"),
            ("to", "pg.xlsx"));
        Assert.StartsWith("Made pg.xlsx (", result);
        string sheet = Sheet("pg.xlsx");
        Assert.Contains("at" + PostgresText.UtcColumn, sheet);
        Assert.Contains("12345678901234567.89", sheet);
        Assert.Contains(">infinity<", sheet);
        Assert.DoesNotContain("2026-10-29", sheet);
    }

    /// <summary>Row 2's cell in <paramref name="column"/>: its whole <c>&lt;c&gt;</c> element.</summary>
    private static string Cell(string sheet, string column) =>
        System.Text.RegularExpressions.Regex.Match(sheet, "<c r=\"" + column + "2\".*?</c>").Value;

    [LivePostgresFact]
    public async Task Postgres_InfinityIsTheEnds_ALastDayAtNoonIsADate_AndATimetzIsText()
    {
        // 2026-10-29, review P1-1 and P6-3, against Npgsql as it is.
        var catalog = new PostgresCatalog([new PostgresNamedConnection("live", LivePostgres.Config!, "test")], []);
        var tools = ChatScreen.PostgresTools(new PostgresAccess(() => catalog), () => new AppSettingsData { PostgresTools = true }, saveRoot: Root);
        string result = await Invoke<PostgresQueryTool>(tools,
            ("sql", "SELECT 'infinity'::date AS di, '9999-12-31'::date AS dl, 'infinity'::timestamp AS ti, '9999-12-31 12:00'::timestamp AS tl, " +
                "'-infinity'::timestamp AS tm, TIMETZ '13:45:00+02' AS tz"),
            ("to", "ends.xlsx"));
        Assert.StartsWith("Made ends.xlsx (", result);
        string sheet = Sheet("ends.xlsx");
        Assert.Contains(">infinity<", Cell(sheet, "A"));
        Assert.Contains(">infinity<", Cell(sheet, "C"));
        Assert.Contains("<v>", Cell(sheet, "D"));   // a real date-time
        Assert.Contains(">-infinity<", Cell(sheet, "E"));
        Assert.Contains(">13:45:00+02:00<", Cell(sheet, "F"));
        Assert.True(Cell(sheet, "B").Length > 0, sheet);
    }

    /// <summary>
    /// Money a number, a month's interval as PostgreSQL writes it, NaN and the infinities as words, a numeric past decimal by its exact digits
    /// (2026-10-30, review F16 and F117, against Npgsql as it is: each but money wrote the can't-be-read placeholder).
    /// </summary>
    [LivePostgresFact]
    public async Task Postgres_MoneyIntervalsNaNAndLongNumerics()
    {
        var catalog = new PostgresCatalog([new PostgresNamedConnection("live", LivePostgres.Config!, "test")], []);
        var tools = ChatScreen.PostgresTools(new PostgresAccess(() => catalog), () => new AppSettingsData { PostgresTools = true }, saveRoot: Root);
        string result = await Invoke<PostgresQueryTool>(tools,
            ("sql", "SELECT 12.34::money AS m, interval '1 month 2 days' AS i, interval '1 day 2 hours' AS j, 'NaN'::numeric AS nan, " +
                "'-Infinity'::numeric AS ninf, 1e40::numeric AS huge, 12345678901234567890123456789012.5::numeric AS long"),
            ("to", "pgnum.xlsx"));
        Assert.StartsWith("Made pgnum.xlsx (", result);
        string sheet = Sheet("pgnum.xlsx");
        Assert.Contains("<v>12.34</v>", Cell(sheet, "A"));
        Assert.Contains(">1 mon 2 days<", Cell(sheet, "B"));
        Assert.Contains(">1 day 02:00:00<", Cell(sheet, "C"));
        Assert.Contains(">NaN<", Cell(sheet, "D"));
        Assert.Contains(">-Infinity<", Cell(sheet, "E"));
        Assert.Contains("<v>", Cell(sheet, "F"));   // 1e40: one significant digit, so a number
        Assert.Contains(">12345678901234567890123456789012.5<", Cell(sheet, "G"));
        Assert.DoesNotContain("can't be read", sheet);
    }

    /// <summary>A table of one 40 KB LONG RAW, made once (a read-only read of a table made seconds ago is ORA-01466, so it waits after).</summary>
    private static async Task LongRawFixture()
    {
        using var connection = new global::Oracle.ManagedDataAccess.Client.OracleConnection(Environment.GetEnvironmentVariable(LiveOracle.Variable));
        connection.Open();
        using (var count = new global::Oracle.ManagedDataAccess.Client.OracleCommand("SELECT COUNT(*) FROM user_tables WHERE table_name = 'NS_LONGRAW'", connection))
        {
            if (Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        using (var create = new global::Oracle.ManagedDataAccess.Client.OracleCommand("CREATE TABLE ns_longraw (id NUMBER(10), b LONG RAW)", connection))
        {
            create.ExecuteNonQuery();
        }

        using (var insert = new global::Oracle.ManagedDataAccess.Client.OracleCommand("INSERT INTO ns_longraw (id, b) VALUES (1, :b)", connection))
        {
            insert.Parameters.Add("b", global::Oracle.ManagedDataAccess.Client.OracleDbType.LongRaw).Value = Enumerable.Repeat((byte)0xAB, 40_000).ToArray();
            insert.ExecuteNonQuery();
        }

        await Task.Delay(5_000);
    }

    [LiveOracleFact]
    public async Task Oracle_ALongClob_ALongRaw_AndAMidnightDate()
    {
        // 2026-10-29, review P1-7, P1-8, P5-5.
        await LongRawFixture();
        var catalog = new OracleCatalog([new OracleNamedConnection("free", LiveOracle.Config!, "test")], []);
        var tools = ChatScreen.OracleTools(new OracleAccess(() => catalog), () => new AppSettingsData { OracleTools = true }, saveRoot: Root);
        string result = await Invoke<OracleQueryTool>(tools,
            ("sql", "SELECT DATE '2026-10-29' AS d, RPAD(TO_CLOB('y'), 40000, 'y') AS c, b FROM ns_longraw WHERE id = 1"),
            ("to", "long.xlsx"));
        Assert.StartsWith("Made long.xlsx (", result);
        string sheet = Sheet("long.xlsx");
        Assert.StartsWith("<c r=\"A2\" s=\"3\"><v>", Cell(sheet, "A"));   // a date-time: Oracle's DATE holds a time
        Assert.Contains(new string('y', XlsxValue.MaxChars - 1) + "…", Cell(sheet, "B"));
        string raw = Cell(sheet, "C");
        Assert.Contains("0xABAB", raw);
        Assert.Contains("…", raw);
    }
}
