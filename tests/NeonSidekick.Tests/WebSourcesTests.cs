using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Web;
using NeonSidekick.WebUi;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The sources under a reply (2026-11-15, the web-tools plan's §2, feature idea #27): what the tools hand on, what a turn's trace keeps, the
/// 📚 line, the Web UI's entry, the store's <c>turn_sources</c>, and the export's list.
/// </summary>
public sealed class WebSourcesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static WebSource Page(string url, string title = "") => new(WebSourceKind.Page, url, title);

    private static readonly WebSource Search = new(WebSourceKind.Search, "", "");

    private static TurnEvent.ToolResult Result(string name, string text, WebSource? source = null, string id = "c1") =>
        new(name, id, text, Source: source);

    // ── The trace ────────────────────────────────────────────────────────────

    [Fact]
    public void TheTrace_KeepsEachPageOnce_InFirstUseOrder_ItsFragmentAside_AndCountsTheSearchesThatAnswered()
    {
        var trace = new TurnTrace();
        trace.Observe(Result(WebSearchTool.ToolName, "Searched \"x\" (3 results, SearXNG):"));
        trace.Observe(Result(WebSearchTool.ToolName, "Error: the search failed"));
        trace.Observe(Result(WebFetchTool.ToolName, "page b", Page("https://b.example/#one", "B")));
        trace.Observe(Result(WebFetchTool.ToolName, "page a", Page("https://a.example/", "A")));
        trace.Observe(Result(WebFetchTool.ToolName, "page b again", Page("https://b.example/#two", "B")));
        trace.Observe(Result(DownloadFileTool.ToolName, "downloaded b", new WebSource(WebSourceKind.Download, "https://b.example/", "b.html")));
        trace.Observe(Result(WebFetchTool.ToolName, "Error: offset past the end", Page("https://c.example/", "C")));

        Assert.Equal([WebSourceKind.Search, WebSourceKind.Page, WebSourceKind.Page, WebSourceKind.Download], trace.Sources.Select(s => s.Kind));
        Assert.Equal(["", "https://b.example/#one", "https://a.example/", "https://b.example/"], trace.Sources.Select(s => s.Url));
    }

    [Fact]
    public void ASourcesUrl_LosesItsCredentials_AndItsTitle_IsOneShortLine()
    {
        var kept = TurnTrace.Kept(Page("https://user:secret@example.com/a?q=1", "Line one\nline\u202Etwo " + new string('x', 400)))!;

        Assert.Equal("https://example.com/a?q=1", kept.Url);
        Assert.DoesNotContain('\n', kept.Title);
        Assert.Contains("\\u202E", kept.Title, StringComparison.Ordinal);
        Assert.True(kept.Title.Length <= TurnTrace.MaxSourceTitleChars);
        Assert.Null(TurnTrace.Kept(Page("  ")));
    }

    // ── The tools and the loop ───────────────────────────────────────────────

    private sealed class SourcedTool : AIFunction
    {
        private static readonly System.Text.Json.JsonElement Schema = NeonSidekick.Llm.Tools.ToolSchema.Parse("""{"type":"object"}""");

        public override string Name => "sourced";

        public override string Description => "Answers with a source.";

        public override System.Text.Json.JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            new(new ToolSourcedResult("the page's text", Page("https://example.com/", "Example")));
    }

    [Fact]
    public async Task TheLoop_HandsTheModelTheTextAlone_AndTheSourceOnTheEvent()
    {
        var answer = await Assistant.InvokeToolWithDiffAsync([new SourcedTool()], new FunctionCallContent("c1", "sourced", new Dictionary<string, object?>()), CancellationToken.None);

        Assert.Equal("the page's text", answer.Text);
        Assert.Equal("https://example.com/", answer.Source!.Url);
        Assert.Equal("the page's text", (await Assistant.InvokeToolAsync([new SourcedTool()], new FunctionCallContent("c2", "sourced", new Dictionary<string, object?>()), CancellationToken.None)).Text);
    }

    [Fact]
    public async Task WebFetch_SourcesItsFinalUrlAndTitle_AndARefusal_NothingAtAll()
    {
        var http = new StubHttpMessageHandler();
        http.Map("https://example.com/", HttpStatusCode.OK, "<title>Example Domain</title><p>hi</p>", "text/html");
        var web = new WebAccess(new HttpClient(http), new FakeHeadlessBrowser(), _time, (host, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }), _ => { });
        var fetch = new WebFetchTool(web, () => new Settings.AppSettingsData());

        var read = Assert.IsType<ToolSourcedResult>(await fetch.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["url"] = "https://example.com/" })));
        Assert.Equal(new WebSource(WebSourceKind.Page, "https://example.com/", "Example Domain"), read.Source);
        Assert.IsType<ToolSourcedResult>(await fetch.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["url"] = "https://example.com/", ["outline"] = true })));
        Assert.IsType<string>(await fetch.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["url"] = "https://example.com/", ["offset"] = 900 })));
        Assert.IsType<string>(await fetch.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["url"] = "ftp://x" })));
    }

    // ── The line ─────────────────────────────────────────────────────────────

    private static string Plain(IRenderable renderable)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        console.Write(renderable);
        return console.Output.TrimEnd();
    }

    private static List<Segment> Segments(IRenderable renderable)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        return [.. renderable.Render(new RenderOptions(console.Profile.Capabilities, new Size(200, 10)), 200)];
    }

    [Fact]
    public void TheLine_NamesEachTitleAndItsHost_LinksTheTitles_AndCountsTheRest()
    {
        var sources = Enumerable.Range(1, 7).Select(i => Page("https://s" + i + ".example/p", "Title " + i)).Append(Search).Append(Search).ToList();

        var line = TranscriptRenderer.SourcesLine(sources);

        Assert.Equal("📚 Title 1 (s1.example) · Title 2 (s2.example) · Title 3 (s3.example) · Title 4 (s4.example) · Title 5 (s5.example) · +2 more · 2 searches", Plain(line));
        var segments = Segments(line);
        Assert.Equal("Title 1", string.Concat(segments.Where(s => s.Link?.Url == "https://s1.example/p").Select(s => s.Text)));
        Assert.DoesNotContain(segments, s => s.Link is not null && s.Text.Contains("s1.example", StringComparison.Ordinal));
    }

    [Fact]
    public void ATitleWithBrackets_RendersAsWritten_ANoTitlePage_IsItsHost_AndADownload_IsMarked()
    {
        var line = TranscriptRenderer.SourcesLine([Page("https://a.example/", "[red]x[/] ]"), Page("https://b.example/x"), new WebSource(WebSourceKind.Download, "https://c.example/f.zip", "f.zip")]);

        Assert.Equal("📚 [red]x[/] ] (a.example) · b.example · 📥 f.zip (c.example)", Plain(line));
    }

    [Fact]
    public void AnUnsafeUrl_IsShownWithoutALink()
    {
        var segments = Segments(TranscriptRenderer.SourcesLine([Page("https://a.example/\u202Eevil", "Evil"), Page("javascript:alert(1)", "Script")]));

        Assert.DoesNotContain(segments, s => s.Link is not null);
    }

    [Fact]
    public void EndAssistant_DrawsTheLine_AboveTheFooter_OnThePlainConsole_AndNothingForSearchesAlone()
    {
        var console = new TestConsole();
        var t = new TranscriptRenderer(console);
        t.BeginAssistant(markdown: false, glyph: null);
        t.AppendDelta("reply");
        t.EndAssistant("footer", [Page("https://a.example/", "A")]);
        t.BeginAssistant(markdown: false, glyph: null);
        t.AppendDelta("second");
        t.EndAssistant(null, [Search]);

        string output = console.Output;
        Assert.Contains("reply\n📚 A (a.example)\nfooter\n", output, StringComparison.Ordinal);
        Assert.Single(output.Split('\n'), l => l.StartsWith("📚", StringComparison.Ordinal));
    }

    [Fact]
    public void EndAssistant_InThePanesSlot_DrawsTheLineToo_AndTheFeedGetsItsOwnEntry()
    {
        using var console = new TestConsole().Interactive();
        console.Profile.Width = 100;
        console.Profile.Height = 20;
        using var pane = new ScreenPane(console, new ScreenGeometry(() => null, () => null), _time);
        var feed = new TranscriptFeed();
        var changes = new List<FeedChange>();
        feed.Changed += changes.Add;
        var t = new TranscriptRenderer(pane) { Feed = feed };
        pane.Show();
        t.BeginAssistant(markdown: true, glyph: null);
        t.AppendDelta("reply");
        t.EndAssistant(null, [Page("https://en.wikipedia.org/wiki/Mercury_(planet)", "Mercury"), Search]);

        string rows = string.Join("\n", pane.TranscriptRows().Select(row => string.Concat(row.Select(s => s.Text))));
        Assert.Contains("📚 Mercury (en.wikipedia.org) · 1 search", rows, StringComparison.Ordinal);
        var added = changes.OfType<FeedChange.Appended>().Select(a => a.Entry).Single(e => e.Kind == "sources");
        Assert.Equal("📚 [Mercury](<https://en.wikipedia.org/wiki/Mercury_(planet)>) (en.wikipedia.org) · 1 search", added.Text);
        var ended = changes.OfType<FeedChange.Ended>().Single(e => e.Kind == FeedKind.Sources);
        Assert.Equal(added.Seq, ended.Seq);
        string html = feed.HtmlOf(added.Seq, (Func<string, string, string>)WebUiMarkdown.ToHtml)!;
        Assert.Contains("href=\"https://en.wikipedia.org/wiki/Mercury_(planet)\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFeedsMarkdown_NeverLinksAnUnsafeUrl()
    {
        string markdown = SessionExport.SourcesLineMarkdown([Page("javascript:alert(1)", "Script"), Page("https://ok.example/", "a](javascript:x)")]);

        string html = WebUiMarkdown.ToHtml(markdown, "f1-");
        Assert.DoesNotContain("javascript:", html.Replace("a](javascript:x)", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"https://ok.example/\"", html, StringComparison.Ordinal);
    }

    // ── The store ────────────────────────────────────────────────────────────

    [Fact]
    public void TheStore_RoundTripsATurnsSources_AndARewindOrAPurge_TakesThemAway()
    {
        using var store = new SessionStore(_dir, _time);
        long id = store.Begin("t", "m")!.Value;
        store.AppendTurn(id, "one", "r1", 1, ["web_fetch"], [], 0, 1, 1, false, [Page("https://a.example/", "A"), Search]);
        store.AppendTurn(id, "two", "r2", 0, [], [], 0, 1, 1, false);
        store.AppendTurn(id, "three", "r3", 1, ["download_file"], [], 0, 1, 1, false, [new WebSource(WebSourceKind.Download, "https://b.example/f", "f")]);

        var turns = store.Load(id)!.Turns;
        Assert.Equal([Page("https://a.example/", "A"), Search], turns[0].SourceList);
        Assert.Empty(turns[1].SourceList);
        Assert.Equal(WebSourceKind.Download, Assert.Single(turns[2].SourceList).Kind);

        store.TruncateTurns(id, 1);
        Assert.Equal(2, Rows("SELECT count(*) FROM turn_sources"));   // turn 1's two stay, turn 3's went
        Assert.True(store.Purge(id));
        Assert.Equal(0, Rows("SELECT count(*) FROM turn_sources"));
    }

    [Fact]
    public void AFileFromBefore_GainsTheTable_OnOpen()
    {
        using (var store = new SessionStore(_dir, _time))
        {
            store.Begin("t", "m");
        }

        Exec("DROP TABLE turn_sources");
        using (var store = new SessionStore(_dir, _time))
        {
            long id = store.Begin("u", "m")!.Value;
            store.AppendTurn(id, "one", "r1", 1, ["web_fetch"], [], 0, 1, 1, false, [Page("https://a.example/", "A")]);
            Assert.Single(store.Load(id)!.Turns[0].SourceList);
        }
    }

    private string DbPath => Path.Combine(_dir, SessionStore.FolderName, SessionStore.FileName);

    private int Rows(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + DbPath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Exec(string sql)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection("Data Source=" + DbPath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // ── The export ───────────────────────────────────────────────────────────

    private static SessionRecord Record(params WebSource[] sources) =>
        new(new SessionSummary(12, ManualTimeProvider.DefaultUtcNow, ManualTimeProvider.DefaultUtcNow, "t", TitleSource.FirstLine, "m", 1),
            [new SessionTurn(1, ManualTimeProvider.DefaultUtcNow, "look it up", "Found it.", 2, ["web_search", "web_fetch"], [], 0, 1, 1, false, sources)], "");

    [Fact]
    public void TheMarkdownExport_ListsTheSources_UnderTheToolLine_WithTheSearchesLast()
    {
        var doc = SessionExport.Build(Record(Search, Page("https://example.com/a b", "Notes [draft] | #1"), Page("https://en.wikipedia.org/wiki/Mercury_(planet)"), Search),
            SessionExportFormat.Markdown, ManualTimeProvider.DefaultZone, Theme.Current, "p");

        Assert.Contains(SessionText.ExportSourcesHeading + "\n\n"
            + "- [Notes \\[draft\\] \\| \\#1](<https://example.com/a%20b>) (example.com)\n"
            + "- [en.wikipedia.org](<https://en.wikipedia.org/wiki/Mercury_(planet)>)\n"
            + "- and 2 searches\n", doc.Text, StringComparison.Ordinal);
        Assert.True(doc.Text.IndexOf("web_fetch", StringComparison.Ordinal) < doc.Text.IndexOf(SessionText.ExportSourcesHeading, StringComparison.Ordinal));
    }

    [Fact]
    public void TheHtmlExport_RendersTheSameList_ItsLinksVetted()
    {
        var doc = SessionExport.Build(Record(Page("https://example.com/", "Ex ] ample"), Page("https://a.example/\u202Eevil", "Evil")),
            SessionExportFormat.Html, ManualTimeProvider.DefaultZone, Theme.Current, "p");

        Assert.Contains("<div class=\"sources\">", doc.Text, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com/\"", doc.Text, StringComparison.Ordinal);
        Assert.Contains("Ex ] ample", doc.Text, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", doc.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil\"", doc.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATurnWithSearchesAlone_ExportsNoList()
    {
        var doc = SessionExport.Build(Record(Search), SessionExportFormat.Markdown, ManualTimeProvider.DefaultZone, Theme.Current, "p");

        Assert.DoesNotContain(SessionText.ExportSourcesHeading, doc.Text, StringComparison.Ordinal);
    }
}
