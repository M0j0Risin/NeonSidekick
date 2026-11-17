using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Office;
using NeonSidekick.Pdf;
using NeonSidekick.Render;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The admin-themes-render review's fixes to the drawn pictures (2026-11-17, plan sharded-hugging-puppy §7, §8): charts refused rather than
/// drawn empty, number forms, time axes and ticks, the table text's delimiters, the sanitizer's CDATA and <c>url()</c>, the DevTools cap, the
/// collage's skips and folder, and the one browser check per turn.
/// </summary>
public sealed class RenderReviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "render-review-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly WorkingDirectory _files;
    private readonly PictureRendering _rendering;

    public RenderReviewTests()
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

    private static string? Refusal(ChartSpec spec, ChartData data)
    {
        Assert.Null(ChartSvg.Svg(spec, data, out _, out _, out string? refused));
        return refused;
    }

    // ---- refused, not drawn empty (#24, #48, #67, #83, #218, #220) ----

    [Fact]
    public void ACharts_EmptyOrUnreadableCases_AreRefused_WithWhatToChange()
    {
        Assert.Equal(RenderText.NothingToDraw, Refusal(new ChartSpec(ChartKind.Bar), Table("m,a\nJan,\nFeb,", "m", "a")));
        Assert.Equal(RenderText.PieNothingAbove0, Refusal(new ChartSpec(ChartKind.Pie), Table("m,a\nJan,0\nFeb,0", "m", "a")));
        Assert.Equal(RenderText.NumbersTooLarge, Refusal(new ChartSpec(ChartKind.Bar), Table("m,a\nJan,-1e308\nFeb,1e308", "m", "a")));
        Assert.Equal(RenderText.PlotTooSmall, Refusal(new ChartSpec(ChartKind.Bar, Title: new string('T', 40), Width: 200), Table("m,a\nJan,1\nFeb,2", "m", "a")));

        string many = "m,a\n" + string.Join('\n', Enumerable.Range(1, 900).Select(i => $"r{i},{i}"));
        Assert.Equal(RenderText.BarsTooThin(900, 1), Refusal(new ChartSpec(ChartKind.Bar, Width: 600), Table(many, "m", "a")));
    }

    [Fact]
    public void ARepeatedHeader_IsRefused_OnlyWhenCharted_AndYCantNameAColumnTwice()
    {
        var table = new ChartTable();
        Assert.Null(TableSource.TextTable("m,a,a,b\nJan,1,2,3", "data", table, CancellationToken.None));
        Assert.Null(table.Data("m", ["a"], out string? error));
        Assert.Equal(RenderText.AmbiguousColumn("a", 2), error);
        Assert.NotNull(table.Data("m", ["b"], out error));   // the unused repeat doesn't matter
        Assert.Null(table.Data("m", ["b", "B"], out error));
        Assert.Equal(RenderText.RepeatedY("B"), error);
    }

    [Fact]
    public async Task TheTool_RefusesBarOnlyOptions_AndAYThatIsntStrings_AndNotesAStackOfOne()
    {
        var tool = new RenderChartTool(_files, _rendering);
        Assert.Equal(RenderText.BarOnly("horizontal", "pie"), await tool.InvokeAsync(Args(("kind", "pie"), ("data", "a,b\nx,1"), ("x", "a"), ("y", "b"), ("horizontal", true))));
        Assert.Equal(RenderText.BarOnly("stacked", "line"), await tool.InvokeAsync(Args(("kind", "line"), ("data", "a,b\nx,1"), ("x", "a"), ("y", "b"), ("stacked", true))));
        Assert.Equal(RenderText.YNotStrings, await tool.InvokeAsync(Args(("kind", "bar"), ("data", "a,b\nx,1"), ("x", "a"), ("y", 5))));
        Assert.Equal(RenderText.NoY, await tool.InvokeAsync(Args(("kind", "bar"), ("data", "a,b\nx,1"), ("x", "a"), ("y", Array.Empty<string>()))));
        Assert.Equal(RenderText.OverwriteNeedsTo, await tool.InvokeAsync(Args(("kind", "bar"), ("data", "a,b\nx,1"), ("x", "a"), ("y", "b"), ("overwrite", true))));
        Assert.Empty(_browser.RenderJobs);

        string answer = TextOf(await tool.InvokeAsync(Args(("kind", "bar"), ("data", "a,b\nx,1\ny,2"), ("x", "a"), ("y", "b"), ("stacked", true))));
        Assert.EndsWith(RenderText.StackedOneSeries, answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChartFrom_AFileHeldOpenWithoutSharing_AnswersWithASentence()
    {
        Put("held.csv", "m,a\nJan,1\n"u8.ToArray());
        using var held = new FileStream(Path.Combine(_root, "held.csv"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var tool = new RenderChartTool(_files, _rendering);
        object? answer = await tool.InvokeAsync(Args(("kind", "bar"), ("from", "held.csv"), ("x", "m"), ("y", "a")));
        Assert.StartsWith("Error:", Assert.IsType<string>(answer), StringComparison.Ordinal);
        Assert.Empty(_browser.RenderJobs);
    }

    // ---- layout ----

    [Fact]
    public void APie_DropsZeroSlices_WithACount_AndMergesIntoARealOther()
    {
        var zeros = Table("s,v\na,5\nb,0\nc,\nd,3", "s", "v");
        string? svg = ChartSvg.Svg(new ChartSpec(ChartKind.Pie), zeros, out _, out int dropped, out _);
        Assert.NotNull(svg);
        Assert.Equal(2, dropped);

        string rows = string.Join('\n', Enumerable.Range(1, 14).Select(i => $"s{i},{i}")) + "\nOther,50";
        svg = ChartSvg.Svg(new ChartSpec(ChartKind.Pie), Table("s,v\n" + rows, "s", "v"), out int folded, out _, out _);
        Assert.True(folded > 0);
        Assert.Single(Regex.Matches(svg!, ">" + RenderText.Other + "  "));   // one Other row, not two
    }

    [Fact]
    public void HorizontalBars_SwapTheAxisTitles()
    {
        var data = Table("m,a\nJan,1\nFeb,2", "m", "a");
        string svg = ChartSvg.Svg(new ChartSpec(ChartKind.Bar, XLabel: "Month", YLabel: "Sales", Horizontal: true), data, out _, out _, out _)!;
        // The value axis runs along the bottom when the bars run left to right: its title is drawn level there, the labels' title turned.
        var month = Regex.Match(svg, "<text[^>]*>Month</text>").Value;
        var sales = Regex.Match(svg, "<text[^>]*>Sales</text>").Value;
        Assert.Contains("rotate", month, StringComparison.Ordinal);
        Assert.DoesNotContain("rotate", sales, StringComparison.Ordinal);
    }

    [Fact]
    public void ALonePoint_OnALongLine_GetsADot()
    {
        string rows = string.Join('\n', Enumerable.Range(1, 80).Select(i => i == 40 ? "r40,7" : $"r{i},"));
        rows += "\nr81,1\nr82,2";
        string svg = ChartSvg.Svg(new ChartSpec(ChartKind.Line), Table("m,a\n" + rows, "m", "a"), out _, out _, out _)!;
        Assert.Single(Regex.Matches(svg, "<circle "));
    }

    [Fact]
    public void Ticks_KeepTinySteps_Apart_AndPercentsRoundFirst()
    {
        var labels = Enumerable.Range(1, 9).Select(i => ChartSvg.Tick(1 + (i * 1e-7), 1e-7)).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("1.0000003", ChartSvg.Tick(1.0000003, 1e-7));
        Assert.Equal("2.5", ChartSvg.Tick(2.5, 2.5));
        Assert.Equal("20", ChartSvg.Tick(20, 20));

        Assert.Equal(">99.9%", ChartSvg.Percent(0.9997, alone: false));
        Assert.Equal("<0.1%", ChartSvg.Percent(0.0003, alone: false));
        Assert.Equal("100%", ChartSvg.Percent(1, alone: true));
        Assert.Equal("12%", ChartSvg.Percent(0.12, alone: false));
        Assert.Equal("5.5%", ChartSvg.Percent(0.055, alone: false));
    }

    [Fact]
    public void ALineOfDates_IsPlacedByTime_WithTicksForTheSpan()
    {
        var data = Table("when,a\n2026-01-01,1\n2026-01-02,2\n2026-03-01,3", "when", "a");
        Assert.NotNull(data.Times);
        string svg = ChartSvg.Svg(new ChartSpec(ChartKind.Line), data, out _, out _, out _)!;
        Assert.Contains(">2026-01</text>", svg, StringComparison.Ordinal);   // month ticks over a two-month span
        Assert.Contains(">2026-03</text>", svg, StringComparison.Ordinal);
        Assert.DoesNotContain(">2026-01-02</text>", svg, StringComparison.Ordinal);

        var hours = ChartSvg.TimeTicks(new DateTime(2026, 1, 1, 9, 10, 0), new DateTime(2026, 1, 1, 17, 0, 0));
        Assert.InRange(hours.Count, 2, ChartSvg.MaxTimeTicks);
        Assert.True(hours[0].At >= new DateTime(2026, 1, 1, 9, 10, 0));
        Assert.Contains("2026-01-01", hours[0].Label, StringComparison.Ordinal);   // the date written on the first tick
        Assert.Matches(@"^\d\d:\d\d$", hours[1].Label);

        var years = ChartSvg.TimeTicks(new DateTime(2000, 6, 1), new DateTime(2030, 1, 1));
        Assert.All(years, t => Assert.Matches(@"^\d{4}$", t.Label));
        Assert.True(years.Count <= ChartSvg.MaxTimeTicks);

        // Bars stay categories.
        Assert.Contains(">2026-01-02</text>", ChartSvg.Svg(new ChartSpec(ChartKind.Bar), Table("when,a\n2026-01-01,1\n2026-01-02,2", "when", "a"), out _, out _, out _)!, StringComparison.Ordinal);
    }

    // ---- text ----

    [Fact]
    public void Labels_KeepTimesDurationsAndSeconds()
    {
        var times = Table("t,a\n12:00:00,1\n13:30:00,2", "t", "a");
        Assert.Equal("12:00:00", times.Labels[0]);
        var seconds = Table("t,a\n2026-01-01 10:00:01,1\n2026-01-01 10:00:02,2", "t", "a");
        Assert.NotEqual(seconds.Labels[0], seconds.Labels[1]);
        var minutes = Table("t,a\n2026-01-01 10:00,1\n2026-01-01 10:01,2", "t", "a");
        Assert.Equal("2026-01-01 10:00", minutes.Labels[0]);
    }

    [Theory]
    [InlineData("1,234", 1234d)]
    [InlineData("$5", 5d)]
    [InlineData("12%", 12d)]
    [InlineData("-1,234,567.5", -1234567.5)]
    [InlineData("€3.25", 3.25)]
    public void Numbers_TakeGroupsCurrencyAndPercent(string cell, double expected)
    {
        Assert.Equal(expected, ChartTable.NumberOf(cell));
    }

    [Theory]
    [InlineData("1,2")]
    [InlineData("12,34,5")]
    [InlineData("abc")]
    public void Numbers_RefuseBadGroups(string cell)
    {
        Assert.Null(ChartTable.NumberOf(cell));
    }

    [Fact]
    public void TheTableText_KeepsALeadingTab_ReadsJsonOnlyWhenItParses_AndHonorsSep()
    {
        var corner = Table("\tA\tB\nx\t1\t2", "", "A", "B");
        Assert.Equal(["x"], corner.Labels);

        var notJson = Table("[a],b\nx,1", "[a]", "b");   // a bracket that isn't JSON reads as CSV
        Assert.Equal(["x"], notJson.Labels);

        var table = new ChartTable();
        Assert.Equal(OfficeText.BadJson("data", OfficeText.JsonNotArray), TableSource.TextTable("{\"a\":1}", "data", table, CancellationToken.None));

        var sep = Table("sep=;\nm;a\nJan;3", "m", "a");
        Assert.Equal(["Jan"], sep.Labels);
        Assert.Equal(';', TableSource.Probe("sep=;", tsvByExtension: false, out bool skip));
        Assert.True(skip);
        Assert.Equal('\t', TableSource.Probe("a,b", tsvByExtension: true, out skip));
        Assert.False(skip);
        Assert.Equal(',', TableSource.Probe("sep=,", tsvByExtension: true, out skip));   // the line wins over the extension

        var quoted = Table("m,a\n\"x\ty\",1", "m", "a");   // a quoted tab in a comma table
        Assert.Equal(["x\ty"], quoted.Labels);
    }

    [Fact]
    public async Task AFromFile_StartingSep_ChartsAndConvertsWithIt()
    {
        Put("semi.csv", "sep=;\nm;a\nJan;3\n"u8.ToArray());
        var tool = new RenderChartTool(_files, _rendering);
        Assert.StartsWith("Drew chart.png", TextOf(await tool.InvokeAsync(Args(("kind", "bar"), ("from", "semi.csv"), ("x", "m"), ("y", "a")))), StringComparison.Ordinal);
        Assert.Contains(">Jan</text>", _browser.RenderJobs.Single().Html, StringComparison.Ordinal);
    }

    // ---- the sanitizer and the tokenizer (#28, #113, #160, #161, #192) ----

    [Fact]
    public void ThePage_InlinesSvgImagesAndCssUrls_FromTheSandbox_AndEmptiesWebOnes()
    {
        byte[] png = FakeHeadlessBrowser.RenderPng;
        Func<string, byte[]?> resolve = a => a == "photo.png" ? png : null;
        string data = "data:image/png;base64," + Convert.ToBase64String(png);

        string svg = PicturePage.ForModelSvg("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"photo.png\" width=\"4\" height=\"3\"/><image xlink:href=\"https://example.com/x.png\"/></svg>", resolve);
        Assert.Contains(data, svg, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", svg, StringComparison.Ordinal);

        string html = PicturePage.ForModelHtml("<div style=\"background:url(photo.png)\">a</div><style>p { background: url('https://example.com/y.png') }</style>", resolve);
        Assert.Contains("url(&quot;" + data, html, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", html, StringComparison.Ordinal);
        Assert.Contains("url()", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Cdata_KeepsAStylesRules_AndAnSvgsText_ButNotPlainHtmls()
    {
        string svg = PicturePage.ForModelSvg("<svg xmlns=\"http://www.w3.org/2000/svg\"><style><![CDATA[ text { fill: red } ]]></style><text><![CDATA[a < b]]></text></svg>", _ => null);
        Assert.Contains("text { fill: red }", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("CDATA", svg, StringComparison.Ordinal);
        Assert.Contains("a &lt; b", svg, StringComparison.Ordinal);

        string html = PicturePage.ForModelHtml("<p>x</p><![CDATA[hidden]]>", _ => null);
        Assert.DoesNotContain("hidden", html, StringComparison.Ordinal);

        Assert.DoesNotContain("x]]", HtmlToMarkdown.Convert("<p>before</p><![CDATA[x]]><p>after</p>").Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ADoctypesInternalSubset_IsSkippedWhole()
    {
        var page = HtmlToMarkdown.Convert("<!DOCTYPE svg [<!ENTITY x \"a>b\">]><p>Hello</p>");
        Assert.Equal("Hello", page.Markdown.Trim());
    }

    [Fact]
    public void AFence_IsTakenOff_OnlyWhenItCloses()
    {
        Assert.Equal("graph TD\nA-->B", RenderPictureTool.Unfence("```mermaid\ngraph TD\nA-->B\n```"));
        Assert.Equal("<svg/>", RenderPictureTool.Unfence("  ~~~svg\n<svg/>\n~~~  "));
        Assert.Equal("```mermaid\ngraph TD", RenderPictureTool.Unfence("```mermaid\ngraph TD"));
        Assert.Equal("graph TD", RenderPictureTool.Unfence("graph TD"));
    }

    // ---- DevTools (#124) ----

    [Fact]
    public async Task AnOversizedEvent_IsSkipped_AndTheReplyStillComes()
    {
        var socket = new ScriptedSocket();
        socket.Incoming.Enqueue(Encoding.UTF8.GetBytes("{\"method\":\"Page.big\",\"params\":{\"x\":\"" + new string('a', 300) + "\"}}"));
        socket.Incoming.Enqueue(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{\"ok\":true}}"));
        await using var client = new DevToolsClient(socket, maxMessageBytes: 100);
        var result = await client.SendAsync("Page.enable", null, null, CancellationToken.None);
        Assert.True(result.GetProperty("ok").GetBoolean());

        var big = new ScriptedSocket();
        big.Incoming.Enqueue(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{\"data\":\"" + new string('a', 300) + "\"}}"));
        await using var tooLarge = new DevToolsClient(big, maxMessageBytes: 100);
        var thrown = await Assert.ThrowsAsync<DevToolsException>(() => tooLarge.SendAsync("Page.captureScreenshot", null, null, CancellationToken.None));
        Assert.True(thrown.TooLarge);
    }

    // A socket that hands out its queued messages in 64-byte pieces and takes whatever is sent.
    private sealed class ScriptedSocket : WebSocket
    {
        private byte[]? _current;
        private int _at;

        public Queue<byte[]> Incoming { get; } = new();

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            _current ??= Incoming.Dequeue();
            int count = Math.Min(Math.Min(64, buffer.Count), _current.Length - _at);
            Array.Copy(_current, _at, buffer.Array!, buffer.Offset, count);
            _at += count;
            bool end = _at == _current.Length;
            if (end)
            {
                _current = null;
                _at = 0;
            }

            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Text, end));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ---- the collage (#13, #77, #102, #108, #125, #133, #137, #181) ----

    [Fact]
    public async Task ACollage_NamesEachSkipUnderItsReason()
    {
        Put("a.png", FakeHeadlessBrowser.RenderPng);
        Put("b.png", FakeHeadlessBrowser.RenderPng);
        Put("notes.txt", "x"u8.ToArray());
        Put("bad.png", [1, 2, 3]);
        var tool = new RenderCollageTool(_files, _rendering);
        string answer = TextOf(await tool.InvokeAsync(Args(("paths", new[] { "a.png", "notes.txt", "b.png", "bad.png" }))));
        Assert.StartsWith("Drew collage.jpg", answer, StringComparison.Ordinal);
        Assert.Contains(RenderText.SkipNotPicture + ": notes.txt", answer, StringComparison.Ordinal);
        Assert.Contains("bad.png", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderCollage_LeavesOutEarlierCollages_AtTheTopOnly()
    {
        Put("collage.jpg", FakeHeadlessBrowser.RenderPng);
        Put("collage-2.jpg", FakeHeadlessBrowser.RenderPng);
        Put("shot.png", FakeHeadlessBrowser.RenderPng);
        var tool = new RenderCollageTool(_files, _rendering);
        string answer = TextOf(await tool.InvokeAsync(Args(("folder", "."))));
        Assert.Contains(RenderText.SkipEarlierCollage + ": ", answer, StringComparison.Ordinal);
        Assert.Contains("collage.jpg", answer, StringComparison.Ordinal);
        Assert.Contains("collage-2.jpg", answer, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(_browser.RenderJobs.Single().Html!, "<img "));

        Assert.True(RenderCollageTool.IsEarlierCollage("collage-12.png"));
        Assert.True(RenderCollageTool.IsEarlierCollage("Collage.JPEG"));
        Assert.False(RenderCollageTool.IsEarlierCollage("collage-x.jpg"));
        Assert.False(RenderCollageTool.IsEarlierCollage("my-collage.jpg"));
    }

    [Fact]
    public async Task ABigFolder_GivesItsFirst48_InNaturalOrder()
    {
        for (int i = 1; i <= 60; i++)
        {
            Put($"pics/shot-{i}.png", FakeHeadlessBrowser.RenderPng);
        }

        for (int i = 0; i < 240; i++)
        {
            Put($"pics/note-{i}.txt", "x"u8.ToArray());
        }

        var tool = new RenderCollageTool(_files, _rendering);
        string answer = TextOf(await tool.InvokeAsync(Args(("folder", "pics"), ("captions", new[] { "names" }))));
        Assert.Contains(RenderText.FirstPictures(48, 60), answer, StringComparison.Ordinal);
        string html = _browser.RenderJobs.Single().Html!;
        Assert.Equal(48, Regex.Matches(html, "<img ").Count);
        Assert.True(html.IndexOf(">shot-2<", StringComparison.Ordinal) < html.IndexOf(">shot-10<", StringComparison.Ordinal));
        Assert.DoesNotContain(">shot-49<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACollage_RefusesAPatternWithoutAFolder_AndColumnsPastTheCount()
    {
        Put("a.png", FakeHeadlessBrowser.RenderPng);
        var tool = new RenderCollageTool(_files, _rendering);
        Assert.Equal(RenderText.PatternNeedsFolder, await tool.InvokeAsync(Args(("paths", new[] { "a.png" }), ("pattern", "*.png"))));
        Assert.Equal(ArgumentText.OutOfRange("columns", "13", 1, RenderCollageTool.MaxColumns), await tool.InvokeAsync(Args(("paths", new[] { "a.png" }), ("columns", 13))));
        Assert.Empty(_browser.RenderJobs);

        await tool.InvokeAsync(Args(("paths", new[] { "a.png" }), ("columns", 4)));
        Assert.Contains("repeat(1, minmax(0, 1fr))", _browser.RenderJobs.Single().Html, StringComparison.Ordinal);   // capped to the count (#181)
    }

    // ---- the picture (#9, #42, #165) ----

    [Fact]
    public async Task APictureFromTheImagesFolder_IsInlined_AndAMissingOneIsNamed()
    {
        string imagesRoot = Path.Combine(_dir, "images");
        Directory.CreateDirectory(imagesRoot);
        string photo = Path.Combine(imagesRoot, "cam.png");
        File.WriteAllBytes(photo, FakeHeadlessBrowser.RenderPng);
        var images = new WorkingDirectory(() => imagesRoot, TimeProvider.System);
        var tool = new RenderPictureTool(_files, _rendering, images);

        string answer = TextOf(await tool.InvokeAsync(Args(("html", $"<img src=\"{new Uri(photo).AbsoluteUri}\"><img src=\"gone.png\">"))));
        Assert.Contains("data:image/png;base64,", _browser.RenderJobs.Single().Html, StringComparison.Ordinal);
        Assert.EndsWith(RenderText.PicturesLeftOut(["gone.png"]), answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATargetThatsTaken_IsRefusedBeforeTheDraw()
    {
        Put("keep.png", [9]);
        var tool = new RenderPictureTool(_files, _rendering);
        Assert.StartsWith("Error:", TextOf(await tool.InvokeAsync(Args(("mermaid", "graph TD"), ("to", "keep.png")))), StringComparison.Ordinal);
        Assert.Empty(_browser.RenderJobs);
    }

    // ---- the words (#20) ----

    [Fact]
    public void EveryRefusal_StartsWithError_AndTheAnswersTailsDont()
    {
        string[] refusals =
        [
            RenderText.OneSource, RenderText.OneDataSource, RenderText.NoX, RenderText.NoY, RenderText.OnePictureSource, RenderText.NoPictures,
            RenderText.PatternNeedsFolder, RenderText.OverwriteNeedsTo, RenderText.NoBrowser(""), RenderText.NoBrowser("C:\\x.exe"),
            RenderText.BadNumber("scale", "9", "1–3"), RenderText.BadChoice("kind", "radar", ["bar"]), RenderText.BadList("y", "5"),
            RenderText.BadBackground("bluish"), RenderText.BadExtension("a.gif", ".png"), RenderText.MermaidError("x"), RenderText.Failed("x"),
            RenderText.TooLarge, RenderText.NoColumn("a", ["b"]), RenderText.AmbiguousColumn("a", 2), RenderText.RepeatedY("a"),
            RenderText.NotANumber("a", 1, "x"), RenderText.TooManySeries(12), RenderText.PieOneSeries, RenderText.PieNegative("a"), RenderText.NoRows,
            RenderText.BarOnly("stacked", "pie"), RenderText.YNotStrings, RenderText.NothingToDraw, RenderText.PieNothingAbove0,
            RenderText.NumbersTooLarge, RenderText.PlotTooSmall, RenderText.BarsTooThin(9, 1), RenderText.TooManyPictures(48), RenderText.CaptionsCount(1, 2),
        ];
        Assert.All(refusals, r => Assert.StartsWith("Error: ", r, StringComparison.Ordinal));

        string[] tails =
        [
            RenderText.Written("a.png", 1, 1, 1, cut: true, wide: true), RenderText.PointsCut(5), RenderText.SlicesFolded(2), RenderText.SlicesDropped(1),
            RenderText.Skipped([("a", RenderText.SkipNotPicture)]), RenderText.StackedOneSeries, RenderText.FirstPictures(48, 60), RenderText.FolderListCut(5000),
        ];
        Assert.All(tails, t => Assert.DoesNotContain("Error:", t, StringComparison.Ordinal));

        string skipped = RenderText.Skipped([.. Enumerable.Range(0, 12).Select(i => ("f" + i.ToString(CultureInfo.InvariantCulture), RenderText.SkipNotPicture))]);
        Assert.EndsWith("f9 and 2 more.", skipped, StringComparison.Ordinal);
        Assert.Contains("wider than", RenderText.Written("a.png", 1, 1, 1, cut: false, wide: true), StringComparison.Ordinal);
    }

    // ---- the offering (#10, #84, #127, #132) ----

    [Fact]
    public void WithoutABrowser_TheTurnLeavesTheRenderToolsOut_AndTheSummaryDimsThem()
    {
        var tools = ChatScreen.ImageTools(_files, () => new Settings.AppSettingsData(), rendering: _rendering);
        var on = ChatScreen.ComposeTurnTools(new TurnToolInputs { Standing = [], Images = tools, ImagesEnabled = true, Drawable = true });
        var off = ChatScreen.ComposeTurnTools(new TurnToolInputs { Standing = [], Images = tools, ImagesEnabled = true, Drawable = false });
        Assert.Contains(RenderChartTool.ToolName, on.Tools.Select(t => t.Name));
        Assert.DoesNotContain(off.Tools, t => t is RenderTool);
        Assert.Contains(off.Tools, t => t.Name == ImageToolNames.All[0]);

        var image = Assert.Single(SystemPromptSummary.ToolGroups([], [], [], [], false, images: tools, imagesEnabled: true, drawable: false), g => g.Label == ToolsText.ImageMenuTitle);
        Assert.True(image.Offered);
        Assert.Contains(SystemPromptSummary.NoBrowserSuffix, image.ToolNotes[RenderPictureTool.ToolName], StringComparison.Ordinal);
        Assert.False(image.Offers(RenderChartTool.ToolName));
        Assert.True(image.Offers(ImageToolNames.All[0]));
        var drawn = Assert.Single(SystemPromptSummary.ToolGroups([], [], [], [], false, images: tools, imagesEnabled: true), g => g.Label == ToolsText.ImageMenuTitle);
        Assert.True(drawn.Offers(RenderCollageTool.ToolName));
    }

    [Fact]
    public void TheBrowserIsLookedFor_OnlyWithImageToolsOn()
    {
        Assert.False(ChatScreen.Drawable(imagesOn: false, _rendering));
        Assert.Empty(_browser.LocateCalls);
        Assert.True(ChatScreen.Drawable(imagesOn: true, _rendering));
        Assert.Single(_browser.LocateCalls);
        _browser.Executable = null;
        Assert.False(ChatScreen.Drawable(imagesOn: true, _rendering));
    }
}
