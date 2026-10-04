using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class InfoPaneTests : IDisposable
{
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();
    private readonly List<string> _built = new();

    public InfoPaneTests()
    {
        _console.Profile.Width = 40;
        _console.Profile.Height = 12;
    }

    public void Dispose() => _console.Dispose();

    private ScreenPane Pane(bool geometry = true) =>
        new(_console, geometry ? new ScreenGeometry(() => null) : null, _time) { Hint = () => "idle" };

    private KeySource Source() => new(_console.Input, TimeSpan.FromMilliseconds(1));

    private InfoTab Tab(string title, string content) =>
        new(title, () =>
        {
            _built.Add(title);
            return new Markup(content);
        });

    private IReadOnlyList<InfoTab> Tabs() => [Tab("One", "first"), Tab("Two", "second"), Tab("Three", "third")];

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary>A strip row as the pane prints it since 2026-09-18: the text, then the × close glyph in column width − 2.</summary>
    private static string Titled(string row, int width = 40) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    private string Output => _console.Output;

    [Fact]
    public async Task Esc_ClosesThePane_AndTheInputRowComesBack()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);

        Assert.Equal(["One", "Two", "Three"], _built);   // the open measures the other tabs (2026-10-01)
        Assert.Contains(Rule(40) + "\n" + Titled(InfoPane.Title + "   One    Two    Three ") + "\n \nfirst\n" + Rule(40) + "\n" + InfoPane.HintText + "\n", Output);
        Assert.False(pane.OverlayOpen);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    /// <summary>One tab (2026-10-04, the user's report: the list panes read "/screen list   /screen list"): the label alone, the hint without tabs.</summary>
    [Fact]
    public async Task OneTab_IsTheLabelAlone_TheHintWithoutTabs_AndTheTabKeysAndAStripClickChangeNothing()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Right);   // nothing to switch to: no redraw
        _console.Input.PushKey(Keys.Tab);
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync("/screen list", [Tab("/screen list", "Monitors:")], 0, CancellationToken.None);

        Assert.Contains(Rule(40) + "\n" + Titled("/screen list") + "\n \nMonitors:\n" + Rule(40) + "\n" + InfoPane.SingleTabHintText + "\n", Output);
        Assert.DoesNotContain("/screen list   /screen list", Output);
        Assert.Single(_built);   // built once: the keys drew nothing again
        Assert.Equal("ESC closes · ↑/↓ scroll", InfoPane.SingleTabHintText);
    }

    [Fact]
    public async Task CtrlC_ClosesThePane_LikeEsc()
    {
        // Ctrl+C backs out of a pane as ESC does (2026-09-17); the next one at the line is a first.
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.CtrlC);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);

        Assert.False(pane.OverlayOpen);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    [Fact]
    public async Task RightAndTab_GoForward_AndWrap()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Right);
        _console.Input.PushKey(Keys.Tab);
        _console.Input.PushKey(Keys.Right);
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);

        Assert.Equal(["One", "Two", "Three", "Two", "Three", "One"], _built);   // the open measures, then Right, Tab, Right
    }

    [Fact]
    public async Task LeftAndShiftTab_GoBack_AndWrap()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Left);
        _console.Input.PushKey(Keys.ShiftTab);
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, Tabs(), 1, CancellationToken.None);

        Assert.Equal(["Two", "One", "Three", "One", "Three"], _built);   // the open measures, then Left, Shift+Tab
    }

    [Fact]
    public async Task OtherKeys_AreSwallowed_WithoutARedraw()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Char('x'));
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.F4);
        _console.Input.PushKey(Keys.Up);
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);

        Assert.Equal(["One", "Two", "Three"], _built);
        Assert.DoesNotContain("x", Output.Replace("first", "").Replace(InfoPane.HintText, ""));
    }

    [Fact]
    public async Task ASingleTab_IgnoresTheTabKeys()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Right);
        _console.Input.PushKey(Keys.Left);
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [Tab("Only", "alone")], 0, CancellationToken.None);

        Assert.Equal(["Only"], _built);
    }

    [Fact]
    public async Task NoKeyboard_ClosesThePane()
    {
        using var pane = Pane();
        pane.Show();

        // A drained TestConsoleInput throws: the read ran dry, the pane closes, nothing is thrown on.
        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);

        Assert.False(pane.OverlayOpen);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    [Fact]
    public async Task ACancelledToken_ClosesThePane()
    {
        using var pane = Pane();
        pane.Show();
        var input = new ScriptedInput();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await new InfoPane(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))).ShowAsync(InfoPane.Title, Tabs(), 0, cts.Token);

        Assert.Equal(["One", "Two", "Three"], _built);
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task ADisabledPane_ShowsNothing_AndReadsNoKey()
    {
        using var pane = Pane(geometry: false);
        _console.Input.PushKey(Keys.Char('k'));
        var keys = Source();

        await new InfoPane(pane, keys).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);

        Assert.Empty(_built);
        Assert.Equal("", Output);
        Assert.True(keys.IsKeyAvailable());
    }

    [Fact]
    public async Task NoTabs_Throws()
    {
        using var pane = Pane();
        await Assert.ThrowsAsync<ArgumentException>(() => new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [], 0, CancellationToken.None));
    }

    [Fact]
    public void TabStripMarkup_IsPinned()
    {
        string strip = InfoPane.TabStripMarkup(InfoPane.Title, ["Commands", "Keys"], 0);

        Assert.Equal(
            $"[{Theme.Label.ToMarkup()}]❓ Help[/]  [{Theme.MenuHighlight.ToMarkup()}] Commands [/]  [{Theme.DimText.ToMarkup()}] Keys [/]",
            strip);
        Assert.Equal("❓ Help   Commands    Keys ", Markup.Remove(strip));
        // A title with brackets is escaped, not parsed.
        Assert.Equal("Help   a[b] ", Markup.Remove(InfoPane.TabStripMarkup("Help", ["a[b]"], 0)));
    }

    [Fact]
    public void TabStrip_HighlightsEveryLitTitle()
    {
        // A strip of switches (later on 2026-09-29, the embedded model filters): any number lit, the rest dim.
        string strip = InfoPane.TabStripMarkup("M", ["8GB", "16GB", "uncensored"], new HashSet<int> { 0, 2 });
        string lit = Theme.MenuHighlight.ToMarkup();
        string dim = Theme.DimText.ToMarkup();
        Assert.Equal($"[{Theme.Label.ToMarkup()}]M[/]  [{lit}] 8GB [/]  [{dim}] 16GB [/]  [{lit}] uncensored [/]", strip);
        Assert.Equal(strip, InfoPane.TabStripRows("M", ["8GB", "16GB", "uncensored"], new HashSet<int> { 0, 2 }, 200).Single());
        Assert.Equal(InfoPane.TabStripMarkup("M", ["a", "b"], -1), InfoPane.TabStripMarkup("M", ["a", "b"], new HashSet<int>()));

        // A menu page's buttons light as they are On.
        var page = new MenuPage("M", ["x"], "keys") { Buttons = [new MenuButton("8GB", '1', On: true), new MenuButton("16GB", '2')] };
        Assert.Equal($"[{Theme.Label.ToMarkup()}]M[/]  [{lit}] 8GB [/]  [{dim}] 16GB [/]", MenuPane.TopMarkup(page));
    }

    /// <summary>The label's ❓ is two cells, as Windows Terminal draws it (2026-09-28): the strip's indent and its click columns count from it.</summary>
    [Fact]
    public void TheTitlesGlyph_IsTwoCellsWide()
    {
        Assert.Equal(2, TextCells.Width("❓"));
        Assert.Equal(7, TextCells.Width(InfoPane.Title));
    }

    /// <summary>The one tab-key definition, shared with the menu pane.</summary>
    [Fact]
    public void TabStep_IsPinned()
    {
        Assert.Equal(1, InfoPane.TabStep(Keys.Right));
        Assert.Equal(1, InfoPane.TabStep(Keys.Tab));
        Assert.Equal(-1, InfoPane.TabStep(Keys.Left));
        Assert.Equal(-1, InfoPane.TabStep(Keys.ShiftTab));
        Assert.Null(InfoPane.TabStep(Keys.Down));
        Assert.Null(InfoPane.TabStep(Keys.Enter));
        Assert.Null(InfoPane.TabStep(Keys.Char('t')));
    }

    [Fact]
    public void TheStrip_TheSpacer_AndTheContent_EachTakeTheirRows()
    {
        var rows = new Rows(new Markup(InfoPane.TabStripMarkup(InfoPane.Title, ["One"], 0)), new Text(" "), new Markup("a\nb\nc"));
        Assert.Equal(5, ScreenPane.MeasureRows(rows, _console, 40));
    }

    [Fact]
    public void TabStripMarkup_TakesTheLabel()
    {
        Assert.Equal("System prompt   Prompt    Tools ", Markup.Remove(InfoPane.TabStripMarkup("System prompt", ["Prompt", "Tools"], 0)));
    }

    [Theory]
    [InlineData(0, 6, 0, 0, 0)]     // nothing to show
    [InlineData(4, 6, 3, 0, 4)]     // fits: all of it, from the top
    [InlineData(6, 6, 2, 0, 6)]     // exactly fits
    [InlineData(20, 6, 0, 0, 5)]    // cut: one row kept for the more row
    [InlineData(20, 6, 3, 3, 5)]
    [InlineData(20, 6, 99, 15, 5)]  // clamped so the last page is full
    [InlineData(20, 6, -4, 0, 5)]
    [InlineData(20, 1, 2, 2, 1)]    // a one-row window still shows a row
    [InlineData(20, 0, 2, 0, 0)]
    public void Viewport_IsPure(int count, int capacity, int first, int expectedFirst, int expectedShown)
    {
        Assert.Equal((expectedFirst, expectedShown), InfoPane.Viewport(count, capacity, first));
    }

    private static string Numbered(int count) => string.Join("\n", Enumerable.Range(1, count).Select(i => "line" + i));

    [Fact]
    public async Task ContentTallerThanTheWindow_IsCut_WithTheMoreRow()
    {
        // 12 rows: MaxOverlayRows(12, 0) = 8, minus the strip and the spacer = 6, minus the more row = 5 lines.
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [Tab("Long", Numbered(20))], 0, CancellationToken.None);

        Assert.Contains("\nline1\nline2\nline3\nline4\nline5\n" + MenuPane.MoreHint + "\n" + Rule(40) + "\n" + InfoPane.SingleTabHintText + "\n", Output);   // one tab: nothing to switch (2026-10-04)
        Assert.DoesNotContain("line6", Output);
    }

    [Fact]
    public async Task DownPageDownEndHome_MoveTheWindow_AndUpAtTheTopIsSwallowed()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Up);        // at the top already: no redraw
        _console.Input.PushKey(Keys.Down);      // 2..6
        _console.Input.PushKey(Keys.Char('x')); // swallowed; keeps the PageDown from folding into the Down's move
        _console.Input.PushKey(Keys.PageDown);  // 7..11
        _console.Input.PushKey(Keys.End);       // 16..20
        _console.Input.PushKey(Keys.Down);      // at the end: no redraw
        _console.Input.PushKey(Keys.PageUp);    // 11..15
        _console.Input.PushKey(Keys.Home);      // 1..5
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [Tab("Long", Numbered(20))], 0, CancellationToken.None);

        // The content is built once for the visit (2026-10-03): a scroll draws from the lines laid out at the open.
        Assert.Single(_built);
        int a = Output.IndexOf("\nline2\nline3\nline4\nline5\nline6\n", StringComparison.Ordinal);
        int b = Output.IndexOf("\nline7\nline8\nline9\nline10\nline11\n", StringComparison.Ordinal);
        int c = Output.IndexOf("\nline16\nline17\nline18\nline19\nline20\n", StringComparison.Ordinal);
        int d = Output.IndexOf("\nline11\nline12\nline13\nline14\nline15\n", StringComparison.Ordinal);
        int e = Output.LastIndexOf("\nline1\nline2\nline3\nline4\nline5\n", StringComparison.Ordinal);
        Assert.True(0 < a && a < b && b < c && c < d && d < e, Output);
        // The more row stays on every page, the last one included: it points both ways.
        Assert.Contains("\nline20\n" + MenuPane.MoreHint + "\n", Output);
    }

    [Fact]
    public async Task QueuedScrollKeys_FoldIntoOneMove_DrawnOnce()
    {
        // 2026-10-03, the user's ask: a held arrow in /sys queued presses faster than the pane drew them. The queued run is
        // one move and one draw, a page key in it too; a key that is not a scroll key ends the run.
        using var pane = Pane();
        pane.Show();
        for (int i = 0; i < 5; i++)
        {
            _console.Input.PushKey(Keys.Down);
        }

        _console.Input.PushKey(Keys.PageDown);   // 5 + 5: 11..15
        _console.Input.PushKey(Keys.Up);         // 10..14
        _console.Input.PushKey(Keys.Char('x'));
        _console.Input.PushKey(Keys.Up);         // after the run: 9..13, its own draw
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [Tab("Long", Numbered(20))], 0, CancellationToken.None);

        Assert.Single(_built);
        Assert.DoesNotContain("\nline2\nline3\nline4\nline5\nline6\n", Output);
        Assert.DoesNotContain("\nline11\nline12\nline13\nline14\nline15\n", Output);
        int run = Output.IndexOf("\nline10\nline11\nline12\nline13\nline14\n", StringComparison.Ordinal);
        int after = Output.IndexOf("\nline9\nline10\nline11\nline12\nline13\n", StringComparison.Ordinal);
        Assert.True(0 < run && run < after, Output);
    }

    [Fact]
    public async Task EveryTab_KeepsTheTallestTabsHeight_TheBlankUnderTheContent()
    {
        // 2026-10-01, the user's ask: /help and /usage jumped as one tabbed through them.
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Right);
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [Tab("Short", "brief"), Tab("Tall", Numbered(3))], 0, CancellationToken.None);

        string strip = Titled(InfoPane.Title + "   Short    Tall ");
        Assert.Contains("\n" + strip + "\n \nbrief\n \n \n" + Rule(40), Output);
        Assert.Contains("\n" + strip + "\n \nline1\nline2\nline3\n" + Rule(40), Output);
    }

    [Fact]
    public async Task MenusMaxHeight_CapsThePane_AndTheContentScrollsInside()
    {
        // Half of 24 rows less the rules and hint: 9 content rows, the strip and the spacer two of them (2026-10-01).
        _console.Profile.Height = 24;
        using var pane = Pane();
        pane.MenuHeight = () => "half-screen";
        pane.Show();
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [Tab("Long", Numbered(20))], 0, CancellationToken.None);

        Assert.Contains("\n \nline1\nline2\nline3\nline4\nline5\nline6\n" + MenuPane.MoreHint + "\n" + Rule(40), Output);
        Assert.DoesNotContain("line7", Output);
    }

    [Fact]
    public async Task SwitchingTabs_StartsAtTheTop()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.End);
        _console.Input.PushKey(Keys.Right);
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [Tab("Long", Numbered(20)), Tab("Short", "brief")], 0, CancellationToken.None);

        Assert.Equal(["Long", "Short", "Short"], _built);   // End draws from the lines built at the open (2026-10-03)
        Assert.Contains(Titled(InfoPane.Title + "   Long    Short ") + "\n \nbrief\n \n \n \n \n \n" + Rule(40) + "\n" + InfoPane.HintText + "\n", Output);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    [Fact]
    public async Task ABlankLineInTheContent_KeepsItsRow()
    {
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source()).ShowAsync(InfoPane.Title, [Tab("Gap", "a\n\nb")], 0, CancellationToken.None);

        Assert.Contains("\na\n \nb\n" + Rule(40), Output);
    }

    // ── The mouse ───────────────────────────────────────────────────────────

    /// <summary>A pane whose cursor row the geometry reports (the click's frame) over a scripted source that carries clicks.</summary>
    private (ScreenPane Pane, ScriptedInput Input, KeySource Keys) ClickablePane(int cursorTop)
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => cursorTop), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        return (pane, input, new KeySource(input, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void TabAt_IsPinned()
    {
        // "Help   One    Two    Three ": the label and two spaces, then each title with its one space either side, two spaces between.
        string[] titles = ["One", "Two", "Three"];
        Assert.Equal(0, InfoPane.TabAt("Help", titles, 6));
        Assert.Equal(0, InfoPane.TabAt("Help", titles, 10));
        Assert.Equal(1, InfoPane.TabAt("Help", titles, 13));
        Assert.Equal(1, InfoPane.TabAt("Help", titles, 17));
        Assert.Equal(2, InfoPane.TabAt("Help", titles, 20));
        Assert.Equal(2, InfoPane.TabAt("Help", titles, 26));
        Assert.Null(InfoPane.TabAt("Help", titles, 0));     // the label
        Assert.Null(InfoPane.TabAt("Help", titles, 5));     // the gap before the first
        Assert.Null(InfoPane.TabAt("Help", titles, 11));    // the gap between
        Assert.Null(InfoPane.TabAt("Help", titles, 12));
        Assert.Null(InfoPane.TabAt("Help", titles, 27));    // past the end
        Assert.Null(InfoPane.TabAt("Help", titles, -1));
        // A wide title takes its cells, not its characters: "日本" is four cells.
        Assert.Equal(0, InfoPane.TabAt("H", ["日本", "b"], 8));
        Assert.Equal(1, InfoPane.TabAt("H", ["日本", "b"], 11));
        Assert.Null(InfoPane.TabAt("H", ["日本", "b"], 9));
    }

    /// <summary>
    /// A strip wider than the window takes a second row lined up under the first title (2026-09-27): at 30
    /// columns the budget is 26 (the × and its gap kept clear), so "Help   One    Two " fits and Three moves
    /// down to column 6, Four after it.
    /// </summary>
    [Fact]
    public void TabStripLayout_BreaksBetweenTitles_AndLinesTheNextRowUpUnderTheFirstTitle()
    {
        string[] titles = ["One", "Two", "Three", "Four"];
        var (rows, places) = InfoPane.TabStripLayout("Help", titles, 30);
        Assert.Equal(2, rows);
        Assert.Equal([(0, 6), (0, 13), (1, 6), (1, 15)], places);
        Assert.Equal(
            ["Help   One    Two ", "       Three    Four "],
            InfoPane.TabStripRows("Help", titles, 2, 30).Select(Markup.Remove));

        // Wide enough: one row, exactly TabStripMarkup's.
        Assert.Equal(1, InfoPane.TabStripLayout("Help", titles, 80).Rows);
        Assert.Equal([InfoPane.TabStripMarkup("Help", titles, 1)], InfoPane.TabStripRows("Help", titles, 1, 80));

        // The label is counted in cells: a two-cell glyph moves the whole column.
        Assert.Equal((1, 10), InfoPane.TabStripLayout("🛠️ Tools", titles, 30).Places[2]);

        // A title too wide for any row sits alone on its own row, never on an empty one before it.
        var lone = InfoPane.TabStripLayout("H", ["a", "much too long for this", "b"], 20);
        Assert.Equal([(0, 3), (1, 3), (2, 3)], lone.Places);
    }

    [Fact]
    public void TabAt_OnALaterRow_HitsTheTitleThere_AndMissesTheIndent()
    {
        string[] titles = ["One", "Two", "Three", "Four"];
        Assert.Equal(2, InfoPane.TabAt("Help", titles, 30, 6, 1));
        Assert.Equal(2, InfoPane.TabAt("Help", titles, 30, 12, 1));
        Assert.Equal(3, InfoPane.TabAt("Help", titles, 30, 15, 1));
        Assert.Null(InfoPane.TabAt("Help", titles, 30, 3, 1));      // the indent under the label
        Assert.Null(InfoPane.TabAt("Help", titles, 30, 6, 2));      // no third row
        Assert.Equal(0, InfoPane.TabAt("Help", titles, 30, 6, 0));  // One, above Three
        Assert.Null(InfoPane.TabAt("Help", titles, 30, 22, 0));     // where Three was on one row
    }

    /// <summary>At 30 columns the strip is two rows (buffer 100 and 101), the spacer 102, the content from 103; a click on the second row's title switches to it.</summary>
    [Fact]
    public async Task AStripOnTwoRows_DrawsTheSecondUnderTheFirstTitle_AndAClickThereSwitches()
    {
        _console.Profile.Width = 30;
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        input.PushClick(11, 101);                        // "Three" on the second row
        input.Push(Keys.Escape);

        await new InfoPane(pane, keys).ShowAsync(InfoPane.Title, [Tab("One", "first"), Tab("Two", "second"), Tab("Three", "third"), Tab("Four", "fourth")], 0, CancellationToken.None);

        Assert.Equal(["One", "Two", "Three", "Four", "Three"], _built);
        Assert.Contains("\n" + Titled(InfoPane.Title + "   One    Two ", 30) + "\n          Three    Four \n \nthird\n" + Rule(30), Output);
    }

    /// <summary>The overlay's first row is the cursor's (buffer row 100): the strip 100, the spacer 101, the content from 102.</summary>
    [Fact]
    public async Task AClickOnATabTitle_SwitchesToIt_AndStartsAtTheTop()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        input.Push(Keys.Right, Keys.End);                // Two, scrolled to its end
        input.PushClick(25, 100);                        // "Three" on the strip
        input.PushClick(25, 100);                        // again: the active tab, nothing to draw
        input.PushClick(10, 100);                        // "One"
        input.Push(Keys.Escape);

        await new InfoPane(pane, keys).ShowAsync(InfoPane.Title, [Tab("One", "first"), Tab("Two", Numbered(20)), Tab("Three", "third")], 0, CancellationToken.None);

        Assert.Equal(["One", "Two", "Three", "Two", "Three", "One"], _built);   // End draws from Two's lines (2026-10-03)
        Assert.Contains("\n" + Titled(InfoPane.Title + "   One    Two    Three ") + "\n \nthird\n \n \n \n \n \n" + Rule(40), Output);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    /// <summary>The × at the strip's right edge (columns 37–39 at width 40) closes the pane like ESC (2026-09-18); a click left of it is nothing.</summary>
    [Fact]
    public async Task AClickOnTheCloseGlyph_ClosesThePane_LikeEsc()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        input.PushClick(36, 100);                        // the padding: nothing
        input.PushClick(38, 100);                        // the glyph
        input.Push(Keys.Escape);                         // never read

        await new InfoPane(pane, keys).ShowAsync(InfoPane.Title, [Tab("One", "first"), Tab("Two", "second")], 0, CancellationToken.None);

        Assert.Equal(["One", "Two"], _built);
        Assert.False(pane.OverlayOpen);
        Assert.True(input.IsAvailable);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    /// <summary>Two left clicks off the pane — the transcript, then the hint row — within the interval close it like ESC (later on 2026-09-18); one alone, or one around a key, is nothing.</summary>
    [Fact]
    public async Task TwoClicksOffThePane_CloseIt_LikeEsc()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        input.PushClick(5, 50);                          // the transcript: a first
        input.Push(Keys.Right);                          // a key ends the pair (Two)
        input.PushClick(5, 50);                          // a first again
        input.PushClick(20, 104);                        // the hint row (strip, spacer, "second" at 100–102, the rule at 103): another part (later on 2026-09-21), no pair
        input.PushClick(20, 104);                        // the hint row again: the pair
        input.Push(Keys.Escape);                         // never read

        await new InfoPane(pane, keys).ShowAsync(InfoPane.Title, [Tab("One", "first"), Tab("Two", "second")], 0, CancellationToken.None);

        Assert.Equal(["One", "Two", "Two"], _built);
        Assert.False(pane.OverlayOpen);
        Assert.False(pane.Dismissed);
        Assert.True(input.IsAvailable);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    [Fact]
    public async Task AClickOffTheTitles_ARightClick_ADrag_ChangeNothing()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        input.PushClick(1, 100);                         // the label
        input.PushClick(11, 100);                        // the gap between One and Two
        input.PushClick(14, 101);                        // the spacer
        input.PushClick(14, 102);                        // the content
        input.PushClick(14, 99);                         // the upper rule
        input.PushClick(14, 100, MouseButton.Right);     // a right click on "Two"
        input.PushDrag(14, 100);                         // a drag over "Two"
        input.Push(Keys.Escape);

        await new InfoPane(pane, keys).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);

        // One draw (the open), nothing redrawn for any of the seven events.
        Assert.Equal(["One", "Two", "Three"], _built);
    }

    /// <summary>A notch is three lines (<see cref="InfoPane.WheelLines"/>), clamped like the keys; the no-op notches do not redraw.</summary>
    [Fact]
    public async Task TheWheel_ScrollsThreeLinesANotch_ClampedAtBothEnds()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        input.PushWheel(1);         // at the top already: no redraw
        input.PushWheel(-1);        // 4..8
        input.PushWheel(-2, 30, 50); // two notches, anywhere on the screen: 10..14
        input.PushWheel(-5);        // clamped at the end: 16..20
        input.PushWheel(-1);        // at the end: no redraw
        input.PushWheel(1);         // 13..17
        input.PushWheel(9);         // clamped at the top: 1..5
        input.Push(Keys.Escape);

        await new InfoPane(pane, keys).ShowAsync(InfoPane.Title, [Tab("Long", Numbered(20))], 0, CancellationToken.None);

        Assert.Single(_built);   // built at the open; every notch draws from those lines (2026-10-03)
        int a = Output.IndexOf("\nline4\nline5\nline6\nline7\nline8\n", StringComparison.Ordinal);
        int b = Output.IndexOf("\nline10\nline11\nline12\nline13\nline14\n", StringComparison.Ordinal);
        int c = Output.IndexOf("\nline16\nline17\nline18\nline19\nline20\n", StringComparison.Ordinal);
        int d = Output.IndexOf("\nline13\nline14\nline15\nline16\nline17\n", StringComparison.Ordinal);
        int e = Output.LastIndexOf("\nline1\nline2\nline3\nline4\nline5\n", StringComparison.Ordinal);
        Assert.True(0 < a && a < b && b < c && c < d && d < e, Output);
    }

    [Fact]
    public async Task TheMouse_IsTakenForTheVisit_AndHandedBackOnClose()
    {
        var owned = new List<bool>();
        using var pane = Pane();
        pane.Show();
        _console.Input.PushKey(Keys.Escape);

        await new InfoPane(pane, Source(), owned.Add).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);
        Assert.Equal([true, false], owned);

        // The drained keyboard closes the pane too, and hands the mouse back the same way.
        await new InfoPane(pane, Source(), owned.Add).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);
        Assert.Equal([true, false, true, false], owned);

        // Without the pane nothing is shown and nothing taken.
        using var none = Pane(geometry: false);
        await new InfoPane(none, Source(), owned.Add).ShowAsync(InfoPane.Title, Tabs(), 0, CancellationToken.None);
        Assert.Equal([true, false, true, false], owned);
    }
}
