using System.IO.Compression;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Office;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The workbook writers (2026-10-29, the Office writing plan): the cell rules (<see cref="XlsxValue"/>), sheet names, a new workbook and an
/// added or replaced sheet, each read back through our own <see cref="ExcelReader"/> and checked by the Open XML SDK's validator.
/// </summary>
public sealed class OfficeWriterTests
{
    private static readonly ManualTimeProvider Clock = new();

    internal static byte[] NewBook(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<XlsxValue>> rows, string sheet = "Data", Action<SheetResult>? result = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new ExcelWriter(buffer, sheet, Clock, "Report"))
        {
            Assert.Null(writer.Sheet.Begin(columns));
            foreach (var row in rows)
            {
                writer.Sheet.Add(row);
            }

            var done = writer.Finish();
            result?.Invoke(done);
        }

        return buffer.ToArray();
    }

    internal static string Read(byte[] file, OfficeRequest? request = null)
    {
        using var stream = new MemoryStream(file, writable: false);
        return OfficeReader.Read(stream, "out.xlsx", OfficeKind.Excel, request ?? new OfficeRequest(), OfficeLimits.Default, CancellationToken.None);
    }

    internal static string Part(byte[] file, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static byte[] Entry(byte[] file, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
        using var stream = zip.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static XlsxValue T(string text) => XlsxValue.OfText(text);

    // ── XlsxValue ───────────────────────────────────────────────────────────

    [Fact]
    public void Numbers_PastFifteenDigits_AreText_DoublesStayNumbers()
    {
        Assert.Equal(XlsxKind.Text, XlsxValue.From(1234567890123456789L).Kind);
        Assert.Equal(XlsxKind.Number, XlsxValue.From(123456789012345L).Kind);
        Assert.Equal(XlsxKind.Text, XlsxValue.From(1234567890123456m).Kind);
        Assert.Equal(XlsxKind.Number, XlsxValue.From(123.4500m).Kind);
        Assert.Equal("123.4500", XlsxValue.From(123.4500m).Text);
        Assert.Equal(XlsxKind.Number, XlsxValue.From(0.1 + 0.2).Kind);
        Assert.Equal(XlsxKind.Text, XlsxValue.From(double.NaN).Kind);
        Assert.Equal(XlsxKind.Text, XlsxValue.From(double.PositiveInfinity).Kind);
        Assert.Equal(15, XlsxValue.SignificantDigits("-0.00123456789012345"));
    }

    [Fact]
    public void Dates_Times_Durations_Offsets()
    {
        Assert.Equal(XlsxKind.Date, XlsxValue.From(new DateOnly(2026, 10, 29)).Kind);
        Assert.Equal(XlsxKind.Date, XlsxValue.From(new DateTime(2026, 10, 29), dateColumn: true).Kind);
        Assert.Equal(XlsxKind.DateTime, XlsxValue.From(new DateTime(2026, 10, 29)).Kind);
        Assert.Equal(XlsxKind.Time, XlsxValue.From(new TimeOnly(18, 0)).Kind);
        Assert.Equal(XlsxKind.Time, XlsxValue.From(TimeSpan.FromHours(23)).Kind);
        Assert.Equal(XlsxKind.Duration, XlsxValue.From(TimeSpan.FromHours(36)).Kind);
        Assert.Equal(XlsxKind.Text, XlsxValue.From(TimeSpan.FromHours(-1)).Kind);
        Assert.Equal("2026-10-29T09:30:00+02:00", XlsxValue.From(new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.FromHours(2))).Text);
        Assert.Null(NumberFormats.Serial(new DateTime(1900, 2, 28), date1904: false));
        Assert.Equal(61, NumberFormats.Serial(new DateTime(1900, 3, 1), date1904: false));
        Assert.Equal(0, NumberFormats.Serial(new DateTime(1904, 1, 1), date1904: true));
    }

    [Fact]
    public void Text_CutAtExcelsCap_Escaped_AndBytesAsHex()
    {
        var cut = T(new string('a', XlsxValue.MaxChars + 5));
        Assert.True(cut.Cut);
        Assert.Equal(XlsxValue.MaxChars, cut.Text!.Length);
        Assert.False(T(new string('a', XlsxValue.MaxChars)).Cut);
        Assert.Equal("a_x005F_x0041_b_x000D_\nc\uFFFD_x0007_", XlsxValue.Escape("a_x0041_b\r\nc\uD800\u0007"));
        Assert.Equal("ab\tc\n\uFFFD", XlsxValue.XmlSafe("a\u0001b\tc\n\uDC00"));
        Assert.Equal("0x0AFF", XlsxValue.From(new byte[] { 0x0A, 0xFF }).Text);
        Assert.True(XlsxValue.OfBytes(new byte[40_000]).Cut);
    }

    [Fact]
    public void SheetNames_RefusedByTheRule_MadeFitWhenOurs()
    {
        Assert.False(SheetName.Problem("Q3 Sales"));
        Assert.True(SheetName.Problem("a/b"));
        Assert.True(SheetName.Problem("'quoted"));
        Assert.True(SheetName.Problem("history"));
        Assert.True(SheetName.Problem(new string('x', 32)));
        Assert.True(SheetName.Problem(" "));
        Assert.Equal("a-b-c", SheetName.Fit("a/b?c", []));
        Assert.Equal("Query (2)", SheetName.Fit("Query", ["query"]));
        Assert.Equal("Sheet1", SheetName.Fit("''", []));
        Assert.Equal(31, SheetName.Fit(new string('x', 40), [new string('x', 31)]).Length);
    }

    [Fact]
    public void AnEmoji_NamesASheet_AndNoCutSplitsOne()
    {
        // 2026-10-29, review P2-7 and P3-11.
        Assert.False(SheetName.Problem("Sales 📈"));
        Assert.True(SheetName.Problem("half \uD83D"));
        Assert.True(SheetName.Problem("tab\there"));
        Assert.Equal("Sales 📈", SheetName.Fit("Sales 📈", []));
        Assert.Equal("half -", SheetName.Fit("half \uD83D", []));
        string thirty = new string('x', 30) + "📈";   // the pair at 30-31: a cut at 31 drops it whole
        Assert.Equal(new string('x', 30), SheetName.Fit(thirty, []));
        string near = new string('x', 26) + "📈" + "yy";   // " (2)" makes room at 27: the pair at 26-27 goes whole
        Assert.Equal(new string('x', 26) + " (2)", SheetName.Fit(near, [near]));
        Assert.Equal(new string('a', 59), FileNames.Safe(new string('a', 59) + "📈"));
        Assert.Equal("Weekly-Notes", FileNames.Safe("Weekly Notes", lowerCase: false));
    }

    // ── a new workbook ──────────────────────────────────────────────────────

    [Fact]
    public void NewWorkbook_ReadsBack_TypesHeaderAndEscapes_AndValidates()
    {
        byte[] book = NewBook(["Id", "Name", "When", "Paid", "Total", ""],
        [
            [XlsxValue.From(1234567890123456789L), T("a_x0041_b"), XlsxValue.From(new DateOnly(2026, 10, 29)), XlsxValue.From(true), XlsxValue.From(12.5m), T(" edge ")],
            [XlsxValue.From(2L), T("line\ntwo"), XlsxValue.From(new DateTime(2026, 10, 29, 14, 30, 0)), XlsxValue.From(false), XlsxValue.Blank, XlsxValue.From(new DateTime(1899, 1, 1))],
        ]);
        OfficeSchema.ValidWorkbook(book);
        string answer = Read(book, new OfficeRequest(Sheet: "Data"));
        Assert.Contains("| 1 | Id | Name | When | Paid | Total | (column 6) |", answer, StringComparison.Ordinal);
        Assert.Contains("| 2 | 1234567890123456789 | a_x0041_b | 2026-10-29 | TRUE | 12.5 |  edge  |", answer, StringComparison.Ordinal);
        Assert.Contains("| 3 | 2 | line\\ntwo | 2026-10-29T14:30:00 | FALSE |  | 1899-01-01 |", answer, StringComparison.Ordinal);
        string sheet = Part(book, "xl/worksheets/sheet1.xml");
        Assert.Contains("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\" />", sheet, StringComparison.Ordinal);
        Assert.Contains("numberStoredAsText=\"1\"", sheet, StringComparison.Ordinal);
        Assert.Contains("<ignoredError sqref=\"A1:A3\"", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<dimension", sheet, StringComparison.Ordinal);
        Assert.Contains("_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'Data'!$A$1:$F$3<", Part(book, "xl/workbook.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void NewWorkbook_Properties_NameNoPerson_AndCarryTheClock()
    {
        string core = Part(NewBook(["A"], []), "docProps/core.xml");
        Assert.DoesNotContain("creator", core, StringComparison.Ordinal);
        Assert.DoesNotContain("lastModifiedBy", core, StringComparison.Ordinal);
        Assert.Contains("<dc:title>Report</dc:title>", core, StringComparison.Ordinal);
        Assert.Contains(">2026-09-11T21:05:30Z</dcterms:created>", core, StringComparison.Ordinal);
        Assert.Contains("<Application>NeonSidekick</Application>", Part(NewBook(["A"], []), "docProps/app.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void NewWorkbook_HeaderOnly_AndColumnRules()
    {
        byte[] book = NewBook(["A", "a", "B"], []);
        OfficeSchema.ValidWorkbook(book);
        Assert.Contains("| 1 | A | a (2) | B |", Read(book, new OfficeRequest(Sheet: "Data")), StringComparison.Ordinal);
        using var buffer = new MemoryStream();
        using var writer = new ExcelWriter(buffer, "S", Clock);
        Assert.Equal(OfficeText.NoColumns, writer.Sheet.Begin([]));
        Assert.StartsWith("Error: the result has 16,385 columns", writer.Sheet.Begin([.. Enumerable.Repeat("c", CellReference.MaxColumn + 1)]), StringComparison.Ordinal);
    }

    [Fact]
    public void NumberFormats_ReadOurFormatsAsDatesAndTimes()
    {
        Assert.Equal(CellFormat.Date, NumberFormats.ClassifyCode(ExcelWriter.FormatCodes[0]));
        Assert.Equal(CellFormat.DateTime, NumberFormats.ClassifyCode(ExcelWriter.FormatCodes[1]));
        Assert.Equal(CellFormat.Time, NumberFormats.ClassifyCode(ExcelWriter.FormatCodes[2]));
        Assert.Equal(CellFormat.Elapsed, NumberFormats.ClassifyCode(ExcelWriter.FormatCodes[3]));
        byte[] book = NewBook(["T", "D"], [[XlsxValue.From(new TimeOnly(18, 0)), XlsxValue.From(TimeSpan.FromHours(36))]]);
        Assert.Contains("| 2 | 18:00:00 | 36:00:00 |", Read(book, new OfficeRequest(Sheet: "Data")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.5e2", "150")]
    [InlineData("0.000", "0.000")]
    [InlineData("12.50", "12.50")]
    [InlineData("1e-30", "1E-30")]
    [InlineData("-0.25", "-0.25")]
    public void ANumberThatKeepsItsDigits_IsANumber(string text, string written)
    {
        // 2026-10-29, review P1-2/P2-5.
        Assert.True(XlsxValue.TryExcelNumber(text, out var value));
        Assert.Equal((XlsxKind.Number, written), (value.Kind, value.Text));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("1e-400")]
    [InlineData("1234567890123456")]
    [InlineData("0.1000000000000000000000000000001")]
    [InlineData("abc")]
    public void ANumberThatWouldLoseDigits_StaysText(string text)
    {
        Assert.False(XlsxValue.TryExcelNumber(text, out _));
        Assert.Equal((XlsxKind.Text, text), (XlsxValue.OfNumberText(text).Kind, XlsxValue.OfNumberText(text).Text));
        var judge = new ColumnJudge();
        judge.Observe(0, new RawCell(text));
        Assert.Equal(XlsxKind.Text, judge.Convert(0, new RawCell(text)).Kind);
        var json = new ColumnJudge();
        json.Observe(0, new RawCell(text, RawKind.Number));
        Assert.Equal(XlsxKind.Text, json.Convert(0, new RawCell(text, RawKind.Number)).Kind);
    }

    [Fact]
    public void ALargeWholeNumber_ShowsEveryDigit_InANewAndAnAddedSheet()
    {
        // 2026-10-29, review P6-1: General shows a 13-digit barcode as 4.00638E+12.
        byte[] book = NewBook(["Code", "Price", "Small"], [[XlsxValue.From(4006381333931L), XlsxValue.From(123456789012.5m), XlsxValue.From(10_000_000_000L)]]);
        OfficeSchema.ValidWorkbook(book);
        string sheet = Part(book, "xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"A2\" s=\"6\"><v>4006381333931</v>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"B2\"><v>", sheet, StringComparison.Ordinal);   // a fraction: General
        Assert.Contains("<c r=\"C2\"><v>", sheet, StringComparison.Ordinal);   // 11 digits: General shows them all
        Assert.Equal("1", XfFormat(book, 6));
        Assert.Contains("| 2 | 4006381333931 |", Read(book, new OfficeRequest(Sheet: "Data")), StringComparison.Ordinal);

        byte[] added = Append(book, "More", replace: false, out _, out _, fill: rows => rows.Add([XlsxValue.From(4006381333931L), XlsxValue.Blank]));
        OfficeSchema.ValidWorkbook(added);
        string more = Part(added, "xl/worksheets/sheet2.xml");
        var style = System.Text.RegularExpressions.Regex.Match(more, "<c r=\"A2\" s=\"(\\d+)\"><v>4006381333931</v>");
        Assert.True(style.Success, more);
        Assert.Equal("1", XfFormat(added, int.Parse(style.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>The number format of <c>cellXfs</c>' xf at <paramref name="index"/>.</summary>
    private static string? XfFormat(byte[] book, int index)
    {
        var styles = System.Xml.Linq.XDocument.Parse(Part(book, "xl/styles.xml"));
        System.Xml.Linq.XNamespace s = SheetWriter.Ns;
        return styles.Root!.Element(s + "cellXfs")!.Elements(s + "xf").ElementAt(index).Attribute("numFmtId")?.Value;
    }

    [Fact]
    public void ALongDuration_IsWideEnough_AndANumericHeader_IsWaived()
    {
        // 2026-10-29, review P2-12 and P6-7.
        byte[] book = NewBook(["D", "2024"], [[XlsxValue.From(TimeSpan.FromHours(1000)), T("x")]]);
        string sheet = Part(book, "xl/worksheets/sheet1.xml");
        Assert.Contains("<col min=\"1\" max=\"1\" width=\"10\" customWidth=\"1\" />", sheet, StringComparison.Ordinal);
        Assert.Contains("<ignoredError sqref=\"B1:B2\"", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_StreamToTheStream_BeforeTheLastIsAdded()
    {
        var sink = new MemoryStream();
        using var writer = new ExcelWriter(sink, "S", Clock);
        writer.Sheet.Begin(["n"]);
        long before = sink.Length;
        for (int i = 0; i < SheetWriter.WidthRows + 20_000; i++)
        {
            writer.Sheet.Add([XlsxValue.From(i)]);
        }

        Assert.True(sink.Length > before + 1000, "the rows reached the stream before the sheet was finished");
    }

    [Fact]
    public void LongCell_CutAndCounted()
    {
        SheetResult? result = null;
        byte[] book = NewBook(["Long"], [[T(new string('z', 40_000))]], result: r => result = r);
        Assert.Equal(1, result!.CutCells);
        Assert.Contains("1 cell cut at Excel's 32,767 characters", OfficeText.Notes(result), StringComparison.Ordinal);
        Assert.Contains(new string('z', 100) + "…", Part(book, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    // ── an existing workbook ────────────────────────────────────────────────

    /// <summary>
    /// A workbook as Excel writes one: <c>mc:Ignorable</c> prefixes, an <c>x15ac:absPath</c> choice, an <c>extLst</c>, no <c>definedNames</c>
    /// and no <c>numFmts</c>, a calc chain, a 1904 date system when asked, a plain sheet, a sheet with a table, a chart sheet and a part named
    /// <c>Sheet2.XML</c>.
    /// </summary>
    internal static byte[] ExcelLike(bool date1904 = false, string definedNames = "", bool locked = false, string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
    {
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        const string ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        const string pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        string workbook =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<workbook xmlns=\"{ns}\" xmlns:r=\"{r}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"x15 xr xr6 xr10 xr2\" xmlns:x15=\"http://schemas.microsoft.com/office/spreadsheetml/2010/11/main\" xmlns:xr=\"http://schemas.microsoft.com/office/spreadsheetml/2014/revision\" xmlns:xr6=\"http://schemas.microsoft.com/office/spreadsheetml/2016/revision6\" xmlns:xr10=\"http://schemas.microsoft.com/office/spreadsheetml/2016/revision10\" xmlns:xr2=\"http://schemas.microsoft.com/office/spreadsheetml/2015/revision2\">"
            + "<fileVersion appName=\"xl\" lastEdited=\"7\" lowestEdited=\"7\" rupBuild=\"28228\"/>"
            + $"<workbookPr{(date1904 ? " date1904=\"1\"" : "")} defaultThemeVersion=\"166925\"/>"
            + "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"x15\"><x15ac:absPath url=\"C:\\Users\\x\\\" xmlns:x15ac=\"http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac\"/></mc:Choice></mc:AlternateContent>"
            + "<xr:revisionPtr revIDLastSave=\"0\" documentId=\"8_{A}\" xr6:coauthVersionLast=\"47\" xr6:coauthVersionMax=\"47\" xr10:uidLastSave=\"{00000000-0000-0000-0000-000000000000}\"/>"
            + (locked ? "<workbookProtection lockStructure=\"1\"/>" : "")
            + "<bookViews><workbookView xWindow=\"-120\" yWindow=\"-120\" windowWidth=\"29040\" windowHeight=\"15720\" xr2:uid=\"{1}\"/></bookViews>"
            + "<sheets><sheet name=\"Plain\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Tabled\" sheetId=\"3\" r:id=\"rId2\"/><sheet name=\"Chart1\" sheetId=\"4\" r:id=\"rId3\"/></sheets>"
            + definedNames
            + "<calcPr calcId=\"191029\"/>"
            + "<extLst><ext uri=\"{140A7094-0E35-4892-8432-C4D2E57EDEB5}\" xmlns:x15=\"http://schemas.microsoft.com/office/spreadsheetml/2010/11/main\"><x15:workbookPr chartTrackingRefBase=\"1\"/></ext></extLst>"
            + "</workbook>";
        string styles =
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<styleSheet xmlns=\"{ns}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"x14ac x16r2 xr\" xmlns:x14ac=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac\" xmlns:x16r2=\"http://schemas.microsoft.com/office/spreadsheetml/2015/02/main\" xmlns:xr=\"http://schemas.microsoft.com/office/spreadsheetml/2014/revision\">"
            + "<fonts count=\"1\" x14ac:knownFonts=\"1\"><font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Aptos Narrow\"/><family val=\"2\"/><scheme val=\"minor\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>"
            + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles><dxfs count=\"0\"/><tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium2\" defaultPivotStyle=\"PivotStyleLight16\"/>"
            + "<extLst><ext uri=\"{EB79DEF2-80B8-43e5-95BD-54CBDDF9020C}\" xmlns:x14=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/main\"><x14:slicerStyles defaultSlicerStyle=\"SlicerStyleLight1\"/></ext></extLst></styleSheet>";
        string sheet(string data) => $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<worksheet xmlns=\"{ns}\" xmlns:r=\"{r}\"><sheetData>{data}</sheetData></worksheet>";
        string tabled = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<worksheet xmlns=\"{ns}\" xmlns:r=\"{r}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>H</t></is></c></row></sheetData><tableParts count=\"1\"><tablePart r:id=\"rId1\"/></tableParts></worksheet>";
        return TestOffice.Zip(
        [
            ("[Content_Types].xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Types xmlns=\"{ct}\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/Sheet2.XML\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/chartsheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.chartsheet+xml\"/>"
                + "<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>"
                + "<Override PartName=\"/xl/tables/table1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                + "<Override PartName=\"/xl/calcChain.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml\"/></Types>"),
            ("_rels/.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"{pr}\"><Relationship Id=\"rId1\" Type=\"{rel}officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", workbook),
            ("xl/_rels/workbook.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"{pr}\"><Relationship Id=\"rId1\" Type=\"{rel}worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + $"<Relationship Id=\"rId2\" Type=\"{rel}worksheet\" Target=\"worksheets/Sheet2.XML\"/><Relationship Id=\"rId3\" Type=\"{rel}chartsheet\" Target=\"chartsheets/sheet1.xml\"/>"
                + $"<Relationship Id=\"rId5\" Type=\"{rel}styles\" Target=\"styles.xml\"/><Relationship Id=\"rId6\" Type=\"{rel}calcChain\" Target=\"calcChain.xml\"/></Relationships>"),
            ("xl/styles.xml", styles),
            ("xl/worksheets/sheet1.xml", sheet("<row r=\"1\"><c r=\"A1\" s=\"1\"><v>45000</v></c><c r=\"B1\"><f>A1+1</f><v>45001</v></c></row>")),
            ("xl/worksheets/Sheet2.XML", tabled),
            ("xl/worksheets/_rels/Sheet2.XML.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"{pr}\"><Relationship Id=\"rId1\" Type=\"{rel}table\" Target=\"../tables/table1.xml\"/></Relationships>"),
            ("xl/tables/table1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<table xmlns=\"{ns}\" id=\"1\" name=\"Table1\" displayName=\"Table1\" ref=\"A1:A2\" totalsRowShown=\"0\"><autoFilter ref=\"A1:A2\"/><tableColumns count=\"1\"><tableColumn id=\"1\" name=\"H\"/></tableColumns><tableStyleInfo name=\"TableStyleMedium2\" showFirstColumn=\"0\" showLastColumn=\"0\" showRowStripes=\"1\" showColumnStripes=\"0\"/></table>"),
            ("xl/chartsheets/sheet1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<chartsheet xmlns=\"{ns}\" xmlns:r=\"{r}\"><sheetViews><sheetView workbookViewId=\"0\"/></sheetViews><drawing r:id=\"rId1\"/></chartsheet>"),
            ("xl/chartsheets/_rels/sheet1.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"{pr}\"><Relationship Id=\"rId1\" Type=\"{rel}drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>"),
            ("xl/drawings/drawing1.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\"/>"),
            ("xl/calcChain.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<calcChain xmlns=\"{ns}\"><c r=\"B1\" i=\"1\"/></calcChain>"),
        ]);
    }

    internal static byte[] Append(byte[] existing, string sheet, bool replace, out WorkbookAppender? opened, out string? refusal, Action<ISheetRows>? fill = null, Action<SheetResult>? result = null)
    {
        using var input = new MemoryStream(existing, writable: false);
        opened = WorkbookAppender.Open(input, "book.xlsx", OfficeLimits.Default, out refusal);
        if (opened is null)
        {
            return [];
        }

        using var appender = opened;
        var target = replace ? appender.Find(sheet) : null;
        if (target is not null && appender.ReplaceProblem(target) is { } problem)
        {
            refusal = problem;
            return [];
        }

        using var output = new MemoryStream();
        refusal = appender.Start(output, sheet, target, Clock);
        Assert.Null(refusal);
        Assert.Null(appender.Sheet.Begin(["Name", "When"]));
        if (fill is null)
        {
            appender.Sheet.Add([T("x"), XlsxValue.From(new DateOnly(1904, 1, 2))]);
        }
        else
        {
            fill(appender.Sheet);
        }

        var done = appender.Finish();
        result?.Invoke(done);
        return output.ToArray();
    }

    [Fact]
    public void AddedSheet_KeepsEverything_InSchemaOrder_AndValidates()
    {
        byte[] book = Append(ExcelLike(), "New", replace: false, out _, out _);
        OfficeSchema.ValidWorkbook(book);
        string workbook = Part(book, "xl/workbook.xml");
        Assert.Contains("mc:Ignorable=\"x15 xr xr6 xr10 xr2\"", workbook, StringComparison.Ordinal);
        Assert.Contains("<xr:revisionPtr", workbook, StringComparison.Ordinal);
        Assert.Contains("<x15:workbookPr chartTrackingRefBase=\"1\" />", workbook, StringComparison.Ordinal);
        Assert.Contains("<sheet name=\"New\" sheetId=\"5\" r:id=\"rId4\" /></sheets><definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"3\" hidden=\"1\">'New'!$A$1:$B$2</definedName></definedNames><calcPr", workbook, StringComparison.Ordinal);
        Assert.NotNull(new ZipArchive(new MemoryStream(book)).GetEntry("xl/worksheets/sheet3.xml"));   // sheet2 is taken by Sheet2.XML, ignoring case
        string styles = Part(book, "xl/styles.xml");
        Assert.Contains("<numFmts count=\"4\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd\" />", styles, StringComparison.Ordinal);
        Assert.Contains("<fonts count=\"2\" x14ac:knownFonts=\"1\">", styles, StringComparison.Ordinal);
        Assert.Contains("<font><b /><sz val=\"11\" /><color theme=\"1\" /><name val=\"Aptos Narrow\" />", styles, StringComparison.Ordinal);
        Assert.Contains("<cellXfs count=\"8\">", styles, StringComparison.Ordinal);   // theirs, the header, four formats, a whole number (P6-1)
        Assert.DoesNotContain("calcChain", Part(book, "[Content_Types].xml"), StringComparison.Ordinal);
        Assert.DoesNotContain("calcChain", Part(book, "xl/_rels/workbook.xml.rels"), StringComparison.Ordinal);
        Assert.Null(new ZipArchive(new MemoryStream(book)).GetEntry("xl/calcChain.xml"));
        string answer = Read(book, new OfficeRequest(Sheet: "Plain"));
        Assert.Contains("| 1 | 2023-03-15 | 45001 |", answer, StringComparison.Ordinal);   // the original cells keep their formats
    }

    [Fact]
    public void AddedSheet_In1904Workbook_DatesMatch()
    {
        byte[] book = Append(ExcelLike(date1904: true), "New", replace: false, out _, out _);
        Assert.Contains("| 2 | x | 1904-01-02 |", Read(book, new OfficeRequest(Sheet: "New")), StringComparison.Ordinal);
        Assert.Contains("<v>1</v>", Part(book, "xl/worksheets/sheet3.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacedSheet_KeepsPositionAndId_DropsItsFilterName()
    {
        string names = "<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">Plain!$A$1:$B$1</definedName><definedName name=\"Rate\">Plain!$A$1</definedName></definedNames>";
        byte[] book = Append(ExcelLike(definedNames: names), "plain", replace: true, out _, out _);
        OfficeSchema.ValidWorkbook(book);
        string workbook = Part(book, "xl/workbook.xml");
        Assert.Contains("<sheet name=\"Plain\" sheetId=\"1\" r:id=\"rId1\" />", workbook, StringComparison.Ordinal);
        Assert.Contains("<definedNames><definedName name=\"Rate\">Plain!$A$1</definedName><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'Plain'!$A$1:$B$2</definedName></definedNames>", workbook, StringComparison.Ordinal);
        Assert.Contains("| 2 | x | 1904-01-02 |", Read(book, new OfficeRequest(Sheet: "Plain")), StringComparison.Ordinal);
        Assert.Null(new ZipArchive(new MemoryStream(book)).GetEntry("xl/calcChain.xml"));
    }

    [Fact]
    public void Replace_RefusesATableSheet_AndAChartSheet()
    {
        Append(ExcelLike(), "Tabled", replace: true, out _, out string? tabled);
        Assert.Equal(OfficeText.SheetNotPlain("book.xlsx", "Tabled", "a table", "Tabled new"), tabled);
        Append(ExcelLike(), "Chart1", replace: true, out _, out string? chart);
        Assert.StartsWith("Error: \"Chart1\" in 'book.xlsx' is not a worksheet", chart, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_RefusesStrict_Locked_Encrypted_NotZip_AndABomb()
    {
        Append(ExcelLike(ns: "http://purl.oclc.org/ooxml/spreadsheetml/main"), "N", false, out _, out string? strict);
        Assert.Equal(OfficeText.StrictWorkbook("book.xlsx"), strict);
        Append(ExcelLike(locked: true), "N", false, out _, out string? locked);
        Assert.Equal(OfficeText.StructureLocked("book.xlsx"), locked);
        Append([0xD0, 0xCF, 0x11, 0xE0, 0, 0, 0, 0, 0], "N", false, out _, out string? encrypted);
        Assert.Equal(OfficeText.NotAWorkbook("book.xlsx", encrypted: true), encrypted);
        Append(Encoding.UTF8.GetBytes("just text"), "N", false, out _, out string? text);
        Assert.Equal(OfficeText.NotAWorkbook("book.xlsx", encrypted: false), text);

        // A bomb in an entry that is only copied is caught by the counted copy.
        var parts = new List<(string, string)>();
        using (var zip = new ZipArchive(new MemoryStream(ExcelLike()), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                parts.Add((entry.FullName, reader.ReadToEnd()));
            }
        }

        parts.Add(("xl/media/bomb.xml", new string('a', 2_000_000)));
        byte[] bombed = TestOffice.Zip(parts);
        using var input = new MemoryStream(bombed, writable: false);
        using var appender = WorkbookAppender.Open(input, "book.xlsx", new OfficeLimits(100_000_000, 1_000_000, 1_500_000), out _)!;
        string? copied = appender.Start(new MemoryStream(), "N", null, Clock);
        Assert.Equal(OfficeText.PartTooLarge("book.xlsx", "xl/media/bomb.xml"), copied);
    }

    // ── 2026-10-29, the review's appending fixes ─────────────────────────────

    /// <summary><paramref name="book"/> with one entry's text changed, or an entry of bytes added (stored as is).</summary>
    internal static byte[] Patched(byte[] book, string name, Func<string, string>? change = null, byte[]? add = null)
    {
        var stream = new MemoryStream();
        stream.Write(book);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(name);
            if (change is not null)
            {
                string text;
                using (var reader = new StreamReader(entry!.Open()))
                {
                    text = reader.ReadToEnd();
                }

                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(change(text));
            }
            else if (add is not null)
            {
                entry?.Delete();
                using var target = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                target.Write(add);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void ACopiedPicture_IsStored_NotDeflatedAgain()
    {
        // P6-5.
        var noise = new byte[8192];
        new Random(1).NextBytes(noise);
        byte[] book = Patched(ExcelLike(), "xl/media/image1.png", add: noise);
        byte[] added = Append(book, "New", replace: false, out _, out string? refusal);
        Assert.Null(refusal);
        var entry = new ZipArchive(new MemoryStream(added)).GetEntry("xl/media/image1.png")!;
        Assert.Equal(entry.Length, entry.CompressedLength);
        Assert.Equal(CompressionLevel.NoCompression, WorkbookAppender.CopyLevel((100, 99)));
        Assert.Equal(CompressionLevel.Fastest, WorkbookAppender.CopyLevel((100, 10)));
        Assert.Equal(CompressionLevel.Fastest, WorkbookAppender.CopyLevel(null));
    }

    [Fact]
    public void AStrayStylesPart_IsNotWrittenTwice()
    {
        // P1-14a: a styles.xml no relationship names is still taken.
        byte[] book = Patched(ExcelLike(), "xl/_rels/workbook.xml.rels", rels => System.Text.RegularExpressions.Regex.Replace(rels, "<Relationship Id=\"rId5\"[^>]*/>", ""));
        byte[] added = Append(book, "New", replace: false, out _, out string? refusal);
        Assert.Null(refusal);
        var zip = new ZipArchive(new MemoryStream(added));
        Assert.Single(zip.Entries, e => e.FullName == "xl/styles.xml");
        Assert.NotNull(zip.GetEntry("xl/styles2.xml"));
        Assert.Contains("Target=\"styles2.xml\"", Part(added, "xl/_rels/workbook.xml.rels"), StringComparison.Ordinal);
        Assert.Contains("PartName=\"/xl/styles2.xml\"", Part(added, "[Content_Types].xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeaderFont_TakesTheFace_NotTheUnderline()
    {
        // P1-14b.
        byte[] book = Patched(ExcelLike(), "xl/styles.xml", styles => styles.Replace("<font><sz val=\"11\"/>", "<font><u/><strike/><sz val=\"11\"/>", StringComparison.Ordinal));
        byte[] added = Append(book, "New", replace: false, out _, out _);
        string styles = Part(added, "xl/styles.xml");
        Assert.Contains("<font><b /><sz val=\"11\" /><color theme=\"1\" /><name val=\"Aptos Narrow\" /><family val=\"2\" /><scheme val=\"minor\" /></font>", styles, StringComparison.Ordinal);
        Assert.Contains("<font><u /><strike />", styles, StringComparison.Ordinal);   // theirs as it was
    }

    [Fact]
    public void AReplacedSheet_RecalculatesOnOpen_AnAddedOneLeavesItAsItWas()
    {
        // P4-1: a formula over the replaced cells kept its old value.
        byte[] replaced = Append(ExcelLike(), "Plain", replace: true, out _, out string? refusal);
        Assert.Null(refusal);
        OfficeSchema.ValidWorkbook(replaced);
        Assert.Contains("<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\" />", Part(replaced, "xl/workbook.xml"), StringComparison.Ordinal);
        Assert.DoesNotContain("fullCalcOnLoad", Part(Append(ExcelLike(), "New", replace: false, out _, out _), "xl/workbook.xml"), StringComparison.Ordinal);
        // A workbook with no calcPr gets one after definedNames.
        byte[] ours = Append(NewBook(["A"], [[T("x")]]), "Data", replace: true, out _, out refusal);
        Assert.Null(refusal);
        OfficeSchema.ValidWorkbook(ours);
        Assert.Contains("</definedNames><calcPr fullCalcOnLoad=\"1\" />", Part(ours, "xl/workbook.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void APasswordToModify_ARevisionLock_AndAProtectedSheet_AreNotWrittenOver()
    {
        // P4-5.
        byte[] reserved = Patched(ExcelLike(), "xl/workbook.xml", w => w.Replace("<workbookPr", "<fileSharing userName=\"x\" reservationPassword=\"CC3D\"/><workbookPr", StringComparison.Ordinal));
        Append(reserved, "New", replace: false, out _, out string? refusal);
        Assert.Equal(OfficeText.WriteReserved("book.xlsx"), refusal);
        byte[] recommended = Patched(ExcelLike(), "xl/workbook.xml", w => w.Replace("<workbookPr", "<fileSharing readOnlyRecommended=\"1\"/><workbookPr", StringComparison.Ordinal));
        Append(recommended, "New", replace: false, out _, out refusal);
        Assert.Null(refusal);
        byte[] revision = Patched(ExcelLike(locked: true), "xl/workbook.xml", w => w.Replace("lockStructure", "lockRevision", StringComparison.Ordinal));
        Append(revision, "New", replace: false, out _, out refusal);
        Assert.Equal(OfficeText.StructureLocked("book.xlsx"), refusal);
        byte[] guarded = Patched(ExcelLike(), "xl/worksheets/sheet1.xml", w => w.Replace("</sheetData></worksheet>", "</sheetData><sheetProtection sheet=\"1\" objects=\"1\"/></worksheet>", StringComparison.Ordinal));
        Append(guarded, "Plain", replace: true, out _, out refusal);
        Assert.Equal(OfficeText.SheetProtected("book.xlsx", "Plain", "Plain new"), refusal);
        Append(guarded, "Beside", replace: false, out _, out refusal);   // a sheet beside it is fine
        Assert.Null(refusal);
    }

    [Fact]
    public void ADamagedContentTypesPart_IsARefusal_BeforeAnythingIsWritten()
    {
        // P7-1.
        byte[] book = Patched(ExcelLike(), "[Content_Types].xml", _ => "<Types");
        Append(book, "New", replace: false, out var opened, out string? refusal);
        Assert.Null(opened);
        Assert.Equal(OfficeText.Damaged("book.xlsx", "[Content_Types].xml"), refusal);
    }
}
