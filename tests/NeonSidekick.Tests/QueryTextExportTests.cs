using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.DuckDb;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Office;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A query's rows to a CSV, JSON or JSON Lines file (2026-11-14, the database-tools plan's #13): the formula guard (text, not numbers), each
/// kind's spelling in both writers, the shapes and the byte-order-mark rule, a lone surrogate, the UTC header, the target's refusals and its
/// release, the checkpoint journal, and the round trip — each format written by <c>sqlite_query</c> and read back by <c>duckdb_query</c>.
/// </summary>
public sealed class QueryTextExportTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "querytext-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new() { SqliteTools = true, SqliteSandboxFiles = true, DuckDbTools = true, DuckDbSandboxFiles = true };
    private readonly SaveRoot _saveRoot;

    public QueryTextExportTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _saveRoot = new SaveRoot(_files, null);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "shop.db"), Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE things (id INTEGER, price REAL, name TEXT, note TEXT);
            INSERT INTO things VALUES (1, 2.5, 'plain', 'say "hi", then go');
            INSERT INTO things VALUES (2, -7, '-abc', '');
            INSERT INTO things VALUES (3, NULL, '=1+1', NULL);
            INSERT INTO things VALUES (4, 0.25, '-5', 'é <b>');
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private SqliteQueryTool Sqlite() =>
        new(new SqliteAccess(() => SqliteCatalog.Empty, () => _files), () => _settings) { ExportRoot = () => _saveRoot, Clock = _time };

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

    private Task<string> FromSqlite(string to, string sql = "SELECT * FROM things ORDER BY id", params (string Key, object? Value)[] more) =>
        Invoke(Sqlite(), [("sql", sql), ("database", "shop.db"), ("to", to), .. more]);

    private byte[] Bytes(string relative) => File.ReadAllBytes(Path.Combine(_root, relative));

    private string Text(string relative) => new UTF8Encoding(false).GetString(Bytes(relative));

    // ── the formula guard ───────────────────────────────────────────────────

    [Theory]
    [InlineData("-abc", "'-abc")]
    [InlineData("@x", "'@x")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1 555", "'+1 555")]
    [InlineData("\tx", "'\tx")]
    [InlineData("-5", "-5")]
    [InlineData("-1.5e3", "-1.5e3")]
    [InlineData("-1234567890123456789", "-1234567890123456789")]
    [InlineData("-١٢", "'-١٢")]   // Arabic-Indic digits are not [0-9]: still guarded
    [InlineData("plain", "plain")]
    public void TheGuard_TakesText_NotPlainNumbers(string value, string written) =>
        Assert.Equal(written, CsvCell.Guard(value, exemptNumbers: true));

    [Fact]
    public void WithTheExemptionOff_ANumberIsGuardedToo_AsTheDirectoryCsvHasIt()
    {
        Assert.Equal("'-5", CsvCell.Guard("-5", exemptNumbers: false));
        Assert.Equal("'-1", DirectoryCsv.Cell("-1"));
        Assert.Equal("\"a,b\"", CsvCell.Text("a,b", exemptNumbers: true));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvCell.Text("say \"hi\"", exemptNumbers: true));
    }

    // ── each kind, both writers ─────────────────────────────────────────────

    private static readonly XlsxValue[] EveryKind =
    [
        XlsxValue.OfInteger(42),
        XlsxValue.OfDecimal(12345678901234.5678901234m),
        XlsxValue.OfBool(true),
        XlsxValue.OfDate(new DateOnly(2026, 11, 14)),
        XlsxValue.OfDateTime(new DateTime(2026, 11, 14, 9, 30, 0)),
        XlsxValue.OfTime(new TimeOnly(12, 0, 0)),
        XlsxValue.OfTimeSpan(TimeSpan.FromHours(36)),
        XlsxValue.OfOffset(new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.FromHours(-6))),
        XlsxValue.OfBytes([0xCA, 0xFE]),
        XlsxValue.Blank,
        XlsxValue.EmptyText,
        XlsxValue.OfInteger(System.Numerics.BigInteger.Parse("12345678901234567890", CultureInfo.InvariantCulture)),
    ];

    private static readonly string[] EveryKindColumns = ["int", "dec", "bool", "date", "dt", "time", "dur", "offset", "bytes", "nul", "empty", "big"];

    private static string Write(Func<Stream, TextRows> make, IReadOnlyList<string> columns, params IReadOnlyList<XlsxValue>[] rows)
    {
        using var stream = new MemoryStream();
        using (var writer = make(stream))
        {
            Assert.Null(writer.Begin(columns));
            foreach (var row in rows)
            {
                Assert.True(writer.Add(row));
            }

            writer.Finish();
        }

        return new UTF8Encoding(false).GetString(stream.ToArray());
    }

    [Fact]
    public void TheCsv_SpellsEachKind()
    {
        string csv = Write(s => new CsvRows(s), EveryKindColumns, EveryKind);
        Assert.StartsWith("\uFEFFint,dec,", csv);   // the BOM, then the header
        string line = csv.Split("\r\n")[1];
        Assert.Equal("42,12345678901234.5678901234,true,2026-11-14,2026-11-14T09:30:00,12:00:00,36:00:00,2026-11-14T09:30:00-06:00,0xCAFE,,\"\",12345678901234567890", line);
        Assert.EndsWith("\r\n", csv);
    }

    [Fact]
    public void TheJson_SpellsEachKind_NullApartFromEmpty()
    {
        string json = Write(s => new JsonRows(s, lines: true), EveryKindColumns, EveryKind);
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        Assert.Equal(JsonValueKind.Number, o.GetProperty("int").ValueKind);
        Assert.Equal("12345678901234.5678901234", o.GetProperty("dec").GetRawText());   // every digit, not the double's
        Assert.True(o.GetProperty("bool").GetBoolean());
        Assert.Equal("2026-11-14", o.GetProperty("date").GetString());
        Assert.Equal("2026-11-14T09:30:00", o.GetProperty("dt").GetString());
        Assert.Equal("12:00:00", o.GetProperty("time").GetString());
        Assert.Equal("36:00:00", o.GetProperty("dur").GetString());
        Assert.Equal("2026-11-14T09:30:00-06:00", o.GetProperty("offset").GetString());
        Assert.Equal("0xCAFE", o.GetProperty("bytes").GetString());
        Assert.Equal(JsonValueKind.Null, o.GetProperty("nul").ValueKind);
        Assert.Equal("", o.GetProperty("empty").GetString());
        Assert.Equal("12345678901234567890", o.GetProperty("big").GetString());   // past 15 digits: a string, safe for JavaScript
    }

    [Fact]
    public void AFractionPast15Digits_KeepsItsDigits_ButAWorkbookStillReadsTheDouble()
    {
        var value = XlsxValue.OfNumberText("0.1234567890123456789012");
        Assert.Equal(XlsxKind.Number, value.Kind);
        Assert.Equal("0.1234567890123456789012", value.Exact);
        Assert.NotEqual(value.Exact, value.Text);
        Assert.Null(XlsxValue.OfNumberText("2.5").Exact);
    }

    [Fact]
    public void ALoneSurrogate_IsReplaced_APairKept_InValuesAndKeys()
    {
        var row = new[] { XlsxValue.OfText("a\uD800b"), XlsxValue.OfText("😀") };
        string[] columns = ["x\uDC00", "y"];
        string csv = Write(s => new CsvRows(s), columns, row);
        Assert.Contains("x�,y\r\na�b,😀\r\n", csv);
        string json = Write(s => new JsonRows(s, lines: true), columns, row);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("a�b", doc.RootElement.GetProperty("x�").GetString());
        Assert.Equal("😀", doc.RootElement.GetProperty("y").GetString());
        Assert.Equal("a�b", XlsxValue.WellFormed("a\uD800b"));
    }

    [Fact]
    public void ARepeatedOrBlankName_IsMadeUnique_CaseBlind()
    {
        string json = Write(s => new JsonRows(s, lines: true), ["Id", "id", ""], [XlsxValue.OfInteger(1), XlsxValue.OfInteger(2), XlsxValue.OfInteger(3)]);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["Id", "id (2)", SqlText.UnnamedColumn(2)], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void AUtcColumn_IsWrittenWithAZ()
    {
        var when = XlsxValue.OfDateTime(new DateTime(2026, 11, 14, 9, 30, 0));
        Assert.Contains("2026-11-14T09:30:00Z", Write(s => new CsvRows(s, new HashSet<int> { 0 }), ["created_at"], [when]));
        Assert.Contains("\"2026-11-14T09:30:00Z\"", Write(s => new JsonRows(s, lines: true, new HashSet<int> { 0 }), ["created_at"], [when]));
    }

    [Fact]
    public async Task TheUtcHook_MarksATextFilesColumn_AndKeepsEveryName()
    {
        // 2026-11-15, the database-tools review's #46: the family says which columns are UTC; a text file keeps the names (a user's own
        // "x (UTC)" too) and writes a Z, a workbook's header adds the suffix. The preview shows the file's spelling.
        var when = new DateTime(2026, 11, 14, 9, 30, 0);
        static bool IsUtc(System.Data.Common.DbDataReader r, int i) => r.GetDataTypeName(i) == "timestamptz";
        using var stream = new MemoryStream();
        using (var csv = new CsvRows(stream))
        {
            using var reader = new FakeDataReader(["created_at", "x (UTC)", ""], ["timestamptz", "text", "timestamptz"], [when, "a", when]);
            var job = new SqlExportJob(csv, (r, i, _, _) => XlsxValue.From(r.GetValue(i)), IsUtc);
            var grid = await job.ReadAsync(reader, 10, CancellationToken.None);
            Assert.Equal(["2026-11-14T09:30:00Z", "a", "2026-11-14T09:30:00Z"], grid.Rows[0]);
            csv.Finish();
        }

        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).TrimStart('\uFEFF').Split("\r\n");
        Assert.Equal("created_at,x (UTC)," + SqlText.UnnamedColumn(2), lines[0]);
        Assert.Equal("2026-11-14T09:30:00Z,a,2026-11-14T09:30:00Z", lines[1]);

        var sheet = new HeaderRows();
        using var again = new FakeDataReader(["created_at"], ["timestamptz"], [when]);
        await new SqlExportJob(sheet, (r, i, _, _) => XlsxValue.From(r.GetValue(i)), IsUtc).ReadAsync(again, 10, CancellationToken.None);
        Assert.Equal(["created_at" + SqlExport.UtcColumn], sheet.Header);
    }

    /// <summary>A sheet that keeps the header it was given.</summary>
    private sealed class HeaderRows : ISheetRows
    {
        public IReadOnlyList<string> Header { get; private set; } = [];

        public string? Begin(IReadOnlyList<string> columns)
        {
            Header = [.. columns];
            return null;
        }

        public bool Add(IReadOnlyList<XlsxValue> row) => true;
    }

    /// <summary>A writer whose close throws after it lets go, as a full disk's flush does.</summary>
    private sealed class ThrowingDispose(IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            throw new IOException("the disk is full");
        }
    }

    // ── shapes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACsvExport_HasTheBom_Crlf_TheGuard_AndTheQuotes()
    {
        string result = await FromSqlite("out.csv");
        Assert.StartsWith("Made out.csv (", result);
        Assert.Contains("): 4 rows × 4 columns", result);
        Assert.Contains("\n\nThe first 4 rows:\n\n", result);
        byte[] bytes = Bytes("out.csv");
        Assert.Equal(Bom, bytes[..3]);
        string csv = Text("out.csv");
        Assert.Equal(
            "\uFEFFid,price,name,note\r\n1,2.5,plain,\"say \"\"hi\"\", then go\"\r\n2,-7,'-abc,\"\"\r\n3,,'=1+1,\r\n4,0.25,-5,é <b>\r\n",
            csv);
    }

    [Fact]
    public async Task AJsonExport_IsAnIndentedArray_AndJsonLinesOneObjectALine_NeitherWithABom()
    {
        await FromSqlite("out.json");
        await FromSqlite("out.jsonl");
        Assert.NotEqual(Bom, Bytes("out.json")[..3]);
        Assert.NotEqual(Bom, Bytes("out.jsonl")[..3]);
        string json = Text("out.json");
        Assert.StartsWith("[\n  {\n    \"id\": 1,", json);
        Assert.EndsWith("]\n", json);
        Assert.DoesNotContain("\r", json);
        Assert.Contains("\"note\": \"é <b>\"", json);   // the relaxed escaping
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal(4, doc.RootElement.GetArrayLength());
        }

        string[] lines = Text("out.jsonl").Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Equal("", lines[^1]);
        Assert.Equal("{\"id\":1,\"price\":2.5,\"name\":\"plain\",\"note\":\"say \\\"hi\\\", then go\"}", lines[0]);
        Assert.Equal("{\"id\":3,\"price\":null,\"name\":\"=1+1\",\"note\":null}", lines[2]);   // no guard in JSON: it is never a spreadsheet
    }

    [Theory]
    [InlineData("none.csv", "No rows: the file holds the header alone.", "\uFEFFid,price,name,note\r\n")]
    [InlineData("none.json", "No rows: the file holds an empty array.", "[]\n")]
    [InlineData("none.jsonl", "No rows, so the file is empty.", "")]
    public async Task NoRows_StillWritesTheFile_WordedForIt(string to, string said, string content)
    {
        string result = await FromSqlite(to, "SELECT * FROM things WHERE id > 99");
        Assert.EndsWith(said, result);
        Assert.Equal(content, Text(to));
    }

    // ── names and refusals ──────────────────────────────────────────────────

    [Fact]
    public async Task OutJson_StaysJson_AndTheExtensionIsMatchedInAnyCase()
    {
        await FromSqlite("out.json");
        Assert.True(File.Exists(Path.Combine(_root, "out.json")));
        Assert.False(File.Exists(Path.Combine(_root, "out.json.xlsx")));
        Assert.StartsWith("Made OUT.CSV (", await FromSqlite("OUT.CSV"));
        Assert.Equal(Bom, Bytes("OUT.CSV")[..3]);
    }

    [Theory]
    [InlineData("out.tsv")]
    [InlineData("out.docx")]
    [InlineData("out.pdf")]
    public async Task AnotherKnownType_IsRefused_NamingTheFour(string to) =>
        Assert.Equal(SqlExportText.WrongTarget(to), await FromSqlite(to));

    [Fact]
    public async Task TheLockFilePrefix_IsRefused_ForACsvToo() =>
        Assert.Equal(OfficeText.ReservedName("~$out.csv"), await FromSqlite("~$out.csv"));

    [Fact]
    public async Task ASheet_WithACsv_IsRefused() =>
        Assert.Equal(OfficeText.SheetNeedsXlsx(".csv"), await FromSqlite("out.csv", more: ("sheet", "Q")));

    [Fact]
    public async Task AnExistingCsv_NeedsOverwrite_ThenIsReplaced()
    {
        File.WriteAllText(Path.Combine(_root, "out.csv"), "old");
        Assert.Equal(OfficeText.TextFileExists("out.csv"), await FromSqlite("out.csv"));
        Assert.Equal("old", Text("out.csv"));
        Assert.StartsWith("Replaced out.csv (", await FromSqlite("out.csv", more: ("overwrite", true)));
        Assert.StartsWith("\uFEFFid,", Text("out.csv"));
    }

    [Fact]
    public async Task AFolderNamedLikeAFile_StillMeansAWorkbookInside()
    {
        Directory.CreateDirectory(Path.Combine(_root, "exports.csv"));
        string result = await FromSqlite("exports.csv");
        Assert.StartsWith("Made exports.csv", result);
        Assert.Contains("query-shop-", result);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "exports.csv"), "*.xlsx"));
    }

    [Fact]
    public void TheTarget_LetsGoOfThePendingFile_WhenTheWriterThrowsOnRelease()
    {
        var target = TextTableTarget.Plan(_files, "out.csv", TextTableFormat.Csv, null, overwrite: false, out string? refusal)!;
        Assert.Null(refusal);
        var rows = target.Begin(out refusal)!;
        Assert.Null(rows.Begin(["a"]));
        target.WrapWriter(writer => new ThrowingDispose(writer));
        target.Dispose();
        Assert.Empty(Directory.GetFiles(_root, "*.neon-tmp*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_root, "out.csv")));
    }

    [Fact]
    public void ACsvExport_IsJournaled_SoUndoTakesItBack()
    {
        using var rig = new CheckpointRig();
        rig.Turn(files =>
        {
            using var target = TextTableTarget.Plan(files, "made.csv", TextTableFormat.Csv, null, overwrite: false, out _)!;
            var rows = target.Begin(out _)!;
            rows.Begin(["a"]);
            rows.Add([XlsxValue.OfInteger(1)]);
            Assert.StartsWith("Made made.csv (", target.Commit().Answer);
        });

        Assert.Contains(Assert.Single(rig.Journal!.Logs).Entries, e => e is { Op: CheckpointOp.Absent, Path: "made.csv" });
    }

    // ── the round trip ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("rt.csv")]
    [InlineData("rt.jsonl")]
    [InlineData("rt.json")]
    public async Task EachFormat_ReadsBackThroughDuckDb(string to)
    {
        await FromSqlite(to, "SELECT id, price, name FROM things WHERE id IN (1, 4) ORDER BY id");
        string result = await Invoke(DuckDb(), ("sql", $"SELECT id, price, name, typeof(id) AS t FROM '{to}' ORDER BY id"));
        // The first column's name reads id (no BOM in it), the numbers as numbers, the text as written.
        Assert.Contains("| id | price | name | t |", result);
        Assert.Contains("| 1 | 2.5 | plain | BIGINT |", result);
        Assert.Contains("| 4 | 0.25 | -5 | BIGINT |", result);
    }

    // ── the review's fixes (2026-11-15) ─────────────────────────────────────

    [Theory]
    [InlineData("out.txt")]
    [InlineData("out.parquet")]
    [InlineData("out.md")]
    [InlineData("out.TSV")]
    public async Task ADataTypeAQueryDoesntWrite_IsRefused_NotMadeAWorkbook(string to)
    {
        // #38: out.txt was out.txt.xlsx.
        Assert.Equal(SqlExportText.WrongTarget(to), await FromSqlite(to));
        Assert.Empty(Directory.GetFiles(_root, "out*"));
    }

    [Fact]
    public async Task AnNdjsonName_IsJsonLines_AndKeepsItsName()
    {
        // #45: one table of names; .ndjson reads as JSON Lines, the messages still say .jsonl.
        Assert.Equal(TextTableFormat.JsonLines, TextTableTarget.FormatOf("rows.NDJSON"));
        Assert.Equal(".jsonl", TextTableTarget.ExtensionOf(TextTableFormat.JsonLines));
        Assert.Equal([".xlsx", ".csv", ".json", ".jsonl", ".ndjson"], OutputNames.QueryKinds);
        Assert.StartsWith("Made rows.ndjson (", await FromSqlite("rows.ndjson"));
        Assert.Equal(4, Text("rows.ndjson").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void TheLockFilePrefix_IsJudgedOnItsOwn()
    {
        // #90: no longer told apart by the refusal's wording.
        Assert.True(OutputNames.IsReservedName("~$a.csv"));
        Assert.True(OutputNames.IsReservedName("sub/~$b.xlsx"));
        Assert.False(OutputNames.IsReservedName("a~$.csv"));
        Assert.Equal(OfficeText.ReservedName("~$a.csv"), OutputNames.QueryRefusal("~$a.csv", intoFolder: false));
        Assert.Null(OutputNames.QueryRefusal("~$a.csv", intoFolder: true));
    }

    [Fact]
    public async Task ACsvCell_KeepsTextExcelWouldCut()
    {
        // #32: 40,000 characters, past Excel's 32,767, written whole; the workbook still cuts.
        string sql = "SELECT replace(hex(zeroblob(20000)), '00', 'ab') AS big";
        Assert.DoesNotContain("cell cut", await FromSqlite("big.csv", sql));
        Assert.Equal(40_000, Text("big.csv").Split("\r\n")[1].Length);
        Assert.Contains("1 cell cut at", await FromSqlite("big.xlsx", sql));
    }

    [Fact]
    public void TheNotes_SayTheRowCapIsAWorkbooks_AndTheCellCapIsTheFiles()
    {
        string notes = OfficeText.TextNotes(new SheetResult(5, 1, 2, true, ""));
        Assert.Contains("(a workbook's limit)", notes);   // #14
        Assert.Contains("cut at " + TextRows.CellCap.ToString("N0", CultureInfo.InvariantCulture) + " characters", notes);
        Assert.StartsWith("csv writer of out.csv failed to close", OfficeText.ReleaseFailedLogLine("csv", "writer", "out.csv", "IOException", "full"));   // #133
    }

    [Theory]
    [InlineData("007", false)]
    [InlineData("-007", false)]
    [InlineData("0", true)]
    [InlineData("-0.5", true)]
    [InlineData("0.5", true)]
    [InlineData("1E+20", true)]
    [InlineData("5\n", false)]
    [InlineData(".5", false)]
    public void AJsonNumber_IsThePlainGrammar_WithNoLeadingZero(string text, bool number) =>
        Assert.Equal(number, JsonRows.IsJsonNumber(text));   // #78/#134/#122

    [Fact]
    public void ATrailingLineFeed_IsNoPlainNumber()
    {
        Assert.False(CsvCell.IsPlainNumber("5\n"));   // #122: $ matched before it
        Assert.Equal("'-5\n", CsvCell.Guard("-5\n", exemptNumbers: true));
    }

    [Fact]
    public void AnExactSpellingJsonCantRead_IsAString()
    {
        var odd = XlsxValue.OfDouble(7) with { Exact = "007" };
        string json = Write(s => new JsonRows(s, lines: true), ["n", "m"], [odd, XlsxValue.OfDecimal(0.5m)]);
        Assert.Equal("{\"n\":\"007\",\"m\":0.5}\n", json);
    }

    [Fact]
    public void NamesAreMadeWellFormed_BeforeTheyAreMadeUnique()
    {
        // #113: two names that differ only in a lone surrogate became one key.
        string json = Write(s => new JsonRows(s, lines: true), ["a\uD800", "a\uDBFF"], [XlsxValue.OfInteger(1), XlsxValue.OfInteger(2)]);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["a\uFFFD", "a\uFFFD (2)"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void ANegativeDuration_IsSigned_AndNotGuarded()
    {
        // #144: it was c text (-1.02:00:00), which the guard then prefixed.
        Assert.Equal("-26:00:00", CsvRows.Field(XlsxValue.From(TimeSpan.FromHours(-26)), utc: false));
    }

    [Fact]
    public void JsonLines_ManyRows_AllReachTheFile()
    {
        // #61: rows gather in a buffer and drain past 64 KB, never one flush a row.
        var rows = Enumerable.Range(0, 5000).Select(i => (IReadOnlyList<XlsxValue>)[XlsxValue.OfInteger(i), XlsxValue.OfText(new string('x', 40))]).ToArray();
        string json = Write(s => new JsonRows(s, lines: true), ["i", "t"], rows);
        string[] lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5000, lines.Length);
        Assert.StartsWith("{\"i\":4999,", lines[^1]);
    }

    [Fact]
    public void ThePreview_OfATextFile_ShowsTheFilesSpelling()
    {
        // #30/#132: the answer's rows read as the file does.
        var when = XlsxValue.OfDateTime(new DateTime(2026, 11, 14, 9, 30, 0));
        Assert.Equal("2026-11-14T09:30:00Z", SqlExport.Show(when, asText: true, utc: true));
        var exact = XlsxValue.OfDecimal(12345678901234.5678901234m);
        Assert.Equal("12345678901234.5678901234", SqlExport.Show(exact, asText: true));
        Assert.Equal("-26:00:00", SqlExport.Show(XlsxValue.From(TimeSpan.FromHours(-26))));
    }

    [Fact]
    public void ASqliteDateColumnsTimestamp_KeepsItsTime()
    {
        // #151: a DATE column's "… 00:00:00" was made a date; only the ten characters of a date are one.
        using var reader = new FakeDataReader(["d"], ["DATE"], ["2026-11-14 00:00:00"], ["2026-11-14"]);
        Assert.True(reader.Read());
        Assert.Equal(XlsxKind.DateTime, SqliteAccess.ExportCell(reader, 0, "DATE").Kind);
        Assert.True(reader.Read());
        Assert.Equal(XlsxKind.Date, SqliteAccess.ExportCell(reader, 0, "DATE").Kind);
    }

    [Fact]
    public void Commit_LetsGoOfTheWriter_BeforeTheFileIsPutInPlace()
    {
        // #18/#130: the writer was released only at Dispose, after the commit.
        using var target = TextTableTarget.Plan(_files, "made.csv", TextTableFormat.Csv, null, overwrite: false, out _)!;
        var rows = target.Begin(out _)!;
        rows.Begin(["a"]);
        rows.Add([XlsxValue.OfInteger(1)]);
        var counted = new CountingDispose();
        target.WrapWriter(writer => counted.Over(writer));
        Assert.StartsWith("Made made.csv (", target.Commit().Answer);
        Assert.Equal(1, counted.Count);
        target.Dispose();
        Assert.Equal(1, counted.Count);
        Assert.Equal("\uFEFFa\r\n1\r\n", Text("made.csv"));
    }

    private sealed class CountingDispose
    {
        public int Count { get; private set; }

        public IDisposable Over(IDisposable inner) => new Wrapped(this, inner);

        private sealed class Wrapped(CountingDispose owner, IDisposable inner) : IDisposable
        {
            public void Dispose()
            {
                owner.Count++;
                inner.Dispose();
            }
        }
    }

    [Fact]
    public async Task ADecimalPast15Digits_KeepsEveryDigit_InACsvFromDuckDb()
    {
        string result = await Invoke(DuckDb(), ("sql", "SELECT 12345678901234.5678901234::DECIMAL(38,10) AS d"), ("to", "dec.csv"));
        Assert.StartsWith("Made dec.csv (", result);
        Assert.Equal("\uFEFFd\r\n12345678901234.5678901234\r\n", Text("dec.csv"));
    }
}
