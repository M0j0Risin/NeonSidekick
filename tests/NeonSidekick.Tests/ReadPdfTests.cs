using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Pdf;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// Reading PDFs (2026-10-14, the PDF plan's phase 1): a PDF is never text (<see cref="PdfFile"/>, <see cref="WorkingDirectory.LooksBinary"/>),
/// the hints that point at <c>read_pdf</c>, <see cref="PdfPageRange"/>, <see cref="PdfFind"/>, the words of <see cref="PdfText"/>, the cache
/// (<see cref="PdfReading"/>), and <see cref="ReadPdfTool"/> over a real temp folder with the fake browser reading.
/// </summary>
public sealed class ReadPdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "readpdf-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new();
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly PdfReading _reading;
    private readonly ReadPdfTool _tool;

    public ReadPdfTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _reading = new PdfReading(_browser, () => _browser.Executable);
        _tool = new ReadPdfTool(_files, _reading, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] PdfHead = Encoding.ASCII.GetBytes("%PDF-1.7\n% made for a test\n1 0 obj << >> endobj\n%%EOF\n");

    private string PutPdf(string relative, byte[]? bytes = null)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes ?? PdfHead);
        return full;
    }

    private void Pages(params string[] texts)
    {
        _browser.PdfReadPages.Clear();
        for (int i = 0; i < texts.Length; i++)
        {
            _browser.PdfReadPages.Add(new PdfPageRead(i + 1, texts[i], FakeHeadlessBrowser.TinyPng, 1, 1));
        }
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in values)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private async Task<object?> Call(params (string Name, object? Value)[] values) => await _tool.InvokeAsync(Args(values), CancellationToken.None);

    private async Task<string> Text(params (string Name, object? Value)[] values) => await Call(values) switch
    {
        ToolImageResult pictures => pictures.Text,
        var other => (string)other!,
    };

    // ── a PDF is never text ─────────────────────────────────────────────────

    [Fact]
    public void LooksLikePdf_IsTheHeaderAtTheFirstByte_AndADigit()
    {
        Assert.True(PdfFile.LooksLikePdf("%PDF-1.7\nrest"u8));
        Assert.False(PdfFile.LooksLikePdf("%PDF-x"u8));
        Assert.False(PdfFile.LooksLikePdf(" %PDF-1.7"u8));
        Assert.False(PdfFile.LooksLikePdf("%PDF-"u8));
        Assert.False(PdfFile.LooksLikePdf([]));
    }

    [Fact]
    public void HasPdfHeader_ToleratesJunkUpToTheWindow()
    {
        Assert.True(PdfFile.HasPdfHeader([.. new byte[300].Select(_ => (byte)'x'), .. "%PDF-1.4"u8.ToArray()]));
        Assert.False(PdfFile.HasPdfHeader([.. new byte[2000].Select(_ => (byte)'x'), .. "%PDF-1.4"u8.ToArray()]));
        Assert.True(PdfFile.IsPdfPath("A/Report.PDF"));
        Assert.False(PdfFile.IsPdfPath("report.pdf.txt"));
    }

    [Fact]
    public void IsPdfFile_NeedsTheExtensionAndTheHeader()
    {
        Assert.True(PdfFile.IsPdfFile(PutPdf("a.pdf")));
        Assert.False(PdfFile.IsPdfFile(PutPdf("b.pdf", "hello"u8.ToArray())));
        Assert.False(PdfFile.IsPdfFile(PutPdf("c.txt")));
        Assert.False(PdfFile.IsPdfFile(Path.Combine(_root, "missing.pdf")));
    }

    /// <summary>The plan's gap 1: a PDF with no NUL in its first 8 KB read as its raw syntax; a source file that mentions the header stays text.</summary>
    [Fact]
    public void APdfWithoutANul_IsNotText_ButASourceThatMentionsTheHeaderIs()
    {
        PutPdf("ascii.pdf");
        Assert.Equal(FileOutcome.NotText, _files.ReadText("ascii.pdf", null, null).Outcome);
        Assert.False(WorkingDirectory.IsTextFile(Path.Combine(_root, "ascii.pdf")));

        File.WriteAllText(Path.Combine(_root, "Probe.cs"), "// a probe\n// of PDFs\nbool pdf = bytes.StartsWith(\"%PDF-1.7\"u8);\n");
        var read = _files.ReadText("Probe.cs", null, null);
        Assert.Equal(FileOutcome.Ok, read.Outcome);
        Assert.Contains("%PDF-1.7", read.Text, StringComparison.Ordinal);
        Assert.True(WorkingDirectory.IsTextFile(Path.Combine(_root, "Probe.cs")));
    }

    [Fact]
    public void TheHints_PointAPdfAtReadPdf()
    {
        Assert.Equal("Error: 'a.pdf' is not a text file; use read_pdf to read its text", FileText.NotText("a.pdf"));
        Assert.Equal("Error: 'a.pdf' could not be read as an image; it is a PDF: use read_pdf (pictures: true for its pages)", FileText.NotAnImage("a.pdf"));
        Assert.Equal("Error: 'a.png' could not be read as an image", FileText.NotAnImage("a.png"));
        Assert.EndsWith("; read_pdf reads it", WebText.Downloaded(new WriteResult(FileOutcome.Ok, "docs/a.pdf", 10, false), "https://e.com/a.pdf", "application/pdf"), StringComparison.Ordinal);
    }

    // ── pages ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3", 10, "3")]
    [InlineData("2-5", 10, "2,3,4,5")]
    [InlineData("5-", 7, "5,6,7")]
    [InlineData("1,3,7-9", 20, "1,3,7,8,9")]
    [InlineData(" 9 - 10 , 2 ", 20, "2,9,10")]
    [InlineData("3,3,2-3", 20, "2,3")]
    [InlineData("8-12", 9, "8,9")]
    public void PageRange_ResolvesAgainstTheCount(string spec, int count, string expected)
    {
        Assert.True(PdfPageRange.TryParse(spec, out var range, out _));
        Assert.Equal(expected, string.Join(",", range!.Resolve(count)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("a")]
    [InlineData("5-2")]
    [InlineData("1,,2")]
    [InlineData("-3")]
    [InlineData("1-200000")]
    public void PageRange_RefusesWhatIsNotOne(string spec)
    {
        Assert.False(PdfPageRange.TryParse(spec, out _, out string error));
        Assert.Equal(PdfText.BadPages(spec), error);
    }

    /// <summary>
    /// 2026-10-22, review S1: a first page near the top made the read's last page <see cref="int.MaxValue"/>, whose <c>int</c> walk never
    /// ended (the process filled a set until it died). The walk now counts in <see cref="long"/> and the sum is held.
    /// </summary>
    [Fact]
    public void PageRange_NearTheTop_ResolvesAndEnds()
    {
        Assert.True(PdfPageRange.TryParse("2147481648-", out var range, out _));
        int last = PdfPageRange.LastOf(range!.First, PdfReading.MaxPagesPerRead);
        Assert.Equal(int.MaxValue, last);
        var pages = range.Resolve(last);
        Assert.Equal(PdfReading.MaxPagesPerRead, pages.Count);
        Assert.Equal(int.MaxValue, pages[^1]);
    }

    /// <summary>2026-10-22, review K10: more than <see cref="PdfPageRange.MaxParts"/> parts is refused before anything is resolved.</summary>
    [Fact]
    public void PageRange_TooManyParts_IsRefused()
    {
        string spec = string.Join(",", Enumerable.Repeat("1-2000", PdfPageRange.MaxParts + 1));
        Assert.False(PdfPageRange.TryParse(spec, out _, out string error));
        Assert.Equal(PdfText.TooManyPageParts(PdfPageRange.MaxParts), error);
        Assert.True(PdfPageRange.TryParse(string.Join(",", Enumerable.Repeat("3", PdfPageRange.MaxParts)), out _, out _));
    }

    [Fact]
    public void Text_RunsHeadersAndHeadings()
    {
        Assert.Equal("1–3, 7, 9–12", PdfText.Runs([1, 2, 3, 7, 9, 10, 11, 12]));
        Assert.Equal("1-3,7", PdfText.Spec([1, 2, 3, 7]));
        Assert.Equal("report.pdf (pages 1–12 of 87; next: pages \"13-\")", PdfText.Header("report.pdf", [.. Enumerable.Range(1, 12)], 87, 13));
        Assert.Equal("report.pdf (page 4 of 4)", PdfText.Header("report.pdf", [4], 4, null));
        Assert.Equal("--- page 14 (printed \"xii\") ---", PdfText.PageHeading(14, "xii"));
        Assert.Equal("--- page 3 ---", PdfText.PageHeading(3, "3"));
        Assert.Equal("(page 3 of docs/r.pdf; read_pdf with pictures reads it again)", PdfText.PageNotKept("docs/r.pdf", 3));
    }

    // ── find ────────────────────────────────────────────────────────────────

    [Fact]
    public void Find_MatchesEveryWord_CaseAndAccentsFolded()
    {
        var words = PdfFind.Words("Résumé \"Period\"");
        Assert.Equal(["resume", "period"], words);
        Assert.True(PdfFind.Matches("The RESUME covers the warranty period.", words));
        Assert.False(PdfFind.Matches("The résumé only.", words));
        Assert.Equal("Warranty period: two years", PdfFind.Context("Contents\nWarranty period: two years\nNotes", PdfFind.Words("period")));
        string longLine = new string('a', 300) + " needle " + new string('b', 300);
        string context = PdfFind.Context(longLine, PdfFind.Words("needle"));
        Assert.Contains("needle", context, StringComparison.Ordinal);
        Assert.True(context.Length <= PdfFind.ContextChars + 2);
    }

    // ── reflow for /read (gap 89) ───────────────────────────────────────────

    [Fact]
    public void Reflow_JoinsLines_MendsHyphens_AndKeepsPagesApart()
    {
        Assert.Equal("The warranty covers the whole machine. An example of it.\n\nPage two.",
            PdfReflow.Reflow(["The warranty covers the\nwhole machine. An exam-\nple of it.", "Page two.", ""]));
        // A compound split at a line's end keeps its hyphen (2026-10-22, review T6: "well-" + "known" read "wellknown").
        Assert.Equal("a well-known one", PdfReflow.Join(["a well-", "known one"]));
        Assert.Equal("Cost: 12- 15 units", PdfReflow.Join(["Cost: 12-", "15 units"]));   // a hyphen after a digit stays, joined with a space
        Assert.Equal("See Part-", PdfReflow.Join(["See Part-"]));
        Assert.Equal("Part- One", PdfReflow.Join(["Part-", "One"]));   // a capital next: a real dash, kept
    }

    [Fact]
    public void Reflow_DropsARunningHeaderAndFooter_ButNotOnAShortDocument()
    {
        string[] pages =
        [
            "Acme Manual\nFirst page text.\nPage 1 of 4",
            "Acme Manual\nSecond page text.\nPage 2 of 4",
            "Acme Manual\nThird page text.\nPage 3 of 4",
            "Acme Manual\nFourth page text.\nPage 4 of 4",
        ];
        Assert.Equal("First page text.\n\nSecond page text.\n\nThird page text.\n\nFourth page text.", PdfReflow.Reflow(pages));
        Assert.Equal("Acme Manual One. Page 1\n\nAcme Manual Two. Page 2", PdfReflow.Reflow(["Acme Manual\nOne.\nPage 1", "Acme Manual\nTwo.\nPage 2"]));   // twice is not a pattern
    }

    // ── the tool ────────────────────────────────────────────────────────────

    [Fact]
    public void Tool_IsAReadOnlyQuietFileTool_InTheWorkingDirectory()
    {
        Assert.Contains(ReadPdfTool.ToolName, PlanTools.ReadOnly);
        Assert.Contains(ReadPdfTool.ToolName, ChatScreen.QuietTools);
        Assert.Contains("working directory", _tool.Description, StringComparison.Ordinal);
        Assert.Equal(ReadPdfTool.BaseDescription, _tool.Description);
        var tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, reading: _reading);
        Assert.Equal(ReadFileTool.ToolName, tools[tools.ToList().FindIndex(t => t.Name == ReadPdfTool.ToolName) - 1].Name);
    }

    [Fact]
    public async Task NoPages_StartsWithTheOutline_ThenThePages()
    {
        PutPdf("r.pdf");
        Pages("Introduction text", "Scope text", "Body text");
        _browser.PdfOutline.AddRange([new PdfOutlineItem("Introduction", 1, 1), new PdfOutlineItem("Scope", 2, 2), new PdfOutlineItem("Unresolved", 1, null)]);
        string text = await Text(("path", "r.pdf"));
        Assert.Equal(
            "r.pdf (pages 1–3 of 3)\nOutline:\n- Introduction (page 1)\n  - Scope (page 2)\n- Unresolved\n" +
            "--- page 1 ---\nIntroduction text\n--- page 2 ---\nScope text\n--- page 3 ---\nBody text",
            text);
        var seen = Assert.Single(_browser.PdfReadOptionsSeen);
        Assert.True(seen.Outline);
        Assert.True(seen.Labels);
        Assert.False(seen.Pictures);
    }

    [Fact]
    public async Task Pages_ReadOnlyThose_WithTheirPrintedLabels()
    {
        PutPdf("r.pdf");
        Pages("one", "two", "three", "four");
        _browser.PdfLabels.AddRange(["i", "ii", "1", "2"]);
        string text = await Text(("path", "r.pdf"), ("pages", "2-3"));
        Assert.Equal("r.pdf (pages 2–3 of 4)\n--- page 2 (printed \"ii\") ---\ntwo\n--- page 3 (printed \"1\") ---\nthree", text);
        Assert.Equal([2, 3], _browser.PdfReadOptionsSeen[0].Pages!);
        Assert.False(_browser.PdfReadOptionsSeen[0].Outline);
    }

    [Fact]
    public async Task ASecondCall_ComesFromTheCache_AndAChangedFileIsReadAgain()
    {
        string full = PutPdf("r.pdf");
        Pages("one", "two", "three");
        await Text(("path", "r.pdf"));
        Assert.Single(_browser.PdfReads);

        Assert.Equal("r.pdf (page 2 of 3)\n--- page 2 ---\ntwo", await Text(("path", "r.pdf"), ("pages", "2")));
        Assert.Single(_browser.PdfReads);   // no bytes read, no browser (the plan's gap 97)

        File.WriteAllBytes(full, [.. PdfHead, (byte)' ']);
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(1));
        Pages("ONE", "TWO", "THREE");
        Assert.Equal("r.pdf (page 2 of 3)\n--- page 2 ---\nTWO", await Text(("path", "r.pdf"), ("pages", "2")));
        Assert.Equal(2, _browser.PdfReads.Count);
    }

    [Fact]
    public async Task OnlyTheMissingPages_AreAskedFor()
    {
        PutPdf("r.pdf");
        Pages([.. Enumerable.Range(1, 12).Select(n => "page text " + n)]);
        await Text(("path", "r.pdf"), ("pages", "1-10"));
        await Text(("path", "r.pdf"), ("find", "text"));
        Assert.Equal(2, _browser.PdfReadOptionsSeen.Count);
        Assert.Equal([11, 12], _browser.PdfReadOptionsSeen[1].Pages!);
    }

    [Fact]
    public async Task ALongDocument_ComesBackInPart_WithTheNextPages()
    {
        PutPdf("r.pdf");
        Pages([.. Enumerable.Range(1, 20).Select(n => new string((char)('a' + n % 26), 5_000))]);
        string text = await Text(("path", "r.pdf"));
        string header = text.Split('\n')[0];
        Assert.StartsWith("r.pdf (pages 1–", header, StringComparison.Ordinal);
        Assert.EndsWith(" of 20; next: pages \"7-\")", header, StringComparison.Ordinal);
        Assert.True(text.Length <= WorkingDirectory.MaxReadChars + 200);
    }

    [Fact]
    public async Task APageLongerThanTheCap_IsCutWithANote()
    {
        PutPdf("r.pdf");
        Pages(new string('x', 50_000), "two");
        string text = await Text(("path", "r.pdf"), ("pages", "1-2"));
        // What is left of the pages asked, not an open range (2026-10-22, review T1).
        Assert.StartsWith("r.pdf (page 1 of 2; next: pages \"2\")", text, StringComparison.Ordinal);
        Assert.Contains("(page 1 cut at ", text, StringComparison.Ordinal);
        // The note names where to read on since 2026-10-22 (review S8: it sent a text-only model to pictures).
        Assert.Contains(" of 50,000 characters; next: pages \"1\" with from_char ", text, StringComparison.Ordinal);
    }

    /// <summary>2026-10-22, review S8: from_char reads a long page on from its cut, and needs one page.</summary>
    [Fact]
    public async Task ALongPage_ReadsOnFromItsCut()
    {
        PutPdf("r.pdf");
        Pages(new string('x', 40_000) + new string('y', 10_000), "two");
        string rest = await Text(("path", "r.pdf"), ("pages", "1"), ("from_char", 40000));
        Assert.Contains(new string('y', 10_000), rest, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxx", rest, StringComparison.Ordinal);
        Assert.Equal(PdfText.FromCharNeedsOnePage, await Text(("path", "r.pdf"), ("pages", "1-2"), ("from_char", 5)));
    }

    [Fact]
    public async Task PagesWithNoText_SayToLookAtThem()
    {
        PutPdf("scan.pdf");
        Pages("", "");
        Assert.EndsWith(PdfText.NoTextLayer, await Text(("path", "scan.pdf")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pictures_ComeAfterTheResult_Capped_AndMarkedAsAPdfsPages()
    {
        PutPdf("r.pdf");
        Pages("one", "two", "three", "four");
        _settings.FileViewImageMaxPerCall = 2;
        var answer = Assert.IsType<ToolImageResult>(await Call(("path", "r.pdf"), ("pages", "1-4"), ("pictures", true)));
        Assert.Equal(2, answer.Images.Count);
        Assert.All(answer.Images, i => Assert.Equal("", i.Path));
        Assert.Equal(["r.pdf#1", "r.pdf#2"], answer.Images.Select(i => i.PdfPage));
        Assert.Contains("pictures of pages 1–2 are in the next message; at most 2 a call: ask for pages \"3-4\" with pictures next", answer.Text, StringComparison.Ordinal);
        Assert.Equal([1, 2], _browser.PdfReadOptionsSeen[0].PicturePages!);

        // The part a picture becomes carries the page, and a stored session or a trimmed request names it (gap 21).
        var part = ConversationHistory.ImagePart(answer.Images[0]);
        Assert.Equal(("r.pdf", 1), ConversationHistory.PdfPage(part));
        Assert.Equal(PdfText.PageNotKept("r.pdf", 1), PictureBudget.LeftOutFor(part));
    }

    [Fact]
    public async Task Find_ListsThePagesHoldingEveryWord()
    {
        PutPdf("manual.pdf");
        Pages("Contents", "The warranty lasts\na period of two years", "Nothing here", "Warranty period: see page 2");
        _browser.PdfLabels.AddRange(["i", "1", "2", "3"]);
        string text = await Text(("path", "manual.pdf"), ("find", "warranty period"));
        Assert.Equal(
            "manual.pdf: \"warranty period\" is on 2 pages (searched all 4 pages)\n" +
            "page 2 (printed \"1\"): The warranty lasts\n" +
            "page 4 (printed \"3\"): Warranty period: see page 2",
            text);
        Assert.Equal("manual.pdf: \"absent\" is on none of the pages (searched all 4 pages)", await Text(("path", "manual.pdf"), ("find", "absent")));
        Assert.Equal(PdfText.FindNoPictures, await Text(("path", "manual.pdf"), ("find", "x"), ("pictures", true)));
    }

    [Fact]
    public async Task Find_SaysWhatItSearched_WhenTheReadStoppedShort()
    {
        PutPdf("long.pdf");
        Pages([.. Enumerable.Range(1, 10).Select(n => n == 2 ? "needle" : "hay")]);
        _browser.PdfStopAfter = 4;
        string text = await Text(("path", "long.pdf"), ("find", "needle"));
        Assert.StartsWith("long.pdf: \"needle\" is on 1 page (searched 4 of 10 pages)", text, StringComparison.Ordinal);
        Assert.EndsWith("(stopped after 2 min)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusals()
    {
        Assert.Equal(PdfText.BadPages("0"), await Text(("path", "x.pdf"), ("pages", "0")));
        Assert.Equal(FileText.PathRequired("path"), await Text(("pages", "1")));
        Assert.StartsWith("Error: nothing is at", await Text(("path", "missing.pdf")), StringComparison.Ordinal);

        PutPdf("fake.pdf", "hello, not a PDF"u8.ToArray());
        Assert.Equal(PdfText.NotAPdf("fake.pdf"), await Text(("path", "fake.pdf")));

        PutPdf("r.pdf");
        Pages("one", "two");
        Assert.Equal(PdfText.PastTheEnd("r.pdf", 2, "5-9"), await Text(("path", "r.pdf"), ("pages", "5-9")));

        PutPdf("locked.pdf", [.. PdfHead, (byte)'!']);
        _browser.PdfLocked = true;
        Assert.Equal(PdfText.Locked("locked.pdf"), await Text(("path", "locked.pdf")));
        _browser.PdfLocked = false;

        _browser.Executable = null;
        PutPdf("other.pdf", [.. PdfHead, (byte)'?']);
        Assert.Equal(PdfText.NoBrowserToRead, await Text(("path", "other.pdf")));
    }

    [Fact]
    public async Task ACachedDocument_StillAnswers_WithNoBrowser()
    {
        PutPdf("r.pdf");
        Pages("one", "two");
        await Text(("path", "r.pdf"));
        _browser.Executable = null;
        Assert.Equal("r.pdf (page 2 of 2)\n--- page 2 ---\ntwo", await Text(("path", "r.pdf"), ("pages", "2")));
    }

    [Fact]
    public async Task TheCache_KeepsAtMostItsDocuments()
    {
        Pages("one");
        for (int i = 0; i < PdfReading.MaxDocuments + 3; i++)
        {
            PutPdf($"d{i}.pdf");
            await Text(("path", $"d{i}.pdf"));
        }

        Assert.Equal(PdfReading.MaxDocuments, _reading.Held);
    }

    /// <summary>web_fetch's PDF answer draws the outline as read_pdf does, held to its character cap too (2026-10-20, review).</summary>
    [Fact]
    public void AWebPdfsOutline_IsHeldToTheCharacterCap()
    {
        var outline = Enumerable.Range(1, 50).Select(i => new PdfOutlineItem(new string('t', 200), 1, i)).ToList();
        var read = new PdfRead(true, "", 1, [new PdfPageRead(1, "body", null, 0, 0)]) { Outline = outline };

        string text = WebFetcher.PdfAnswerText(read);

        string head = text[..text.IndexOf("--- page 1", StringComparison.Ordinal)];
        Assert.True(head.Length <= PdfText.MaxOutlineChars + PdfText.OutlineMore.Length + 2, head.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(PdfText.OutlineMore, head, StringComparison.Ordinal);
        Assert.EndsWith("--- page 1 ---\nbody", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentOverTheLimitAlone_IsAnswered_NotKept_AndTheOthersStay()
    {
        Pages("one");
        PutPdf("a.pdf");
        await Text(("path", "a.pdf"));
        PutPdf("b.pdf", [.. PdfHead, (byte)'b']);
        await Text(("path", "b.pdf"));

        Pages(new string('x', (int)PdfReading.MaxChars + 1));
        PutPdf("huge.pdf", [.. PdfHead, (byte)'h']);
        Assert.StartsWith("huge.pdf", await Text(("path", "huge.pdf")), StringComparison.Ordinal);

        Assert.Equal(2, _reading.Held);   // a.pdf and b.pdf, not let go for a document that is never kept
    }
}
