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
                "TO_CLOB(RPAD('y', 3000, 'y')) AS c FROM dual"),
            ("to", "ora.xlsx"));
        Assert.StartsWith("Made ora.xlsx (", result);
        string sheet = Sheet("ora.xlsx");
        Assert.Contains("12345678901234567.89", sheet);
        Assert.Contains("+02:00", sheet);
        Assert.Contains(new string('y', 3000), sheet);
        Assert.DoesNotContain("2026-10-29", sheet);
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
}
