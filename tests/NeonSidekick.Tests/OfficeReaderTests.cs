using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Office;
using NeonSidekick.Pdf;
using NeonSidekick.Tests.Fakes;
using static NeonSidekick.Tests.Fakes.TestOffice;

namespace NeonSidekick.Tests;

/// <summary>
/// The Office readers (2026-10-28, the Office plan): Word's body as Markdown, Excel's sheets and ranges as tables, PowerPoint's slides,
/// both dialects, the refusals a name or a first byte decides, and the package's guards (damage, bombs, forged sizes, the streamed bound,
/// nothing outside the zip opened). Built from <see cref="TestOffice"/>'s in-memory files.
/// </summary>
public sealed class OfficeReaderTests
{
    private static string Read(byte[] file, string name, OfficeRequest? request = null, OfficeLimits? limits = null)
    {
        using var stream = new MemoryStream(file, writable: false);
        return OfficeReader.Read(stream, name, OfficeFile.Kind(name)!.Value, request ?? new OfficeRequest(), limits ?? OfficeLimits.Default, CancellationToken.None);
    }

    private static string Word(string body, Dialect? dialect = null, IEnumerable<(string, string)>? parts = null, IEnumerable<(string, string, string)>? rels = null, OfficeRequest? request = null) =>
        Read(Docx(body, dialect, parts, rels), "doc.docx", request);

    // ── Word ────────────────────────────────────────────────────────────────

