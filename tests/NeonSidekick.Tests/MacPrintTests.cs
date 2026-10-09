using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Printing;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI.Markdown;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// Printing on a Mac (2026-10-08): <see cref="MacPrintRules"/>' paper, faces, coordinates and options on every OS; the words; a
/// PDF sent as it is through <see cref="PrintService"/> over the fake spooler's options path; and, on a Mac,
/// <see cref="MacPdfDocument"/> drawing real PDFs that are read back (pages, media boxes, PDFKit's text) and the smoke's
/// <c>print:cups</c>. Nothing here submits a job: that is <see cref="LivePrinterFactAttribute"/>'s, held and cancelled.
/// </summary>
public sealed class MacPrintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new();
    private readonly FakePrintSpooler _spooler = new() { TakesOptions = true };
    private readonly PrintService _print;

    /// <summary>US Letter as the Brother HL-L2340D reports it through <c>cupsGetDestMediaDefault</c> (measured 2026-10-08): 4.32 mm margins.</summary>
    private static readonly MacPaper Letter = MacPrintRules.FromCups("na_letter_8.5x11in", 21590, 27940, 432, 432, 432, 432);

    public MacPrintTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _print = new PrintService(_spooler, new WorkingDirectory(() => _root, _time), () => _settings, _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ── the paper ───────────────────────────────────────────────────────────

    [Fact]
    public void Paper_FromCups_IsInPoints()
    {
        Assert.Equal("na_letter_8.5x11in", Letter.Media);
        Assert.Equal(612, Letter.Width, 3);
        Assert.Equal(792, Letter.Height, 3);
        Assert.Equal(12.246, Letter.Left, 3);
        Assert.Equal(Letter.Left, Letter.Top);
        Assert.Equal(Letter.Left, Letter.Bottom);
        Assert.Null(MacPrintRules.FromCups(" ", 21000, 29700, 0, 0, 0, 0).Media);
        Assert.Equal(595.276, MacPrintRules.FromCups("iso_a4_210x297mm", 21000, 29700, 0, 0, 0, 0).Width, 3);
    }

    [Fact]
    public void Paper_ByRegion_IsLetterOrA4_WithAQuarterInch()
    {
        var us = MacPrintRules.ForRegion("US");
        Assert.Equal((612.0, 792.0, 18.0), (us.Width, us.Height, us.Left));
        Assert.Null(us.Media);
        Assert.Equal(612, MacPrintRules.ForRegion(" ca ").Width);
        Assert.Equal(595.2756, MacPrintRules.ForRegion("DE").Width, 3);
        Assert.Equal(841.8898, MacPrintRules.ForRegion(null).Height, 3);
    }

    [Fact]
    public void Metrics_Portrait_IsThePaper_AndLandscape_TurnsTheMargins()
    {
        var paper = new MacPaper(null, 600, 800, 10, 20, 30, 40);

        Assert.Equal(new PageMetrics(600, 800, 10, 20, 570, 760), MacPrintRules.Metrics(paper, landscape: false));
        // A quarter turn counter-clockwise: left ← the paper's bottom, top ← its left, right ← its top, bottom ← its right.
        Assert.Equal(new PageMetrics(800, 600, 40, 10, 780, 570), MacPrintRules.Metrics(paper, landscape: true));
    }

    // ── the faces and the coordinates ───────────────────────────────────────

    [Fact]
    public void FontName_IsHelveticaNeueAndMenlo_InFourStyles()
    {
        Assert.Equal("HelveticaNeue", MacPrintRules.FontName(new PrintFont(PrintFace.Sans, 10)));
        Assert.Equal("HelveticaNeue-Bold", MacPrintRules.FontName(new PrintFont(PrintFace.Sans, 10, Bold: true)));
        Assert.Equal("HelveticaNeue-Italic", MacPrintRules.FontName(new PrintFont(PrintFace.Sans, 10, Italic: true)));
        Assert.Equal("HelveticaNeue-BoldItalic", MacPrintRules.FontName(new PrintFont(PrintFace.Sans, 10, true, true)));
        Assert.Equal("Menlo-Regular", MacPrintRules.FontName(new PrintFont(PrintFace.Mono, 9)));
        Assert.Equal("Menlo-Bold", MacPrintRules.FontName(new PrintFont(PrintFace.Mono, 9, Bold: true)));
        Assert.Equal("Menlo-Italic", MacPrintRules.FontName(new PrintFont(PrintFace.Mono, 9, Italic: true)));
        Assert.Equal("Menlo-BoldItalic", MacPrintRules.FontName(new PrintFont(PrintFace.Mono, 9, true, true)));
    }

    [Fact]
    public void Ops_LandInPdfSpace_YUp()
    {
        var page = MacPrintRules.Metrics(Letter, landscape: false);

        Assert.Equal((72.0, 720.0), MacPrintRules.Baseline(72, 72, page));
        Assert.Equal((10.0, 772.0, 100.0, 10.0), MacPrintRules.Rect(10, 10, 100, 10, page));   // the box's bottom-left corner
        Assert.Equal((0.0, 0.0, 612.0, 792.0), MacPrintRules.Rect(0, 0, 612, 792, page));
    }

    [Fact]
    public void Landscape_TurnsThePageAQuarter_CounterClockwise_OntoThePaper()
    {
        var page = MacPrintRules.Metrics(Letter, landscape: true);   // 792 wide, 612 high
        Assert.Equal((0.0, 0.0), MacPrintRules.Turn(Letter, landscape: false));
        Assert.Equal(612, MacPrintRules.Turn(Letter, landscape: true).TranslateX);

        // The landscape page's top-left corner lands at the paper's bottom-left, its top-right at the paper's top-left (the top
        // runs up the paper's left edge), its bottom-left at the paper's bottom-right.
        AssertNear((0, 0), MacPrintRules.OnPaper(0, page.Height, Letter, landscape: true));
        AssertNear((0, 792), MacPrintRules.OnPaper(page.Width, page.Height, Letter, landscape: true));
        AssertNear((612, 0), MacPrintRules.OnPaper(0, 0, Letter, landscape: true));
        AssertNear((100, 200), MacPrintRules.OnPaper(100, 200, Letter, landscape: false));
    }

    private static void AssertNear((double X, double Y) expected, (double X, double Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }

    // ── the options and the printers ────────────────────────────────────────

    [Fact]
    public void Options_TheMedia_TheCopies_AndThePages()
    {
        Assert.Equal(new KeyValuePair<string, string>[] { new("media", "na_letter_8.5x11in") }, MacPrintRules.JobOptions(Letter));
        Assert.Empty(MacPrintRules.JobOptions(MacPrintRules.ForRegion("US")));
        Assert.Empty(MacPrintRules.FileOptions(1, null));
        Assert.Equal(
            new KeyValuePair<string, string>[] { new("copies", "2"), new("multiple-document-handling", "separate-documents-collated-copies"), new("page-ranges", "1-3,5") },
            MacPrintRules.FileOptions(2, [1, 2, 3, 5]));
        Assert.True(MacPrintRules.IsPdf("a/Doc.PDF"));
        Assert.False(MacPrintRules.IsPdf("a/doc.docx"));
        Assert.Equal("CUPS gave no reason", MacPrintRules.Detail(" "));
        Assert.Equal("client-error-not-found", MacPrintRules.Detail(" client-error-not-found "));
    }

    [Fact]
    public void Printer_FromCupsOptions_KeepsADescriptionThatSaysMore()
    {
        Assert.Equal(
            new PrinterInfo("Brother_HL_L2340D_series", true, "Brother HL-L2340D series"),
            MacPrintRules.Printer("Brother_HL_L2340D_series", true, new Dictionary<string, string> { ["printer-info"] = " Brother HL-L2340D series ", ["printer-state"] = "3" }));
        Assert.Null(MacPrintRules.Printer("Office", false, new Dictionary<string, string> { ["printer-info"] = "Office" }).Description);
        Assert.Null(MacPrintRules.Printer("Office", false, new Dictionary<string, string>()).Description);
    }

    [Fact]
    public void PrinterLine_ShowsTheDescription_BesideTheQueueName()
    {
        var brother = new PrinterInfo("Brother_HL_L2340D_series", true, "Brother HL-L2340D series");

        Assert.Equal($"Brother_HL_L2340D_series — Brother HL-L2340D series ({PrintText.DefaultMark}, Print default printer)", PrintText.PrinterLine(brother, "brother_hl_l2340d_series"));
        Assert.Equal("Office", PrintText.PrinterLine(new PrinterInfo("Office", false), null));
        Assert.Contains("Brother HL-L2340D series", SettingsMenu.PrinterRow("Brother_HL_L2340D_series", [brother]));
    }

    [Fact]
    public void ResolvePrinter_MatchesTheDescription_TooWhole_OrInPart()
    {
        List<PrinterInfo> printers = [new("Brother_HL_L2340D_series", true, "Brother HL-L2340D series"), new("Office_Color", false, "Office Color")];

        Assert.Equal(("Brother_HL_L2340D_series", (string?)null), _print.ResolvePrinter("Brother HL-L2340D series", printers));
        Assert.Equal(("Brother_HL_L2340D_series", (string?)null), _print.ResolvePrinter("brother hl", printers));
        Assert.Equal(("Office_Color", (string?)null), _print.ResolvePrinter("office color", printers));
        Assert.Equal(("Office_Color", (string?)null), _print.ResolvePrinter("office_c", printers));
        Assert.Equal((null, PrintText.UnknownPrinter("epson", printers)), _print.ResolvePrinter("epson", printers));
    }

    // ── the words ───────────────────────────────────────────────────────────

    [Fact]
    public void TheMacWords_ArePinned()
    {
        Assert.Equal("Error: report.docx cannot be printed from here on a Mac: only PDF files go to the printer as they are, and the sidekick itself prints text, code, markdown and pictures; print .docx files from the program that made them", PrintText.MacNoHandler("report.docx"));
        Assert.Equal("Error: doc.pdf goes to the printer as it is, each page as the PDF lays it out; leave out landscape", PrintText.PdfLandscape("doc.pdf"));
        Assert.Equal("Error: doc.pdf goes to the printer as it is and cannot print to a file", PrintText.PdfNoFileOutput("doc.pdf"));
        Assert.Equal("Error: doc.pdf could not be read as a PDF (a damaged file, or one locked by a password)", PrintText.NotAPdf("doc.pdf"));
        Assert.Equal("Error: this Mac has no default printer; name one: A, B", PrintText.MacNoDefault([new("A", false), new("B", false)]));
        Assert.Equal("No printers are set up on this Mac", PrintText.MacNoPrinters);
        Assert.Equal("Printing needs Windows or a Mac: this build has no printer support.", PrintText.NotHere);
    }

    // ── a PDF sent as it is (the fake's options path) ──────────────────────

    private string Pdf(string name, int pages)
    {
        File.WriteAllBytes(Path.Combine(_root, name), "%PDF-1.7\n"u8.ToArray());
        _spooler.PageCounts[name] = pages;
        return name;
    }

    [Fact]
    public async Task APdf_GoesAsItIs_WithThePrinterCopiesAndPages()
    {
        Pdf("doc.pdf", 5);

        var (plan, error) = _print.Prepare(new PrintRequest("doc.pdf", Printer: "color", Copies: 2, Pages: "2-3"));
        Assert.Null(error);
        Assert.Equal((PrintKind.Shell, FakePrintSpooler.Color, 5, 2), (plan!.Kind, plan.Printer, plan.TotalPages, plan.Copies));
        Assert.Equal("Let the model print doc.pdf (pages 2-3 of 5 × 2 copies) on Office Color?", PrintText.ConfirmQuestion(plan));

        string result = await _print.PrintAsync(plan, CancellationToken.None);

        Assert.Equal("Printed doc.pdf: pages 2-3 of 5 × 2 copies to Office Color", result);
        var sent = Assert.Single(_spooler.SentAsItIs);
        Assert.Equal(("doc.pdf", FakePrintSpooler.Color, 2), (Path.GetFileName(sent.Path), sent.Printer, sent.Copies));
        Assert.Equal([2, 3], sent.Pages!);
        Assert.Empty(_spooler.ShellPrinted);
        Assert.Empty(_spooler.Jobs);
        Assert.Empty(_spooler.Opened);
    }

    [Fact]
    public async Task APdf_EveryPage_GoesWithNoRange_ToTheDefault()
    {
        Pdf("doc.pdf", 3);

        string result = await _print.RunAsync(new PrintRequest("doc.pdf"), CancellationToken.None);

        Assert.Equal("Printed doc.pdf: 3 pages to Office Laser", result);
        Assert.Null(Assert.Single(_spooler.SentAsItIs).Pages);
    }

    [Fact]
    public void APdf_IsRefused_Landscape_ToAFile_Unreadable_OrOutOfRange()
    {
        Pdf("doc.pdf", 2);
        Pdf("locked.pdf", 0);

        Assert.Equal(PrintText.PdfLandscape("doc.pdf"), _print.Prepare(new PrintRequest("doc.pdf", Landscape: true)).Error);
        Assert.Equal(PrintText.PdfNoFileOutput("doc.pdf"), _print.Prepare(new PrintRequest("doc.pdf", OutputFile: Path.Combine(_dir, "x.pdf"))).Error);
        Assert.Equal(PrintText.NotAPdf("locked.pdf"), _print.Prepare(new PrintRequest("locked.pdf")).Error);
        Assert.Equal(PrintText.BadPages("5", 2), _print.Prepare(new PrintRequest("doc.pdf", Pages: "5")).Error);
        Assert.Equal(PrintText.UnknownPrinter("epson", _spooler.Installed), _print.Prepare(new PrintRequest("doc.pdf", Printer: "epson")).Error);
    }

    [Fact]
    public void AnOfficeFile_IsRefused_WithTheMacSentence()
    {
        File.WriteAllBytes(Path.Combine(_root, "report.docx"), [0x50, 0x4B, 3, 4, 0, 0]);

        Assert.Equal(PrintText.MacNoHandler("report.docx"), _print.Prepare(new PrintRequest("report.docx")).Error);
        Assert.Empty(_spooler.SentAsItIs);
    }

    [Fact]
    public async Task APdf_ThePrintersRefusal_IsTheResult()
    {
        Pdf("doc.pdf", 1);
        _spooler.FailWith = PrintText.Failed(FakePrintSpooler.Laser, "client-error-not-possible");

        Assert.Equal(_spooler.FailWith, await _print.RunAsync(new PrintRequest("doc.pdf"), CancellationToken.None));
    }

    // ── the real drawing, on a Mac ──────────────────────────────────────────

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Fonts_AreTheOnesAskedFor_NoFallbackStandsIn()
    {
        foreach (var face in new[] { PrintFace.Sans, PrintFace.Mono })
        {
            foreach (var (bold, italic) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var font = new PrintFont(face, 10, bold, italic);
                Assert.Equal(MacPrintRules.FontName(font), MacPdfDocument.ResolvedFontName(font));
            }
        }
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Measuring_IsCoreTexts_InTheFacesSizes()
    {
        using var surface = new MacPdfDocument(Letter, landscape: false);
        var sans = new PrintFont(PrintFace.Sans, 10);
        var mono = new PrintFont(PrintFace.Mono, 10);

        Assert.Equal(0, surface.Width("", sans));
        Assert.InRange(surface.Width("Hello, world", sans), 40, 80);
        Assert.True(surface.Width("Hello", sans with { Bold = true }) > surface.Width("Hello", sans));
        Assert.Equal(surface.Width("Hello", sans) * 2, surface.Width("Hello", sans with { Size = 20 }), 3);
        Assert.Equal(surface.Width("iiii", mono), surface.Width("MMMM", mono), 3);   // Menlo is fixed
        Assert.Equal(6.02, surface.Width("M", mono), 1);
        Assert.True(surface.Width("日本語", sans) > 0);   // CoreText's fallback measures what Helvetica Neue lacks
    }

    private static List<PrintPage> Sheets(IPrintSurface surface)
    {
        var document = new MarkdigParser().Parse("# Heading\n\nSome **bold** prose with `code` in it.\n\n- one\n- two\n\n---\n\n```\nlisting line\n```");
        var pages = PrintLayout.Markdown(document, surface.Page, surface, 10).ToList();
        var bitmap = ViewerImage.Decode(SmokeChecks.SolidBmp(8, 2), "solid.bmp")!;
        pages.AddRange(PrintLayout.Picture(bitmap, surface.Page).Select(p => p with { Number = pages.Count + 1 }));
        return PrintLayout.WithHeaders(pages, "notes.md", "2026-10-08 12:00", surface.Page, surface).ToList();
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Render_Portrait_OneSheetAPage_OnThePaper_WithItsWords()
    {
        using var surface = new MacPdfDocument(Letter, landscape: false);
        var sheets = Sheets(surface);

        byte[] pdf = surface.Render(sheets, 1, CancellationToken.None);

        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
        var boxes = MacPdfDocument.MediaBoxes(pdf);
        Assert.Equal(sheets.Count, boxes.Count);
        Assert.All(boxes, b => Assert.Equal((612.0, 792.0), (Math.Round(b.Width, 3), Math.Round(b.Height, 3))));
        string text = MacPdfDocument.Text(pdf)!;
        foreach (string word in new[] { "Heading", "bold", "code", "listing line", "notes.md", "2026-10-08 12:00", "page 1 of 2" })
        {
            Assert.Contains(word, text);
        }
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Render_Landscape_IsTurnedOntoPortraitSheets_AndCopiesRepeat()
    {
        using var surface = new MacPdfDocument(Letter, landscape: true);
        Assert.True(surface.Page.Width > surface.Page.Height);
        var sheets = Sheets(surface);

        byte[] pdf = surface.Render(sheets, 2, CancellationToken.None);

        var boxes = MacPdfDocument.MediaBoxes(pdf);
        Assert.Equal(sheets.Count * 2, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.Width < b.Height));
        Assert.Contains("Heading", MacPdfDocument.Text(pdf)!);
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Render_Cancelled_Throws_AndKeepsNothing()
    {
        using var surface = new MacPdfDocument(Letter, landscape: false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => surface.Render(Sheets(surface), 1, cancelled.Token));
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void PageCount_OfADrawnPdf_AndNoneOfAnythingElse()
    {
        using var surface = new MacPdfDocument(Letter, landscape: false);
        string pdf = Path.Combine(_dir, "drawn.pdf");
        File.WriteAllBytes(pdf, surface.Render(Sheets(surface), 1, CancellationToken.None));
        string junk = Path.Combine(_dir, "junk.pdf");
        File.WriteAllText(junk, "not a pdf");

        Assert.Equal(2, MacPdfDocument.PageCount(pdf));
        Assert.Equal(0, MacPdfDocument.PageCount(junk));
        Assert.Equal(0, MacPdfDocument.PageCount(Path.Combine(_dir, "missing.pdf")));
        Assert.Empty(MacPdfDocument.MediaBoxes("nope"u8.ToArray()));
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void TheSpooler_ListsAndRefusesAnUnknownPrinter_SubmittingNothing()
    {
        var spooler = new MacPrintSpooler();

        Assert.True(MacPrintSpooler.Available);
        var printers = spooler.Printers();
        Assert.True(printers.Count(p => p.IsDefault) <= 1);
        Assert.Null(spooler.Open("NeonSidekick_No_Such_Printer", landscape: false, out string error));
        Assert.Equal(PrintText.CannotOpen("NeonSidekick_No_Such_Printer", "CUPS has no such printer"), error);
        Assert.Equal(PrintText.CannotOpen("NeonSidekick_No_Such_Printer", "CUPS has no such printer"), spooler.Print(new PrintJob("NeonSidekick_No_Such_Printer", "t", []), CancellationToken.None));
        Assert.True(spooler.CanShellPrint("a.pdf"));
        Assert.False(spooler.CanShellPrint("a.docx"));
        Assert.True(((IPrintSpooler)spooler).ShellPrintTakesOptions);
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void TheSpooler_PrintsToAFile_OnAPrintersPaper_WhenThereIsOne()
    {
        var spooler = new MacPrintSpooler();
        if (spooler.Printers().FirstOrDefault(p => p.IsDefault) is not { } printer)
        {
            return;   // no printer set up: the smoke draws on the region's paper instead
        }

        using var surface = spooler.Open(printer.Name, landscape: false, out string error)!;
        Assert.Equal("", error);
        string path = Path.Combine(_dir, "out.pdf");

        Assert.Null(spooler.Print(new PrintJob(printer.Name, "test", Sheets(surface), OutputFile: path), CancellationToken.None));

        var box = MacPdfDocument.MediaBoxes(File.ReadAllBytes(path))[0];
        Assert.Equal(surface.Page.Width, box.Width, 2);
        Assert.Equal(surface.Page.Height, box.Height, 2);
    }

    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Smoke_PrintCups_Passes()
    {
        var check = SmokeChecks.ProbeMacPrint();

        Assert.True(check.Passed, check.Detail);
        Assert.Contains("nothing sent", check.Detail);
    }

    [Fact]
    public void Smoke_PrintCups_SkipsOffAMac()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal("skipped: not macOS", SmokeChecks.ProbeMacPrint().Detail);
    }
}
