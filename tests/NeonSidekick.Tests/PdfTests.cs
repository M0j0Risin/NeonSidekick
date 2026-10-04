using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Pdf;
using NeonSidekick.Plans;
using NeonSidekick.Printing;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI.Markdown;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// PDFs (2026-10-03): the page the browser prints (<see cref="PdfHtml"/>, <see cref="PdfHtmlSanitizer"/>), the pure decisions
/// (<see cref="PdfPlan"/>, <see cref="PdfEngine"/>, <see cref="PdfPaper"/>), the converter over the fake browser and the fake
/// spooler (the routes, the fallback, the refusals before any work, the sandbox), the tool, <c>/pdf</c>, the browser's command
/// line and the print-to-file wait. The real browser and printer are the smoke's.
/// </summary>
public sealed class PdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _temp;
    private readonly AppSettingsData _settings = new() { WebTools = true, FileTools = true };
    private readonly FakePrintSpooler _spooler = new();
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly WorkingDirectory _files;
    private readonly PrintService _print;
    private readonly WebAccess _web;
    private readonly PdfConverter _pdf;
    private IPAddress[] _resolved = [IPAddress.Parse("93.184.216.34")];

    public PdfTests()
    {
        _root = Path.Combine(_dir, "files");
        _temp = Path.Combine(_dir, "temp");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, TimeProvider.System);
        _print = new PrintService(_spooler, _files, () => _settings, TimeProvider.System);
        _web = new WebAccess(new HttpClient(new StubHttpMessageHandler()), _browser, TimeProvider.System, (_, _) => Task.FromResult(_resolved));
        _pdf = new PdfConverter(_web.Browser, _web.Fetcher, _print, _files, () => _settings, TimeProvider.System, _temp, TimeSpan.FromMilliseconds(400));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string Write(string relative, string text)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private string WriteBytes(string relative, byte[] bytes)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    /// <summary>Eight bytes that start like a PNG: enough for a data URI, which nobody decodes here.</summary>
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in pairs)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private Task<string> Convert(PdfRequest request) => _pdf.ConvertAsync(request, CancellationToken.None);

    // ── the page ────────────────────────────────────────────────────────────

    [Fact]
    public void Document_CarriesThePolicy_TheEscapedTitle_AndThePaper()
    {
        string html = PdfHtml.Document("a <b> & \"c\"", "<p>x</p>", PdfPaperSize.A4, landscape: true);
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains(PdfHtml.PolicyMeta, html, StringComparison.Ordinal);
        Assert.Contains("<title>a &lt;b&gt; &amp; &quot;c&quot;</title>", html, StringComparison.Ordinal);
        Assert.Contains("size: A4 landscape;", html, StringComparison.Ordinal);
        Assert.Contains("@top-left { content: \"a \\3C b> & \\\"c\\\"\";", html, StringComparison.Ordinal);
        Assert.Contains("counter(page)", html, StringComparison.Ordinal);
        Assert.Contains("<body>\n<p>x</p>\n</body>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePolicy_AllowsOnlyWhatIsInline()
    {
        Assert.Equal("default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; base-uri 'none'; form-action 'none'", PdfHtml.ContentSecurityPolicy);
    }

    [Theory]
    [InlineData(PdfPaperSize.Letter, false, "letter")]
    [InlineData(PdfPaperSize.Letter, true, "letter landscape")]
    [InlineData(PdfPaperSize.A4, false, "A4")]
    [InlineData(PdfPaperSize.Legal, true, "legal landscape")]
    public void Paper_Css(PdfPaperSize paper, bool landscape, string css) => Assert.Equal(css, PdfPaper.Css(paper, landscape));

    [Fact]
    public void Paper_Parses_TheThreeNames_AnyCase()
    {
        Assert.True(PdfPaper.TryParse(" A4 ", out var a4));
        Assert.Equal(PdfPaperSize.A4, a4);
        Assert.True(PdfPaper.TryParse("legal", out var legal));
        Assert.Equal(PdfPaperSize.Legal, legal);
        Assert.False(PdfPaper.TryParse("tabloid", out var fallback));
        Assert.Equal(PdfPaper.Default, fallback);
        Assert.Equal(PdfPaperSize.Letter, PdfPaper.Default);   // the user's call
    }

    [Fact]
    public void Markdown_RendersTablesTaskListsAndColouredCode()
    {
        string body = PdfHtml.Markdown("# T\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\n```csharp\npublic int X = 1;\n```\n\n~~old~~", _ => null);
        Assert.Contains("<h1", body, StringComparison.Ordinal);
        Assert.Contains("<table>", body, StringComparison.Ordinal);
        Assert.Contains("task-list-item", body, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tk-keyword\">public</span>", body, StringComparison.Ordinal);
        Assert.Contains("<span class=\"tk-number\">1</span>", body, StringComparison.Ordinal);
        Assert.Contains("<del>old</del>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_EscapesRawHtml()
    {
        string body = PdfHtml.Markdown("Hi <script>alert(1)</script>\n\n<iframe src=\"file:///C:/x\"></iframe>", _ => null);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_Pictures_InlinedOrTheirAltText()
    {
        var asked = new List<string>();
        string body = PdfHtml.Markdown("![one](a.png) ![two](b.png) ![](c.png)", url =>
        {
            asked.Add(url);
            return url == "a.png" ? "data:image/png;base64,AAAA" : null;
        });
        Assert.Equal(["a.png", "b.png", "c.png"], asked);
        Assert.Contains("src=\"data:image/png;base64,AAAA\"", body, StringComparison.Ordinal);
        Assert.Contains("[image: two]", body, StringComparison.Ordinal);
        Assert.Contains("[image]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("b.png", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Listing_Escapes_AndColoursByTheExtension()
    {
        string cs = PdfHtml.Listing("if (a < b) { }", "src/x.cs");
        Assert.StartsWith("<pre class=\"listing\"><code>", cs, StringComparison.Ordinal);
        Assert.Contains("tk-keyword", cs, StringComparison.Ordinal);
        Assert.Contains("&lt;", cs, StringComparison.Ordinal);
        Assert.Equal("<pre class=\"listing\"><code>a &amp; b</code></pre>", PdfHtml.Listing("a & b", "notes.txt"));
    }

    [Fact]
    public void CodeClass_EveryKindButPlain()
    {
        foreach (var kind in Enum.GetValues<CodeTokenKind>())
        {
            Assert.Equal(kind == CodeTokenKind.Plain, PdfHtml.CodeClass(kind) is null);
            if (PdfHtml.CodeClass(kind) is { } css)
            {
                Assert.Contains("." + css + " {", PdfHtml.Stylesheet, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ImageMediaType_ByExtension()
    {
        Assert.Equal("image/png", PdfHtml.ImageMediaType("a.PNG"));
        Assert.Equal("image/jpeg", PdfHtml.ImageMediaType("a.jpeg"));
        Assert.Equal("image/svg+xml", PdfHtml.ImageMediaType("a.svg"));
        Assert.Null(PdfHtml.ImageMediaType("a.txt"));
    }

    // ── a user's HTML ───────────────────────────────────────────────────────

    [Fact]
    public void Sanitizer_DropsWhatRunsOrReachesOut_KeepsTheRest()
    {
        const string page = """
            <!doctype html><html><head><base href="file:///C:/"><meta http-equiv="refresh" content="0;url=https://x">
            <script>steal()</script><link rel="icon" href="i.png"></head>
            <body onload="go()"><h1 class="t">Hi &amp; bye</h1><iframe src="file:///C:/Windows/win.ini"></iframe>
            <a href="javascript:alert(1)">j</a><a href="https://example.com">e</a><img src="pic.png" srcset="big.png 2x"><img src="file:///C:/out.png" alt="o"></body></html>
            """;
        string html = PdfHtmlSanitizer.Rewrite(page, PdfPaperSize.Letter, landscape: false, address => address == "pic.png" ? PngBytes : null);
        Assert.StartsWith("<!DOCTYPE html>\n<meta charset=\"utf-8\">\n" + PdfHtml.PolicyMeta, html, StringComparison.Ordinal);
        Assert.Contains("@page { size: letter; margin: 16mm; }", html, StringComparison.Ordinal);
        foreach (string gone in new[] { "<base", "refresh", "<script", "steal()", "<iframe", "onload", "javascript:", "srcset", "file:///", "<link" })
        {
            Assert.DoesNotContain(gone, html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("<h1 class=\"t\">Hi &amp; bye</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://example.com\">e</a>", html, StringComparison.Ordinal);
        Assert.Contains("<img src=\"data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.Contains("<img alt=\"o\" />", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitizer_InlinesASandboxStylesheet_KeepsStyleBodies()
    {
        string html = PdfHtmlSanitizer.Rewrite(
            "<link rel=\"stylesheet\" href=\"s.css\"><link rel=\"stylesheet\" href=\"gone.css\"><style>p > b { color: red }</style><p>x</p>",
            PdfPaperSize.A4, landscape: true, address => address == "s.css" ? Encoding.UTF8.GetBytes("body{color:blue}</style>") : null);
        Assert.Contains("<style>body{color:blue}<\\/style></style>", html, StringComparison.Ordinal);
        Assert.Contains("<style>p > b { color: red }</style>", html, StringComparison.Ordinal);
        Assert.Contains("size: A4 landscape", html, StringComparison.Ordinal);
        Assert.DoesNotContain("gone.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", html, StringComparison.Ordinal);   // the page had none
    }

    // ── the decisions ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("a.md", PdfSourceKind.Markdown)]
    [InlineData("a.markdown", PdfSourceKind.Markdown)]
    [InlineData("a.HTML", PdfSourceKind.Html)]
    [InlineData("a.htm", PdfSourceKind.Html)]
    [InlineData("a.png", PdfSourceKind.Picture)]
    [InlineData("a.cs", PdfSourceKind.Listing)]
    public void Classify_ByName(string path, PdfSourceKind kind)
    {
        Assert.Equal(kind, PdfPlan.Classify(path, _ => true, out bool isPdf));
        Assert.False(isPdf);
    }

    [Fact]
    public void Classify_APdf_AndABinary()
    {
        Assert.Null(PdfPlan.Classify("a.pdf", _ => true, out bool isPdf));
        Assert.True(isPdf);
        Assert.Null(PdfPlan.Classify("a.exe", _ => false, out isPdf));
        Assert.False(isPdf);
    }

    [Theory]
    // auto: the browser when found, else the printer for what it can draw.
    [InlineData(PdfSourceKind.Markdown, "auto", true, true, PdfRoute.Browser)]
    [InlineData(PdfSourceKind.Markdown, "auto", false, true, PdfRoute.Printer)]
    [InlineData(PdfSourceKind.Picture, "", false, true, PdfRoute.Printer)]
    [InlineData(PdfSourceKind.Html, "auto", true, false, PdfRoute.Browser)]
    [InlineData(PdfSourceKind.Url, "browser", true, false, PdfRoute.Browser)]
    [InlineData(PdfSourceKind.Listing, "printer", true, true, PdfRoute.Printer)]
    public void Choose_Routes(PdfSourceKind kind, string engine, bool browser, bool printer, PdfRoute route)
    {
        var (chosen, error) = PdfPlan.Choose(kind, engine, browser, printer, "x");
        Assert.Null(error);
        Assert.Equal(route, chosen);
    }

    [Fact]
    public void Choose_Refusals()
    {
        Assert.Equal(PdfText.NeedsBrowser("a web page"), PdfPlan.Choose(PdfSourceKind.Url, "auto", false, true, "a web page").Error);
        Assert.Equal(PdfText.NoRoute(PrintText.PrintToPdfPrinter), PdfPlan.Choose(PdfSourceKind.Markdown, "auto", false, false, "x").Error);
        Assert.Equal(PdfText.NoBrowser, PdfPlan.Choose(PdfSourceKind.Markdown, "browser", false, true, "x").Error);
        Assert.Equal(PdfText.NeedsBrowserByEngine("p.html"), PdfPlan.Choose(PdfSourceKind.Html, "printer", true, true, "p.html").Error);
        Assert.Equal(PdfText.NoPrinter(PrintText.PrintToPdfPrinter), PdfPlan.Choose(PdfSourceKind.Markdown, "printer", true, false, "x").Error);
    }

    [Fact]
    public void Engine_Resolves_AndDescribes()
    {
        Assert.Equal(["auto", "browser", "printer"], PdfEngine.Names);
        Assert.Equal("auto", new AppSettingsData().PdfEngine);
        Assert.Equal("printer", PdfEngine.Resolve(" PRINTER "));
        Assert.Equal("auto", PdfEngine.Resolve("pdfium"));
        Assert.All(PdfEngine.Names, name => Assert.False(string.IsNullOrWhiteSpace(PdfEngine.Describe(name))));
    }

    [Theory]
    [InlineData(null, "notes.md", null, null, "notes.pdf")]
    [InlineData(null, "docs/a b.md", null, null, "docs/a b.pdf")]
    [InlineData("out/x", "notes.md", null, null, "out/x.pdf")]
    [InlineData("out/x.PDF", "notes.md", null, null, "out/x.PDF")]
    [InlineData("out/", "docs/notes.md", null, null, "out/notes.pdf")]
    [InlineData("folder", "notes.md", null, null, "folder/notes.pdf")]
    [InlineData(null, null, "https://www.example.com/docs/intro.html?x=1", null, "example.com-intro.pdf")]
    [InlineData(null, null, "https://example.com/", null, "example.com.pdf")]
    [InlineData(null, null, null, null, "reply-2026-10-03-1405.pdf")]
    [InlineData(null, null, null, "Reply", "reply-2026-10-03-1405.pdf")]
    [InlineData("folder", null, null, "Weekly report", "folder/Weekly-report-2026-10-03-1405.pdf")]
    public void OutputFor_Names(string? to, string? source, string? url, string? title, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 3, 14, 5, 0, TimeSpan.Zero);
        Assert.Equal(expected, PdfPlan.OutputFor(to, source, url is null ? null : new Uri(url), title, now, path => path == "folder"));
    }

    // ── the converter ───────────────────────────────────────────────────────

    [Fact]
    public async Task Markdown_ThroughTheBrowser_LandsBesideTheSource()
    {
        Write("docs/notes.md", "# Notes\n\n![p](img/p.png) ![out](../../outside.png)");
        WriteBytes("docs/img/p.png", PngBytes);
        string result = await Convert(new PdfRequest(Path: "docs/notes.md"));

        Assert.Equal($"Made {Path.Combine("docs", "notes.pdf")} (21 B) from {Path.Combine("docs", "notes.md")} with msedge", result);
        Assert.True(PrintToFile.IsPdf(Path.Combine(_root, "docs", "notes.pdf")));
        var (page, output) = Assert.Single(_browser.PdfRuns);
        Assert.True(page.IsFile);
        Assert.StartsWith(_temp, output, StringComparison.OrdinalIgnoreCase);
        string html = Assert.Single(_browser.PdfPages);
        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.Contains("[image: out]", html, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_temp));   // the engine's files went
        Assert.Empty(_spooler.Jobs);
    }

    [Fact]
    public async Task AListing_APicture_AndTheReply()
    {
        Write("a.cs", "class A { }");
        WriteBytes("p.png", PngBytes);
        Assert.StartsWith("Made a.pdf", await Convert(new PdfRequest(Path: "a.cs", Paper: PdfPaperSize.A4, Landscape: true)), StringComparison.Ordinal);
        Assert.Contains("tk-keyword", _browser.PdfPages[^1], StringComparison.Ordinal);
        Assert.Contains("size: A4 landscape", _browser.PdfPages[^1], StringComparison.Ordinal);

        Assert.StartsWith("Made p.pdf", await Convert(new PdfRequest(Path: "p.png")), StringComparison.Ordinal);
        Assert.Contains("class=\"picture\"", _browser.PdfPages[^1], StringComparison.Ordinal);

        string reply = await Convert(new PdfRequest(Markdown: "**hi**", Title: PrintText.ReplyTitle, To: "out/"));
        Assert.Matches(@"^Made out[\\/]reply-\d{4}-\d{2}-\d{2}-\d{4}\.pdf ", reply);
        Assert.Contains("<strong>hi</strong>", _browser.PdfPages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHtmlFile_IsCleanedBeforeTheBrowserSeesIt()
    {
        Write("site/page.html", "<html><body><script>x()</script><img src=\"logo.png\"><p>Hello</p></body></html>");
        WriteBytes("site/logo.png", PngBytes);
        Assert.StartsWith("Made", await Convert(new PdfRequest(Path: "site/page.html")), StringComparison.Ordinal);
        string html = Assert.Single(_browser.PdfPages);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.Contains("<img src=\"data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.Contains(PdfHtml.PolicyMeta, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWebPage_IsPrintedAsItIs_AfterTheNetworkModeSaysYes()
    {
        string result = await Convert(new PdfRequest(Url: new Uri("https://example.com/docs/intro.html")));
        Assert.Equal("Made example.com-intro.pdf (21 B) from https://example.com/docs/intro.html with msedge", result);
        Assert.Equal(new Uri("https://example.com/docs/intro.html"), Assert.Single(_browser.PdfRuns).Page);
        Assert.Empty(_browser.PdfPages);
    }

    [Fact]
    public async Task AWebPage_OnTheLan_IsRefusedUnderInternet()
    {
        _resolved = [IPAddress.Parse("192.168.1.20")];
        string result = await Convert(new PdfRequest(Url: new Uri("http://nas.home/")));
        Assert.Equal(WebText.LanRefused("nas.home"), result);
        Assert.Empty(_browser.PdfRuns);
        Assert.False(File.Exists(Path.Combine(_root, "nas.home.pdf")));
    }

    [Fact]
    public async Task AWebPage_KeepsItsOwnLayout()
    {
        Assert.Equal(PdfText.PageLayoutFixed, await Convert(new PdfRequest(Url: new Uri("https://example.com/"), Landscape: true)));
        Assert.Equal(PdfText.PageLayoutFixed, await Convert(new PdfRequest(Url: new Uri("https://example.com/"), Paper: PdfPaperSize.A4)));
    }

    [Fact]
    public async Task Refusals_ComeBeforeAnyWork()
    {
        Write("notes.md", "# x");
        Write("notes.pdf", "already");
        Write("doc.pdf", "%PDF");
        WriteBytes("tool.exe", [0, 1, 2, 0, 0, 0]);

        Assert.Equal(PdfText.OneSource, await Convert(new PdfRequest()));
        Assert.Equal(PdfText.OneSource, await Convert(new PdfRequest(Path: "notes.md", Markdown: "x")));
        Assert.Equal(FileText.Exists("notes.pdf"), await Convert(new PdfRequest(Path: "notes.md")));
        Assert.Equal(FileText.OutsideRoot("../x.pdf"), await Convert(new PdfRequest(Path: "notes.md", To: "../x.pdf")));
        Assert.Equal(FileText.OutsideRoot("../notes.md"), await Convert(new PdfRequest(Path: "../notes.md")));
        Assert.Equal(FileText.Missing("gone.md"), await Convert(new PdfRequest(Path: "gone.md")));
        Assert.Equal(PdfText.AlreadyPdf("doc.pdf"), await Convert(new PdfRequest(Path: "doc.pdf")));
        Assert.Equal(PdfText.NotConvertible("tool.exe"), await Convert(new PdfRequest(Path: "tool.exe")));
        Assert.Empty(_browser.PdfRuns);
        Assert.Equal("already", File.ReadAllText(Path.Combine(_root, "notes.pdf")));

        Assert.StartsWith("Replaced notes.pdf", await Convert(new PdfRequest(Path: "notes.md", Overwrite: true)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoBrowser_MarkdownGoesToThePrinter_HtmlIsRefused()
    {
        _browser.Executable = null;
        Write("notes.md", "# Notes");
        Write("page.html", "<p>x</p>");

        string result = await Convert(new PdfRequest(Path: "notes.md", Landscape: true));
        Assert.Equal($"Made notes.pdf (15 B) from notes.md with {PrintText.PrintToPdfPrinter}", result);
        var job = Assert.Single(_spooler.Jobs);
        Assert.Equal(FakePrintSpooler.Pdf, job.Printer);
        Assert.True(job.Landscape);
        Assert.StartsWith(_temp, job.OutputFile, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_temp));

        Assert.Equal(PdfText.NeedsBrowser("page.html"), await Convert(new PdfRequest(Path: "page.html")));
        Assert.Equal(PdfText.NeedsBrowser("a web page"), await Convert(new PdfRequest(Url: new Uri("https://example.com/"))));
    }

    [Fact]
    public async Task ABrowserFailure_FallsBackToThePrinter_UnderAutoOnly()
    {
        _browser.PdfFailure = "exit code 21";
        Write("notes.md", "# Notes");
        string result = await Convert(new PdfRequest(Path: "notes.md"));
        Assert.Equal($"Made notes.pdf (15 B) from notes.md with {PrintText.PrintToPdfPrinter}; the browser failed first: exit code 21", result);
        Assert.Single(_spooler.Jobs);

        _settings.PdfEngine = PdfEngine.Browser;
        Assert.Equal(PdfText.BrowserFailed("msedge", "exit code 21"), await Convert(new PdfRequest(Path: "notes.md", Overwrite: true)));
        Assert.Single(_spooler.Jobs);

        _settings.PdfEngine = PdfEngine.Auto;
        Write("page.html", "<p>x</p>");
        Assert.Equal(PdfText.BrowserFailed("msedge", "exit code 21"), await Convert(new PdfRequest(Path: "page.html")));
    }

    [Fact]
    public async Task EnginePrinter_NeverAsksTheBrowser()
    {
        _settings.PdfEngine = PdfEngine.Printer;
        Write("notes.md", "# Notes");
        Assert.EndsWith("with " + PrintText.PrintToPdfPrinter, await Convert(new PdfRequest(Path: "notes.md")), StringComparison.Ordinal);
        Assert.Empty(_browser.LocateCalls);
        Assert.Empty(_browser.PdfRuns);
    }

    [Fact]
    public async Task ThePrinterWritesNothing_OrNotAPdf()
    {
        _browser.Executable = null;
        Write("notes.md", "# Notes");
        _spooler.OutputBytes = null;
        Assert.Equal(PdfText.PrinterNoFile(PrintText.PrintToPdfPrinter), await Convert(new PdfRequest(Path: "notes.md")));
        _spooler.OutputBytes = "hello"u8.ToArray();
        Assert.Equal(PdfText.PrinterNotPdf(PrintText.PrintToPdfPrinter), await Convert(new PdfRequest(Path: "notes.md")));
        Assert.False(File.Exists(Path.Combine(_root, "notes.pdf")));
    }

    [Fact]
    public async Task NoBrowserAndNoPrinter()
    {
        _browser.Executable = null;
        _spooler.Installed.RemoveAll(p => p.Name == FakePrintSpooler.Pdf);
        Write("notes.md", "# Notes");
        Assert.Equal(PdfText.NoRoute(PrintText.PrintToPdfPrinter), await Convert(new PdfRequest(Path: "notes.md")));
    }

    // ── the tool ────────────────────────────────────────────────────────────

    private ConvertToPdfTool Tool() => new(_files, _pdf, () => _settings);

    [Fact]
    public async Task Tool_Arguments()
    {
        Write("notes.md", "# Notes");
        var tool = Tool();
        Assert.Equal(FileText.BadChoice("paper", "tabloid", PdfText.PaperChoices), await tool.InvokeAsync(Args(("path", "notes.md"), ("paper", "tabloid"))));
        Assert.Equal(FileText.BadBoolean("landscape", "sideways"), await tool.InvokeAsync(Args(("path", "notes.md"), ("landscape", "sideways"))));
        Assert.Equal(PdfText.OneSource, await tool.InvokeAsync(Args(("path", "notes.md"), ("markdown", "# x"))));
        Assert.Equal(WebText.NotHttp("ftp://x"), await tool.InvokeAsync(Args(("url", "ftp://x"))));

        Assert.StartsWith("Made notes.pdf", (string)(await tool.InvokeAsync(Args(("path", "notes.md"), ("paper", "a4"))))!, StringComparison.Ordinal);
        Assert.Contains("size: A4;", _browser.PdfPages[^1], StringComparison.Ordinal);
        Assert.StartsWith("Made report.pdf", (string)(await tool.InvokeAsync(Args(("markdown", "# R"), ("title", "R"), ("to", "report"))))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_AUrlNeedsTheWebTools()
    {
        _settings.WebTools = false;
        Assert.Equal(PdfText.UrlNeedsWebTools, await Tool().InvokeAsync(Args(("url", "https://example.com"))));
        Assert.Empty(_browser.PdfRuns);
    }

    [Fact]
    public void Tool_IsAFileTool_NotForPlanMode_AndQuiet()
    {
        var tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, pdf: _pdf);
        Assert.Equal(FileToolNames.All, tools.Select(t => t.Name));
        Assert.Equal(ConvertToPdfTool.ToolName, FileToolNames.All[^1]);
        Assert.Equal(FileToolNames.WithoutPdf, ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings).Select(t => t.Name));
        Assert.Contains(ConvertToPdfTool.ToolName, PlanTools.Mutating);
        Assert.DoesNotContain(ConvertToPdfTool.ToolName, PlanTools.ReadOnly);
        Assert.Contains(ConvertToPdfTool.ToolName, ChatScreen.QuietTools);
    }

    // ── /pdf ────────────────────────────────────────────────────────────────

    [Fact]
    public void Command_Parse()
    {
        var parsed = PdfCommand.Parse("my notes.md to=\"out/a b.pdf\" paper=A4 landscape overwrite");
        Assert.Equal(new PdfArguments("my notes.md", "out/a b.pdf", PdfPaperSize.A4, true, true, null), parsed);
        Assert.Equal(FileText.BadChoice("paper", "huge", PdfText.PaperChoices), PdfCommand.Parse("a.md paper=huge").Error);
        Assert.True(PdfCommand.IsWebAddress("HTTPS://x"));
        Assert.False(PdfCommand.IsWebAddress("notes.md"));
    }

    [Fact]
    public async Task Command_Runs()
    {
        Write("notes.md", "# Notes");
        Assert.Equal(PdfText.Usage, await PdfCommand.RunAsync(_pdf, "", () => null, CancellationToken.None));
        Assert.Equal(PdfText.NoReply, await PdfCommand.RunAsync(_pdf, "reply", () => " ", CancellationToken.None));
        Assert.StartsWith("Made", await PdfCommand.RunAsync(_pdf, "reply to=r.pdf", () => "**r**", CancellationToken.None), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "r.pdf")));
        Assert.StartsWith("Made notes.pdf", await PdfCommand.RunAsync(_pdf, "notes.md", () => null, CancellationToken.None), StringComparison.Ordinal);
        Assert.StartsWith("Made example.com.pdf", await PdfCommand.RunAsync(_pdf, "https://example.com/", () => null, CancellationToken.None), StringComparison.Ordinal);

        // The user's own hand: no Web tools switch asked.
        _settings.WebTools = false;
        Assert.StartsWith("Replaced example.com.pdf", await PdfCommand.RunAsync(_pdf, "https://example.com/ overwrite", () => null, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public void Command_Complete()
    {
        Assert.Equal(["reply"], PdfCommand.Complete("re").Select(i => i.Text));
        var after = PdfCommand.Complete("notes.md paper=a4 ").Select(i => i.Text).ToList();
        Assert.Contains("notes.md paper=a4 to=", after);
        Assert.Contains("notes.md paper=a4 landscape", after);
        Assert.Contains("notes.md paper=a4 overwrite", after);
        Assert.DoesNotContain(after, t => t.EndsWith("paper=letter", StringComparison.Ordinal));
    }

    [Fact]
    public void Command_IsRegistered()
    {
        Assert.Equal((SlashCommand.Pdf, "a.md landscape"), SlashCommands.Parse("/pdf a.md landscape"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Pdf));
        Assert.True(ChatScreen.TakesPathArgument("/pdf"));
        Assert.Contains("/pdf", SlashCommands.Words);
        Assert.Contains(SlashCommands.HelpEntries, e => e.Command == "/pdf");
    }

    // ── the setting ─────────────────────────────────────────────────────────

    [Fact]
    public void Setting_LastOnThePrintTab()
    {
        int tab = ToolsText.TabTitles.ToList().IndexOf(ToolsText.PrintTabTitle);
        Assert.Equal(SettingsField.PdfEngine, SettingsMenu.ToolsTabFields[tab - 1][^1]);
        Assert.Equal("auto", SettingsMenu.FieldValue(SettingsField.PdfEngine, new AppSettingsData(), _dir));
        Assert.Equal("printer", SettingsMenu.FieldValue(SettingsField.PdfEngine, new AppSettingsData { PdfEngine = "Printer" }, _dir));
        Assert.Equal("PDF engine", SettingsMenu.FieldName(SettingsField.PdfEngine));
    }

    // ── the browser's command line ──────────────────────────────────────────

    [Fact]
    public void BrowserPdfArguments_Pinned()
    {
        Assert.Equal(
            [
                "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                "--disable-background-networking", "--mute-audio", "--no-pdf-header-footer", @"--print-to-pdf=C:\T\a b.pdf",
                @"--user-data-dir=C:\T\browser", "file:///C:/T/a%20b.html",
            ],
            HeadlessBrowser.PdfArguments(new Uri(@"C:\T\a b.html"), @"C:\T\a b.pdf", @"C:\T\browser", isWeb: false));
        Assert.Contains("--virtual-time-budget=5000", HeadlessBrowser.PdfArguments(new Uri("https://example.com/"), "o.pdf", "d", isWeb: true));
        Assert.DoesNotContain(HeadlessBrowser.PdfArguments(new Uri("https://example.com/"), "o.pdf", "d", isWeb: true), a => a.StartsWith("--blink-settings", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(60), HeadlessBrowser.PdfTimeout);
    }

    [Fact]
    public void SmokeProbe_PdfHtml_Passes()
    {
        var check = SmokeChecks.ProbePdfHtml();
        Assert.True(check.Passed, check.Detail);
    }

    // ── the print-to-file wait ──────────────────────────────────────────────

    [Fact]
    public async Task WaitForPdf_ReadyMissingAndNotPdf()
    {
        Directory.CreateDirectory(_temp);
        string path = Path.Combine(_temp, "w.pdf");
        Assert.Equal(PdfFileState.Missing, await PrintToFile.WaitForPdfAsync(path, TimeSpan.FromMilliseconds(250), TimeProvider.System, CancellationToken.None));
        File.WriteAllText(path, "%PDF-1.7");
        Assert.Equal(PdfFileState.Ready, await PrintToFile.WaitForPdfAsync(path, TimeSpan.FromSeconds(5), TimeProvider.System, CancellationToken.None));
        File.WriteAllText(path, "nope");
        Assert.Equal(PdfFileState.NotPdf, await PrintToFile.WaitForPdfAsync(path, TimeSpan.FromSeconds(5), TimeProvider.System, CancellationToken.None));
    }

    [Fact]
    public async Task WaitForPdf_AFileStillHeldForWriting_IsUnfinished()
    {
        Directory.CreateDirectory(_temp);
        string path = Path.Combine(_temp, "held.pdf");
        await using var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        held.Write("%PDF-1.7"u8);
        held.Flush();
        Assert.Equal(PdfFileState.Unfinished, await PrintToFile.WaitForPdfAsync(path, TimeSpan.FromMilliseconds(300), TimeProvider.System, CancellationToken.None));
    }

    [Fact]
    public void PrintRequest_OutputFile_ReachesTheJob_AndTheShellRefusesIt()
    {
        Write("a.txt", "x");
        WriteBytes("b.pdf", [0x25, 0x50, 0, 0, 1, 0]);
        var (plan, _) = _print.Prepare(new PrintRequest("a.txt", Printer: FakePrintSpooler.Pdf, OutputFile: @"C:\out.pdf"));
        Assert.Equal(@"C:\out.pdf", plan!.Job!.OutputFile);
        Assert.Equal(PrintText.NoFileOutput("b.pdf"), _print.Prepare(new PrintRequest("b.pdf", OutputFile: @"C:\out.pdf")).Error);
    }
}