    [Fact]
    public void Word_HeadingsListsAndParagraphs_AsMarkdown()
    {
        string answer = Word(P("Report", "Title") + P("Intro", "Heading1") + P("Body text.") + P("Detail", "MyHeading")
            + Li("one", 1) + Li("nested", 1, 1) + Li("first", 2) + Li("second", 2));
        Assert.StartsWith("doc.docx (Word document, ", answer, StringComparison.Ordinal);
        Assert.Contains("# Report\n\n# Intro\n\nBody text.\n\n## Detail", answer, StringComparison.Ordinal);   // a style based on Heading 2 is one
        Assert.Contains("- one\n  - nested\n1. first\n1. second", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_BothDialects_ReadAlike()
    {
        string body = P("Intro", "Heading1") + P("Hello there.");
        Assert.Equal(Word(body), Word(body, Strict));
        Assert.Contains("# Intro", Word(body, Strict), StringComparison.Ordinal);
    }

    [Fact]
    public void Word_Table_KeepsItsColumns_WithPipesBreaksAndMergedCells()
    {
        string table = "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Name</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Note</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>X</w:t></w:r></w:p></w:tc></w:tr>"
            + "<w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"2\"/></w:tcPr><w:p><w:r><w:t>a|b</w:t></w:r></w:p><w:p><w:r><w:t>line two</w:t></w:r></w:p></w:tc><w:tc><w:tcPr><w:vMerge w:val=\"restart\"/></w:tcPr><w:p><w:r><w:t>m</w:t></w:r></w:p></w:tc></w:tr>"
            + "<w:tr><w:tc><w:p><w:r><w:t>c</w:t></w:r></w:p></w:tc><w:tc><w:p/></w:tc><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p><w:r><w:t>hidden by merge</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
        string answer = Word(table);
        Assert.Contains("| Name | Note | X |\n| --- | --- | --- |\n| a\\|b\\nline two |  | m |\n| c |  |  |", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_NestedTable_FlattenedIntoItsCell()
    {
        string inner = "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>i1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>i2</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
        string answer = Word($"<w:tbl><w:tr><w:tc><w:p><w:r><w:t>H</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>outer</w:t></w:r></w:p>{inner}</w:tc></w:tr></w:tbl>");
        Assert.Contains("| outer\\ni1 / i2 |", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_LongCell_IsCut()
    {
        string answer = Word($"<w:tbl><w:tr><w:tc><w:p><w:r><w:t>{new string('x', 600)}</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
        Assert.Contains("| " + new string('x', ExcelReader.MaxCellChars) + "… |", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_Hyperlink_ShowsItsUrl_NeverFetched()
    {
        string answer = Word("<w:p><w:r><w:t xml:space=\"preserve\">See </w:t></w:r><w:hyperlink r:id=\"rIdL\"><w:r><w:t>the site</w:t></w:r></w:hyperlink></w:p>",
            rels: [("rIdL", "hyperlink!", "https://example.com/a")]);
        Assert.Contains("See [the site](https://example.com/a)", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_TrackedChanges_InsertionsKept_DeletionsDropped_AMoveReadOnce()
    {
        string answer = Word("<w:p><w:r><w:t xml:space=\"preserve\">Keep </w:t></w:r><w:ins><w:r><w:t xml:space=\"preserve\">added </w:t></w:r></w:ins><w:del><w:r><w:delText>gone </w:delText></w:r></w:del><w:r><w:t>end</w:t></w:r></w:p>"
            + "<w:p><w:moveFrom><w:r><w:t>moved text</w:t></w:r></w:moveFrom></w:p><w:p><w:moveTo><w:r><w:t>moved text</w:t></w:r></w:moveTo></w:p>");
        Assert.Contains("Keep added end", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("gone", answer, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(answer, "moved text"));
    }

    [Fact]
    public void Word_Field_ShowsItsResult_NeverItsCode()
    {
        string answer = Word("<w:p><w:r><w:t xml:space=\"preserve\">Page </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> PAGE \\* MERGEFORMAT </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>7</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>"
            + "<w:p><w:fldSimple w:instr=\" DATE \"><w:r><w:t>2026-10-28</w:t></w:r></w:fldSimple></w:p>");
        Assert.Contains("Page 7", answer, StringComparison.Ordinal);
        Assert.Contains("2026-10-28", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("PAGE", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("MERGEFORMAT", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_FootnotesEndnotesAndComments_AtTheEnd_WithTheAuthor()
    {
        var d = Transitional;
        string answer = Word("<w:p><w:r><w:t>Claim</w:t></w:r><w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>",
            parts:
            [
                ("word/footnotes.xml", $"<w:footnotes xmlns:w=\"{d.W}\"><w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote><w:footnote w:id=\"1\"><w:p><w:r><w:t>Source: a book.</w:t></w:r></w:p></w:footnote></w:footnotes>"),
                ("word/endnotes.xml", $"<w:endnotes xmlns:w=\"{d.W}\"><w:endnote w:id=\"2\"><w:p><w:r><w:t>An endnote.</w:t></w:r></w:p></w:endnote></w:endnotes>"),
                ("word/comments.xml", $"<w:comments xmlns:w=\"{d.W}\"><w:comment w:id=\"0\" w:author=\"Ann Lee\"><w:p><w:r><w:t>Check this.</w:t></w:r></w:p></w:comment></w:comments>"),
            ],
            rels: [("rIdF", "footnotes", "footnotes.xml"), ("rIdE", "endnotes", "endnotes.xml"), ("rIdC", "comments", "comments.xml")]);
        Assert.Contains("Claim[^1]", answer, StringComparison.Ordinal);
        Assert.Contains("Footnotes:\n[^1]: Source: a book.", answer, StringComparison.Ordinal);
        Assert.Contains("Endnotes:\n[^e2]: An endnote.", answer, StringComparison.Ordinal);
        Assert.Contains("Comments:\n- Ann Lee: Check this.", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_HeaderText_LeftOut()
    {
        var d = Transitional;
        string answer = Word(P("Body"), parts: [("word/header1.xml", $"<w:hdr xmlns:w=\"{d.W}\"><w:p><w:r><w:t>CONFIDENTIAL HEADER</w:t></w:r></w:p></w:hdr>")],
            rels: [("rIdH", "header", "header1.xml")]);
        Assert.DoesNotContain("CONFIDENTIAL HEADER", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_HiddenRun_IsMarked()
    {
        string answer = Word("<w:p><w:r><w:t xml:space=\"preserve\">Seen </w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>secret words</w:t></w:r><w:r><w:t xml:space=\"preserve\"> again</w:t></w:r></w:p>");
        Assert.Contains("Seen [hidden: secret words] again", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_Markers_ForWhatIsntText()
    {
        string drawing(string uri, string alt) =>
            $"<w:r><w:drawing><wp:inline><wp:docPr id=\"1\" name=\"x\" descr=\"{alt}\"/><a:graphic><a:graphicData uri=\"{uri}\"/></a:graphic></wp:inline></w:drawing></w:r>";
        string answer = Word("<w:p>" + drawing("http://schemas.openxmlformats.org/drawingml/2006/picture", "A cat")
            + drawing("http://schemas.openxmlformats.org/drawingml/2006/chart", "Sales by month")
            + drawing("http://schemas.openxmlformats.org/drawingml/2006/diagram", "Org chart") + "</w:p>"
            + "<w:p><m:oMathPara><m:oMath><m:r><m:t>x=1</m:t></m:r></m:oMath></m:oMathPara></w:p>"
            + "<w:p><w:r><w:object><v:shape id=\"o\"><v:imagedata o:title=\"preview\"/></v:shape><o:OLEObject ProgID=\"Excel.Sheet.12\"/></w:object></w:r></w:p>"
            + "<w:altChunk r:id=\"rIdAlt\"/>");
        Assert.Contains("[picture: A cat] [chart: Sales by month] [diagram: Org chart]", answer, StringComparison.Ordinal);
        Assert.Contains(OfficeText.Equation, answer, StringComparison.Ordinal);
        Assert.DoesNotContain("x=1", answer, StringComparison.Ordinal);
        Assert.Contains("[embedded object: Excel.Sheet.12]", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("preview", answer, StringComparison.Ordinal);
        Assert.Contains(OfficeText.EmbeddedDocument, answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Word_ContentControlAndTextBox_Inline_AndAnAlternateReadOnce()
    {
        string textBox = "<w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wp:anchor><wp:docPr id=\"2\" name=\"tb\"/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">"
            + "<wps:wsp><wps:txbx><w:txbxContent><w:p><w:r><w:t>boxed words</w:t></w:r></w:p></w:txbxContent></wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice>"
            + "<mc:Fallback><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>boxed words</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r>";
        string answer = Word("<w:sdt><w:sdtPr><w:alias w:val=\"x\"/></w:sdtPr><w:sdtContent>" + P("In a control") + "</w:sdtContent></w:sdt><w:p><w:r><w:t>Before</w:t></w:r>" + textBox + "</w:p>");
        Assert.Contains("In a control", answer, StringComparison.Ordinal);
        Assert.Contains("Before boxed words", answer, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(answer, "boxed words"));
    }

    [Fact]
    public void Word_ExternalPictureAndTemplate_ReadAtOnce_NothingFetched()
    {
        var d = Transitional;
        var clock = Stopwatch.StartNew();
        string answer = Word("<w:p><w:r><w:drawing><wp:inline><wp:docPr id=\"1\" name=\"x\" descr=\"Linked logo\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:blipFill><a:blip r:link=\"rIdPic\"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>",
            parts: [("word/settings.xml", $"<w:settings xmlns:w=\"{d.W}\" xmlns:r=\"{d.R}\"><w:attachedTemplate r:id=\"rIdT\"/></w:settings>"), ("word/_rels/settings.xml.rels", Rels(d, ("rIdT", "attachedTemplate!", @"file:///\\nowhere\share\t.dotx")))],
            rels: [("rIdPic", "image!", @"\\nowhere\share\logo.png"), ("rIdSet", "settings", "settings.xml")]);
        Assert.Contains("[picture: Linked logo]", answer, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "nothing outside the package is reached");
    }

    [Fact]
    public void Word_FromChar_ContinuesALongDocument_AndTheHeaderNamesNext()
    {
        string body = string.Concat(Enumerable.Range(0, 400).Select(i => P($"Paragraph {i:D3} " + new string('w', 100))));
        string first = Word(body);
        Assert.Matches(@"^doc\.docx \(Word document, characters 1–32,000; next: from_char 32000\)\n", first);
        string second = Word(body, request: new OfficeRequest(FromChar: 32_000));
        Assert.Matches(@"^doc\.docx \(Word document, characters 32,001–[\d,]+ of [\d,]+\)\n", second);
        Assert.Contains("Paragraph 399", second, StringComparison.Ordinal);
        Assert.StartsWith("Error: 'doc.docx' has", Word(P("short"), request: new OfficeRequest(FromChar: 500)), StringComparison.Ordinal);
    }

    [Fact]
    public void Word_ShortDocument_HeaderCountsItsCharacters()
    {
        Assert.Equal("doc.docx (Word document, 5 characters)\nHello", Word(P("Hello")));
    }

    // ── Excel ───────────────────────────────────────────────────────────────

    private static string Cell(string r, string v, string? t = null, int? s = null, string inner = "") =>
        $"<c r=\"{r}\"{(t is null ? "" : $" t=\"{t}\"")}{(s is null ? "" : $" s=\"{s}\"")}>{inner}{(v.Length > 0 ? $"<v>{v}</v>" : "")}</c>";

    private static string Row(int r, params string[] cells) => $"<row r=\"{r}\">{string.Concat(cells)}</row>";

    private static string Book(IEnumerable<SheetSpec> sheets, OfficeRequest? request = null, IEnumerable<string>? shared = null, IEnumerable<int>? xfs = null,
        IDictionary<int, string>? codes = null, bool date1904 = false, string definedNames = "", Dialect? dialect = null, bool sharedRaw = false) =>
        Read(Xlsx(sheets, shared, xfs, codes, date1904, definedNames, dialect, sharedRaw), "book.xlsx", request);

    [Fact]
    public void Excel_Overview_ListsSheetsNamesAndTheFirstRows()
    {
        string answer = Book(
        [
            new("Sales", SheetData(Row(1, Cell("A1", "0", "s"), Cell("B1", "1", "s")) + Row(2, Cell("A2", "2", "s"), Cell("B2", "42")), "A1:B2")),
            new("Old", SheetData(Row(1, Cell("A1", "5")), "A1"), "hidden"),
            new("Secret", SheetData(Row(1, Cell("A1", "6")), "A1"), "veryHidden"),
            new("Chart1", "", Chart: true),
        ], shared: ["Region", "Total", "North"], definedNames: "<definedName name=\"TaxRate\">Sales!$B$2</definedName><definedName name=\"_xlnm._FilterDatabase\" hidden=\"1\">Sales!$A$1:$B$2</definedName>");
        Assert.StartsWith("book.xlsx (Excel workbook, 4 sheets; showing Sales!A1:B2 of its used range A1:B2)\n", answer, StringComparison.Ordinal);
        Assert.Contains("1. Sales: A1:B2\n2. Old: A1 (hidden)\n3. Secret: A1 (very hidden)\n4. Chart1: chart sheet (not read)", answer, StringComparison.Ordinal);
        Assert.Contains("- TaxRate: Sales!$B$2", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("_FilterDatabase", answer, StringComparison.Ordinal);
        Assert.Contains("| | A | B |\n| --- | --- | --- |\n| 1 | Region | Total |\n| 2 | North | 42 |", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_BothDialects_ReadAlike()
    {
        SheetSpec[] sheets = [new("S", SheetData(Row(1, Cell("A1", "0", "s")), "A1"))];
        Assert.Equal(Book(sheets, shared: ["x"]), Book(sheets, shared: ["x"], dialect: Strict));
    }

    [Fact]
    public void Excel_SharedStrings_RichTextJoined_PhoneticLeftOut_EscapesDecoded()
    {
        string answer = Book([new("S", SheetData(Row(1, Cell("A1", "0", "s"), Cell("B1", "1", "s"), Cell("C1", "2", "s"), Cell("D1", "3", "s")), "A1:D1"))],
            new OfficeRequest(Sheet: "S", Range: "A1:D1"),
            shared: ["<si><r><t>Bold</t></r><r><t xml:space=\"preserve\"> plain</t></r><rPh sb=\"0\" eb=\"1\"><t>ふりがな</t></rPh></si>",
                "<si><t>line_x000D__x000A_two</t></si>", "<si><t>a_x005F_x0041_b</t></si>", "<si><t>bell_x0007_esc_x001B_[31m</t></si>"],
            sharedRaw: true);
        Assert.Contains("| Bold plain | line\\ntwo | a_x0041_b | bell\uFFFDesc\uFFFD[31m |", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("ふりがな", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_Dates_1900And1904_ThePhantomLeapDay_TimesAndIsoCells()
    {
        string answer = Book([new("S", SheetData(Row(1, Cell("A1", "59", s: 1), Cell("B1", "60", s: 1), Cell("C1", "61", s: 1), Cell("D1", "46323.6041666667", s: 2),
            Cell("E1", "0.75", s: 3), Cell("F1", "46323.5", s: 1), Cell("G1", "2026-10-28T09:00:00", "d"), Cell("H1", "46323", s: 4)), "A1:H1"))],
            new OfficeRequest(Sheet: "S"), xfs: [0, 14, 22, 21, 164], codes: new Dictionary<int, string> { [164] = "dd/mm/yyyy" });
        Assert.Contains("| 1900-02-28 | 1900-02-29 | 1900-03-01 | 2026-10-28T14:30:00 | 18:00:00 | 2026-10-28T12:00:00 | 2026-10-28T09:00:00 | 2026-10-28 |", answer, StringComparison.Ordinal);
        string nineteen04 = Book([new("S", SheetData(Row(1, Cell("A1", "0", s: 1), Cell("B1", "1", s: 1)), "A1:B1"))], new OfficeRequest(Sheet: "S"), xfs: [0, 14], date1904: true);
        Assert.Contains("| 1904-01-01 | 1904-01-02 |", nineteen04, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_Numbers_FifteenDigits_Percent_Booleans_Errors_StrResults()
    {
        string answer = Book([new("S", SheetData(Row(1, Cell("A1", "0.30000000000000004"), Cell("B1", "0.25", s: 1), Cell("C1", "0.125", s: 2), Cell("D1", "1", "b"),
            Cell("E1", "#N/A", "e"), Cell("F1", "text result", "str", inner: "<f>CONCAT(\"a\")</f>"), Cell("G1", "0.5", s: 3)), "A1:G1"))],
            new OfficeRequest(Sheet: "S"), xfs: [0, 9, 10, 164], codes: new Dictionary<int, string> { [164] = "0.0&quot;%&quot;" });
        Assert.Contains("| 0.3 | 25% | 12.5% | TRUE | #N/A | text result | 0.5 |", answer, StringComparison.Ordinal);   // a % inside quotes is no percent format
    }

    [Fact]
    public void Excel_Formulas_CachedValue_TheFormulaWhenNone_AndSharedFormulas()
    {
        SheetSpec[] sheets = [new("S", SheetData(
            Row(1, Cell("A1", "1"), Cell("B1", "2", inner: "<f t=\"shared\" ref=\"B1:B3\" si=\"0\">A1*2</f>")) +
            Row(2, Cell("A2", "2"), Cell("B2", "4", inner: "<f t=\"shared\" si=\"0\"/>")) +
            Row(3, Cell("A3", "3"), Cell("B3", "", inner: "<f>SUM(A1:A3)</f>")), "A1:B3"))];
        string plain = Book(sheets, new OfficeRequest(Sheet: "S"));
        Assert.Contains("| 1 | 1 | 2 |\n| 2 | 2 | 4 |\n| 3 | 3 | =SUM(A1:A3) |", plain, StringComparison.Ordinal);
        string formulas = Book(sheets, new OfficeRequest(Sheet: "S", Formulas: true));
        Assert.Contains("| 1 | 1 | 2 (=A1*2) |\n| 2 | 2 | 4 (formula shared with B1) |", formulas, StringComparison.Ordinal);
        string fromRow2 = Book(sheets, new OfficeRequest(Sheet: "S", Range: "B2", Formulas: true));
        Assert.Contains("B2: 4 (formula shared with B1)", fromRow2, StringComparison.Ordinal);   // the first cell above the range still named
    }

    private static readonly SheetSpec[] Grid =
    [
        new("Q3 Sales", SheetData(string.Concat(Enumerable.Range(1, 10).Select(r => Row(r, Enumerable.Range(1, 5).Select(c => Cell(CellReference.Cell(c, r), (r * 10 + c).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray()))), "A1:E10")),
        new("Two", SheetData(Row(1, Cell("A1", "99")), "A1")),
    ];

    [Fact]
    public void Excel_Ranges_QuotedSheetDollarsOneCellColumnsAndRows()
    {
        string quoted = Book(Grid, new OfficeRequest(Range: "'Q3 Sales'!$B$2:$C$3"));
        Assert.StartsWith("book.xlsx (sheet 'Q3 Sales', B2:C3 of used range A1:E10)\n", quoted, StringComparison.Ordinal);
        Assert.Contains("| | B | C |\n| --- | --- | --- |\n| 2 | 22 | 23 |\n| 3 | 32 | 33 |", quoted, StringComparison.Ordinal);
        Assert.Contains("B7: 72", Book(Grid, new OfficeRequest(Sheet: "Q3 Sales", Range: "B7")), StringComparison.Ordinal);
        Assert.Contains("| 10 | 101 | 102 |", Book(Grid, new OfficeRequest(Sheet: "1", Range: "A:B")), StringComparison.Ordinal);   // by number, whole columns bounded by the used range
        string rows = Book(Grid, new OfficeRequest(Sheet: "q3 sales", Range: "2:3"));
        Assert.Contains("A2:E3 of used range", rows, StringComparison.Ordinal);
        Assert.Contains("A1: 99", Book(Grid, new OfficeRequest(Range: "Two!A1")), StringComparison.Ordinal);   // a sheet in the range alone
    }

    [Fact]
    public void Excel_RangeRefusals()
    {
        Assert.Equal(OfficeText.SheetsDisagree("Two", "Q3 Sales"), Book(Grid, new OfficeRequest(Sheet: "Two", Range: "'Q3 Sales'!A1")));
        Assert.Equal(OfficeText.RangePastTheEdge("XFE1"), Book(Grid, new OfficeRequest(Sheet: "Two", Range: "XFE1")));
        Assert.Equal(OfficeText.RangePastTheEdge("A1048577"), Book(Grid, new OfficeRequest(Sheet: "Two", Range: "A1048577")));
        Assert.Equal(OfficeText.NameAsRange("TaxRate"), Book(Grid, new OfficeRequest(Sheet: "Two", Range: "TaxRate")));
        Assert.Equal(OfficeText.BadRange("A1:"), Book(Grid, new OfficeRequest(Sheet: "Two", Range: "A1:")));
        Assert.StartsWith("Error: 'book.xlsx' has no sheet \"Nope\"", Book(Grid, new OfficeRequest(Sheet: "Nope")), StringComparison.Ordinal);
        Assert.Equal(OfficeText.NoValues("book.xlsx", "'Q3 Sales'", "G20:H30", "A1:E10"), Book(Grid, new OfficeRequest(Sheet: "Q3 Sales", Range: "G20:H30")));
    }

    [Fact]
    public void Excel_HiddenRowsAndColumn_NamedAboveTheTable()
    {
        string sheet = "<dimension ref=\"A1:C6\"/><cols><col min=\"3\" max=\"3\" hidden=\"1\"/></cols><sheetData>"
            + string.Concat(Enumerable.Range(1, 6).Select(r => $"<row r=\"{r}\"{(r is >= 4 and <= 6 ? " hidden=\"1\"" : "")}><c r=\"A{r}\"><v>{r}</v></c><c r=\"C{r}\"><v>{r * 3}</v></c></row>")) + "</sheetData>";
        string answer = Book([new("S", sheet)], new OfficeRequest(Sheet: "S"));
        Assert.Contains("hidden: rows 4–6, column C\n| | A | B | C |", answer, StringComparison.Ordinal);
        Assert.Contains("| 5 | 5 |  | 15 |", answer, StringComparison.Ordinal);   // read like any other
    }

    [Fact]
    public void Excel_NoDimension_RowsAndCellsWithNoR()
    {
        string sheet = "<sheetData><row><c><v>1</v></c><c><v>2</v></c></row><row><c r=\"B2\"><v>3</v></c><c><v>4</v></c></row></sheetData>";
        string answer = Book([new("S", sheet)]);
        Assert.Contains("1. S: A1:C2", answer, StringComparison.Ordinal);
        Assert.Contains("| 1 | 1 | 2 |  |\n| 2 |  | 3 | 4 |", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_ACut_NamesTheNextRange_AndWideSheetsShowFortyColumns()
    {
        string tall = SheetData(string.Concat(Enumerable.Range(1, 2_000).Select(r => Row(r, Cell($"A{r}", "0", "s"), Cell($"B{r}", r.ToString(System.Globalization.CultureInfo.InvariantCulture))))), "A1:B2000");
        string answer = Book([new("Big Data", tall)], new OfficeRequest(Sheet: "Big Data"), shared: [new string('t', 200)]);
        Assert.Matches(@"^book\.xlsx \(sheet 'Big Data', A1:B\d+ of used range A1:B2000; next: range ""'Big Data'!A\d+:B2000""\)\n", answer);
        Assert.True(answer.Length <= WorkingDirectory.MaxReadChars + 500);

        string wide = SheetData(Row(1, Enumerable.Range(1, 50).Select(c => Cell(CellReference.Cell(c, 1), c.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray()), "A1:AX1");
        string cut = Book([new("W", wide)], new OfficeRequest(Sheet: "W"));
        Assert.Contains("A1:AN1 of used range A1:AX1; next: range \"W!AO1:AX1\"", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_LongCell_CutInTheTable_WholeByAOneCellRange()
    {
        string text = new('z', 900);
        SheetSpec[] sheets = [new("S", SheetData(Row(1, Cell("A1", "0", "s"), Cell("B1", "1")), "A1:B1"))];
        Assert.Contains("| " + new string('z', ExcelReader.MaxCellChars) + "… |", Book(sheets, new OfficeRequest(Sheet: "S"), shared: [text]), StringComparison.Ordinal);
        Assert.Contains("A1: " + text, Book(sheets, new OfficeRequest(Sheet: "S", Range: "A1"), shared: [text]), StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_Damage_MissingSheetPart_AndASharedStringPastTheEnd()
    {
        var file = Xlsx([new("S", SheetData(Row(1, Cell("A1", "5", "s")), "A1"))], shared: ["only"]);
        Assert.Equal(OfficeText.Damaged("book.xlsx", "xl/sharedStrings.xml"), Read(file, "book.xlsx", new OfficeRequest(Sheet: "S")));

        var missing = Strip(Xlsx([new("S", SheetData(Row(1, Cell("A1", "1")), "A1"))]), "xl/worksheets/sheet1.xml");
        Assert.Equal(OfficeText.Damaged("book.xlsx", "xl/worksheets/sheet1.xml"), Read(missing, "book.xlsx", new OfficeRequest(Sheet: "S")));
    }

    [Fact]
    public void Excel_ARepetitiveSheetPastTheBombRatio_StillReads()
    {
        string rows = string.Concat(Enumerable.Range(1, 40_000).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\" s=\"0\"/><c r=\"B{r}\" s=\"0\"/></row>"));
        var file = Xlsx([new("S", SheetData(rows + Row(40_001, Cell("A40001", "7")), "A1:B40001"))]);
        string answer = Read(file, "book.xlsx", new OfficeRequest(Sheet: "S"));
        Assert.Contains("| 40001 | 7 |", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_AStreamedSheet_StopsAtItsBound_WithWhatItHas()
    {
        string rows = string.Concat(Enumerable.Range(1, 5_000).Select(r => Row(r, Cell($"A{r}", "1"))));
        var file = Xlsx([new("S", SheetData(rows, "A1:A5000"))]);
        string answer = Read(file, "book.xlsx", new OfficeRequest(Sheet: "S"), OfficeLimits.Default with { MaxStreamedPartBytes = 20_000 });
        Assert.Contains("| 1 | 1 |", answer, StringComparison.Ordinal);
        Assert.Contains(OfficeText.RestTooLarge("xl/worksheets/sheet1.xml"), answer, StringComparison.Ordinal);
    }

    // ── PowerPoint ──────────────────────────────────────────────────────────

    [Fact]
    public void Slides_InPresentationOrder_TitleByPlaceholder_GroupsInPlace_NotesAndHidden()
    {
        string group = "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"5\" name=\"g\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>"
            + "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"6\" name=\"g2\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" + Shape(null, "deep text") + "</p:grpSp></p:grpSp>";
        var file = Pptx(
        [
            new(Shape("body", "First body") + Shape("title", "Opening")),
            new(Shape("ctrTitle", "Second") + group, Hidden: true, Notes: "Say hello"),
            new(Shape("title", "Third")),
        ], fileOrder: [3, 1, 2]);
        string answer = Read(file, "deck.pptx", new OfficeRequest(Notes: true));
        Assert.StartsWith("deck.pptx (slides 1–3 of 3)\n", answer, StringComparison.Ordinal);
        Assert.Contains("## Slide 1 — Opening\nFirst body\n\n## Slide 2 — Second (hidden)\ndeep text\nNotes:\nSay hello\n\n## Slide 3 — Third", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Notes:", Read(file, "deck.pptx"), StringComparison.Ordinal);
    }

    [Fact]
    public void Slides_BothDialects_ReadAlike()
    {
        SlideSpec[] slides = [new(Shape("title", "T") + Shape(null, "x"))];
        Assert.Equal(Read(Pptx(slides), "deck.pptx"), Read(Pptx(slides, Strict), "deck.pptx"));
    }

    [Fact]
    public void Slides_TableChartAndObject()
    {
        string frame(string inner, string uri, string alt = "") =>
            $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"f\" descr=\"{alt}\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm/><a:graphic><a:graphicData uri=\"{uri}\">{inner}</a:graphicData></a:graphic></p:graphicFrame>";
        string table = "<a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>H1</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>H|2</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl>";
        string answer = Read(Pptx([new(frame(table, "http://schemas.openxmlformats.org/drawingml/2006/table")
            + frame("", "http://schemas.openxmlformats.org/drawingml/2006/chart", "Revenue")
            + frame("<p:oleObj progId=\"Word.Document.12\"/>", "http://schemas.openxmlformats.org/presentationml/2006/ole"))]), "deck.pptx");
        Assert.Contains("| H1 | H\\|2 |\n| --- | --- |", answer, StringComparison.Ordinal);
        Assert.Contains("[chart: Revenue]", answer, StringComparison.Ordinal);
        Assert.Contains("[embedded object: Word.Document.12]", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Slides_PastTheEnd_AndFromCharOnOneSlide()
    {
        var file = Pptx([new(Shape("title", "A")), new(Shape("title", "B") + Shape(null, new string('q', 40_000)))]);
        Assert.True(PdfPageRange.TryParse("5-6", out var past, out _));
        Assert.Equal(OfficeText.SlidesPastTheEnd("deck.pptx", 2, "5-6"), Read(file, "deck.pptx", new OfficeRequest(Slides: past, SlidesSpec: "5-6")));
        Assert.True(PdfPageRange.TryParse("2", out var two, out _));
        string cut = Read(file, "deck.pptx", new OfficeRequest(Slides: two, SlidesSpec: "2"));
        Assert.Contains("(slide 2 cut at ", cut, StringComparison.Ordinal);
        string on = Read(file, "deck.pptx", new OfficeRequest(Slides: two, SlidesSpec: "2", FromChar: 32_000));
        Assert.StartsWith("deck.pptx (slide 2 of 2)\n", on, StringComparison.Ordinal);
        Assert.DoesNotContain("## Slide 2", on, StringComparison.Ordinal);
    }

    // ── refusals and the package's guards ───────────────────────────────────

    [Fact]
    public void Refusals_ByName()
    {
        Assert.Equal(OfficeText.Legacy("a.doc"), OfficeReader.RefuseByName("a.doc"));
        Assert.Equal(OfficeText.Legacy("a.XLS"), OfficeReader.RefuseByName("a.XLS"));
        Assert.Equal(OfficeText.BinaryWorkbook("a.xlsb"), OfficeReader.RefuseByName("a.xlsb"));
        Assert.Equal(OfficeText.OpenDocument("a.odt"), OfficeReader.RefuseByName("a.odt"));
        Assert.Equal(OfficeText.LockFile("dir/~$report.docx", "dir/report.docx"), OfficeReader.RefuseByName("dir/~$report.docx"));
        Assert.Equal(OfficeText.NotAnOfficePath("a.txt"), OfficeReader.RefuseByName("a.txt"));
        Assert.Null(OfficeReader.RefuseByName("a.ppsx"));
    }

    [Fact]
    public void Refusals_ByTheFirstBytes_AndANonOfficeZip()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0];
        Assert.Equal(OfficeText.Protected("p.docx"), Read(ole, "p.docx"));
        Assert.Equal(OfficeText.NotOffice("t.docx"), Read("just text"u8.ToArray(), "t.docx"));
        Assert.Equal(OfficeText.NotOffice("z.xlsx"), Read(Zip([("readme.txt", "hi")]), "z.xlsx"));
        Assert.Equal(OfficeText.NotOffice("w.xlsx"), Read(Docx(P("x")), "w.xlsx"));   // a document under a workbook's name
    }

    [Fact]
    public void Damage_ATruncatedZip_ABadXmlPart_ADtd()
    {
        var file = Docx(P("Hello"));
        Assert.StartsWith("Error: the file looks damaged (", Read(file[..(file.Length / 2)], "doc.docx"), StringComparison.Ordinal);
        var bad = Zip([("_rels/.rels", Rels(Transitional, ("rId1", "officeDocument", "word/document.xml"))), ("word/document.xml", "<w:document xmlns:w=\"" + Transitional.W + "\"><w:body><w:p>")]);
        Assert.Equal(OfficeText.Damaged("doc.docx", "word/document.xml"), Read(bad, "doc.docx"));
        var dtd = Zip([("_rels/.rels", Rels(Transitional, ("rId1", "officeDocument", "word/document.xml"))),
            ("word/document.xml", "<!DOCTYPE x [<!ENTITY e \"boom\">]><w:document xmlns:w=\"" + Transitional.W + "\"><w:body><w:p><w:r><w:t>&e;</w:t></w:r></w:p></w:body></w:document>")]);
        Assert.Equal(OfficeText.Damaged("doc.docx", "word/document.xml"), Read(dtd, "doc.docx"));
    }

    [Fact]
    public void Damage_AfterSomeText_KeepsItAndSaysWhere()
    {
        string doc = "<w:document xmlns:w=\"" + Transitional.W + "\"><w:body>" + P("Kept paragraph.") + "<w:p><w:r><w:t>broken";
        var file = Zip([("_rels/.rels", Rels(Transitional, ("rId1", "officeDocument", "word/document.xml"))), ("word/document.xml", doc)]);
        string answer = Read(file, "doc.docx");
        Assert.Contains("Kept paragraph.", answer, StringComparison.Ordinal);
        Assert.Contains(OfficeText.DamagedPast("word/document.xml"), answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Guards_ABombPart_AForgedStoredPart_AForgedDeflatePart()
    {
        string many = string.Concat(Enumerable.Repeat("<si><t>aaaaaaaaaaaaaaaaaaaa</t></si>", 120_000));
        var bomb = Xlsx([new("S", SheetData(Row(1, Cell("A1", "0", "s")), "A1"))], shared: [], sharedRaw: true);
        bomb = Replace(bomb, "xl/sharedStrings.xml", $"<sst xmlns=\"{Transitional.S}\">{many}</sst>", CompressionLevel.SmallestSize);
        Assert.Equal(OfficeText.PartTooLarge("book.xlsx", "xl/sharedStrings.xml"), Read(bomb, "book.xlsx", new OfficeRequest(Sheet: "S")));

        var stored = Xlsx([new("S", SheetData(Row(1, Cell("A1", "0", "s")), "A1"))], shared: [new string('s', 50_000)], level: CompressionLevel.NoCompression);
        stored = ForgeSize(stored, "xl/sharedStrings.xml", 100);
        Assert.Equal(OfficeText.PartTooLarge("book.xlsx", "xl/sharedStrings.xml"), Read(stored, "book.xlsx", new OfficeRequest(Sheet: "S"), OfficeLimits.Default with { MaxWholePartBytes = 10_000 }));

        var deflated = Xlsx([new("S", SheetData(Row(1, Cell("A1", "0", "s")), "A1"))], shared: [new string('d', 50_000)]);
        deflated = ForgeSize(deflated, "xl/sharedStrings.xml", 100);
        Assert.Equal(OfficeText.Damaged("book.xlsx", "xl/sharedStrings.xml"), Read(deflated, "book.xlsx", new OfficeRequest(Sheet: "S"), OfficeLimits.Default with { MaxWholePartBytes = 10_000 }));
    }

    [Fact]
    public void Guards_AFileOverTheCap()
    {
        Assert.Equal(OfficeText.TooBig("doc.docx", 10), Read(Docx(P("x")), "doc.docx", limits: OfficeLimits.Default with { MaxFileBytes = 10 }));
    }

    // ── the pieces ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a_x000D__x000A_b", "a\nb")]
    [InlineData("a_x000D_b", "a\nb")]
    [InlineData("tab_x0009_here", "tab\there")]
    [InlineData("_x005F_x0041_", "_x0041_")]
    [InlineData("esc_x001B_", "esc\uFFFD")]
    [InlineData("c1_x0085_", "c1\uFFFD")]
    [InlineData("plain_text", "plain_text")]
    public void Unescape_DecodesOoxmlEscapes_ScrubsControls(string raw, string expected) => Assert.Equal(expected, OfficeXml.Unescape(raw));

    [Theory]
    [InlineData(14, null, CellFormat.Date)]
    [InlineData(22, null, CellFormat.DateTime)]
    [InlineData(20, null, CellFormat.Time)]
    [InlineData(46, null, CellFormat.Time)]
    [InlineData(31, null, CellFormat.Date)]
    [InlineData(57, null, CellFormat.Date)]
    [InlineData(9, null, CellFormat.Percent)]
    [InlineData(2, null, CellFormat.General)]
    [InlineData(164, "yyyy-mm-dd hh:mm", CellFormat.DateTime)]
    [InlineData(164, "mm:ss", CellFormat.Time)]
    [InlineData(164, "\"day\" 0", CellFormat.General)]
    [InlineData(164, "[$-409]mmmm d, yyyy", CellFormat.Date)]
    [InlineData(164, "0.00E+00", CellFormat.General)]
    [InlineData(164, "0.0%", CellFormat.Percent)]
    [InlineData(164, "h:mm AM/PM", CellFormat.Time)]
    [InlineData(164, "General", CellFormat.General)]
    public void NumberFormats_Classify(int id, string? code, CellFormat expected) => Assert.Equal(expected, NumberFormats.Classify(id, code));

    [Fact]
    public void CellReference_ColumnsCellsAndQuotedSheets()
    {
        Assert.Equal("A", CellReference.Column(1));
        Assert.Equal("Z", CellReference.Column(26));
        Assert.Equal("AA", CellReference.Column(27));
        Assert.Equal("XFD", CellReference.Column(CellReference.MaxColumn));
        Assert.Equal(16_384, CellReference.ColumnNumber("xfd"));
        Assert.Equal("Sales", CellReference.QuoteSheet("Sales"));
        Assert.Equal("'Q3 Sales'", CellReference.QuoteSheet("Q3 Sales"));
        Assert.Equal("'Bob''s'", CellReference.QuoteSheet("Bob's"));
        Assert.Equal("'A1'", CellReference.QuoteSheet("A1"));
        Assert.Equal(RangeProblem.None, CellReference.TryParseRange("'Bob''s'!a1:b2", out string? sheet, out var range));
        Assert.Equal("Bob's", sheet);
        Assert.Equal("A1:B2", range!.ToString());
        Assert.Equal(RangeProblem.None, CellReference.TryParseRange("C3:A1", out _, out range));
        Assert.Equal("A1:C3", range!.ToString());
        Assert.Equal(RangeProblem.Bad, CellReference.TryParseRange("'open!A1", out _, out _));
    }

    private static byte[] Strip(byte[] zip, string entry) => Rebuild(zip, entry, null, CompressionLevel.Optimal);

    private static byte[] Replace(byte[] zip, string entry, string content, CompressionLevel level) => Rebuild(zip, entry, content, level);

    private static byte[] Rebuild(byte[] zip, string entry, string? content, CompressionLevel level)
    {
        var parts = new List<(string, string, CompressionLevel)>();
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            foreach (var e in archive.Entries)
            {
                using var reader = new StreamReader(e.Open(), Encoding.UTF8);
                string text = reader.ReadToEnd();
                if (e.FullName == entry)
                {
                    if (content is not null)
                    {
                        parts.Add((e.FullName, content, level));
                    }

                    continue;
                }

                parts.Add((e.FullName, text, CompressionLevel.Optimal));
            }
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text, lvl) in parts)
            {
                using var stream = archive.CreateEntry(name, lvl).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return buffer.ToArray();
    }
}
