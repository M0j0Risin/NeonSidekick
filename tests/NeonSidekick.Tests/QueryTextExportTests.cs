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
    public void TheHeaderHook_TakesOffOnlyTheUtcItAdded()
    {
        using var reader = new FakeDataReader(["created_at", "x (UTC)", ""], ["timestamptz", "text", "timestamptz"]);
        static string Hook(System.Data.Common.DbDataReader r, int i) =>
            r.GetDataTypeName(i) == "timestamptz" ? (string.IsNullOrEmpty(r.GetName(i)) ? SqlText.UnnamedColumn(i) : r.GetName(i)) + SqlExport.UtcColumn : r.GetName(i);
        var utc = new HashSet<int>();
        var header = ServerTool<SqliteAccess>.TextHeader(Hook, utc)!;
        Assert.Equal(["created_at", "x (UTC)", SqlText.UnnamedColumn(2)], Enumerable.Range(0, 3).Select(i => header(reader, i)));
        Assert.Equal([0, 2], utc.Order());
        Assert.Null(ServerTool<SqliteAccess>.TextHeader(null, utc));
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
        target.ReleaseRowsOverride = () => throw new IOException("the disk is full");
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

    [Fact]
    public async Task ADecimalPast15Digits_KeepsEveryDigit_InACsvFromDuckDb()
    {
        string result = await Invoke(DuckDb(), ("sql", "SELECT 12345678901234.5678901234::DECIMAL(38,10) AS d"), ("to", "dec.csv"));
        Assert.StartsWith("Made dec.csv (", result);
        Assert.Equal("\uFEFFd\r\n12345678901234.5678901234\r\n", Text("dec.csv"));
    }
}
