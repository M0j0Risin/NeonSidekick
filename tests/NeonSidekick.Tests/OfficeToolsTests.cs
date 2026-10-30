using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Office;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Office writers as the model and the user reach them (2026-10-29, the Office writing plan): <c>convert_to_xlsx</c> (a table, a CSV,
/// TSV or JSON file; a sheet added or replaced; the target rules), <c>convert_to_docx</c>, <c>/docx</c>'s parsing and completion, and
/// <c>write_file</c>'s refusal of an Office path.
/// </summary>
public sealed class OfficeToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "officetools-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly ConvertToXlsxTool _xlsx;
    private readonly ConvertToDocxTool _docx;

    public OfficeToolsTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _xlsx = new ConvertToXlsxTool(_files, _time);
        _docx = new ConvertToDocxTool(_files, new DocxConverter(_files, _time));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Put(string relative, string text) => File.WriteAllText(Path.Combine(_root, relative), text, new UTF8Encoding(false));

    private byte[] Bytes(string relative) => File.ReadAllBytes(Path.Combine(_root, relative));

    private string Stamp => _time.GetLocalNow().ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static AIFunctionArguments Args(params (string Key, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in pairs)
        {
            arguments[key] = value;
        }

        return arguments;
    }

    private async Task<string> Xlsx(params (string, object?)[] pairs) => (string)(await _xlsx.InvokeAsync(Args(pairs)))!;

    private async Task<string> Docx(params (string, object?)[] pairs) => (string)(await _docx.InvokeAsync(Args(pairs)))!;

    private static string Sheets(byte[] book) => OfficeWriterTests.Read(book);

    private static string Cells(byte[] book, string sheet) => OfficeWriterTests.Read(book, new OfficeRequest(Sheet: sheet));

    // ── convert_to_xlsx ─────────────────────────────────────────────────────

    [Fact]
    public async Task ATable_MakesATypedWorkbook_NamedTableAndTheMinute()
    {
        string result = await Xlsx(("columns", Json("""["Name","Count","When","Ok"]""")), ("rows", Json("""[["a",1,"2026-10-29",true],["b",2.5,"2026-10-30",false],["c",null,"",true]]""")));
        string name = "table-" + Stamp + ".xlsx";
        Assert.StartsWith($"Made {name} (", result);
        Assert.Contains("sheet \"Sheet1\": 3 rows × 4 columns", result);
        byte[] book = Bytes(name);
        OfficeSchema.ValidWorkbook(book);
        string sheet = Part(book, "xl/worksheets/sheet1.xml");
        Assert.Contains("<v>2.5</v>", sheet);
        Assert.Contains("t=\"b\"", sheet);
        Assert.DoesNotContain("<f>", sheet);
        Assert.Contains("2026-10-30", Cells(book, "Sheet1"));
    }

    private static string Part(byte[] book, string name) => OfficeWriterTests.Part(book, name);

    [Fact]
    public async Task ACsv_GoesBesideIt_ItsSheetNamedForIt_ZipCodesStayText()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Put("data/sales.csv", "zip,amount,note\n01234,10,\"a, b\"\n98765,20,\"line\nbreak\"\n");
        string result = await Xlsx(("path", "data/sales.csv"));
        Assert.StartsWith("Made " + Path.Combine("data", "sales.xlsx"), result);
        Assert.Contains("sheet \"sales\": 2 rows × 3 columns", result);
        byte[] book = Bytes("data/sales.xlsx");
        OfficeSchema.ValidWorkbook(book);
        string cells = Cells(book, "sales");
        Assert.Contains("01234", cells);
        Assert.Contains("a, b", cells);
        Assert.Contains("<v>10</v>", Part(book, "xl/worksheets/sheet1.xml"));
    }

    [Fact]
    public async Task TsvAndJson_AreTablesToo()
    {
        Put("t.tsv", "a\tb\n1\tx\n");
        Put("objects.json", """[{"a":1,"b":"x"},{"a":2,"c":true}]""");
        Put("arrays.json", """[["a","b"],[1,"x"]]""");
        Assert.Contains("1 row × 2 columns", await Xlsx(("path", "t.tsv")));
        Assert.Contains("2 rows × 3 columns", await Xlsx(("path", "objects.json")));
        Assert.Contains("1 row × 2 columns", await Xlsx(("path", "arrays.json")));
        OfficeSchema.ValidWorkbook(Bytes("objects.xlsx"));
    }

    [Fact]
    public async Task ASheet_IsAdded_ThenReplacedOnlyWithOverwrite()
    {
        var columns = Json("""["a"]""");
        Assert.StartsWith("Made book.xlsx", await Xlsx(("columns", columns), ("rows", Json("[[1]]")), ("to", "book.xlsx"), ("sheet", "One")));
        Assert.StartsWith("Added sheet \"Two\" to book.xlsx", await Xlsx(("columns", columns), ("rows", Json("[[2]]")), ("to", "book.xlsx"), ("sheet", "Two")));
        Assert.Equal(OfficeText.SheetExists("book.xlsx", "Two"), await Xlsx(("columns", columns), ("rows", Json("[[3]]")), ("to", "book.xlsx"), ("sheet", "two")));
        Assert.StartsWith("Replaced sheet \"Two\" in book.xlsx", await Xlsx(("columns", columns), ("rows", Json("[[3],[4]]")), ("to", "book.xlsx"), ("sheet", "two"), ("overwrite", true)));
        byte[] book = Bytes("book.xlsx");
        OfficeSchema.ValidWorkbook(book);
        string sheets = Sheets(book);
        Assert.Contains("One", sheets);
        Assert.Contains("Two", sheets);
        Assert.Equal(OfficeText.WorkbookExists("book.xlsx"), await Xlsx(("columns", columns), ("rows", Json("[[5]]")), ("to", "book.xlsx")));
        Assert.StartsWith("Replaced book.xlsx", await Xlsx(("columns", columns), ("rows", Json("[[5]]")), ("to", "book.xlsx"), ("overwrite", true)));
        Assert.DoesNotContain("Two", Sheets(Bytes("book.xlsx")));
    }

    [Fact]
    public async Task AFileThatIsNotAWorkbook_IsNeverAddedTo()
    {
        Put("fake.xlsx", "just text");
        var columns = Json("""["a"]""");
        Assert.StartsWith("Error:", await Xlsx(("columns", columns), ("rows", Json("[[1]]")), ("to", "fake.xlsx"), ("sheet", "S")));
        Assert.Equal("just text", File.ReadAllText(Path.Combine(_root, "fake.xlsx")));
        Assert.StartsWith("Replaced fake.xlsx", await Xlsx(("columns", columns), ("rows", Json("[[1]]")), ("to", "fake.xlsx"), ("overwrite", true)));
        OfficeSchema.ValidWorkbook(Bytes("fake.xlsx"));
    }

    [Fact]
    public async Task AnUnnamedDefault_NeverReplaces()
    {
        Put("t.csv", "a\n1\n");
        Put("t.xlsx", "old");
        Assert.StartsWith("Made t-2.xlsx", await Xlsx(("path", "t.csv")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "t.xlsx")));
    }

    [Theory]
    [InlineData("out.xlsm")]
    [InlineData("out.pdf")]
    [InlineData("out.csv")]
    [InlineData("~$out.xlsx")]
    public async Task ATargetOfAnotherKind_IsRefused(string to)
    {
        Assert.StartsWith("Error:", await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[1]]")), ("to", to)));
        Assert.False(File.Exists(Path.Combine(_root, to)));
    }

    [Fact]
    public async Task AnEnding_GetsXlsxAdded()
    {
        Assert.StartsWith("Made report.v2.xlsx", await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[1]]")), ("to", "report.v2")));
    }

    [Fact]
    public async Task TheArguments_AreChecked()
    {
        Put("notes.txt", "x");
        Assert.Equal(OfficeText.OneTableSource, await Xlsx());
        Assert.Equal(OfficeText.OneTableSource, await Xlsx(("path", "notes.txt"), ("columns", Json("""["a"]""")), ("rows", Json("[]"))));
        Assert.Equal(OfficeText.ColumnsAndRows, await Xlsx(("columns", Json("""["a"]"""))));
        Assert.Equal(OfficeText.NotATableFile("notes.txt"), await Xlsx(("path", "notes.txt")));
        Assert.Equal(OfficeText.RowTooLong(1, 2, 1), await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[1,2]]"))));
        Assert.Equal(OfficeText.BadCell(1, 1), await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("""[[{"x":1}]]"""))));
        Assert.Equal(OfficeText.BadSheetName("a/b"), await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[1]]")), ("sheet", "a/b")));
        Assert.Equal(FileText.PrunedStubSent(ConvertToXlsxTool.RowsArgument), await Xlsx(("columns", Json("""["a"]""")), ("rows", Json($$"""[["{{ConversationCompactor.PrunedArgumentStub(500)}}"]]"""))));
        Assert.Empty(Directory.GetFiles(_root, "*.xlsx"));
    }

    [Fact]
    public async Task RowsSentAsText_AreReadAsJson()
    {
        Assert.Contains("1 row × 1 column", await Xlsx(("columns", Json("""["a"]""")), ("rows", "[[1]]")));
    }

    [Fact]
    public async Task TheTablesArguments_SayWhatIsWrong()
    {
        // 2026-10-29, review P2-3/P5-12, P6-2, P7-9.
        Assert.Contains("1 row × 1 column", await Xlsx(("columns", """["a,b"]"""), ("rows", "[[1]]"), ("to", "one.xlsx")));   // a JSON array as text: one name
        Assert.Equal(OfficeText.BadTableArgument(ConvertToXlsxTool.ColumnsArgument), await Xlsx(("columns", Json("[1]")), ("rows", Json("[[1]]"))));
        Assert.Equal(OfficeText.NoTableColumns, await Xlsx(("columns", Json("[]")), ("rows", Json("[[1]]"))));
        Assert.Equal(OfficeText.RowNotArray(2), await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[1], 2]"))));
        Put("keyless.json", "[{}, {}]");
        Assert.Equal(OfficeText.EmptyTable("keyless.json"), await Xlsx(("path", "keyless.json")));
        Put("mixed.json", "[{}, []]");
        Assert.Equal(OfficeText.BadJson("mixed.json", OfficeText.JsonMixedItems), await Xlsx(("path", "mixed.json")));
        Put("object.json", "{}");
        Assert.Equal(OfficeText.BadJson("object.json", OfficeText.JsonNotArray), await Xlsx(("path", "object.json")));
        Assert.Single(Directory.GetFiles(_root, "*.xlsx"));
    }

    [Fact]
    public async Task AQuoteThatNeverCloses_IsNamedByItsRow_AndALongFieldIsCut()
    {
        // 2026-10-29, review P4-2.
        Put("open.csv", "a,b\n1,2\n\"3,4\n5,6\n");
        Assert.Equal(OfficeText.UnclosedQuote("open.csv", 3), await Xlsx(("path", "open.csv")));
        Assert.Empty(Directory.GetFiles(_root, "*.xlsx"));

        Put("long.csv", "a,b\n" + new string('x', 40_000) + ",1\n");
        Assert.StartsWith("Made long.xlsx", await Xlsx(("path", "long.csv")));
        var field = TableSource.Delimited(new StringReader("\"" + new string('y', 40_000) + "\",2"), ',').Single();
        Assert.Equal((XlsxValue.MaxChars + 1, "2"), (field[0].Length, field[1]));
        var tsv = TableSource.Lines(new StringReader("a\t" + new string('z', 40_000) + "\r\n\r\nb\tc")).ToList();
        Assert.Equal(2, tsv.Count);   // the blank line skipped
        Assert.Equal(XlsxValue.MaxChars + 1, tsv[0][1].Length);
        var wide = TableSource.Delimited(new StringReader(new string(',', CellReference.MaxColumn + 50)), ',').Single();
        Assert.Equal(CellReference.MaxColumn + 1, wide.Count);
    }

    [Theory]
    [InlineData("a,b", ',')]
    [InlineData("a;b", ';')]
    [InlineData("\"Smith, J\";Age", ';')]
    [InlineData("a;b,c", ',')]
    [InlineData("a;b;c,d", ';')]
    [InlineData("", ',')]
    public void TheDelimiter_IsCountedOutsideQuotes_ATieStaysComma(string first, char expected)
    {
        // 2026-10-29, review P1-16.
        Assert.Equal(expected, TableSource.Delimiter(first));
    }

    [Fact]
    public void TheEncoding_IsJudgedOverTheWholeFile()
    {
        // 2026-10-29, review P4-3: ASCII past the 64 KB head, then a Windows-1252 é.
        var late = new List<byte>(Encoding.ASCII.GetBytes("name\n" + new string('a', 70_000) + "\ncaf"));
        late.Add(0xE9);
        Assert.NotEqual(TextForm.Utf8, TextFile.Detect(new MemoryStream([.. late])).Form);
        // A UTF-8 character across the 64 KB read is still UTF-8.
        byte[] straddle = Encoding.UTF8.GetBytes(new string('a', 64 * 1024 - 1) + "é and more");
        var stream = new MemoryStream(straddle);
        Assert.Equal(TextForm.Utf8, TextFile.Detect(stream).Form);
        Assert.Equal(0, stream.Position);
    }

    /// <summary>A sheet that counts what it is given and is full after <paramref name="room"/> rows.</summary>
    private sealed class CountingSheet(int room) : ISheetRows
    {
        public bool Begun { get; private set; }

        public int Added { get; private set; }

        public string? Begin(IReadOnlyList<string> columns)
        {
            Begun = true;
            return null;
        }

        public bool Add(IReadOnlyList<XlsxValue> row) => ++Added <= room;
    }

    [Fact]
    public void TheJudgingPass_StopsPastTheLastRow_AndAChangedSourceIsRefused()
    {
        // 2026-10-29, review P4-4 and P1-16.
        int read = 0;
        IEnumerable<List<string>> Endless()
        {
            for (int i = 0; ; i++)
            {
                read++;
                yield return [i.ToString(CultureInfo.InvariantCulture)];
            }
        }

        var sheet = new CountingSheet(3);
        Assert.Null(TableSource.Rows(Endless, "x.csv", sheet, CancellationToken.None, maxRows: 3));
        Assert.Equal(4, sheet.Added);   // the fourth row refused: the sheet says it was cut
        Assert.True(read <= 10, read.ToString(CultureInfo.InvariantCulture));

        var before = new CountingSheet(10);
        Assert.Equal(OfficeText.ChangedWhileRead("x.csv"), TableSource.Rows(() => Endless().Take(3), "x.csv", before, CancellationToken.None, () => false));
        Assert.False(before.Begun);
        int asked = 0;
        var after = new CountingSheet(10);
        Assert.Equal(OfficeText.ChangedWhileRead("x.csv"), TableSource.Rows(() => Endless().Take(3), "x.csv", after, CancellationToken.None, () => ++asked == 1));
        Assert.Equal(2, after.Added);
        // A longer file the second time is written only as far as the first pass judged it.
        int pass = 0;
        var grown = new CountingSheet(10);
        Assert.Null(TableSource.Rows(() => Endless().Take(++pass == 1 ? 3 : 6), "x.csv", grown, CancellationToken.None));
        Assert.Equal(2, grown.Added);
    }

    [Fact]
    public async Task JsonObjects_TakeEachKeysLastValue_InFirstSeenOrder()
    {
        // 2026-10-29, review P4-6.
        Put("people.json", """[{"b": 1, "a": 2}, {"c": 3, "b": 4, "b": 5}]""");
        Assert.Contains("2 rows × 3 columns", await Xlsx(("path", "people.json")));
        string read = Cells(Bytes("people.xlsx"), "people");
        Assert.Contains("| b | a | c |", read, StringComparison.Ordinal);
        Assert.Contains("| 5 |", read, StringComparison.Ordinal);
        Assert.DoesNotContain("| 4 |", read, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task AWorkbookHeldShut_IsASentence_NotAToolError()
    {
        // 2026-10-29, review P7-1: Plan opens the workbook to add to; what it throws is now the tool's answer.
        await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[1]]")), ("to", "held.xlsx"));
        using (new FileStream(Path.Combine(_root, "held.xlsx"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            string answer = await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[2]]")), ("to", "held.xlsx"), ("sheet", "More"));
            Assert.False(string.IsNullOrWhiteSpace(answer));
            Assert.DoesNotContain("Made", answer, StringComparison.Ordinal);
        }
    }

    [WindowsFact]
    public async Task AWorkbookOpenInExcel_IsSaidToBeOpen_WhenReplaced()
    {
        // 2026-10-29, review P1-5: Excel holds its file ReadWrite and shares Read, so the replace fails with an access denial, which was
        // read as "access denied" rather than "close it there".
        await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[1]]")), ("to", "open.xlsx"));
        byte[] before = Bytes("open.xlsx");
        using (new FileStream(Path.Combine(_root, "open.xlsx"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Assert.Equal(OfficeText.OpenElsewhere("open.xlsx"), await Xlsx(("columns", Json("""["a"]""")), ("rows", Json("[[2]]")), ("to", "open.xlsx"), ("overwrite", true)));
        }

        Assert.Equal(before, Bytes("open.xlsx"));
        Assert.Single(Directory.GetFiles(_root));   // no temp sibling left
    }

    [Fact]
    public void NoToolsSchema_TypesAPropertyAsAList()
    {
        // 2026-10-29, review P7-2: some servers' grammars refuse "type": [..]; every file tool and a query tool's two schemas say one type.
        foreach (var tool in App.ChatScreen.FileTools(_files, () => true, _ => { }, () => new Settings.AppSettingsData()))
        {
            NoListType(tool.JsonSchema, tool.Name);
        }
    }

    /// <summary>Fails when <paramref name="node"/> holds a <c>"type"</c> given as a list.</summary>
    internal static void NoListType(JsonElement node, string tool)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                Assert.False(property.Name == "type" && property.Value.ValueKind == JsonValueKind.Array, tool);
                NoListType(property.Value, tool);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                NoListType(item, tool);
            }
        }
    }

    // ── convert_to_docx ─────────────────────────────────────────────────────

    [Fact]
    public async Task Markdown_MakesADocument_NamedByItsTitle()
    {
        string result = await Docx(("markdown", "# Hi\n\nThere"), ("title", "Status update"), ("paper", "a4"), ("landscape", true));
        string name = "Status-update-" + Stamp + ".docx";
        Assert.StartsWith("Made " + name, result);
        byte[] file = Bytes(name);
        OfficeSchema.ValidDocument(file);
        Assert.Contains("w:orient=\"landscape\"", Part(file, "word/document.xml"));
    }

    [Fact]
    public async Task TheDocxArguments_AreChecked()
    {
        Assert.Equal(OfficeText.OneDocumentSource, await Docx());
        Assert.StartsWith("Error:", await Docx(("markdown", "x"), ("paper", "tabloid")));
        Assert.StartsWith("Error:", await Docx(("markdown", "x"), ("landscape", "sideways")));
        Assert.Equal(FileText.PrunedStubSent(ConvertToDocxTool.MarkdownArgument), await Docx(("markdown", ConversationCompactor.PrunedArgumentStub(500))));
    }

    [Fact]
    public void TheDescriptions_SayTheyAreTheWay()
    {
        Assert.EndsWith("This is the way to make a Word document: never write .docx bytes yourself or run a script to make one.", ConvertToDocxTool.DescriptionText, StringComparison.Ordinal);
        Assert.EndsWith("This is the way to make a spreadsheet: never write .xlsx bytes yourself or run a script to make one.", ConvertToXlsxTool.DescriptionText, StringComparison.Ordinal);
        Assert.Equal(ConvertToDocxTool.DescriptionText, _docx.Description);
        Assert.Equal(ConvertToXlsxTool.DescriptionText, _xlsx.Description);
    }

    [Fact]
    public void TheConverters_AreQuiet_ChangeFiles_AndAreKept()
    {
        foreach (string name in new[] { ConvertToDocxTool.ToolName, ConvertToXlsxTool.ToolName })
        {
            Assert.Contains(name, App.ChatScreen.QuietTools);
            Assert.Contains(name, PlanTools.Mutating);
            Assert.Contains(name, CheckpointTools.Tracked);
        }
    }

    // ── write_file ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a.docx", ConvertToDocxTool.ToolName)]
    [InlineData("a.DOCX", ConvertToDocxTool.ToolName)]
    [InlineData("a.xlsx", ConvertToXlsxTool.ToolName)]
    [InlineData("sub/a.xlsx", ConvertToXlsxTool.ToolName)]
    public void WriteFile_SendsWordAndExcelToTheirConverter(string path, string tool)
    {
        var (text, done, _) = WriteFileTool.Write(_files, path, "text", WriteMode.Create);
        Assert.False(done);
        Assert.Equal(FileText.OfficeNeedsConverter(path, tool), text);
        Assert.False(File.Exists(Path.Combine(_root, path)));
    }

    [Theory]
    [InlineData("a.pptx")]
    [InlineData("a.doc")]
    [InlineData("a.xlsb")]
    [InlineData("a.odt")]
    [InlineData("a.DOCM")]   // the converters make .docx and .xlsx only (2026-10-29, review P1-9)
    [InlineData("sub/a.xltx")]
    [InlineData("a.xlsm")]
    public void WriteFile_RefusesWhatNoToolWrites(string path)
    {
        Assert.Equal(FileText.OfficeNotWritten(path), WriteFileTool.Write(_files, path, "text", WriteMode.Overwrite).Text);
        Assert.Null(WriteFileTool.OfficeRefusal("a.md"));
    }

    [Fact]
    public void UncWrite_SendsAnOfficeFile_ThroughTheWorkingDirectory()
    {
        // 2026-10-29, review P1-9: no converter writes on a share.
        Assert.Equal(FileText.OfficeOnShare("r.docx", ConvertToDocxTool.ToolName), WriteFileTool.OfficeRefusal("r.docx", onShare: true));
        Assert.Equal(FileText.OfficeOnShare("r.xlsx", ConvertToXlsxTool.ToolName), WriteFileTool.Write(_files, "r.xlsx", "x", WriteMode.Create, onShare: true).Text);
        Assert.Equal(FileText.OfficeNotWritten("r.pptx"), WriteFileTool.OfficeRefusal("r.pptx", onShare: true));
    }

    [Fact]
    public async Task ConvertToDocx_RefusesAPrunedStub_InItsOwnWords()
    {
        // 2026-10-29, review P7-10: the assistant's generic refusal had answered first.
        string stub = ConversationCompactor.PrunedArgumentStub(8_402);
        var (answer, _) = await Assistant.InvokeToolAsync([_docx], new FunctionCallContent("c1", ConvertToDocxTool.ToolName, new Dictionary<string, object?> { [ConvertToDocxTool.MarkdownArgument] = stub }), CancellationToken.None);
        Assert.Equal(FileText.PrunedStubSent(ConvertToDocxTool.MarkdownArgument), answer);
        var (title, _) = await Assistant.InvokeToolAsync([_docx], new FunctionCallContent("c2", ConvertToDocxTool.ToolName, new Dictionary<string, object?> { [ConvertToDocxTool.MarkdownArgument] = "x", [ConvertToDocxTool.TitleArgument] = stub }), CancellationToken.None);
        Assert.Equal(App.CompactionText.PrunedStubSentResult(ConvertToDocxTool.TitleArgument), title);
        Assert.Empty(Directory.GetFiles(_root, "*.docx"));
    }

    // ── /docx ───────────────────────────────────────────────────────────────

    [Fact]
    public void Docx_TakesTheLineAsPdfDoes()
    {
        Put("Q3 overwrite plan.md", "# Q3\n\nBody");
        Put("merge", "x");
        var converter = new DocxConverter(_files, _time);
        Assert.Equal(OfficeText.DocxUsage, DocxCommand.Run(converter, "", () => null, CancellationToken.None));
        Assert.Equal(OfficeText.NoWebSource, DocxCommand.Run(converter, "https://example.com/a.md", () => null, CancellationToken.None));
        Assert.Equal(OfficeText.NoReplyForDocx, DocxCommand.Run(converter, "reply", () => " ", CancellationToken.None));
        Assert.StartsWith("Made Q3 overwrite plan.docx", DocxCommand.Run(converter, "Q3 overwrite plan.md", () => null, CancellationToken.None));
        Assert.Equal(OfficeText.NotADocumentSource("merge"), DocxCommand.Run(converter, "merge", () => null, CancellationToken.None));
        Assert.StartsWith("Replaced Q3 overwrite plan.docx", DocxCommand.Run(converter, "Q3 overwrite plan.md overwrite", () => null, CancellationToken.None));
        Assert.StartsWith("Made reply-" + Stamp + ".docx", DocxCommand.Run(converter, "reply", () => "Some **reply**", CancellationToken.None));
        Assert.StartsWith("Error:", DocxCommand.Run(converter, "reply paper=huge", () => "x", CancellationToken.None));
    }

    [Fact]
    public void Docx_WritesIntoNeon_ByTheUsersHand()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".neon", "plans"));
        Put(".neon/plans/x.md", "# Plan");
        var converter = new DocxConverter(_files, _time);
        Assert.StartsWith("Made x.docx", DocxCommand.Run(converter, ".neon/plans/x.md", () => null, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_root, "x.docx")));
        // A .neon deeper down goes to the top as well (2026-10-29, review P5-3).
        Directory.CreateDirectory(Path.Combine(_root, "app", ".neon", "plans"));
        Put("app/.neon/plans/y.md", "# Plan");
        Assert.StartsWith("Made y.docx", DocxCommand.Run(converter, "app/.neon/plans/y.md", () => null, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_root, "y.docx")));
    }

    [Fact]
    public async Task ADocumentsTitle_IsItsFirstHeading_ForEverySource()
    {
        // 2026-10-29, review P2-9, P5-6, P5-7, P7-3.
        var converter = new DocxConverter(_files, _time);
        string reply = DocxCommand.Run(converter, "reply", () => "# Weekly notes\n\nBody", CancellationToken.None);
        Assert.StartsWith("Made Weekly-notes-" + Stamp + ".docx", reply);
        Assert.EndsWith("from " + Printing.PrintText.ReplyTitle, reply);
        Assert.Contains("Weekly notes", OfficeWriterTests.Part(Bytes("Weekly-notes-" + Stamp + ".docx"), "word/header1.xml"));

        Assert.Equal($"Made Plan-A-{Stamp}.docx", (await Docx(("markdown", "# Plan A\n\nBody"))).Split(" (")[0]);
        Assert.EndsWith("from Plan A", await Docx(("markdown", "# Plan A\n\nAgain")));
        string plain = await Docx(("markdown", "No heading here"));
        Assert.StartsWith($"Made document-{Stamp}.docx", plain);
        Assert.EndsWith("from " + OfficeText.YourMarkdown, plain);
        Assert.Equal(OfficeText.EmptyDocument(OfficeText.YourMarkdown), converter.Convert(new DocxRequest(Markdown: " \n\n "), CancellationToken.None));

        Put("notes.txt", "line one");
        Assert.StartsWith("Made notes.docx", await Docx(("path", "notes.txt"), ("title", "Field notes")));
        Assert.Contains("Field notes", OfficeWriterTests.Part(Bytes("notes.docx"), "word/header1.xml"));

        // A title with a slash and a long one make a safe name, their case kept (review P5-4).
        Assert.Equal("Sales-2025-26-Review-" + Stamp, Files.OutputNames.Stamped("Sales 2025/26 Review", _time.GetLocalNow()));
        Assert.Equal(60 + 1 + Stamp.Length, Files.OutputNames.Stamped(new string('a', 250), _time.GetLocalNow()).Length);
    }

    [Fact]
    public async Task ABadTarget_IsRefused_BeforeTheSourceIsRead()
    {
        // 2026-10-29, review P5-13: a missing source no longer hides a wrong to.
        Assert.Equal(OfficeText.WrongTarget("out.xlsx", DocxConverter.Extension), await Docx(("path", "missing.md"), ("to", "out.xlsx")));
    }

    [Fact]
    public void DocxPaths_OfferOnlyWhatItConverts_AndReplyWhenNothingElseFits()
    {
        // 2026-10-29, review P3-10.
        var sources = new App.ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new MentionResult(FileOutcome.Ok, [], false), _ => new MentionResult(FileOutcome.Ok, [], false))
        {
            AnyFiles = prefix => new MentionResult(FileOutcome.Ok, new[] { "docs/", "report.png", "report.md", "notes.txt" }.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList(), false),
        };
        Assert.Equal(["docs/", "report.md", "notes.txt"], App.ChatScreen.ArgumentPaths("/docx", "", sources)!.Paths);
        Assert.Equal(["report.md"], App.ChatScreen.ArgumentPaths("/docx", "rep", sources)!.Paths);
        Assert.Null(App.ChatScreen.ArgumentPaths("/docx", "re", new App.ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new MentionResult(FileOutcome.Ok, [], false), _ => new MentionResult(FileOutcome.Ok, [], false))
        {
            AnyFiles = prefix => new MentionResult(FileOutcome.Ok, new[] { "report.png" }.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList(), false),
        }));   // only a picture starts so: the word list, which offers reply
        Assert.Equal([PdfCommandWords.Reply], DocxCommand.Complete("re").Select(i => i.Text));
    }

    [Fact]
    public void ThePdfAndDocxOptionLists_AreOneList_WithTheirOwnNotes()
    {
        // 2026-10-29, review P1-19.
        var pdf = Pdf.PdfCommand.Complete("plan.md ").ToList();
        var docx = DocxCommand.Complete("plan.md ").ToList();
        Assert.Equal(pdf.Select(i => i.Text), docx.Select(i => i.Text));
        Assert.Equal(Pdf.PdfText.ToNote, pdf[0].Note);
        Assert.Equal(OfficeText.DocxToNote, docx[0].Note);
        Assert.Contains(pdf, i => i.Note == Pdf.PdfText.PaperNote("a4"));
        Assert.Contains(pdf, i => i.Note == Pdf.PdfText.LandscapeNote);
        Assert.Equal(Pdf.PdfText.OverwriteNote, pdf[^1].Note);
        Assert.Equal(OfficeText.DocxOverwriteNote, docx[^1].Note);
    }

    [Fact]
    public void DocxCompletion_OffersItsOwnOptions_NeverAnEditVerb()
    {
        Assert.Equal([PdfCommandWords.Reply], DocxCommand.Complete("").Select(i => i.Text));
        Assert.Empty(DocxCommand.Complete("mer"));
        var after = DocxCommand.Complete("plan.md ").Select(i => i.Text).ToList();
        Assert.Contains("plan.md to=", after);
        Assert.Contains("plan.md paper=a4", after);
        Assert.Contains("plan.md landscape", after);
        Assert.Contains("plan.md overwrite", after);
        Assert.DoesNotContain(after, t => t.Contains("pages=", StringComparison.Ordinal) || t.Contains("angle=", StringComparison.Ordinal));
        Assert.DoesNotContain("plan.md overwrite", DocxCommand.Complete("plan.md overwrite ").Select(i => i.Text));
        Assert.Contains(DocxCommand.Complete("plan.md ").Select(i => i.Note), d => d == OfficeText.DocxToNote);
    }

    private static class PdfCommandWords
    {
        public const string Reply = Printing.PrintText.ReplyWord;
    }
}
