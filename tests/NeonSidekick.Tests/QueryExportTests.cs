using System.Collections;
using System.Data.SqlTypes;
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
        // 2026-10-29, review P6-8: canceled mid-stream, not before the run, so the temp sibling a stop could leave behind would show.
        using var canceled = new CancellationTokenSource();
        canceled.CancelAfter(150);
        const string Many = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 5000000) SELECT i, 'row ' || i AS name FROM n";
        try
        {
            string answer = await Sqlite().RunAsync(Many, "shop.db", [], null, canceled.Token, new ServerTool<SqliteAccess>.ExportTarget("gone.xlsx", null, false));
            Assert.DoesNotContain("Made", answer, StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            // what a stop between rows throws
        }

        Assert.All(Directory.GetFiles(_root), f => Assert.StartsWith("shop.db", Path.GetFileName(f), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACutChatQuery_OffersTo_OnlyWhileARootIsOpen()
    {
        string open = await Sqlite().RunAsync("SELECT id FROM things", "shop.db", [], 1, CancellationToken.None);
        Assert.Contains("raise max_rows" + SqlExportText.SaveHint + ")", open);
        string closed = await Sqlite(canSave: false).RunAsync("SELECT id FROM things", "shop.db", [], 1, CancellationToken.None);
        Assert.Contains("raise max_rows)", closed);
        Assert.DoesNotContain(SqlExportText.SaveHint, closed);
        _saveRoot = SaveRoot.Refused(FileText.SaveInPlanMode);
        Assert.DoesNotContain(SqlExportText.SaveHint, await Sqlite().RunAsync("SELECT id FROM things", "shop.db", [], 1, CancellationToken.None));
    }

    [Fact]
    public void TheTimeout_IsTheSettingOr600_WhicheverIsLarger_AndTheNameIsFileSafe()
    {
        Assert.Equal(600, SqlExport.TimeoutFor(30));
        Assert.Equal(600, SqlExport.TimeoutFor(600));
        Assert.Equal(900, SqlExport.TimeoutFor(900));
        Assert.Equal("server-instance", FileNames.Safe("server\\instance"));
        // 2026-10-29, review P3-12: a name with nothing file-safe in it makes query, not query-.
        Assert.Equal("query-shop", SqlExport.FileStem("shop"));
        Assert.Equal("query", SqlExport.FileStem("***"));
        Assert.Equal("query", SqlExport.FileStem(null));
    }

    // ── the export's lifecycle (2026-10-29, review P2-8, P1-4, P2-1, P7-7, P2-4, P1-17) ──

    /// <summary>The shared export with a run the test writes: what a family's session does, without a server.</summary>
    private sealed class ExportProbe() : ServerTool<object>(new object(), () => new AppSettingsData())
    {
        public override string Name => "probe_query";

        protected override string? RetiredArgument => null;

        protected override string RetiredRefusal => "";

        protected override ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => ValueTask.FromResult<object?>(null);

        public static string? Cap(ExportTarget? export, int? given, out int rows) => RowCap(export, given, 100, 1, 1000, out rows);

        public Task<string> Export(string to, string? sheet, Func<SqlExportJob, Task<SqlRun>> run, bool overwrite = false) =>
            ExportAsync(new ExportTarget(to, sheet, overwrite), "probe", 10, null, 30,
                (r, i, _) => XlsxValue.From(r.GetValue(i)), (job, _, token) => run(job), r => "Error: " + r.Detail, CancellationToken.None);
    }

    private ExportProbe Probe(Func<SaveRoot>? root = null) => new() { ExportRoot = root ?? (() => _saveRoot), Clock = _time };

    private static SqlRun Ok() => new(SqlOutcome.Ok, "", "probe", "", [], TimeSpan.Zero);

    private static async Task<SqlRun> Rows(SqlExportJob job, params int[] values)
    {
        var grid = await job.ReadAsync(new FakeDataReader(["n"], ["int"], [.. values.Select(v => new object?[] { v })]), 10, CancellationToken.None);
        return new SqlRun(SqlOutcome.Ok, "", "probe", "", [grid], TimeSpan.Zero);
    }

    [Fact]
    public async Task AQueryThatFails_BeginsNoWorkbook()
    {
        // The run fails before any result set: nothing is copied or begun, so the existing workbook and the folder are as they were.
        Assert.StartsWith("Made book.xlsx", await Probe().Export("book.xlsx", null, job => Rows(job, 1)));
        byte[] before = Bytes("book.xlsx");
        string failed = await Probe().Export("book.xlsx", "More", _ => Task.FromResult(new SqlRun(SqlOutcome.Failed, "no such table", "probe", "", [], TimeSpan.Zero)));
        Assert.Equal("Error: no such table" + SqlExportText.NotWritten(0, timedOut: false), failed);
        Assert.Equal(before, Bytes("book.xlsx"));
        Assert.Equal(["book.xlsx", "shop.db"], Directory.GetFiles(_root).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task AStatementWithNoResultSet_WritesNothing()
    {
        Assert.Equal(SqlExportText.NoResultSet, await Probe().Export("none.xlsx", null, _ => Task.FromResult(Ok())));
        Assert.Empty(Workbooks());
    }

    [Fact]
    public async Task AWorkbookSavedElsewhereMeanwhile_IsLeftAsItIs()
    {
        Assert.StartsWith("Made book.xlsx", await Probe().Export("book.xlsx", null, job => Rows(job, 1)));
        string full = Path.Combine(_root, "book.xlsx");
        byte[] saved = [];
        string answer = await Probe().Export("book.xlsx", "Added", async job =>
        {
            var run = await Rows(job, 2, 3);
            // Excel for Mac saves while the rows are written: the copy is of the old file.
            File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddMinutes(1));
            saved = File.ReadAllBytes(full);
            return run;
        });
        Assert.Equal(OfficeText.ChangedMeanwhile("book.xlsx"), answer);
        Assert.Equal(saved, Bytes("book.xlsx"));
        Assert.DoesNotContain("Added", OfficeWriterTests.Read(Bytes("book.xlsx")));
    }

    [Fact]
    public async Task AWorkingFolderMovedMeanwhile_WritesNothing()
    {
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        int asked = 0;
        var moved = new SaveRoot(new WorkingDirectory(() => other, _time), null);
        string answer = await Probe(() => ++asked == 1 ? _saveRoot : moved).Export("moved.xlsx", null, job => Rows(job, 1));
        Assert.Equal(FileText.SaveMovedLate, answer);
        Assert.Empty(Workbooks());
    }

    [Fact]
    public async Task AWorkbookThatIsNoWorkbook_IsReplacedWhole_OnlyWithOverwrite()
    {
        // 2026-10-29, review P2-2: a zip that holds no workbook (a Word file named .xlsx).
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(Path.Combine(_root, "word.xlsx")), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>");
        }

        Assert.StartsWith("Error:", await Probe().Export("word.xlsx", "S", job => Rows(job, 1)));
        string replaced = await Probe().Export("word.xlsx", "S", job => Rows(job, 1), overwrite: true);
        Assert.StartsWith("Replaced word.xlsx", replaced);
        OfficeSchema.ValidWorkbook(Bytes("word.xlsx"));
    }

    [Fact]
    public void TheRowCap_WithTo_RunsOnePastExcelsLastRow_WhenNoneIsGiven()
    {
        var to = new ServerTool<object>.ExportTarget("x.xlsx", null, false);
        Assert.Null(ExportProbe.Cap(to, null, out int rows));
        Assert.Equal(SqlExport.MaxRows + 1, rows);
        Assert.Null(ExportProbe.Cap(to, 5, out rows));
        Assert.Equal(5, rows);
        Assert.Equal(SqlText.BadMaxRows(1, SqlExport.MaxRows), ExportProbe.Cap(to, 0, out _));
        Assert.Null(ExportProbe.Cap(null, null, out rows));
        Assert.Equal(100, rows);
        Assert.Equal(SqlText.BadMaxRows(1, 1000), ExportProbe.Cap(null, 1001, out _));
        // A cut the sheet made itself (none given) says nothing of max_rows: the save sentence says Excel's last row.
        var grid = new SqlGrid(["n"], [["1"]], true);
        Assert.DoesNotContain("max_rows", SqlExportText.Answer("Made x.xlsx", grid, given: null));
        Assert.Contains("stopped at max_rows (1)", SqlExportText.Answer("Made x.xlsx", grid, given: 1));
    }

    [Fact]
    public void TheSchema_OffersTo_OnlyWhileASaveIsOpen()
    {
        // 2026-10-29, review P1-20.
        static bool HasTo(AIFunction tool) => tool.JsonSchema.GetProperty("properties").TryGetProperty("to", out _);
        Assert.True(HasTo(Sqlite()));
        Assert.Contains(ServerTool<SqliteAccess>.ExportSentence, Sqlite().Description);
        Assert.False(HasTo(Sqlite(canSave: false)));
        Assert.DoesNotContain(ServerTool<SqliteAccess>.ExportSentence, Sqlite(canSave: false).Description);
        _saveRoot = SaveRoot.Refused(FileText.SaveInPlanMode);
        var refused = Sqlite();
        Assert.False(HasTo(refused));
        Assert.DoesNotContain("with to", refused.JsonSchema.GetProperty("properties").GetProperty("max_rows").GetProperty("description").GetString());
        OfficeToolsTests.NoListType(Sqlite().JsonSchema, "export");   // review P7-2
        OfficeToolsTests.NoListType(refused.JsonSchema, "plain");
    }

    [Fact]
    public void AQuerysTo_NeedsConvertToXlsxOn()
    {
        // 2026-10-29, review P3-3.
        var on = new AppSettingsData { FileTools = true };
        Assert.NotNull(App.ChatScreen.QuerySaveRootFor(on, planMode: false, _files).Root);
        var off = new AppSettingsData { FileTools = true, ToolsDisabled = [ConvertToXlsxTool.ToolName] };
        Assert.Equal(FileText.SaveNeedsXlsx, App.ChatScreen.QuerySaveRootFor(off, planMode: false, _files).Refusal);
        Assert.Equal(FileText.SaveInPlanMode, App.ChatScreen.QuerySaveRootFor(on, planMode: true, _files).Refusal);
    }

    [Fact]
    public async Task AnEmptyString_IsAnEmptyTextCell_NotABlank()
    {
        // 2026-10-29, review P3-4.
        await Invoke(Sqlite(), ("sql", "SELECT '' AS e, NULL AS n"), ("database", "shop.db"), ("to", "empty.xlsx"));
        string sheet = Part(Bytes("empty.xlsx"), "xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"A2\" t=\"inlineStr\">", sheet);
        Assert.DoesNotContain("r=\"B2\"", sheet);
        Assert.Equal(XlsxKind.Text, XlsxValue.From("").Kind);
        Assert.Equal(XlsxKind.Blank, XlsxValue.OfText("").Kind);   // a CSV's empty field: CSV can't tell the two apart
        Assert.Equal("", SqlExport.Show(XlsxValue.EmptyText));
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
        return cell(reader, ordinal, reader.GetDataTypeName(ordinal));
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
    public void Postgres_InfinityIsTheEnds_ARealLastDayAtNoonIsADate()
    {
        // 2026-10-29, review P1-1: 9999-12-31 at noon is a real date-time, not infinity. A date column's infinity comes as the last day
        // at midnight (the live run), so there that day is the word; a timestamp at that midnight stays a date.
        var noon = Cell(PostgresAccess.ExportCell, new FakeDataReader(["t"], ["timestamp without time zone"], [new DateTime(9999, 12, 31, 12, 0, 0)]));
        Assert.Equal(XlsxKind.DateTime, noon.Kind);
        Assert.Equal("infinity", Cell(PostgresAccess.ExportCell, new FakeDataReader(["d"], ["date"], [new DateTime(9999, 12, 31)])).Text);
        Assert.Equal("infinity", Cell(PostgresAccess.ExportCell, new FakeDataReader(["d"], ["date"], [DateTime.MaxValue])).Text);
        var midnight = Cell(PostgresAccess.ExportCell, new FakeDataReader(["t"], ["timestamp without time zone"], [new DateTime(9999, 12, 31)]));
        Assert.NotEqual(XlsxKind.Text, midnight.Kind);
    }

    [Fact]
    public void Postgres_TimetzIsItsTimeAndOffset()
    {
        // 2026-10-29, review P6-3: Npgsql hands a timetz on 0001-01-02.
        var timetz = new DateTimeOffset(1, 1, 2, 9, 0, 0, TimeSpan.FromHours(2));
        var cell = Cell(PostgresAccess.ExportCell, new FakeDataReader(["t"], ["time with time zone"], [timetz]));
        Assert.Equal((XlsxKind.Text, "09:00:00+02:00"), (cell.Kind, cell.Text));
    }

    [Fact]
    public void Postgres_ArraysBitsAndHstore_AreWrittenWhole()
    {
        // 2026-10-29, review P5-1: the chat's text cut an array at 500 characters.
        int[] numbers = [.. Enumerable.Range(0, 5000)];
        var array = Cell(PostgresAccess.ExportCell, new FakeDataReader(["a"], ["integer[]"], [numbers]));
        Assert.Equal("{" + string.Join(", ", numbers) + "}", array.Text);
        Assert.False(array.Cut);
        var huge = Cell(PostgresAccess.ExportCell, new FakeDataReader(["a"], ["integer[]"], [Enumerable.Range(0, 20_000).ToArray()]));
        Assert.True(huge.Cut);
        Assert.Equal(XlsxValue.MaxChars, huge.Text!.Length);
        var bits = new BitArray([true, false, true, true]);
        Assert.Equal("1011", Cell(PostgresAccess.ExportCell, new FakeDataReader(["b"], ["bit varying"], [bits])).Text);
        var hstore = new Dictionary<string, string?> { ["a"] = "1", ["q\"x"] = null };
        Assert.Equal("\"a\"=>\"1\", \"q\\\"x\"=>NULL", Cell(PostgresAccess.ExportCell, new FakeDataReader(["h"], ["hstore"], [hstore])).Text);
        Assert.Equal("{2009-01-07 13:05:09 UTC, NULL}", PostgresAccess.ExportArrayText(new DateTime?[] { new DateTime(2009, 1, 7, 13, 5, 9, DateTimeKind.Utc), null }));
    }

    [Fact]
    public void Oracle_ADateAtMidnight_IsADateTime()
    {
        // 2026-10-29, review P1-7: Oracle's DATE holds a time; its type name "Date" read as a date alone.
        var cell = Cell(Oracle.OracleAccess.ExportCell, new FakeDataReader(["d"], ["Date"], [new DateTime(2026, 10, 29)]));
        Assert.Equal(XlsxKind.DateTime, cell.Kind);
    }

    [Fact]
    public void SqlServerValues_TypeByTheirProviderValue()
    {
        // 2026-10-29, review P3-13: the per-type rules apart from the sealed reader.
        Assert.Equal((XlsxKind.Number, "12.50"), Pair(SqlAccess.ExportValue(new SqlDecimal(12.50m), "decimal")));
        Assert.Equal(XlsxKind.Text, SqlAccess.ExportValue(SqlDecimal.Parse("12345678901234567890.5"), "decimal").Kind);
        Assert.Equal(XlsxKind.Number, SqlAccess.ExportValue(new SqlMoney(3.25m), "money").Kind);
        Assert.Equal(XlsxKind.Blank, SqlAccess.ExportValue(SqlDecimal.Null, "decimal").Kind);
        Assert.Equal(XlsxKind.Time, SqlAccess.ExportValue(TimeSpan.FromHours(8), "time").Kind);
        Assert.Equal(XlsxKind.Date, SqlAccess.ExportValue(new DateTime(2026, 10, 29), "date").Kind);
        Assert.Equal(XlsxKind.DateTime, SqlAccess.ExportValue(new DateTime(2026, 10, 29), "datetime2").Kind);
        Assert.Equal(XlsxKind.Text, SqlAccess.ExportValue("", "nvarchar").Kind);
        // 2026-10-29, review P1-11: a provider's own type is its invariant text.
        Assert.Equal("1.5", SqlExport.InvariantText(new Formatted(1.5)));
    }

    private static (XlsxKind, string?) Pair(XlsxValue value) => (value.Kind, value.Text);

    private sealed class Formatted(double value) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => value.ToString(formatProvider);
    }

    [Fact]
    public void Sqlite_DeclaredDates_ReadThroughSizesAndSpacing()
    {
        // 2026-10-29, review P5-11.
        Assert.Equal(true, SqliteAccess.DeclaredDate(" date "));
        Assert.Equal(false, SqliteAccess.DeclaredDate("DATETIME2(3)"));
        Assert.Equal(false, SqliteAccess.DeclaredDate("smalldatetime"));
        Assert.Equal(false, SqliteAccess.DeclaredDate("TIMESTAMP  WITH TIME ZONE"));
        Assert.Equal(false, SqliteAccess.DeclaredDate("timestamp without time zone"));
        Assert.Null(SqliteAccess.DeclaredDate("TEXT"));
        Assert.Null(SqliteAccess.DeclaredDate(null));
    }

    [Fact]
    public void DuckDb_DoublesAreNumbers_AndNanosecondsRead()
    {
        // 2026-10-29, review P5-2 and P5-10.
        var real = DuckDbAccess.FromText("0.30000000000000004", typeof(double));
        Assert.Equal((XlsxKind.Number, 0.30000000000000004), (real.Kind, real.Number));
        Assert.Equal(XlsxKind.DateTime, DuckDbAccess.FromText("2026-10-29 13:45:00.123456789", typeof(DateTime)).Kind);
        Assert.Equal(XlsxKind.Time, DuckDbAccess.FromText("08:30:00.123456789", typeof(TimeOnly)).Kind);
        Assert.Equal("2026-10-29 13:45:00.1234567", DuckDbAccess.SevenDigits("2026-10-29 13:45:00.123456789"));
        Assert.Equal("12.123456789", DuckDbAccess.SevenDigits("12.123456789"));   // no time: as it is
        Assert.Equal("10:00:00.123456789+02", DuckDbAccess.SevenDigits("10:00:00.123456789+02"));
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
        var job = new SqlExportJob(sink, (r, i, _) => XlsxValue.From(r.GetValue(i)));
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
