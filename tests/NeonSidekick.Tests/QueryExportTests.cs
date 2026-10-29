using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using MySqlConnector;
using NeonSidekick.DuckDb;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Office;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A query's rows straight into a workbook (2026-10-29, the Office writing plan's Phase 3): the shared export over SQLite and DuckDB in
/// process (types, the preview, the caps, the save root before and after, a sheet added, a cancel), each family's typed reader over a
/// <see cref="FakeDataReader"/> with its provider's own values, the streaming job, and the cut chat query's hint.
/// </summary>
public sealed class QueryExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "queryexport-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new() { SqliteTools = true, SqliteSandboxFiles = true, DuckDbTools = true, DuckDbSandboxFiles = true };
    private SaveRoot _saveRoot;

    public QueryExportTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _saveRoot = new SaveRoot(_files, null);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "shop.db"), Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE things (id INTEGER, price REAL, name TEXT, data BLOB, born DATE, seen DATETIME, note TEXT);
            INSERT INTO things VALUES (1, 2.5, 'a', x'CAFE', '2026-10-29', '2026-10-29 13:45:00', NULL);
            INSERT INTO things VALUES (2, NULL, 'b', NULL, 'not a date', '2026-10-30T08:00:00', 'x');
            INSERT INTO things VALUES (3, 7, replace(hex(zeroblob(20000)), '0', 'x'), NULL, NULL, NULL, NULL);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Stamp => _time.GetLocalNow().ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);

    private SqliteQueryTool Sqlite(bool canSave = true) =>
        new(new SqliteAccess(() => SqliteCatalog.Empty, () => _files), () => _settings) { ExportRoot = canSave ? () => _saveRoot : null, Clock = _time };

    private DuckDbQueryTool DuckDb() =>
        new(new DuckDbAccess(() => DuckDbCatalog.Empty, () => _files), () => _settings) { ExportRoot = () => _saveRoot, Clock = _time };

    private static async Task<string> Invoke(AIFunction tool, params (string Key, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in pairs)
        {
            arguments[key] = value;
        }

        return (string)(await tool.InvokeAsync(arguments))!;
    }

    private byte[] Bytes(string relative) => File.ReadAllBytes(Path.Combine(_root, relative));

    private string[] Workbooks() => [.. Directory.GetFiles(_root, "*.xlsx", SearchOption.AllDirectories).Select(Path.GetFileName)!];

    private static string Part(byte[] book, string name) => OfficeWriterTests.Part(book, name);

    private static string Cells(byte[] book, string sheet) => OfficeWriterTests.Read(book, new OfficeRequest(Sheet: sheet));

    // ── SQLite in process ───────────────────────────────────────────────────

    [Fact]
    public async Task ASqliteExport_TypesTheCells_AndAnswersWithTheFirstRows()
    {
        string result = await Invoke(Sqlite(), ("sql", "SELECT * FROM things ORDER BY id"), ("database", "shop.db"), ("to", "."));
        string name = "query-shop-" + Stamp + ".xlsx";
        Assert.StartsWith("Made " + name + " (", result);
        Assert.Contains("sheet \"Query\": 3 rows × 7 columns", result);
        Assert.Contains("\n\nThe first 3 rows:\n\n", result);
        Assert.Contains("| 1 | 2.5 | a | 0xCAFE | 2026-10-29 |", result);
        byte[] book = Bytes(name);
        OfficeSchema.ValidWorkbook(book);
        string sheet = Part(book, "xl/worksheets/sheet1.xml");
        Assert.Contains("<v>2.5</v>", sheet);
        Assert.Contains("0xCAFE", sheet);
        Assert.Contains(new string('x', XlsxValue.MaxChars - 1) + "…", sheet);   // the long text to Excel's cap, not the chat's 400
        Assert.DoesNotContain(new string('x', XlsxValue.MaxChars), sheet);
        string cells = Cells(book, "Query");
        Assert.Contains("not a date", cells);   // a declared DATE that won't parse stays its text
        Assert.Contains("2026-10-30", cells);
        Assert.DoesNotContain("2026-10-30T08", sheet);   // the ISO date-times read as numbers, not inline text
        Assert.DoesNotContain("2026-10-29 13:45", sheet);
        Assert.Contains("not a date", sheet);
    }

    [Fact]
    public async Task MaxRows_CutsTheExport_AndSaysSo_AndItsRangeIsExcels()
    {
        string result = await Invoke(Sqlite(), ("sql", "SELECT id FROM things ORDER BY id"), ("database", "shop.db"), ("to", "ids.xlsx"), ("max_rows", 2));
        Assert.Contains("; stopped at max_rows (2), so later rows are left out", result);
        Assert.Contains("The first 2 rows:", result);
        Assert.Equal(SqlText.BadMaxRows(1, SqlExport.MaxRows), await Invoke(Sqlite(), ("sql", "SELECT 1"), ("database", "shop.db"), ("to", "x.xlsx"), ("max_rows", 0)));
        Assert.Equal(SqlText.BadMaxRows(1, SqlExport.MaxRows), await Invoke(Sqlite(), ("sql", "SELECT 1"), ("database", "shop.db"), ("to", "x.xlsx"), ("max_rows", SqlExport.MaxRows + 1)));
        Assert.Equal(1_048_575, SqlExport.MaxRows);
    }

    [Fact]
    public async Task NoRows_WritesTheHeaderAlone()
    {
        string result = await Invoke(Sqlite(), ("sql", "SELECT id, name FROM things WHERE id > 99"), ("database", "shop.db"), ("to", "none.xlsx"));
        Assert.EndsWith("\n\nNo rows: the sheet holds the header alone.", result);
        OfficeSchema.ValidWorkbook(Bytes("none.xlsx"));
    }

    [Fact]
    public async Task PlanModeOrNoRoot_RefusesTo_BeforeTheQueryRuns()
    {
        _saveRoot = SaveRoot.Refused(FileText.SaveInPlanMode);
        Assert.Equal(FileText.SaveInPlanMode, await Invoke(Sqlite(), ("sql", "SELECT * FROM things"), ("database", "shop.db"), ("to", ".")));
        Assert.Equal(FileText.SaveNotHere, await Invoke(Sqlite(canSave: false), ("sql", "SELECT * FROM things"), ("database", "shop.db"), ("to", ".")));
        Assert.Empty(Workbooks());
    }

    [Fact]
    public async Task ARootClosedBeforeTheCommit_WritesNothing()
    {
        int asked = 0;
        var tool = new SqliteQueryTool(new SqliteAccess(() => SqliteCatalog.Empty, () => _files), () => _settings)
        {
            ExportRoot = () => ++asked == 1 ? _saveRoot : SaveRoot.Refused(FileText.SaveInPlanMode),
            Clock = _time,
        };
        string result = await Invoke(tool, ("sql", "SELECT * FROM things"), ("database", "shop.db"), ("to", "late.xlsx"));
        Assert.StartsWith("Not saved", result);
        Assert.Empty(Workbooks());
    }

    [Fact]
    public async Task SheetOrOverwrite_WithoutTo_AreRefused_NotDropped()
    {
        Assert.Equal(OfficeText.NeedsTo("sheet"), await Invoke(Sqlite(), ("sql", "SELECT 1"), ("database", "shop.db"), ("sheet", "S")));
        Assert.Equal(OfficeText.NeedsTo("overwrite"), await Invoke(Sqlite(), ("sql", "SELECT 1"), ("database", "shop.db"), ("overwrite", true)));
    }

    [Fact]
    public async Task ASecondExport_AddsASheet_ToTheWorkbook()
    {
        Assert.StartsWith("Made book.xlsx", await Invoke(Sqlite(), ("sql", "SELECT id FROM things"), ("database", "shop.db"), ("to", "book.xlsx")));
        string second = await Invoke(Sqlite(), ("sql", "SELECT name FROM things WHERE id < 3"), ("database", "shop.db"), ("to", "book.xlsx"), ("sheet", "Names"));
        Assert.StartsWith("Added sheet \"Names\" to book.xlsx (", second);
        Assert.Contains(": 2 rows × 1 column", second);
        byte[] book = Bytes("book.xlsx");
        OfficeSchema.ValidWorkbook(book);
        string sheets = OfficeWriterTests.Read(book);
        Assert.Contains("Query", sheets);
        Assert.Contains("Names", sheets);
        // The same sheet again without overwrite is refused; the workbook is left as it was.
        Assert.StartsWith("Error:", await Invoke(Sqlite(), ("sql", "SELECT 1"), ("database", "shop.db"), ("to", "book.xlsx"), ("sheet", "Names")));
        Assert.Equal(book, Bytes("book.xlsx"));
    }

    [Fact]
    public async Task ACanceledExport_LeavesNoFile()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var tool = Sqlite();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.RunAsync("SELECT * FROM things", "shop.db", [], null, canceled.Token, new ServerTool<SqliteAccess>.ExportTarget("gone.xlsx", null, false)));
        Assert.Empty(Workbooks());
    }

    [Fact]
    public async Task ACutChatQuery_OffersTo_OnlyWhileARootIsOpen()
    {
        string open = await Sqlite().RunAsync("SELECT id FROM things", "shop.db", [], 1, CancellationToken.None);
        Assert.Contains("raise max_rows" + SqlExport.SaveHint + ")", open);
        string closed = await Sqlite(canSave: false).RunAsync("SELECT id FROM things", "shop.db", [], 1, CancellationToken.None);
        Assert.Contains("raise max_rows)", closed);
        Assert.DoesNotContain(SqlExport.SaveHint, closed);
        _saveRoot = SaveRoot.Refused(FileText.SaveInPlanMode);
        Assert.DoesNotContain(SqlExport.SaveHint, await Sqlite().RunAsync("SELECT id FROM things", "shop.db", [], 1, CancellationToken.None));
    }

    [Fact]
    public void TheTimeout_IsTheSettingOr600_WhicheverIsLarger_AndTheNameIsFileSafe()
    {
        Assert.Equal(600, SqlExport.TimeoutFor(30));
        Assert.Equal(600, SqlExport.TimeoutFor(600));
        Assert.Equal(900, SqlExport.TimeoutFor(900));
        Assert.Equal("server-instance", FileNames.Safe("server\\instance"));
    }

    // ── DuckDB in process ───────────────────────────────────────────────────

    [Fact]
    public async Task ADuckDbExport_TypesTheWrappedText_ByTheColumnsTypes()
    {
        string sql = "SELECT 1::INTEGER AS i, 2.5::DOUBLE AS d, 12345678901234567.25::DECIMAL(20,2) AS big, DATE '2026-10-29' AS day, " +
            "TIMESTAMP '2026-10-29 13:45:00' AS at, true AS ok, [1, 2] AS list, $who AS who";
        string result = await Invoke(DuckDb(), ("sql", sql), ("params", JsonDocument.Parse("""{"who": "Ada"}""").RootElement.Clone()), ("to", "duck.xlsx"));
        Assert.StartsWith("Made duck.xlsx (", result);
        Assert.Contains("1 row × 8 columns", result);
        byte[] book = Bytes("duck.xlsx");
        OfficeSchema.ValidWorkbook(book);
        string sheet = Part(book, "xl/worksheets/sheet1.xml");
        Assert.Contains("<v>1</v>", sheet);
        Assert.Contains("<v>2.5</v>", sheet);
        Assert.Contains("12345678901234567.25", sheet);   // past 15 digits: text
        Assert.Contains("t=\"b\"", sheet);
        Assert.Contains("[1, 2]", sheet);   // nested: text
        Assert.Contains("Ada", sheet);
        Assert.DoesNotContain("2026-10-29", sheet);   // the DATE and TIMESTAMP typed back: serials, not inline text
        string cells = Cells(book, "Query");
        Assert.Contains("2026-10-29", cells);
    }

    [Fact]
    public async Task ADuckDbExplain_ExportsAsText()
    {
        string result = await Invoke(DuckDb(), ("sql", "EXPLAIN SELECT 1"), ("to", "plan.xlsx"));
        Assert.StartsWith("Made plan.xlsx (", result);
        OfficeSchema.ValidWorkbook(Bytes("plan.xlsx"));
    }

    [Fact]
    public void DuckDbText_ReadsBackByType()
    {
        Assert.Equal(XlsxKind.Number, DuckDbAccess.FromText("42", typeof(long)).Kind);
        Assert.Equal(XlsxKind.Text, DuckDbAccess.FromText("inf", typeof(double)).Kind);
        Assert.Equal(XlsxKind.Bool, DuckDbAccess.FromText("true", typeof(bool)).Kind);
        Assert.Equal(XlsxKind.Date, DuckDbAccess.FromText("2026-10-29", typeof(DateOnly)).Kind);
        Assert.Equal(XlsxKind.DateTime, DuckDbAccess.FromText("2026-10-29 13:45:00.5", typeof(DateTime)).Kind);
        Assert.Equal(XlsxKind.Text, DuckDbAccess.FromText("2026-10-29 13:45:00+00", typeof(DateTime)).Kind);   // a time zone's: text
        Assert.Equal(XlsxKind.Text, DuckDbAccess.FromText("infinity", typeof(DateTime)).Kind);
        Assert.Equal(XlsxKind.Time, DuckDbAccess.FromText("08:30:00", typeof(TimeOnly)).Kind);
        Assert.Equal(XlsxKind.Text, DuckDbAccess.FromText("1 day", typeof(TimeSpan)).Kind);
    }

    // ── each family's typed reader over its provider's values ──────────────

    private static XlsxValue Cell(SqlCellReader cell, FakeDataReader reader, int ordinal = 0)
    {
        Assert.True(reader.Read());
        return cell(reader, ordinal);
    }

    [Fact]
    public void TheCommonRules_TypeScalars_AndFallBackForTheRest()
    {
        var date = new FakeDataReader(["d"], ["date"], [new DateTime(2026, 10, 29)]);
        Assert.Equal(XlsxKind.Date, Cell(SqlAccess.ExportCell, date).Kind);
        var stamp = new FakeDataReader(["d"], ["datetime2"], [new DateTime(2026, 10, 29)]);
        Assert.Equal(XlsxKind.DateTime, Cell(SqlAccess.ExportCell, stamp).Kind);
        var big = new FakeDataReader(["n"], ["decimal"], [1234567890123456.7m]);
        Assert.Equal(XlsxKind.Text, Cell(SqlAccess.ExportCell, big).Kind);
        var none = new FakeDataReader(["n"], ["int"], new object?[] { null });
        Assert.Equal(XlsxKind.Blank, Cell(SqlAccess.ExportCell, none).Kind);
        // A value the provider won't hand over: its provider value's text, by the 15-digit rule.
        var past = new FakeDataReader(["n"], ["numeric"], [new InvalidCastException("past decimal")]);
        past.ProviderValues[(0, 0)] = "123456789012345678901234567890";
        var cell = Cell(PostgresAccess.ExportCell, past);
        Assert.Equal((XlsxKind.Text, "123456789012345678901234567890"), (cell.Kind, cell.Text));
    }

    [Fact]
    public void Postgres_IntervalMoneyAndInfinity_AreText_TimestamptzSaysUtc()
    {
        Assert.Equal(XlsxKind.Text, Cell(PostgresAccess.ExportCell, new FakeDataReader(["i"], ["interval"], [TimeSpan.FromHours(30)])).Kind);
        Assert.Equal(XlsxKind.Text, Cell(PostgresAccess.ExportCell, new FakeDataReader(["m"], ["money"], [12.5m])).Kind);
        Assert.Equal("infinity", Cell(PostgresAccess.ExportCell, new FakeDataReader(["t"], ["timestamp without time zone"], [DateTime.MaxValue])).Text);
        Assert.Equal("-infinity", Cell(PostgresAccess.ExportCell, new FakeDataReader(["t"], ["date"], [DateOnly.MinValue])).Text);
        Assert.Equal(XlsxKind.Date, Cell(PostgresAccess.ExportCell, new FakeDataReader(["d"], ["date"], [new DateOnly(2026, 10, 29)])).Kind);
        Assert.Equal(XlsxKind.Date, Cell(PostgresAccess.ExportCell, new FakeDataReader(["d"], ["date"], [new DateTime(2026, 10, 29)])).Kind);
        var utc = new FakeDataReader(["at", "plain"], ["timestamp with time zone", "text"], [new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc), "x"]);
        Assert.Equal(XlsxKind.DateTime, Cell(PostgresAccess.ExportCell, utc).Kind);
        Assert.Equal("at" + PostgresText.UtcColumn, PostgresAccess.ExportHeader(utc, 0));
        Assert.Equal("plain", PostgresAccess.ExportHeader(utc, 1));
    }

    [Fact]
    public void MySql_ADateIsADate_AZeroDateIsText()
    {
        var day = new FakeDataReader(["d"], ["DATE"], [new MySqlDateTime(2026, 10, 29, 0, 0, 0, 0)]);
        Assert.Equal(XlsxKind.Date, Cell(MySqlAccess.ExportCell, day).Kind);
        var at = new FakeDataReader(["d"], ["DATETIME"], [new MySqlDateTime(2026, 10, 29, 13, 45, 0, 0)]);
        Assert.Equal(XlsxKind.DateTime, Cell(MySqlAccess.ExportCell, at).Kind);
        var zero = new FakeDataReader(["d"], ["DATETIME"], [new MySqlDateTime(0, 0, 0, 0, 0, 0, 0)]);
        Assert.Equal(XlsxKind.Text, Cell(MySqlAccess.ExportCell, zero).Kind);
        var tiny = new FakeDataReader(["b"], ["TINYINT"], [(sbyte)1]);
        var number = Cell(MySqlAccess.ExportCell, tiny);   // TreatTinyAsBoolean is off: the 0/1 the chat shows
        Assert.Equal((XlsxKind.Number, 1d), (number.Kind, number.Number));
    }

    [Fact]
    public async Task TheJob_HandsEachRowToTheSheet_AsItIsRead()
    {
        var reader = new FakeDataReader(["n"], ["int"], [1], [2], [3], [4], [5], [6]);
        var sink = new RecordingRows(reader);
        var job = new SqlExportJob(sink, (r, i) => XlsxValue.From(r.GetValue(i)));
        var grid = await job.ReadAsync(reader, 4, CancellationToken.None);
        Assert.Equal([1, 2, 3, 4], sink.ReadsAtAdd);   // row n reached the sheet before row n + 1 was read
        Assert.True(grid.More);
        Assert.Equal(4, grid.Rows.Count);
        Assert.Equal(4, job.Rows);
        // A second result set is not exported (every gate allows one statement).
        var again = await job.ReadAsync(new FakeDataReader(["m"], ["int"], [9]), 4, CancellationToken.None);
        Assert.Empty(again.Rows);
    }

    private sealed class RecordingRows(FakeDataReader reader) : ISheetRows
    {
        public List<int> ReadsAtAdd { get; } = [];

        public string? Begin(IReadOnlyList<string> columns) => null;

        public bool Add(IReadOnlyList<XlsxValue> row)
        {
            ReadsAtAdd.Add(reader.Reads);
            return true;
        }
    }
}
