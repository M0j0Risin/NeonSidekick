using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Office;
using NeonSidekick.Pdf;
using NeonSidekick.Plans;
using NeonSidekick.Render;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The drawn pictures (2026-11-16, plan sharded-hugging-puppy #9): the chart's SVG and its table (<see cref="ChartSvg"/>, <see cref="ChartTable"/>),
/// the picture and collage pages (<see cref="PicturePage"/>, <see cref="CollageHtml"/>), the browser's pure pieces
/// (<see cref="BrowserPictureRenderer"/>), and <c>render_picture</c>, <c>render_chart</c> and <c>render_collage</c> over the fake browser. The
/// real browser's draws are <see cref="PictureRenderTests"/>'.
/// </summary>
public sealed class RenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "render-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly WorkingDirectory _files;
    private readonly PictureRendering _rendering;

    public RenderTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, TimeProvider.System);
        _rendering = new PictureRendering(_browser, () => "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in pairs)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private static string TextOf(object? result) => result switch
    {
        ToolImageResult image => image.Text,
        string text => text,
        _ => throw new InvalidOperationException("no answer"),
    };

    private void Put(string relative, byte[] bytes)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static ChartData Table(string text, string x, params string[] y)
    {
        var table = new ChartTable();
        Assert.Null(TableSource.TextTable(text, "data", table, CancellationToken.None));
        var data = table.Data(x, y, out string? error);
        Assert.Null(error);
        return data!;
    }

    // A chart that draws: its SVG, the refusal held null.
    private static string Draw(ChartSpec spec, ChartData data, out int folded)
    {
        string? svg = ChartSvg.Svg(spec, data, out folded, out _, out string? refused);
        Assert.Null(refused);
        return svg!;
    }

    // ---- the chart ----

    [Fact]
    public void NiceScale_RoundSteps_WholeRange_AndAFlatRangeOpens()
    {
        Assert.Equal((0d, 100d, 20d), ChartSvg.NiceScale(0, 97));
        Assert.Equal((-10d, 30d, 10d), ChartSvg.NiceScale(-8, 26));
        var (lo, hi, step) = ChartSvg.NiceScale(5, 5);
        Assert.True(lo < 5 && hi > 5 && step > 0);
        Assert.Equal((0d, 1d, 0.2), ChartSvg.NiceScale(double.NaN, 3));
    }

    [Fact]
    public void Tick_TakesTheStepsDecimals_AndNoFloatingDust()
    {
        Assert.Equal("0.3", ChartSvg.Tick(0.1 + 0.2, 0.1));
        Assert.Equal("20", ChartSvg.Tick(20, 20));
        Assert.Equal("0", ChartSvg.Tick(1e-17, 1));
        Assert.Equal("0.0", ChartSvg.Tick(-1e-17, 0.5));
        Assert.Equal("2.5", ChartSvg.Tick(2.5, 2.5));
    }

    [Fact]
    public void EscapeAndCut_KeepTheDataText()
    {
        Assert.Equal("&lt;b&gt;&amp;&quot;x&#39;", ChartSvg.Escape("<b>&\"x'\u0001"));   // XML can't hold U+0001 (#204)
        Assert.Equal("abcd…", ChartSvg.Cut("abcdefghij", 5));
        Assert.Equal("a b", ChartSvg.Cut("a\nb", 10));
    }

    [Fact]
    public void ABarChart_EscapesItsCells_AndWritesInvariantNumbers_UnderAnyCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var data = Table("name,score\n<script>x</script>,1.5\nBob,2.25", "name", "score");
            string svg = Draw(new ChartSpec(ChartKind.Bar, Title: "Scores & <more>"), data, out int folded);
            Assert.Equal(0, folded);
            Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"720\"", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", svg, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;x&lt;/script&gt;", svg, StringComparison.Ordinal);
            Assert.Contains("Scores &amp; &lt;more&gt;", svg, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\d,\d"), svg);
            Assert.Equal(2, Regex.Matches(svg, "<rect x=").Count);   // two bars (the ground is width="100%")
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void TwoSeries_HaveALegend_OneHasNone_AndALineLeavesAGapForABlank()
    {
        var two = Table("month,a,b\nJan,1,2\nFeb,3,4", "month", "a", "b");
        Assert.Contains(">a</text>", Draw(new ChartSpec(ChartKind.Bar), two, out _), StringComparison.Ordinal);
        var one = Table("month,a\nJan,1\nFeb,3", "month", "a");
        Assert.DoesNotContain(">a</text>", Draw(new ChartSpec(ChartKind.Bar), one, out _), StringComparison.Ordinal);

        var gap = Table("month,a\nJan,1\nFeb,\nMar,3\nApr,4", "month", "a");
        string line = Draw(new ChartSpec(ChartKind.Line), gap, out _);
        string path = Regex.Match(line, "<path d=\"([^\"]+)\"").Groups[1].Value;
        Assert.Equal(2, path.Count(c => c == 'M'));   // the pen lifts over Feb
        Assert.Equal(3, Regex.Matches(line, "<circle ").Count);
    }

    [Fact]
    public void APie_FoldsItsSmallestSlicesPastTwelve_IntoOther()
    {
        string rows = string.Join('\n', Enumerable.Range(1, 15).Select(i => $"s{i},{i}"));
        var data = Table("slice,value\n" + rows, "slice", "value");
        string svg = Draw(new ChartSpec(ChartKind.Pie), data, out int folded);
        Assert.Equal(4, folded);
        Assert.Contains(">" + RenderText.Other + "  ", svg, StringComparison.Ordinal);
        Assert.DoesNotContain(">s1  ", svg, StringComparison.Ordinal);   // the smallest went to Other
        Assert.Contains(">s15  ", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTable_NamesAColumnAnyCase_RefusesAWord_AndStopsAtItsCap()
    {
        var table = new ChartTable();
        Assert.Null(TableSource.TextTable("month,sales\nJan,10\nFeb,\nMar,x", "data", table, CancellationToken.None));
        Assert.Null(table.Data("MONTH", ["sales"], out string? error));
        Assert.Equal(RenderText.NotANumber("sales", 3, "x"), error);
        Assert.Null(table.Data("when", ["sales"], out error));
        Assert.Equal(RenderText.NoColumn("when", ["month", "sales"]), error);

        var json = new ChartTable();
        Assert.Null(TableSource.TextTable("[{\"k\":\"a\",\"v\":1},{\"k\":\"b\",\"v\":2.5}]", "data", json, CancellationToken.None));
        var data = json.Data("k", ["v"], out error);
        Assert.Null(error);
        Assert.Equal(["a", "b"], data!.Labels);
        Assert.Equal([1d, 2.5], data.Series[0].Values);

        var capped = new ChartTable(max: 2);
        TableSource.TextTable("a,b\n1,1\n2,2\n3,3", "data", capped, CancellationToken.None);
        Assert.True(capped.Cut);
        Assert.Equal(2, capped.Rows.Count);
    }

    [Fact]
    public void ThePalettes_ReadOnTheirGrounds()
    {
        var light = Spectre.Console.Color.White;
        Assert.True(UI.ThemeFile.TryParseColor(PictureColors.Dark.Ground, out var dark));
        foreach (string hex in ChartSvg.LightPalette)
        {
            Assert.True(UI.ThemeFile.TryParseColor(hex, out var color));
            Assert.True(ThemeTests.Contrast(color, light) >= 3, hex);
        }

        foreach (string hex in ChartSvg.DarkPalette)
        {
            Assert.True(UI.ThemeFile.TryParseColor(hex, out var color));
            Assert.True(ThemeTests.Contrast(color, dark) >= 3, hex);
        }

        Assert.Equal(ChartSvg.MaxSeries, ChartSvg.LightPalette.Count);
        Assert.Equal(ChartSvg.MaxSeries, ChartSvg.DarkPalette.Count);
    }

    // ---- the pages ----

    [Fact]
    public void TheModelsSvg_LosesItsScript_ItsHandlers_AndKeepsItsDrawing_UnderThePolicy()
    {
        string page = PicturePage.ForModelSvg("<?xml version=\"1.0\"?><svg onload=\"x()\"><script>alert(1)</script><rect width=\"4\" height=\"3\"/></svg>", _ => null);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<?xml", page, StringComparison.Ordinal);
        Assert.Contains("<rect", page, StringComparison.Ordinal);
        Assert.Contains(PdfHtml.PolicyMeta, page, StringComparison.Ordinal);
        Assert.Contains("id=\"" + PicturePage.FrameId + "\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("@page", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModelsHtml_InlinesASandboxPicture_AndDropsAWebOne()
    {
        byte[] png = FakeHeadlessBrowser.RenderPng;
        string page = PicturePage.ForModelHtml("<p>hi</p><img src=\"photo.png\"><img src=\"https://example.com/x.png\">", a => a == "photo.png" ? png : null);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(png), page, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", page, StringComparison.Ordinal);
        Assert.StartsWith("<meta charset=\"utf-8\">\n" + PdfHtml.PolicyMeta, page, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrustedSvg_IsFramedAsWritten()
    {
        string page = PicturePage.ForTrustedSvg("<svg><g/></svg>");
        Assert.Contains("<div id=\"neon-picture\"><svg><g/></svg></div>", page, StringComparison.Ordinal);
        Assert.Contains(PdfHtml.PolicyMeta, page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCollage_EscapesItsWords_AndPicksAboutSquareColumns()
    {
        string page = CollageHtml.Page([new CollageItem("image/png", [1, 2, 3], "<b>.jpg"), new CollageItem("image/jpeg", [4], null)], 2, "Trip & <friends>", dark: false);
        Assert.Contains("<figcaption>&lt;b&gt;.jpg</figcaption>", page, StringComparison.Ordinal);
        Assert.Contains("<h1>Trip &amp; &lt;friends&gt;</h1>", page, StringComparison.Ordinal);
        Assert.Contains("repeat(2, minmax(0, 1fr))", page, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(page, "<img ").Count);
        Assert.Contains("src=\"data:image/png;base64,AQID\"", page, StringComparison.Ordinal);
        Assert.Contains(PdfHtml.PolicyMeta, page, StringComparison.Ordinal);
        Assert.Equal(1, CollageHtml.ColumnsFor(1));
        Assert.Equal(3, CollageHtml.ColumnsFor(5));
        Assert.Equal(6, CollageHtml.ColumnsFor(48));
    }

    // ---- the browser's pure pieces ----

    [Fact]
    public void Mermaid_IsEmbedded_StrictAlways_ItsTextNeverCode_AndItsThemeFollowsTheGround()
    {
        Assert.Contains("mermaid", BrowserPictureRenderer.LibraryText(), StringComparison.Ordinal);
        string expression = BrowserPictureRenderer.MermaidExpression("graph TD\nA-->B\n'); alert(1); ('", "default");
        Assert.Contains("securityLevel: 'strict'", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("'); alert(1)", expression, StringComparison.Ordinal);
        Assert.Equal("default", BrowserPictureRenderer.MermaidTheme(System.Drawing.Color.White));
        Assert.Equal("default", BrowserPictureRenderer.MermaidTheme(null));
        Assert.Equal("dark", BrowserPictureRenderer.MermaidTheme(System.Drawing.Color.FromArgb(0x10, 0x10, 0x18)));
        Assert.Equal("default", BrowserPictureRenderer.MermaidTheme(System.Drawing.Color.FromArgb(0, 0x10, 0x10, 0x18)));   // alpha 0 is transparent

        using var drawn = JsonDocument.Parse("{\"svg\":\"<svg/>\"}");
        Assert.Equal(("<svg/>", null), BrowserPictureRenderer.MermaidAnswer(drawn.RootElement));
        using var refused = JsonDocument.Parse("{\"error\":\"Parse error on line 2:\\n...\\nExpecting 'NODE'\"}");
        Assert.Equal((null, "Parse error on line 2: ... Expecting 'NODE'"), BrowserPictureRenderer.MermaidAnswer(refused.RootElement));
    }

    [Fact]
    public void TheClip_HoldsTheWidthAndTheSides_AndTheGroundIsRgba()
    {
        Assert.Equal((1200, 2048), BrowserPictureRenderer.Clip(5000, 9000, 1200, 2, out bool cut, out bool wide));
        Assert.True(cut);
        Assert.True(wide);
        Assert.Equal((300, 200), BrowserPictureRenderer.Clip(300, 200, 1200, 2, out cut, out wide));
        Assert.False(cut);
        Assert.False(wide);
        Assert.Equal((0, 0, 0, 0d), BrowserPictureRenderer.Ground(null));
        Assert.Equal((16, 32, 48, 1d), BrowserPictureRenderer.Ground(System.Drawing.Color.FromArgb(0x10, 0x20, 0x30)));
        Assert.Equal((1, 2, 3, 51 / 255d), BrowserPictureRenderer.Ground(System.Drawing.Color.FromArgb(51, 1, 2, 3)));   // its alpha sent (#55)
        Assert.Equal(2048, BrowserPictureRenderer.LayoutWidth(4000, 2));   // laid out where the clip can keep it (#105)
        Assert.Equal(1200, BrowserPictureRenderer.LayoutWidth(1200, 2));
        using var size = JsonDocument.Parse("{\"width\":10.2,\"height\":0}");
        Assert.Equal((11, 1), BrowserPictureRenderer.Size(size.RootElement));
    }

    // ---- render_picture ----

    [Fact]
    public async Task RenderPicture_DrawsMermaid_SavesNumbered_AndHandsThePictureBack()
    {
        var tool = new RenderPictureTool(_files, _rendering);
        var first = await tool.InvokeAsync(Args(("mermaid", "graph TD\nA-->B")));
        var second = await tool.InvokeAsync(Args(("mermaid", "graph TD\nA-->C")));

        var picture = Assert.IsType<ToolImageResult>(first);
        Assert.StartsWith("Drew diagram.png: 4×3 pixels, ", picture.Text, StringComparison.Ordinal);
        Assert.Single(picture.Images);
        Assert.StartsWith("Drew diagram-2.png", TextOf(second), StringComparison.Ordinal);
        Assert.Equal(FakeHeadlessBrowser.RenderPng, File.ReadAllBytes(Path.Combine(_root, "diagram.png")));
        var job = _browser.RenderJobs[0];
        Assert.Equal(("graph TD\nA-->B", (string?)null, PictureFormat.Png, 1200, 2d, System.Drawing.Color.White.ToArgb(), true), (job.Mermaid, job.Html, job.Format, job.Width, job.Scale, job.Background?.ToArgb(), job.Svg));
        Assert.Equal(512 * 1024, tool.MaxArgumentChars);
    }

    [Fact]
    public async Task RenderPicture_TheModelsSvg_IsRebuilt_AndAPicturePageInlinesFromTheSandbox()
    {
        Put("photo.png", FakeHeadlessBrowser.RenderPng);
        var tool = new RenderPictureTool(_files, _rendering);
        await tool.InvokeAsync(Args(("svg", "<svg><script>alert(1)</script><circle r=\"3\"/></svg>"), ("background", "transparent")));
        await tool.InvokeAsync(Args(("html", "<h1>Hi</h1><img src=\"photo.png\">"), ("width", 600), ("scale", 1)));

        Assert.DoesNotContain("<script", _browser.RenderJobs[0].Html, StringComparison.Ordinal);
        Assert.Null(_browser.RenderJobs[0].Background);
        Assert.Contains("data:image/png;base64,", _browser.RenderJobs[1].Html, StringComparison.Ordinal);
        Assert.Equal((600, 1d), (_browser.RenderJobs[1].Width, _browser.RenderJobs[1].Scale));
        Assert.True(File.Exists(Path.Combine(_root, "picture.png")));
        Assert.True(File.Exists(Path.Combine(_root, "picture-2.png")));
    }

    [Fact]
    public async Task RenderPicture_RefusesBadArguments_BeforeAnyDraw()
    {
        var tool = new RenderPictureTool(_files, _rendering);
        Assert.Equal(RenderText.OneSource, await tool.InvokeAsync(Args()));
        Assert.Equal(RenderText.OneSource, await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("svg", "<svg/>"))));
        Assert.Equal(ArgumentText.OutOfRange("width", "100", 200, 4096), await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("width", 100))));
        Assert.Equal(RenderText.BadNumber("scale", "4", "1–3"), await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("scale", 4))));
        Assert.Equal(RenderText.BadBackground("bluish"), await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("background", "bluish"))));
        Assert.Equal(RenderText.BadExtension("d.jpg", ".png"), await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("to", "d.jpg"))));
        Assert.Empty(_browser.RenderJobs);
    }

    [Fact]
    public async Task RenderPicture_WritesOverAFile_OnlyWithOverwrite_AndNeverIntoTheGit()
    {
        Put("keep.png", [9, 9]);
        var tool = new RenderPictureTool(_files, _rendering);
        string refused = TextOf(await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("to", "keep.png"))));
        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(_root, "keep.png")));
        Assert.DoesNotContain("Drew", refused, StringComparison.Ordinal);

        Assert.StartsWith("Drew keep.png", TextOf(await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("to", "keep.png"), ("overwrite", true)))), StringComparison.Ordinal);
        Assert.Equal(FakeHeadlessBrowser.RenderPng, File.ReadAllBytes(Path.Combine(_root, "keep.png")));

        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Assert.DoesNotContain("Drew", TextOf(await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("to", ".git/x.png")))), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, ".git", "x.png")));
    }

    [Fact]
    public async Task RenderPicture_SaysWhatTheBrowserSaid_AndIsOfferedOnlyWithABrowser()
    {
        var tool = new RenderPictureTool(_files, _rendering);
        _browser.RenderOutcome = new PictureRender(false, "Parse error on line 2", null, 0, 0) { MermaidError = "Parse error on line 2" };
        Assert.Equal(RenderText.MermaidError("Parse error on line 2"), await tool.InvokeAsync(Args(("mermaid", "graph TD\nA-->"))));
        _browser.RenderOutcome = new PictureRender(false, "too large", null, 0, 0) { TooLarge = true };
        Assert.Equal(RenderText.TooLarge, await tool.InvokeAsync(Args(("mermaid", "graph TD"))));
        _browser.RenderOutcome = new PictureRender(false, "stopped after 60 s", null, 0, 0);
        Assert.Equal(RenderText.Failed("stopped after 60 s"), await tool.InvokeAsync(Args(("mermaid", "graph TD"))));
        Assert.Empty(Directory.GetFiles(_root));

        _browser.Executable = null;
        Assert.Equal(RenderText.NoBrowser(""), await tool.InvokeAsync(Args(("mermaid", "graph TD"))));
    }

    // ---- render_chart ----

    [Fact]
    public async Task RenderChart_FromTextOrAFile_DrawsTheAppsSvg_OnItsGround()
    {
        Put("sales.csv", "month,sales\nJan,10\nFeb,12\n"u8.ToArray());
        var tool = new RenderChartTool(_files, _rendering);
        var drawn = await tool.InvokeAsync(Args(("kind", "bar"), ("data", "month,sales\nJan,10\nFeb,12"), ("x", "month"), ("y", new[] { "sales" }), ("title", "Sales")));
        var dark = await tool.InvokeAsync(Args(("kind", "line"), ("from", "sales.csv"), ("x", "month"), ("y", "sales"), ("style", "dark")));

        Assert.StartsWith("Drew chart.png", TextOf(drawn), StringComparison.Ordinal);
        Assert.StartsWith("Drew chart-2.png", TextOf(dark), StringComparison.Ordinal);
        Assert.Contains("<svg xmlns", _browser.RenderJobs[0].Html, StringComparison.Ordinal);
        Assert.Contains(">Sales</text>", _browser.RenderJobs[0].Html, StringComparison.Ordinal);
        Assert.Equal((1200 + (2 * PicturePage.Pad), PictureColors.Light.GroundColor.ToArgb()), (_browser.RenderJobs[0].Width, _browser.RenderJobs[0].Background?.ToArgb()));
        Assert.Equal(PictureColors.Dark.GroundColor.ToArgb(), _browser.RenderJobs[1].Background?.ToArgb());
        Assert.True(_browser.RenderJobs[0].Svg);
    }

    [Fact]
    public async Task RenderChart_RefusesWhatItCantDraw()
    {
        Put("notes.txt", "x"u8.ToArray());
        var tool = new RenderChartTool(_files, _rendering);
        Assert.Equal(RenderText.BadChoice("kind", "radar", ["bar", "line", "pie"]), await tool.InvokeAsync(Args(("kind", "radar"), ("data", "a,b\n1,2"), ("x", "a"), ("y", "b"))));
        Assert.Equal(RenderText.OneDataSource, await tool.InvokeAsync(Args(("kind", "bar"), ("x", "a"), ("y", "b"))));
        Assert.Equal(RenderText.OneDataSource, await tool.InvokeAsync(Args(("kind", "bar"), ("data", "a,b\n1,2"), ("from", "x.csv"), ("x", "a"), ("y", "b"))));
        Assert.Equal(RenderText.PieOneSeries, await tool.InvokeAsync(Args(("kind", "pie"), ("data", "a,b,c\nx,1,2"), ("x", "a"), ("y", new[] { "b", "c" }))));
        Assert.Equal(RenderText.PieNegative("y"), await tool.InvokeAsync(Args(("kind", "pie"), ("data", "a,b\nx,1\ny,-2"), ("x", "a"), ("y", "b"))));
        Assert.Equal(RenderText.NoX, await tool.InvokeAsync(Args(("kind", "bar"), ("data", "a,b\n1,2"), ("y", "b"))));
        Assert.Equal(OfficeText.NotATableFile("notes.txt"), await tool.InvokeAsync(Args(("kind", "bar"), ("from", "notes.txt"), ("x", "a"), ("y", "b"))));
        Assert.Equal(RenderText.TooManySeries(12), await tool.InvokeAsync(Args(("kind", "bar"), ("data", "a,b\n1,2"), ("x", "a"), ("y", Enumerable.Range(0, 13).Select(i => "c" + i).ToArray()))));
        Assert.Empty(_browser.RenderJobs);
    }

    // ---- render_collage ----

    [Fact]
    public async Task RenderCollage_LaysOutThePictures_SkipsTheRest_AsAJpeg()
    {
        Put("a.png", FakeHeadlessBrowser.RenderPng);
        Put("b.png", FakeHeadlessBrowser.RenderPng);
        Put("c.txt", "not a picture"u8.ToArray());
        var tool = new RenderCollageTool(_files, _rendering);
        string answer = TextOf(await tool.InvokeAsync(Args(("paths", new[] { "a.png", "b.png", "c.txt" }), ("captions", new[] { "names" }), ("title", "Two"))));

        Assert.StartsWith("Drew collage.jpg", answer, StringComparison.Ordinal);
        Assert.EndsWith(RenderText.Skipped([("c.txt", RenderText.SkipNotPicture)]), answer, StringComparison.Ordinal);
        var job = _browser.RenderJobs.Single();
        Assert.Equal((PictureFormat.Jpeg, 2400), (job.Format, job.Width));
        Assert.Equal(2, Regex.Matches(job.Html!, "<img ").Count);
        Assert.Contains("<figcaption>a</figcaption>", job.Html, StringComparison.Ordinal);
        Assert.Contains("<h1>Two</h1>", job.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderCollage_TakesAFoldersPictures_ByPattern_AndAPngWhenAsked()
    {
        Put("pics/x1.png", FakeHeadlessBrowser.RenderPng);
        Put("pics/x2.png", FakeHeadlessBrowser.RenderPng);
        Put("pics/y.png", FakeHeadlessBrowser.RenderPng);
        var tool = new RenderCollageTool(_files, _rendering);
        Assert.StartsWith("Drew all.png", TextOf(await tool.InvokeAsync(Args(("folder", "pics"), ("pattern", "x*"), ("to", "all.png")))), StringComparison.Ordinal);

        var job = _browser.RenderJobs.Single();
        Assert.Equal(PictureFormat.Png, job.Format);
        Assert.Equal(2, Regex.Matches(job.Html!, "<img ").Count);
    }

    [Fact]
    public async Task RenderCollage_RefusesBeforeReading()
    {
        var tool = new RenderCollageTool(_files, _rendering);
        Assert.Equal(RenderText.NoPictures, await tool.InvokeAsync(Args()));
        Assert.Equal(RenderText.OnePictureSource, await tool.InvokeAsync(Args(("paths", new[] { "a.png" }), ("folder", "pics"))));
        Assert.Equal(RenderText.TooManyPictures(48), await tool.InvokeAsync(Args(("paths", Enumerable.Range(0, 49).Select(i => $"p{i}.png").ToArray()))));
        Assert.Equal(RenderText.CaptionsCount(1, 2), await tool.InvokeAsync(Args(("paths", new[] { "a.png", "b.png" }), ("captions", new[] { "one" }))));
        Assert.Empty(_browser.RenderJobs);

        Put("a.png", FakeHeadlessBrowser.RenderPng);
        Assert.Equal(RenderText.BadExtension("c.gif", ".jpg or .jpeg or .png"), await tool.InvokeAsync(Args(("paths", new[] { "a.png" }), ("to", "c.gif"))));
        Assert.Empty(_browser.RenderJobs);
    }

    // ---- registration ----

    [Fact]
    public void TheTools_WritePlanModeOff_AndUndoTakesThemBack_AndBotsSeeThemOnlyWithVision()
    {
        foreach (string name in ImageToolNames.Rendered)
        {
            Assert.Contains(name, PlanTools.Mutating);
            Assert.Contains(name, CheckpointTools.Tracked);
            Assert.Contains(name, App.BotChat.UnseenToolPictures(vision: false));
        }

        var tools = App.ChatScreen.ImageTools(_files, () => new Settings.AppSettingsData(), rendering: _rendering);
        Assert.Equal([.. ImageToolNames.All, .. ImageToolNames.Rendered], tools.Select(t => t.Name));
    }
}
