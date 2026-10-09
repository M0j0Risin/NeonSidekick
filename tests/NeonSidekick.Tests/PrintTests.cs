using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Plans;
using NeonSidekick.Printing;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;

namespace NeonSidekick.Tests;

/// <summary>
/// Printing (2026-09-28): the layout on US Letter with a fixed advance, the page ranges and the words, the service over the fake
/// spooler (the sandbox, the file's kind, the printer, the copies, the shell's verb), the two tools and the policy, <c>/print</c>'s
/// parse, engine and argument lists, and the turn's offer. The real spooler is the smoke's and <see cref="PrintToPdfFactAttribute"/>'s.
/// </summary>
public sealed class PrintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new();
    private readonly FakePrintSpooler _spooler = new();
    private readonly WorkingDirectory _files;
    private readonly PrintService _print;

    public PrintTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _print = new PrintService(_spooler, _files, () => _settings, _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly PageMetrics Letter = PageMetrics.Letter();

    private static IEnumerable<PrintTextOp> Texts(PrintPage page) => page.Ops.OfType<PrintTextOp>();

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in pairs)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private string Write(string relative, string text)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    // ── the frame and the listing ───────────────────────────────────────────

    [Fact]
    public void Frame_KeepsTheMargin_AndTheHeadersRoom()
    {
        var frame = PrintLayout.FrameOf(Letter);

        Assert.Equal(43.2, frame.Left, 6);
        Assert.Equal(612 - 43.2, frame.Right, 6);
        Assert.Equal(43.2, frame.HeaderTop, 6);
        Assert.Equal(66, frame.Top, 6);
        Assert.Equal(792 - 43.2, frame.Bottom, 6);

        // A printer that reaches less than the margin: its own edge wins.
        var narrow = PrintLayout.FrameOf(new PageMetrics(612, 792, 60, 50, 552, 742));
        Assert.Equal(60, narrow.Left, 6);
        Assert.Equal(552, narrow.Right, 6);
        Assert.Equal(50, narrow.HeaderTop, 6);
        Assert.Equal(742, narrow.Bottom, 6);
    }

    [Fact]
    public void Listing_WrapsAtTheColumn_AndPaginatesFiftyLinesAPage()
    {
        // 525.6 pt across at 6 pt a character: 87 columns; 682.8 pt down at 13.5 pt a line: 50 lines.
        Assert.Equal(87, PrintLayout.Columns(PrintLayout.FrameOf(Letter).Width, FixedMeasure.Instance, new PrintFont(PrintFace.Mono, 10)));
        var wrapped = PrintLayout.Listing(new string('x', 200), Letter, FixedMeasure.Instance, 10);
        Assert.Equal([87, 87, 26], Texts(Assert.Single(wrapped)).Select(t => t.Text.Length));
        Assert.All(Texts(wrapped[0]), t => Assert.Equal(PrintFace.Mono, t.Font.Face));

        string hundred = string.Join("\n", Enumerable.Range(1, 100).Select(n => "line " + n));
        var pages = PrintLayout.Listing(hundred, Letter, FixedMeasure.Instance, 10);
        Assert.Equal(2, pages.Count);
        Assert.Equal("line 50", Texts(pages[0]).Last().Text);
        Assert.Equal("line 51", Texts(pages[1]).First().Text);
        Assert.Equal([1, 2], pages.Select(p => p.Number));
        Assert.Equal(3, PrintLayout.Listing(hundred + "\nline 101", Letter, FixedMeasure.Instance, 10).Count);

        // Every line sits inside the frame.
        var frame = PrintLayout.FrameOf(Letter);
        Assert.All(pages.SelectMany(Texts), t => Assert.InRange(t.Y, frame.Top, frame.Bottom));
    }

    [Fact]
    public void Listing_ExpandsTabs_DropsControls_AndAFormFeedStartsAPage()
    {
        Assert.Equal([("a   b", false), ("    c", false)], PrintLayout.Rows("a\tb\u0001\r\n\tc").ToList());
        Assert.Equal([("one", false), ("two", true)], PrintLayout.Rows("one\ftwo").ToList());

        var pages = PrintLayout.Listing("one\ftwo\nthree", Letter, FixedMeasure.Instance, 10);
        Assert.Equal(2, pages.Count);
        Assert.Equal(["one"], Texts(pages[0]).Select(t => t.Text));
        Assert.Equal(["two", "three"], Texts(pages[1]).Select(t => t.Text));

        // Nothing at all is still one (blank) page.
        Assert.Empty(Assert.Single(PrintLayout.Listing("", Letter, FixedMeasure.Instance, 10)).Ops);
    }

    [Fact]
    public void WrapColumns_NeverSplitsASurrogatePair()
    {
        string row = "ab😀cd";   // the emoji is two chars at index 2 and 3: the first cut steps back before it
        Assert.Equal(["ab", "😀c", "d"], PrintLayout.WrapColumns(row, 3).ToList());
        Assert.Equal([""], PrintLayout.WrapColumns("", 5).ToList());
    }

    // ── markdown ────────────────────────────────────────────────────────────

    private static IReadOnlyList<PrintPage> Markdown(string text) =>
        PrintLayout.Markdown(new MarkdigParser().Parse(text), Letter, FixedMeasure.Instance, 10);

    [Fact]
    public void Markdown_StylesHeadingsRunsListsAndCode()
    {
        var page = Assert.Single(Markdown("# Title\n\nSome **bold** and `code` here.\n\n- one\n- two\n\n1. first\n2. second\n\n```\nlet x = 1;\n```"));
        var texts = Texts(page).ToList();

        var title = Assert.Single(texts, t => t.Text == "Title");
        Assert.True(title.Font.Bold);
        Assert.Equal(PrintLayout.HeadingSize(1, 10), title.Font.Size, 6);
        Assert.True(Assert.Single(texts, t => t.Text == "bold").Font.Bold);
        Assert.Equal(PrintFace.Mono, Assert.Single(texts, t => t.Text == "code").Font.Face);
        Assert.Equal(2, texts.Count(t => t.Text == "•"));
        Assert.Contains(texts, t => t.Text == "1.");
        Assert.Contains(texts, t => t.Text == "2.");
        var code = Assert.Single(texts, t => t.Text == "let x = 1;");
        Assert.Equal(PrintFace.Mono, code.Font.Face);

        // The list's text hangs to the right of its marker; the code block is indented past its bar.
        var bullet = texts.First(t => t.Text == "•");
        Assert.True(texts.Single(t => t.Text == "one").X > bullet.X);
        Assert.True(code.X > PrintLayout.FrameOf(Letter).Left);
        Assert.Contains(page.Ops.OfType<PrintBoxOp>(), b => b.Width < 1);
    }

    [Fact]
    public void Markdown_WrapsProse_AtTheRightEdge_AndCutsAnOverlongWord()
    {
        var frame = PrintLayout.FrameOf(Letter);
        string words = string.Join(' ', Enumerable.Repeat("word", 60));
        var page = Assert.Single(Markdown(words + "\n\n" + new string('z', 150)));
        var texts = Texts(page).ToList();

        Assert.True(texts.Select(t => t.Y).Distinct().Count() >= 4);
        Assert.All(texts, t => Assert.True(t.X + FixedMeasure.Instance.Width(t.Text, t.Font) <= frame.Right + 0.001, t.Text));
        Assert.Equal(150, texts.Where(t => t.Text.All(c => c == 'z')).Sum(t => t.Text.Length));
    }

    [Fact]
    public void Markdown_QuoteRuleAndTable()
    {
        var page = Assert.Single(Markdown("> quoted\n\n---\n\n| Name | Value |\n|:--|--:|\n| a | 1 |\n| " + new string('w', 300) + " | 2 |"));
        var texts = Texts(page).ToList();
        var boxes = page.Ops.OfType<PrintBoxOp>().ToList();
        var frame = PrintLayout.FrameOf(Letter);

        Assert.True(texts.Single(t => t.Text == "quoted").X > frame.Left + 10);
        Assert.Contains(boxes, b => b.Width == 2);                                   // the quote's bar
        Assert.Contains(boxes, b => Math.Abs(b.Width - frame.Width) < 0.001);        // the rule
        Assert.True(texts.Single(t => t.Text == "Name").Font.Bold);                 // the header row
        Assert.EndsWith("…", texts.Single(t => t.Text.StartsWith("www", StringComparison.Ordinal)).Text);
        Assert.All(texts, t => Assert.True(t.X + FixedMeasure.Instance.Width(t.Text, t.Font) <= frame.Right + 0.001, t.Text));
    }

    [Fact]
    public void Markdown_AHeadingNeverEndsAPage()
    {
        // 49 short paragraphs fill the page to its last line or so; the heading after them goes over with its paragraph.
        string filler = string.Join("\n\n", Enumerable.Range(1, 38).Select(n => "p" + n));
        var pages = Markdown(filler + "\n\n## Next\n\nbody");

        Assert.Equal(2, pages.Count);
        Assert.Contains(Texts(pages[1]), t => t.Text == "Next");
        Assert.Contains(Texts(pages[1]), t => t.Text == "body");
    }

    [Theory]
    [InlineData("site", "https://example.com", "site (https://example.com)")]
    [InlineData("https://example.com", "https://example.com", "https://example.com")]
    [InlineData("", "https://example.com", "https://example.com")]
    [InlineData("top", "#top", "top")]
    public void LinkText_AddsTheAddress_UnlessItIsTheText(string text, string url, string expected) =>
        Assert.Equal(expected, PrintLayout.LinkText(text, url));

    // ── pictures and headers ────────────────────────────────────────────────

    [Fact]
    public void Picture_IsNeverEnlarged_ShrinksToFit_AndIsCentred()
    {
        var frame = PrintLayout.FrameOf(Letter);
        var small = (PrintImageOp)Assert.Single(Assert.Single(PrintLayout.Picture(new NeonSidekick.Viewer.ViewerBitmap(96, 48, new byte[96 * 48 * 4]), Letter)).Ops);
        Assert.Equal(72, small.Width, 6);
        Assert.Equal(36, small.Height, 6);
        Assert.Equal(frame.Left + (frame.Width - 72) / 2, small.X, 6);

        var big = (PrintImageOp)Assert.Single(Assert.Single(PrintLayout.Picture(new NeonSidekick.Viewer.ViewerBitmap(4000, 1000, new byte[16]), Letter)).Ops);
        Assert.Equal(frame.Width, big.Width, 6);
        Assert.Equal(frame.Width / 4, big.Height, 6);
        Assert.Equal(frame.Top + (frame.Height - big.Height) / 2, big.Y, 6);
    }

    [Fact]
    public void Headers_CarryTheTitle_TheStamp_AndPageNOfM()
    {
        var pages = PrintLayout.WithHeaders(PrintLayout.Listing("a\fb", Letter, FixedMeasure.Instance, 10), "notes.txt", "2026-09-28 10:00", Letter, FixedMeasure.Instance);
        var frame = PrintLayout.FrameOf(Letter);

        Assert.Equal(2, pages.Count);
        Assert.True(Texts(pages[0]).Single(t => t.Text == "notes.txt").Font.Bold);
        Assert.Contains(Texts(pages[0]), t => t.Text.Contains("2026-09-28 10:00", StringComparison.Ordinal));
        var number = Texts(pages[1]).Single(t => t.Text == PrintText.PageOf(2, 2));
        Assert.Equal(frame.Right, number.X + FixedMeasure.Instance.Width(number.Text, number.Font), 6);
        Assert.Contains(pages[0].Ops.OfType<PrintBoxOp>(), b => b.Y < frame.Top && Math.Abs(b.Width - frame.Width) < 0.001);

        // A title too long for the line is cut with an ellipsis.
        var long_ = PrintLayout.WithHeaders(pages, new string('t', 400), "", Letter, FixedMeasure.Instance);
        Assert.Contains(Texts(long_[0]), t => t.Text.EndsWith("…", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", 3, new[] { 1, 2, 3 })]
    [InlineData("2", 3, new[] { 2 })]
    [InlineData("1-2", 3, new[] { 1, 2 })]
    [InlineData("2-", 3, new[] { 2, 3 })]
    [InlineData("1, 3", 3, new[] { 1, 3 })]
    [InlineData("2-9", 3, new[] { 2, 3 })]
    public void PageRange_Reads(string spec, int total, int[] expected)
    {
        Assert.True(PrintLayout.PageRange(spec, total, out var pages, out _));
        Assert.Equal(expected, pages);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("a")]
    [InlineData("3-1")]
    [InlineData(",")]
    public void PageRange_Refuses(string spec)
    {
        Assert.False(PrintLayout.PageRange(spec, 3, out _, out string error));
        Assert.Equal(PrintText.BadPages(spec, 3), error);
    }

    [Fact]
    public void Words_SayHowMuchPaper()
    {
        Assert.Equal("1-3, 5", PrintText.Ranges([1, 2, 3, 5]));
        var plan = new PrintPlan(PrintKind.Listing, "a.txt", "P", 5, [2, 3], 2, true, null, null);
        Assert.Equal("pages 2-3 of 5 × 2 copies, landscape", PrintText.Sheets(plan));
        Assert.Equal("1 page", PrintText.Sheets(plan with { TotalPages = 1, Pages = [1], Copies = 1, Landscape = false }));
        Assert.Equal("Let the model print a.txt (pages 2-3 of 5 × 2 copies, landscape) on P?", PrintText.ConfirmQuestion(plan));
        Assert.Equal("Printed a.txt: pages 2-3 of 5 × 2 copies, landscape to P", PrintText.Printed(plan));
    }

    // ── the service ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Service_PrintsATextFile_OnTheDefaultPrinter_WithItsHeader()
    {
        Write(@"docs\notes.txt", "hello\nworld");

        string result = await _print.RunAsync(new PrintRequest(@"docs\notes.txt"), CancellationToken.None);

        var job = Assert.Single(_spooler.Jobs);
        Assert.Equal(FakePrintSpooler.Laser, job.Printer);
        Assert.Equal(@"docs\notes.txt", job.Title);
        Assert.Equal(1, job.Copies);
        Assert.False(job.Landscape);
        var texts = Texts(Assert.Single(job.Pages)).Select(t => t.Text).ToList();
        Assert.Contains("hello", texts);
        Assert.Contains(PrintText.PageOf(1, 1), texts);
        Assert.Contains(texts, t => t.Contains(PrintText.Stamp(_time.GetLocalNow()), StringComparison.Ordinal));
        Assert.Equal(@"Printed docs\notes.txt: 1 page to Office Laser", result);
    }

    [Fact]
    public void Service_TellsMarkdownPicturesAndTheRestApart()
    {
        Write("readme.md", "# Head\n\ntext");
        File.WriteAllBytes(Path.Combine(_root, "dot.bmp"), SmokeChecks.SolidBmp(4, 4));
        File.WriteAllBytes(Path.Combine(_root, "doc.pdf"), [0x25, 0x50, 0x44, 0x46, 0, 1, 2]);
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [0, 1, 2]);

        var (md, _) = _print.Prepare(new PrintRequest("readme.md"));
        Assert.Equal(PrintKind.Markdown, md!.Kind);
        Assert.True(Texts(md.Job!.Pages[0]).Single(t => t.Text == "Head").Font.Bold);

        var (picture, _) = _print.Prepare(new PrintRequest("dot.bmp"));
        Assert.Equal(PrintKind.Picture, picture!.Kind);
        Assert.Single(picture.Job!.Pages[0].Ops.OfType<PrintImageOp>());

        var (pdf, _) = _print.Prepare(new PrintRequest("doc.pdf"));
        Assert.Equal(PrintKind.Shell, pdf!.Kind);
        Assert.Equal(FakePrintSpooler.Laser, pdf.Printer);
        Assert.Null(pdf.Job);

        Assert.Equal(PrintText.NoHandler("blob.bin"), _print.Prepare(new PrintRequest("blob.bin")).Error);
        Assert.Equal(PrintText.ShellDefaultOnly("doc.pdf"), _print.Prepare(new PrintRequest("doc.pdf", Printer: FakePrintSpooler.Color)).Error);
        Assert.Equal(PrintText.ShellDefaultOnly("doc.pdf"), _print.Prepare(new PrintRequest("doc.pdf", Copies: 2)).Error);
    }

    [Fact]
    public async Task Service_HandsAPdf_ToItsOwnProgram()
    {
        File.WriteAllBytes(Path.Combine(_root, "doc.pdf"), [0x25, 0x50, 0x44, 0x46, 0]);

        string result = await _print.RunAsync(new PrintRequest("doc.pdf"), CancellationToken.None);

        Assert.Equal([Path.Combine(_root, "doc.pdf")], _spooler.ShellPrinted);
        Assert.Empty(_spooler.Jobs);
        Assert.StartsWith("Sent doc.pdf to the program Windows prints it with, on Office Laser", result);
    }

    [WindowsFact]
    public void Service_KeepsToTheSandbox()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        Assert.Equal(FileText.OutsideRoot(@"..\x.txt"), _print.Prepare(new PrintRequest(@"..\x.txt")).Error);
        Assert.Equal(FileText.Missing("nope.txt"), _print.Prepare(new PrintRequest("nope.txt")).Error);
        Assert.Equal(FileText.IsDirectory("sub"), _print.Prepare(new PrintRequest("sub")).Error);
        Assert.Equal(FileText.PathRequired(FileTool.PathArgument), _print.Prepare(new PrintRequest(" ")).Error);
        Assert.Empty(_spooler.Opened);
    }

    /// <summary>The Unix twin of <see cref="Service_KeepsToTheSandbox"/> (2026-10-08, printing on a Mac): its paths with <c>/</c>.</summary>
    [UnixFact]
    public void Service_KeepsToTheSandbox_Unix()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        Assert.Equal(FileText.OutsideRoot("../x.txt"), _print.Prepare(new PrintRequest("../x.txt")).Error);
        Assert.Equal(FileText.Missing("nope.txt"), _print.Prepare(new PrintRequest("nope.txt")).Error);
        Assert.Equal(FileText.IsDirectory("sub"), _print.Prepare(new PrintRequest("sub")).Error);
        Assert.Equal(FileText.PathRequired(FileTool.PathArgument), _print.Prepare(new PrintRequest(" ")).Error);
        Assert.Empty(_spooler.Opened);
    }

    [Fact]
    public void Service_ResolvesThePrinter()
    {
        var printers = _spooler.Installed;

        Assert.Equal((FakePrintSpooler.Laser, null), _print.ResolvePrinter(null, printers));
        Assert.Equal((FakePrintSpooler.Color, null), _print.ResolvePrinter("office color", printers));
        Assert.Equal((FakePrintSpooler.Color, null), _print.ResolvePrinter("color", printers));
        Assert.Equal((null, PrintText.AmbiguousPrinter("office", [printers[0], printers[1]])), _print.ResolvePrinter("office", printers));
        Assert.Equal((null, PrintText.UnknownPrinter("inkjet", printers)), _print.ResolvePrinter("inkjet", printers));

        _settings.PrintDefaultPrinter = FakePrintSpooler.Pdf;
        Assert.Equal((FakePrintSpooler.Pdf, null), _print.ResolvePrinter(null, printers));
        Assert.Equal((FakePrintSpooler.Color, null), _print.ResolvePrinter("color", printers));   // named beats the setting

        _settings.PrintDefaultPrinter = "";
        List<PrinterInfo> noDefault = [new("A", false), new("B", false)];
        Assert.Equal((null, PrintText.NoDefaultHere(noDefault)), _print.ResolvePrinter(null, noDefault));
        Assert.Equal((null, PrintText.NoPrinterHere), _print.ResolvePrinter(null, []));
    }

    [Fact]
    public async Task Service_CopiesPagesAndLandscape()
    {
        Write("long.txt", string.Join("\n", Enumerable.Range(1, 120).Select(n => "line " + n)));

        var (plan, error) = _print.Prepare(new PrintRequest("long.txt", FakePrintSpooler.Color, 2, "2", true));
        Assert.Null(error);
        Assert.Equal((FakePrintSpooler.Color, true), Assert.Single(_spooler.Opened));
        Assert.True(plan!.Job!.Landscape);
        Assert.Equal(2, plan.Job.Copies);
        Assert.Equal(2, Assert.Single(plan.Job.Pages).Number);
        Assert.Contains(PrintText.PageOf(2, plan.TotalPages), Texts(plan.Job.Pages[0]).Select(t => t.Text));
        Assert.Equal($"Printed long.txt: page 2 of {plan.TotalPages} × 2 copies, landscape to Office Color", await _print.PrintAsync(plan, CancellationToken.None));

        Assert.Equal(PrintText.BadCopies("11"), _print.Prepare(new PrintRequest("long.txt", Copies: 11)).Error);
        Assert.Equal(PrintText.BadCopies("0"), _print.Prepare(new PrintRequest("long.txt", Copies: 0)).Error);
        Assert.Equal(PrintText.BadPages("9", 3), _print.Prepare(new PrintRequest("long.txt", Pages: "9")).Error);   // 120 lines upright: three pages
        Assert.Equal("Error: '9' is not a page range of this 3-page document; use 3, 1-3, 4- or 1,3,5-7", PrintText.BadPages("9", 3));
    }

    [Fact]
    public async Task Service_SaysWhatTheSpoolerSaid_AndPrintsAReply()
    {
        Write("a.txt", "a");
        _spooler.FailWith = PrintText.Failed(FakePrintSpooler.Laser, "out of paper");
        Assert.Equal(_spooler.FailWith, await _print.RunAsync(new PrintRequest("a.txt"), CancellationToken.None));

        _spooler.FailWith = null;
        string result = await _print.RunAsync(new PrintRequest(null, Markdown: "**Hi** there", Title: PrintText.ReplyTitle), CancellationToken.None);
        Assert.Equal("Printed Reply: 1 page to Office Laser", result);
        Assert.Equal(PrintText.ReplyTitle, _spooler.Jobs[^1].Title);
        Assert.True(Texts(_spooler.Jobs[^1].Pages[0]).Single(t => t.Text == "Hi").Font.Bold);
    }

    [Fact]
    public void Service_RefusesATextFileTooLongToPrint()
    {
        File.WriteAllText(Path.Combine(_root, "huge.log"), new string('x', (int)PrintText.MaxTextBytes + 1));
        Assert.Equal(PrintText.TooLarge("huge.log"), _print.Prepare(new PrintRequest("huge.log")).Error);
    }

    [Fact]
    public void Service_TheFontSizeSetting_IsHeldToItsRange()
    {
        _settings.PrintFontSize = 99;
        Assert.Equal(PrintLayout.MaxFontSize, _print.FontSize);
        _settings.PrintFontSize = 1;
        Assert.Equal(PrintLayout.MinFontSize, _print.FontSize);
    }

    // ── the policy and the tools ────────────────────────────────────────────

    [Fact]
    public void Policy_ResolvesAndJudges()
    {
        Assert.Equal(PrintPolicy.Ask, PrintPolicy.Default);
        Assert.Equal(PrintPolicy.Ask, PrintPolicy.Resolve("bogus"));
        Assert.Equal(PrintPolicy.Allow, PrintPolicy.Resolve(" ALLOW "));
        Assert.Equal(PrintVerdict.Ask, PrintPolicy.Judge(new AppSettingsData()));
        Assert.Equal(PrintVerdict.Refuse, PrintPolicy.Judge(new AppSettingsData { PrintActionPolicy = "off" }));
        Assert.Equal(PrintVerdict.Run, PrintPolicy.Judge(new AppSettingsData { PrintActionPolicy = "allow" }));
        Assert.All(PrintPolicy.Names, n => Assert.False(string.IsNullOrEmpty(PrintPolicy.Describe(n))));
    }

    [Fact]
    public async Task PrintFile_FollowsThePolicy()
    {
        Write("a.txt", "a");
        var asked = new List<string>();
        bool? answer = true;
        var tool = new PrintFileTool(_print, (question, _) => { asked.Add(question); return Task.FromResult(answer); });

        // ask, yes: the question names the file, the pages and the printer.
        Assert.Equal("Printed a.txt: 1 page to Office Laser", await tool.InvokeAsync(Args(("path", "a.txt"))));
        Assert.Equal(["Let the model print a.txt (1 page) on Office Laser?"], asked);

        answer = false;
        Assert.Equal(PrintText.Declined, await tool.InvokeAsync(Args(("path", "a.txt"))));
        answer = null;
        Assert.Equal(PrintText.NotAsked, await tool.InvokeAsync(Args(("path", "a.txt"))));
        Assert.Equal(PrintText.NotAsked, await new PrintFileTool(_print, null).InvokeAsync(Args(("path", "a.txt"))));
        Assert.Single(_spooler.Jobs);

        _settings.PrintActionPolicy = PrintPolicy.Off;
        Assert.Equal(PrintText.PolicyOff, await tool.InvokeAsync(Args(("path", "a.txt"))));

        _settings.PrintActionPolicy = PrintPolicy.Allow;
        asked.Clear();
        Assert.StartsWith("Printed a.txt", (string)(await new PrintFileTool(_print, null).InvokeAsync(Args(("path", "a.txt"), ("copies", 2), ("landscape", true), ("printer", "color"))))!);
        Assert.Empty(asked);
        Assert.Equal((FakePrintSpooler.Color, 2, true), (_spooler.Jobs[^1].Printer, _spooler.Jobs[^1].Copies, _spooler.Jobs[^1].Landscape));
    }

    [Fact]
    public async Task PrintFile_RefusesBadArguments_BeforeAnythingPrints()
    {
        _settings.PrintActionPolicy = PrintPolicy.Allow;
        Write("a.txt", "a");
        var tool = new PrintFileTool(_print, null);

        Assert.Equal(FileText.PathRequired(FileTool.PathArgument), await tool.InvokeAsync(Args()));
        Assert.Equal(PrintText.BadCopies("11"), await tool.InvokeAsync(Args(("path", "a.txt"), ("copies", 11))));
        Assert.Equal(FileText.BadBoolean("landscape", "maybe"), await tool.InvokeAsync(Args(("path", "a.txt"), ("landscape", "maybe"))));
        Assert.Equal(PrintText.UnknownPrinter("inkjet", _spooler.Installed), await tool.InvokeAsync(Args(("path", "a.txt"), ("printer", "inkjet"))));
        Assert.Empty(_spooler.Jobs);
    }

    [Fact]
    public async Task ListPrinters_MarksTheDefaults()
    {
        _settings.PrintDefaultPrinter = FakePrintSpooler.Color;

        string text = (string)(await new ListPrintersTool(_print).InvokeAsync(new AIFunctionArguments()))!;

        Assert.Equal("3 printers\n- Office Laser (" + PrintText.DefaultMark + ")\n- Office Color (Print default printer)\n- Microsoft Print to PDF", text);
        Assert.Equal("3 printers", PrintText.Note(text));
        Assert.Equal(OperatingSystem.IsMacOS() ? PrintText.MacNoPrinters : PrintText.NoPrinters, PrintText.PrinterList([], null));
    }

    [Fact]
    public void EveryTool_IsNamed_AndClassifiedForPlanMode()
    {
        var tools = ChatScreen.PrintTools(_print, null);

        Assert.Equal(ChatScreen.PrintToolNames.Order(StringComparer.Ordinal), tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.True(PlanTools.ReadOnly.Contains(t.Name) ^ PlanTools.Mutating.Contains(t.Name), t.Name));
        Assert.Contains(ListPrintersTool.ToolName, PlanTools.ReadOnly);
        Assert.Contains(PrintFileTool.ToolName, PlanTools.Mutating);
    }

    [Fact]
    public void PrepareTurn_OffersTheGroup_OnlyWhileEnabled()
    {
        var assistant = new Assistant(new FakeChatClient(), new ConversationHistory(""), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        var memory = new MemoryStore(_dir);
        string home = Path.Combine(_dir, "profile");
        var tools = ChatScreen.PrintTools(_print, null);

        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, printTools: tools, printEnabled: true);
        Assert.Contains(PrintFileTool.ToolName, assistant.Tools.Select(t => t.Name));

        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, printTools: tools, printEnabled: false);
        Assert.DoesNotContain(PrintFileTool.ToolName, assistant.Tools.Select(t => t.Name));

        // Planning keeps the list and drops the print.
        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, disabledTools: PlanTools.Widen(null, tools), printTools: tools, printEnabled: true);
        Assert.Contains(ListPrintersTool.ToolName, assistant.Tools.Select(t => t.Name));
        Assert.DoesNotContain(PrintFileTool.ToolName, assistant.Tools.Select(t => t.Name));
    }

    // ── /print ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_TakesThePathAndTheOptions()
    {
        Assert.Equal(new PrintArguments("my notes.txt", "Office Color", 2, "1-3", true, null), PrintCommand.Parse("my notes.txt printer=\"Office Color\" copies=2 pages=1-3 landscape"));
        Assert.Equal(new PrintArguments("reply", null, 1, null, false, null), PrintCommand.Parse(" reply "));
        Assert.Equal(new PrintArguments("", null, 1, null, false, null), PrintCommand.Parse(""));
        Assert.Equal(PrintText.BadCopies("x"), PrintCommand.Parse("a.txt copies=x").Error);
        Assert.Equal(PrintText.BadCopies("11"), PrintCommand.Parse("a.txt copies=11").Error);
        Assert.Equal(["a b", "c"], PrintCommand.Words("\"a b\" c").ToList());
    }

    [Fact]
    public async Task Command_ListsPrintsAndSaysWhy()
    {
        Write("a.txt", "a");

        var usage = await PrintCommand.RunAsync(_print, "", () => null, CancellationToken.None);
        Assert.False(usage.Failed);
        Assert.Equal(PrintText.Usage, usage.Lines[0]);
        Assert.Contains("- Office Laser (" + PrintText.DefaultMark + ")", usage.Lines);

        var list = await PrintCommand.RunAsync(_print, "printers", () => null, CancellationToken.None);
        Assert.Equal("3 printers", list.Lines[0]);

        Assert.Equal(PrintCommandResult.Error(PrintText.NoReply).Lines, (await PrintCommand.RunAsync(_print, "reply", () => " ", CancellationToken.None)).Lines);

        var reply = await PrintCommand.RunAsync(_print, "reply printer=color", () => "# Answer", CancellationToken.None);
        Assert.Equal(["Printed Reply: 1 page to Office Color"], reply.Lines);

        var file = await PrintCommand.RunAsync(_print, "a.txt", () => null, CancellationToken.None);
        Assert.Equal(["Printed a.txt: 1 page to Office Laser"], file.Lines);

        var missing = await PrintCommand.RunAsync(_print, "nope.txt", () => null, CancellationToken.None);
        Assert.True(missing.Failed);
        Assert.Equal([FileText.Missing("nope.txt")], missing.Lines);

        // The user's own hand: printed even with the model's policy off.
        _settings.PrintActionPolicy = PrintPolicy.Off;
        Assert.False((await PrintCommand.RunAsync(_print, "a.txt", () => null, CancellationToken.None)).Failed);
        Assert.Equal(3, _spooler.Jobs.Count);
    }

    [Fact]
    public void Complete_OffersTheVerbs_ThenTheOptions()
    {
        IReadOnlyList<string> printers = [FakePrintSpooler.Laser, "Solo"];

        Assert.Equal([PrintText.ReplyWord, PrintText.PrintersWord], PrintCommand.Complete("", printers).Select(i => i.Text));
        Assert.Equal([PrintText.ReplyWord], PrintCommand.Complete("r", printers).Select(i => i.Text));
        Assert.Equal(
            ["a.txt printer=\"Office Laser\"", "a.txt printer=Solo", "a.txt landscape", "a.txt copies=", "a.txt pages="],
            PrintCommand.Complete("a.txt ", printers).Select(i => i.Text));
        Assert.Equal(["a.txt printer=Solo landscape", "a.txt printer=Solo copies=", "a.txt printer=Solo pages="], PrintCommand.Complete("a.txt printer=Solo ", printers).Select(i => i.Text));
        Assert.Empty(PrintCommand.Complete("printers ", printers));
    }

    [Fact]
    public void PrintPaths_ListsFiles_UntilATargetIsDone()
    {
        Write("a.txt", "a");
        Write("report.md", "r");
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], q => _files.Complete(q), q => _files.Complete(q), AnyFiles: q => _files.Complete(q));

        Assert.Equal(["a.txt", "report.md"], ChatScreen.PrintPaths("", sources)!.Paths);
        Assert.Equal(["report.md"], ChatScreen.PrintPaths("re", sources)!.Paths);
        Assert.Null(ChatScreen.PrintPaths("repl", sources));             // no file, a verb: the word list
        Assert.Null(ChatScreen.PrintPaths("a.txt ", sources));           // a whole path and a space: the options
        Assert.Null(ChatScreen.PrintPaths("reply ", sources));
        Assert.Empty(ChatScreen.PrintPaths("a.txt", sources)!.Paths);    // typed in full: closed
        Assert.Null(ChatScreen.PrintPaths("", sources with { AnyFiles = null }));
    }

    [Fact]
    public void SlashCommand_IsRegistered()
    {
        Assert.Equal((SlashCommand.Print, "a.txt copies=2"), SlashCommands.Parse("/print a.txt copies=2"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Print));
        Assert.Contains("/print", SlashCommands.Words);
        Assert.Contains(SlashCommands.HelpEntries, e => e.Command == "/print");
    }

    // ── settings ────────────────────────────────────────────────────────────

    [Fact]
    public void Settings_ThePrintTab()
    {
        int tab = ToolsText.TabTitles.ToList().IndexOf(ToolsText.PrintTabTitle);
        Assert.Equal(ToolsText.TabTitles.ToList().IndexOf(ToolsText.UncTabTitle) + 1, tab);   // after UNC since 2026-10-03, the user's order (after Claude from later on 2026-10-01)
        Assert.Equal([SettingsField.PrintTools, SettingsField.PrintActionPolicy, SettingsField.PrintDefaultPrinter, SettingsField.PrintFontSize, SettingsField.PdfEngine], SettingsMenu.ToolsTabFields[tab - 1]);   // the PDF engine last since 2026-10-03
        Assert.True(SettingsMenu.IsToggle(SettingsField.PrintTools));

        var data = new AppSettingsData();
        Assert.False(data.PrintTools);
        Assert.Equal(PrintPolicy.Ask, data.PrintActionPolicy);
        Assert.Equal(PrintLayout.DefaultFontSize, data.PrintFontSize);
        Assert.Equal(SettingsMenu.WindowsDefaultPrinterLabel, SettingsMenu.FieldValue(SettingsField.PrintDefaultPrinter, data, _dir));
        Assert.Equal("10 pt", SettingsMenu.FieldValue(SettingsField.PrintFontSize, data, _dir));

        List<PrinterInfo> printers = [new("A", true), new("B", false)];
        Assert.Contains(PrintText.DefaultMark, SettingsMenu.PrinterRow("A", printers));
        Assert.Contains("not installed", SettingsMenu.PrinterRow("Gone", printers));
        Assert.Equal("B", SettingsMenu.PrinterRow("B", printers));
        Assert.Equal(SettingsMenu.WindowsDefaultPrinterLabel, SettingsMenu.PrinterRow("", printers));
    }

    // ── the real spooler ────────────────────────────────────────────────────

    /// <summary>Microsoft Print to PDF, for real: two landscape pages to a temp file (the smoke's probe, under the JIT). A local gate.</summary>
    [PrintToPdfFact]
    public void RealSpooler_PrintsToPdf()
    {
        var check = SmokeChecks.ProbePrintSpooler();
        Assert.True(check.Passed, check.Detail);
        Assert.Contains("two landscape pages printed", check.Detail, StringComparison.Ordinal);
    }
}
