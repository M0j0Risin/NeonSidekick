using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Menu clicks landing where they're aimed (2026-11-07): rows measured as drawn, a click that never scrolls the list, a click aimed at an
/// older layout dropped, and the slab on the row under the pointer. The overlay's first row is buffer row 100.
/// </summary>
public class MenuClicksTests : IDisposable
{
    private const string Long = "alpha bravo charlie delta echo foxtrot golf hotel india juliet";

    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();

    public MenuClicksTests()
    {
        _console.Profile.Width = 40;
        _console.Profile.Height = 30;
    }

    public void Dispose() => _console.Dispose();

    private string Output => _console.Output;

    private (ScreenPane Pane, ScriptedInput Input, KeySource Keys) Rig(Func<int?>? windowTop = null)
    {
        // The window's top at buffer row 0, so a pointer's row is its screen row (OverlayTopRow's frame).
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100, windowTop ?? (() => 0)), _time) { Hint = () => "idle" };
        pane.Show();
        var input = new ScriptedInput();
        return (pane, input, new KeySource(input, TimeSpan.FromMilliseconds(1)));
    }

    private static MenuPage Page(params string[] rows) => new("Settings", rows, "Enter = pick · Esc = back");

    private static MenuPage Sided(params string[] rows) =>
        Page(rows) with { Side = (row, width, count) => Enumerable.Range(0, count).Select(i => (IRenderable)new Text($"side {row}:{i}")).ToList() };

    // ── Rows that take more room than the click math counted ─────────────

    [Fact]
    public async Task AStatusWiderThanTheWindow_LeavesAClickOnTheRowAimedAt_AndStaysWhole()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        menu.Warning(Long);                 // two rows: the title 100, the status 101 and 102, the rows from 103
        input.PushClick(3, 104);            // "two"
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        Assert.Contains("india juliet", Output);
        menu.Close();
    }

    [Fact]
    public async Task AStatusHoldingLineBreaks_LeavesAClickOnTheRowAimedAt()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        menu.Notice("first\nsecond\nthird");   // the status 101 to 103, the rows from 104
        input.PushClick(3, 105);               // "two"
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        Assert.Contains("third", Output);
        menu.Close();
    }

    [Fact]
    public async Task ALongPlainTitle_LeavesAClickOnTheRowAimedAt()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 104);            // the title 100 and 101, the spacer 102, the rows from 103: "two"
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(new MenuPage(Long, ["one", "two", "three"], "hint"), 0, CancellationToken.None));
        Assert.Contains("india juliet", Output);
        menu.Close();
    }

    [Fact]
    public async Task AStatusWiderThanTheWindow_LeavesAClickOnTheCellAimedAt_InAGrid()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        menu.Warning(Long);                                      // the cells' first line at 103
        input.PushClick(TextCells.Width(MenuPane.NoPointer) + 9, 103);   // the second cell
        input.Push(Keys.Enter);
        var page = Page("a", "b", "c") with { Grid = new MenuGrid(8) };

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(page, 0, CancellationToken.None));
        menu.Close();
    }

    /// <summary>What counts is what's on the screen, not what the status list holds now.</summary>
    [Fact]
    public async Task AStatusAddedSinceTheLastDraw_MovesNoClick()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.OnWait = () =>
        {
            input.OnWait = null;
            menu.Warning(Long);
            input.PushClick(3, 103);        // drawn with the spacer: the rows from 102, so "two"
            input.Push(Keys.Enter);
        };

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        menu.Close();
    }

    [Fact]
    public async Task AStatusClearedSinceTheLastDraw_MovesNoClick()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        menu.Warning(Long);
        input.OnWait = () =>
        {
            input.OnWait = null;
            menu.ClearStatus();
            input.PushClick(3, 104);        // drawn with the status: the rows from 103, so "two"
            input.Push(Keys.Enter);
        };

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        menu.Close();
    }

    /// <summary>A page with a side column draws only a status's first row, so it counts one however long it is.</summary>
    [Fact]
    public async Task AStatusOnASideColumnPage_CountsOne()
    {
        _console.Profile.Width = 100;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        menu.Warning(Long + " " + Long + " " + Long);   // the title 100, the status 101, the rows from 102
        input.PushClick(3, 103);                         // "two"
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Sided("one", "two", "three"), 0, CancellationToken.None));
        menu.Close();
    }

    /// <summary>A list row holding a glyph Spectre counts short, the side column still inside the window and in its own column.</summary>
    [Fact]
    public async Task ASideColumnPageRowHoldingAWideGlyph_StaysWithinTheWidth()
    {
        _console.Profile.Width = 100;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        int sideWidth = MenuPane.SideWidth(100);
        var page = Page("🛠️ tools", "plain") with
        {
            Side = (row, width, count) => Enumerable.Range(0, count).Select(i => (IRenderable)new Text("🛠️" + new string('x', sideWidth))).ToList(),
        };
        input.Push(Keys.Escape);

        await menu.PickAsync(page, 0, CancellationToken.None);

        string[] rows = Output.Split('\n');
        string glyphRow = Assert.Single(rows, r => r.Contains("🛠️ tools", StringComparison.Ordinal));
        string plainRow = Assert.Single(rows, r => r.Contains("plain", StringComparison.Ordinal));
        Assert.All(new[] { glyphRow, plainRow }, r =>
        {
            Assert.True(TextCells.Width(r) <= 100, r);
            Assert.True(Segment.CellCount([new Segment(r)]) <= 100, r);
        });
        Assert.Equal(TextCells.Width(plainRow[..plainRow.IndexOf('x', StringComparison.Ordinal)]), TextCells.Width(glyphRow[..glyphRow.IndexOf('x', StringComparison.Ordinal)]));
        menu.Close();
    }

    // ── A long label beside the strip ─────────────────────────────

    /// <summary>A <c>/tasks</c> page on <paramref name="folder"/>, as <see cref="TasksMenu"/> builds it: four tabs, then four buttons.</summary>
    private static MenuPage TasksPage(string folder) =>
        MenuPage.Tabbed(TasksMenu.TitleFor("DESKTOP-4K2M9QX", folder), [.. TasksMenu.TabTitles.Select(title => new MenuTab(title, ["a task"]))], 0, TasksMenu.Keys)
            with { Buttons = [new("⟳ refresh", 'r'), new("computer", 'c'), new("folder", 'f'), new("Windows' own: shown", 'w')], TabButtons = true };

    public static TheoryData<string> TaskFolders() => [.. StripChecks.TaskFolders];

    /// <summary>
    /// The reproduction (2026-11-07, the code review): <c>/tasks</c> on a long folder, where at 90 columns ⟳ refresh wrapped and a click on folder
    /// pressed Windows' own, and at 80 nothing answered. At every width the strip is drawn as laid out and every title's click lands on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(TaskFolders))]
    public async Task TheTasksStrip_DrawsAsLaidOut_AndEveryTitlesClickLands_AtEveryWidth(string folder)
    {
        foreach (int width in StripChecks.Widths)
        {
            _console.Profile.Width = width;
            var page = TasksPage(folder);
            string[] titles = [.. TasksMenu.TabTitles, .. page.Buttons!.Select(b => b.Title)];
            var layout = InfoPane.TabStripLayout(page.Title, titles, width);
            StripChecks.DrawnAsLaidOut(MenuPane.TopRows(page, width), layout, titles, width);
            for (int i = 0; i < titles.Length; i++)
            {
                var (pane, input, keys) = Rig();
                using var _ = pane;
                var menu = new MenuPane(pane, keys);
                var (row, column) = layout.Places[i];
                input.PushClick(column + 1, 100 + row);
                input.Push(Keys.Enter);                  // a tab's click switches to it and Enter picks there; a button's click answers itself

                var pick = await menu.PickAsync(page, 0, CancellationToken.None);
                menu.Close();

                var expected = i < TasksMenu.TabTitles.Count ? (i, -1) : (0, i - TasksMenu.TabTitles.Count);
                Assert.True(expected == (pick?.Tab, pick?.Button), $"{width}, {titles[i]}: {pick}");
            }
        }
    }

    /// <summary>A wide window keeps the whole label, the strip as it was.</summary>
    [Fact]
    public void AWideWindow_KeepsTheWholeLabel()
    {
        var page = TasksPage(StripChecks.TaskFolders[1]);
        Assert.Equal(page.Title, MenuPane.StripLabel(page, 130));
        Assert.StartsWith(TextCells.Spaced(page.Title), Markup.Remove(MenuPane.TopRows(page, 130)[0]), StringComparison.Ordinal);
        Assert.NotEqual(page.Title, MenuPane.StripLabel(page, 90));
    }

    /// <summary>
    /// Every kind of strip draws the label <see cref="MenuPane.StripLabel"/> says (2026-11-07, the second code review: the first strip's titles are
    /// worked out in three places, and a drifted copy would cut the drawn label one way and the clicks' another): tabs and buttons, tabs with
    /// badges over a second strip, and buttons with one hidden.
    /// </summary>
    [Fact]
    public void EveryKindOfStrip_DrawsTheLabelStripLabelSays()
    {
        const string title = "🗓️ Tasks · DESKTOP-4K2M9QX · \\Microsoft\\Windows\\Customer Experience Improvement Program";
        const int width = 60;
        var buttons = new List<MenuButton> { new("⟳ refresh", 'r'), new("computer", 'c'), new("folder", 'f') };
        var withButtons = MenuPage.Tabbed(title, [new("Ready", ["a"]), new("Running", ["b"]), new("Disabled", ["c"])], 0, "hint") with { Buttons = buttons, TabButtons = true };
        var badged = MenuPage.Tabbed(title, [new("Offered", ["a"]) { Badge = "12", SubTabs = [new("all", ["a"]), new("project", ["b"]) { Badge = "3" }] }, new("Options", ["b"]) { Badge = "!" }], 0, "hint");
        var hidden = new MenuPage(title, ["a"], "hint") { Buttons = [new("⟳ refresh", 'r'), new("a very long hidden button title", 'h') { Hidden = true }, new("folder", 'f')] };

        foreach (var page in new[] { withButtons, badged, hidden })
        {
            string label = MenuPane.StripLabel(page, width);
            Assert.NotEqual(title, label);
            Assert.StartsWith(TextCells.Spaced(label) + InfoPane.LabelSeparator, Markup.Remove(MenuPane.TopRows(page, width)[0]), StringComparison.Ordinal);
        }

        string indent = new string(' ', TextCells.Width(TextCells.Spaced(MenuPane.StripLabel(badged, width)))) + InfoPane.LabelSeparator;
        var sub = MenuPane.SubStripRows(badged, width);
        Assert.NotEmpty(sub);
        Assert.All(sub, row => Assert.StartsWith(indent, Markup.Remove(row), StringComparison.Ordinal));
    }

    /// <summary>
    /// A long label holding ⚙️ over a tab with sub-tabs (2026-11-07): the second strip indents by the label the first shows, cut, so its titles
    /// are drawn where its clicks look, and a click on one picks it.
    /// </summary>
    [Fact]
    public async Task ASubStrip_UnderACutLabel_IsDrawnAndClickedAsLaidOut()
    {
        const int width = 80;
        _console.Profile.Width = width;
        var page = MenuPage.Tabbed(
            "⚙️ Settings · " + string.Join(" · ", Enumerable.Repeat("a long part", 6)),
            [new MenuTab("One", ["a"]), new MenuTab("Group", []) { SubTabs = [new MenuTab("X", ["x1"]), new MenuTab("Y", ["y1"]), new MenuTab("Z", ["z1"])] }, new MenuTab("Three", ["d"])],
            1,
            "Enter = pick · Esc = back");
        string label = MenuPane.StripLabel(page, width);
        Assert.NotEqual(page.Title, label);
        Assert.StartsWith("⚙️ Settings · ", label, StringComparison.Ordinal);
        int main = InfoPane.TabStripLayout(page.Title, ["One", "Group", "Three"], width).Rows;
        var sub = InfoPane.SubStripLayout(label, ["X", "Y", "Z"], width);
        StripChecks.DrawnAsLaidOut(MenuPane.SubStripRows(page, width), sub, ["X", "Y", "Z"], width);

        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(sub.Places[1].Column + 1, 100 + main + sub.Places[1].Row);   // Y
        input.Push(Keys.Enter);

        var pick = await menu.PickAsync(page, 0, CancellationToken.None);
        menu.Close();

        Assert.Equal((1, 1, 0), (pick?.Tab, pick?.Sub, pick?.Row));
    }

    /// <summary>A page whose buttons are all hidden (the monitors' names page) has nothing to click in its strip: its long title still wraps whole.</summary>
    [Fact]
    public async Task APageWhoseButtonsAreAllHidden_StillWrapsItsTitle()
    {
        string title = "🖥️ Monitors · " + string.Join(" · ", Enumerable.Repeat("a long part", 5)) + " · the end";
        var page = new MenuPage(title, ["one"], "Esc = back") { Buttons = [new MenuButton("rename", 'r') { Hidden = true }] };
        Assert.Equal(title, MenuPane.StripLabel(page, 40));
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.Push(Keys.Escape);

        await menu.PickAsync(page, 0, CancellationToken.None);
        menu.Close();

        Assert.Contains("the end", Output);
    }

    // ── A click never scrolls the list ────────────────────────────

    // head A 0, a1–a4 1–4, the gap 5, head B 6, b1–b5 7–11: at a height of 12, five rows and the more row in view.
    private static MenuPage Sections() =>
        Page(SectionRule.Markup("head A"), "a1", "a2", "a3", "a4", "", SectionRule.Markup("head B"), "b1", "b2", "b3", "b4", "b5") with { Headings = new HashSet<int> { 0, 6 } };

    [Fact]
    public async Task AClickOnASectionsFirstRow_LeavesTheRowsWhereTheyAre_AndADoubleClickPicksIt()
    {
        _console.Profile.Height = 12;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 102);            // opened on b5: b1–b5 from 102, so b1
        input.PushClick(3, 102);            // the pair

        Assert.Equal(new MenuPick(0, 7), await menu.PickAsync(Sections(), 11, CancellationToken.None));
        Assert.DoesNotContain("head B", Output);
        menu.Close();
    }

    [Fact]
    public async Task AKey_StillBringsASectionsHeadingIn()
    {
        _console.Profile.Height = 12;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 103);            // b2
        input.Push(Keys.Up);                // b1, by key
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 7), await menu.PickAsync(Sections(), 11, CancellationToken.None));
        Assert.Contains("head B", Output);
        menu.Close();
    }

    [Fact]
    public async Task ARedrawAfterAClick_LeavesTheRowsWhereTheyAre()
    {
        _console.Profile.Height = 12;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 102);            // b1
        input.OnWait = () =>
        {
            input.OnWait = () =>
            {
                input.OnWait = null;
                input.Push(Keys.Enter);
            };
            keys.Post(new InputEvent.Resize());
        };

        Assert.Equal(new MenuPick(0, 7), await menu.PickAsync(Sections(), 11, CancellationToken.None));
        Assert.DoesNotContain("head B", Output);
        menu.Close();
    }

    /// <summary>
    /// A filter typed after a click (2026-11-07, the code review): other rows under the clicked index, so the heading comes back in; the same page
    /// drawn again (the same filter, as many rows) still leaves the rows where the click left them.
    /// </summary>
    [Fact]
    public async Task AFilterAfterAClick_BringsTheHeadingBack_ARebuildOfTheSameRowsDoesnt()
    {
        _console.Profile.Height = 12;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 102);            // b1, the heading out of view
        input.Push(Keys.Char('x'));         // typed into the filter: back to the host

        var typed = await menu.PickAsync(Sections() with { Filter = "" }, 11, CancellationToken.None);
        Assert.Equal("x", typed?.Filter);

        int mark = Output.Length;
        input.Push(Keys.Enter);
        Assert.Equal(new MenuPick(0, 7), await menu.PickAsync(Sections() with { Filter = "x" }, 7, CancellationToken.None));
        Assert.Contains("head B", Output[mark..]);
        menu.Close();

        // The same rows and filter again after a click: they stay put.
        var again = new MenuPane(pane, keys);
        input.PushClick(3, 102);
        input.Push(Keys.Char('x'));
        await again.PickAsync(Sections() with { Filter = "" }, 11, CancellationToken.None);
        mark = Output.Length;
        input.Push(Keys.Enter);
        Assert.Equal(new MenuPick(0, 7), await again.PickAsync(Sections() with { Filter = "" }, 7, CancellationToken.None));
        Assert.DoesNotContain("head B", Output[mark..]);
        again.Close();
    }

    /// <summary>
    /// A live page that gains a row after a click (2026-11-07, the second code review: another device adds a line) keeps the rows where the
    /// click left them: only a new filter brings the heading in.
    /// </summary>
    [Fact]
    public async Task ARowAddedAfterAClick_WithTheSameFilter_LeavesTheRowsWhereTheyAre()
    {
        _console.Profile.Height = 12;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 102);            // b1, the heading out of view
        input.Push(Keys.Char('x'));         // back to the host, which builds the page again
        await menu.PickAsync(Sections() with { Filter = "" }, 11, CancellationToken.None);

        int mark = Output.Length;
        input.Push(Keys.Enter);
        var grown = Sections() with { Filter = "" };
        grown = grown with { Rows = [.. grown.Rows, "b6"] };
        Assert.Equal(new MenuPick(0, 7), await menu.PickAsync(grown, 7, CancellationToken.None));
        Assert.DoesNotContain("head B", Output[mark..]);
        menu.Close();
    }

    // head A 0, cells 1–16, the gap 17, head B 18, cells 19–42: four to a line at a width of 40.
    private static MenuPage GridSections()
    {
        var rows = new List<string> { SectionRule.Markup("head A") };
        rows.AddRange(Enumerable.Range(0, 16).Select(i => "a" + i));
        rows.Add("");
        rows.Add(SectionRule.Markup("head B"));
        rows.AddRange(Enumerable.Range(0, 24).Select(i => "b" + i));
        return Page([.. rows]) with { Headings = new HashSet<int> { 0, 18 }, Grid = new MenuGrid(8) };
    }

    [Fact]
    public async Task AClickOnASectionsFirstCell_LeavesTheLinesWhereTheyAre_AndADoubleClickPicksIt()
    {
        _console.Profile.Height = 12;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 102);            // opened on the fifth of B's lines: its first line at 102, so its first cell
        input.PushClick(3, 102);

        Assert.Equal(new MenuPick(0, 19), await menu.PickAsync(GridSections(), 35, CancellationToken.None));
        Assert.DoesNotContain("head B", Output);
        menu.Close();
    }

    // ── A click aimed at an older layout ──────────────────────────

    private void ShowRows(ScreenPane pane, string page, params string[] rows) =>
        pane.ShowOverlay(
            new Rows(rows.Select(IRenderable (r) => new Text(r))),
            "hint",
            close: true,
            aim: new OverlayAim(page, rows.Select((text, i) => new OverlayTarget(i, 0, 39, text)).ToList()));

    private (ScreenPane Pane, ScriptedInput Input, KeySource Keys) Guarded()
    {
        var rig = Rig();
        rig.Pane.GuardsStaleClicks = true;
        return rig;
    }

    private void PastTheGrace() => _time.Advance(ScreenPane.ClickGrace + TimeSpan.FromMilliseconds(1));

    [Fact]
    public void AClick_InsideTheGrace_IsStale_JustAfterItIsNot()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        ShowRows(pane, "p", "one", "two");

        Assert.True(pane.ClickIsStale(3, 1));

        ShowRows(pane, "q", "one", "two");
        PastTheGrace();
        Assert.False(pane.ClickIsStale(3, 1));
    }

    [Fact]
    public void WithTheGuardOff_NoClickIsStale()
    {
        var (pane, _, _) = Rig();
        using var _1 = pane;
        ShowRows(pane, "p", "one", "two");

        Assert.False(pane.ClickIsStale(3, 1));
    }

    [Fact]
    public void APageRedrawnUnchanged_IsNotAChange()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        ShowRows(pane, "p", "one", "two");
        PastTheGrace();
        ShowRows(pane, "p", "one", "two");

        Assert.False(pane.ClickIsStale(3, 1));
    }

    [Fact]
    public void AListReorderedByARedraw_IsStale_ALivePagesUnchangedRowIsNot()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        ShowRows(pane, "p", "one", "two", "three");
        PastTheGrace();
        ShowRows(pane, "p", "one", "three", "two");

        Assert.False(pane.ClickIsStale(3, 0));   // still "one", on the same page: the row aimed at
        Assert.True(pane.ClickIsStale(3, 1));
    }

    [Fact]
    public void ASameShapedNewPage_IsStale_EvenOnARowThatReadsTheSame()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        ShowRows(pane, "p", "one", "two");
        PastTheGrace();
        ShowRows(pane, "q", "one", "two");

        Assert.True(pane.ClickIsStale(3, 0));
    }

    [Fact]
    public void TheOverlayMovingOnTheScreen_IsStale()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        ShowRows(pane, "p", "one", "two", "three");
        PastTheGrace();
        ShowRows(pane, "p", "one", "two", "three", "four");   // one row taller, so its top a row higher

        Assert.True(pane.ClickIsStale(3, 1));
    }

    /// <summary>The cursor's highlight is no change, so the second click of a pair counts.</summary>
    [Fact]
    public async Task ACursorMoveThenADoubleClicksSecondClick_IsNotDropped()
    {
        var (pane, input, keys) = Guarded();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.OnWait = () =>
        {
            input.OnWait = null;
            PastTheGrace();
            input.PushClick(3, 103);
            input.PushClick(3, 103);
        };

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        menu.Close();
    }

    /// <summary>A dropped click moves nothing and ends the pair; the next one counts, as a first.</summary>
    [Fact]
    public async Task ADroppedClick_DoesNothing_AndTheClickAfterItGoesThrough()
    {
        var (pane, input, keys) = Guarded();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 103);            // the page only just opened: dropped
        input.PushClick(3, 103);            // goes through: the cursor on "two", no pair with the dropped one
        input.Push(Keys.Down);              // so the pane's still open
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 2), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        menu.Close();
    }

    [Fact]
    public async Task ADroppedClick_MovesNoCursor()
    {
        var (pane, input, keys) = Guarded();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 103);
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        menu.Close();
    }

    /// <summary>
    /// Judged by the press (2026-11-07, the code review): a click pressed before a change but read long after it, behind a slow callback, is still
    /// aimed at the old rows; one pressed well after the change is not, however late it's read.
    /// </summary>
    [Fact]
    public void AClickPressedBeforeAChange_IsStale_HoweverLateItIsRead()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        ShowRows(pane, "p", "one", "two");
        PastTheGrace();
        long pressed = _time.GetTimestamp();
        _time.Advance(TimeSpan.FromMilliseconds(10));
        ShowRows(pane, "q", "two", "one");
        _time.Advance(TimeSpan.FromMilliseconds(600));   // read late: past the grace from the change

        Assert.False(pane.ClickIsStale(3, 1));   // read time alone would let it land
        Assert.True(pane.ClickIsStale(3, 1, pressed));

        _time.Advance(ScreenPane.DroppedClickPass + TimeSpan.FromMilliseconds(1));
        ShowRows(pane, "r", "one", "two");
        PastTheGrace();
        long later = _time.GetTimestamp();
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(pane.ClickIsStale(3, 1, later));
    }

    /// <summary>A press time from another clock, later than the pane's now, is taken as now: the check is as it was without one.</summary>
    [Fact]
    public void APressStampedAfterNow_IsTakenAsNow()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        ShowRows(pane, "p", "one", "two");
        PastTheGrace();
        Assert.False(pane.ClickIsStale(3, 1, _time.GetTimestamp() + TimeSpan.TicksPerHour));
    }

    /// <summary>The readers' stamp (2026-11-07): a click gets the time unless it has one; anything else passes untouched.</summary>
    [Fact]
    public void Stamped_TimesAClickOnce_AndNothingElse()
    {
        Assert.Equal(new InputEvent.Click(1, 2, MouseButton.Left, At: 42), InputEvent.Stamped(new InputEvent.Click(1, 2, MouseButton.Left), 42));
        Assert.Equal(new InputEvent.Click(1, 2, MouseButton.Left, At: 7), InputEvent.Stamped(new InputEvent.Click(1, 2, MouseButton.Left, At: 7), 42));
        var key = new InputEvent.Key(Keys.Enter);
        Assert.Same(key, InputEvent.Stamped(key, 42));
        var drag = new InputEvent.Drag(1, 2);
        Assert.Same(drag, InputEvent.Stamped(drag, 42));
    }

    /// <summary>Through the menu: a click pressed before the page changed under it, read after the grace, moves nothing.</summary>
    [Fact]
    public async Task AMenuClick_PressedBeforeTheRowsChanged_IsDropped_ThoughReadLate()
    {
        var (pane, input, keys) = Guarded();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        _time.Advance(TimeSpan.FromMilliseconds(1));   // the test clock starts at 0, which is "no press time"
        long pressed = _time.GetTimestamp();
        input.OnWait = () =>
        {
            input.OnWait = null;
            _time.Advance(TimeSpan.FromSeconds(1));   // the page drew after the press, and the click is read a second later
            input.PushClick(3, 103, at: pressed);
            input.Push(Keys.Enter);
        };

        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        menu.Close();
    }

    /// <summary>A page changing faster than the grace never stops answering.</summary>
    [Fact]
    public void APageChangingFasterThanTheGrace_StillAnswers()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        for (int i = 0; i < 3; i++)
        {
            ShowRows(pane, "p", "row " + i);
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.True(pane.ClickIsStale(3, 0));
        ShowRows(pane, "p", "row again");
        Assert.False(pane.ClickIsStale(3, 0));
    }

    /// <summary>
    /// Judged against the layout on show when the click was aimed (2026-11-07, the second code review): two changes after the press, the second
    /// past the grace from the first, still leave it aimed at the first layout, though the last two agree at the spot.
    /// </summary>
    [Fact]
    public void TwoChangesAfterThePress_LeaveTheClickAimedAtTheFirstLayout()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        _time.Advance(TimeSpan.FromMilliseconds(1));   // the test clock starts at 0, which is "no press time"
        ShowRows(pane, "p", "x", "aimed");
        PastTheGrace();
        long pressed = _time.GetTimestamp();
        _time.Advance(TimeSpan.FromMilliseconds(50));
        ShowRows(pane, "p", "x", "moved");
        _time.Advance(TimeSpan.FromMilliseconds(400));
        ShowRows(pane, "p", "y", "moved");
        _time.Advance(TimeSpan.FromMilliseconds(50));

        Assert.True(pane.ClickIsStale(3, 1, pressed));
    }

    /// <summary>A press older than every layout the pane still remembers is taken as stale, once; the next click counts.</summary>
    [Fact]
    public void APressOlderThanTheHistory_IsDroppedOnce()
    {
        var (pane, _, _) = Guarded();
        using var _1 = pane;
        _time.Advance(TimeSpan.FromMilliseconds(1));
        ShowRows(pane, "p", "row", "extra");   // as tall as every redraw after it: only the second row changes
        PastTheGrace();
        long pressed = _time.GetTimestamp();
        for (int i = 0; i < 12; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(10));
            ShowRows(pane, "p", "row", "extra " + i);
        }

        Assert.True(pane.ClickIsStale(3, 0, pressed));
        Assert.False(pane.ClickIsStale(3, 0, pressed));
    }

    [Fact]
    public async Task TheMoreRowAndTheStrip_WorkInsideTheGrace()
    {
        _console.Profile.Height = 12;
        var (pane, input, keys) = Guarded();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        var rows = Enumerable.Range(0, 20).Select(i => "row " + i).ToArray();
        input.PushClick(6, 107);            // the more row's ▼, on a page only just opened
        input.Push(Keys.Enter);

        Assert.Equal(5, (await menu.PickAsync(Page(rows), 0, CancellationToken.None))!.Value.Row);

        input.PushClick(18, 100);           // Two's title
        input.Push(Keys.Enter);
        var tabbed = MenuPage.Tabbed("Settings", [new MenuTab("One", ["a", "b"]), new MenuTab("Two", ["c"])], 0, "hint");

        Assert.Equal(new MenuPick(1, 0), await menu.PickAsync(tabbed, 0, CancellationToken.None));
        menu.Close();
    }

    [Fact]
    public async Task AnInfoPanesLine_DropsAStaleClick()
    {
        var (pane, input, keys) = Guarded();
        using var _ = pane;
        var told = new List<string>();
        input.PushClick(2, 103);            // only just opened: dropped, and the pair ends
        input.PushClick(2, 103);            // a first
        input.Push(Keys.Escape);

        await new InfoPane(pane, keys).ShowAsync("Help", [new InfoTab("One", () => new Text("line1\nline2\nline3")) { LineDoubleClicked = told.Add }], 0, CancellationToken.None);

        Assert.Empty(told);
    }

    [Fact]
    public async Task AFolderPanesRow_DropsAStaleClick()
    {
        var (pane, input, keys) = Guarded();
        using var _ = pane;
        const string Root = @"C:\";
        var tree = new FolderTree(new FakeFolders().Root(Root).Add(Root, "Users", "Windows"));
        input.PushClick(12, 102);           // only just opened: dropped
        input.PushClick(12, 102);           // a first, no pair with the dropped one
        input.Push(Keys.Escape);

        Assert.Null(await new FolderPane(pane, keys).PickAsync(tree, 0, CancellationToken.None));
    }

    // ── The slab on the row under the pointer ─────────────────────

    private void Tick() => _time.Advance(ScreenPane.Tick);

    /// <summary>Runs <paramref name="check"/> while <paramref name="page"/> is open, then closes it with Esc.</summary>
    private async Task WhileOpen(ScreenPane pane, ScriptedInput input, KeySource keys, MenuPage page, Action<MenuPane> check, int cursor = 0)
    {
        var menu = new MenuPane(pane, keys);
        input.OnWait = () =>
        {
            input.OnWait = null;
            check(menu);
            input.Push(Keys.Escape);
        };
        await menu.PickAsync(page, cursor, CancellationToken.None);
        menu.Close();
    }

    [Fact]
    public async Task TheRowUnderThePointer_GetsTheSlab_TheCursorsRowKeepsItsLook()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        await WhileOpen(pane, input, keys, Page("one", "two", "three"), _ =>
        {
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);   // "two"
            Tick();
            Assert.Equal("two", pane.HoverTargetNow?.Text);

            pane.PointerMoved(3, pane.OverlayTopRow + 2);   // "one", the cursor's
            Tick();
            Assert.Null(pane.HoverTargetNow);
        });
    }

    /// <summary>
    /// An idle tick looks nothing up again (2026-11-07, the code review): no window top read, no targets scanned, until the pointer moves; and the
    /// slab still hides when focus goes.
    /// </summary>
    [Fact]
    public async Task AnIdleTick_LooksTheHoverUpOnce_AMoveLooksAgain()
    {
        int reads = 0;
        var (pane, input, keys) = Rig(() =>
        {
            reads++;
            return 0;
        });
        using var _ = pane;
        await WhileOpen(pane, input, keys, Page("one", "two", "three"), _ =>
        {
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);   // "two"
            Tick();
            Assert.Equal("two", pane.HoverTargetNow?.Text);
            int looked = reads;

            Tick();
            Tick();
            Tick();
            Assert.Equal(looked, reads);
            Assert.Equal("two", pane.HoverTargetNow?.Text);

            pane.PointerMoved(3, pane.OverlayTopRow + 4);   // "three"
            Tick();
            Assert.True(reads > looked);
            Assert.Equal("three", pane.HoverTargetNow?.Text);

            pane.FocusChanged(false);
            Tick();
            Assert.Null(pane.HoverTargetNow);
        });
    }

    /// <summary>
    /// The slab's repaint takes the rows the last draw rendered (2026-11-07, the code review): crossing rows renders the overlay no more, and
    /// the first row, left again, keeps its ×.
    /// </summary>
    [Fact]
    public void TheRepaint_RendersNothingAgain_AndTheFirstRowKeepsItsClose()
    {
        _console.EmitAnsiSequences();
        var (pane, _, _) = Rig();
        using var shown = pane;
        var content = new Counted(new Rows(new Text("title"), new Text("one"), new Text("two")));
        pane.ShowOverlay(content, "hint", close: true, aim: new OverlayAim("p", [new(0, 0, 4, "title"), new(1, 0, 2, "one"), new(2, 0, 2, "two")]));
        Tick();
        int renders = content.Renders;

        pane.PointerMoved(1, pane.OverlayTopRow);
        Tick();
        Assert.Equal("title", pane.HoverTargetNow?.Text);
        pane.PointerMoved(1, pane.OverlayTopRow + 1);
        int mark = Output.Length;
        Tick();
        Assert.Equal("one", pane.HoverTargetNow?.Text);
        Assert.Contains(ScreenPane.CloseGlyph, Output[mark..]);
        pane.PointerMoved(1, pane.OverlayTopRow + 2);
        Tick();
        Assert.Equal("two", pane.HoverTargetNow?.Text);
        Assert.Equal(renders, content.Renders);
    }

    /// <summary>A renderable that counts its renders.</summary>
    private sealed class Counted(IRenderable inner) : IRenderable
    {
        public int Renders { get; private set; }

        public Measurement Measure(RenderOptions options, int maxWidth) => inner.Measure(options, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            Renders++;
            return inner.Render(options, maxWidth);
        }
    }

    [Fact]
    public async Task TheSlab_IsPaintedBehindTheRowAndToTheListsEdge()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        await WhileOpen(pane, input, keys, Page("one", "two", "three"), _ =>
        {
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            int mark = Output.Length;
            Tick();
            var back = Theme.MenuHover;
            Assert.Matches($@"48;2;{back.R};{back.G};{back.B}m  two {{20,}}", Output[mark..]);
        });
    }

    /// <summary>
    /// A full row isn't erased after it's repainted (seen in Windows Terminal: an erase with the cursor waiting at the edge wipes the last cell, and
    /// every cut row lost its ellipsis); a short one is, or leaving it would leave its slab. Only the rows the slab moved between are written.
    /// </summary>
    [Fact]
    public async Task TheRepaint_LeavesAFullRowsLastCell_AndWritesOnlyTheRowsItMovedBetween()
    {
        _console.EmitAnsiSequences();
        _console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        string wide = new('w', 60);
        await WhileOpen(pane, input, keys, Page("one", wide, "three", "four"), _ =>
        {
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);   // the full row
            int mark = Output.Length;
            Tick();
            string frame = Output[mark..];
            Assert.Contains("…", frame);
            Assert.DoesNotContain("[K", frame);
            Assert.DoesNotContain("three", frame);
            Assert.DoesNotContain("four", frame);

            pane.PointerMoved(3, pane.OverlayTopRow + 4);   // off it, onto a short one
            mark = Output.Length;
            Tick();
            frame = Output[mark..];
            Assert.Contains("…", frame);
            Assert.Contains("three", frame);
            Assert.Contains("[K", frame);                   // the short row's rest, under the slab it may have left
            Assert.DoesNotContain("four", frame);
        });
    }

    [Fact]
    public async Task AGridsSlab_IsOneCell()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        int pointer = TextCells.Width(MenuPane.NoPointer);
        await WhileOpen(pane, input, keys, Page("a", "b", "c") with { Grid = new MenuGrid(8) }, _ =>
        {
            Tick();
            pane.PointerMoved(pointer + 9, pane.OverlayTopRow + 2);
            Tick();
            Assert.Equal(new OverlayTarget(2, pointer + 8, pointer + 15, "b"), pane.HoverTargetNow);

            pane.PointerMoved(pointer + 30, pane.OverlayTopRow + 2);   // past the last cell
            Tick();
            Assert.Null(pane.HoverTargetNow);
        });
    }

    [Fact]
    public async Task AHeadingAGapAndTheSideColumn_ShowNone()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var page = Page(SectionRule.Markup("Alpha"), "a", "", SectionRule.Markup("Beta"), "b") with { Headings = new HashSet<int> { 0, 3 } };
        await WhileOpen(pane, input, keys, page, _ =>
        {
            Tick();
            foreach (int row in new[] { 2, 4, 5 })   // Alpha, the gap, Beta
            {
                pane.PointerMoved(3, pane.OverlayTopRow + row);
                Tick();
                Assert.Null(pane.HoverTargetNow);
            }
        }, cursor: 4);
    }

    [Fact]
    public async Task TheSideColumn_ShowsNone_TheListBesideItDoes()
    {
        _console.Profile.Width = 100;
        var (pane, input, keys) = Rig();
        using var _ = pane;
        await WhileOpen(pane, input, keys, Sided("one", "two"), _ =>
        {
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            Tick();
            Assert.Equal("two", pane.HoverTargetNow?.Text);

            pane.PointerMoved(99 - MenuPane.SideWidth(100) + 2, pane.OverlayTopRow + 3);
            Tick();
            Assert.Null(pane.HoverTargetNow);
        });
    }

    [Fact]
    public async Task APlainInfoTab_ShowsNone_OneWhoseLinesAct_Does()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        input.OnWait = () =>
        {
            input.OnWait = null;
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            Tick();
            Assert.Null(pane.HoverTargetNow);
            input.Push(Keys.Escape);
        };
        await new InfoPane(pane, keys).ShowAsync("Help", [new InfoTab("One", () => new Text("line1\nline2"))], 0, CancellationToken.None);

        input.OnWait = () =>
        {
            input.OnWait = null;
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            Tick();
            Assert.Equal("line2", pane.HoverTargetNow?.Text);
            input.Push(Keys.Escape);
        };
        await new InfoPane(pane, keys).ShowAsync("Help", [new InfoTab("One", () => new Text("line1\nline2")) { LineDoubleClicked = _ => { } }], 0, CancellationToken.None);
    }

    /// <summary>A blank line of an info tab whose lines act is no target (2026-11-07, the code review): no slab, and a double-click on it does nothing.</summary>
    [Fact]
    public async Task AnInfoTabsBlankLine_GetsNoSlab_AndADoubleClickOnItDoesNothing()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var clicked = new List<string>();
        input.OnWait = () =>
        {
            input.OnWait = null;
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);   // the blank line
            Tick();
            Assert.Null(pane.HoverTargetNow);
            pane.PointerMoved(3, pane.OverlayTopRow + 4);
            Tick();
            Assert.Equal("line3", pane.HoverTargetNow?.Text);
            input.PushClick(3, 103);
            input.PushClick(3, 103);
            input.Push(Keys.Escape);
        };
        await new InfoPane(pane, keys).ShowAsync("Help", [new InfoTab("One", () => new Text("line1\n\nline3")) { LineDoubleClicked = clicked.Add }], 0, CancellationToken.None);

        Assert.Empty(clicked);
    }

    /// <summary>Under an edit the rows take no click, so none lights up (2026-11-07, the code review).</summary>
    [Fact]
    public async Task UnderAnEdit_NoRowLightsUp()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        var line = new InputLine(pane, keys);
        input.OnWait = () =>
        {
            input.OnWait = null;
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);   // "two"
            Tick();
            Assert.Null(pane.HoverTargetNow);
            input.Push(Keys.Escape);
        };

        Assert.IsType<InputResult.Cancelled>(await menu.EditAsync(Page("one", "two", "three"), 0, line, "old", allowEmpty: false, CancellationToken.None));
        menu.Close();
    }

    /// <summary>
    /// Rows that stand in for rows (2026-11-07, the code review) are no target: a filter's no-match row takes no slab, under another row too, and a
    /// page of one such row, or of an empty list's one dim notice, has no target at all, so the mouse's moves aren't even asked for. A dim row among
    /// real ones (a tool that's off) is still one.
    /// </summary>
    [Fact]
    public async Task PlaceholderRows_AreNoTarget_ADimRowAmongOthersIs()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var hover = new List<bool>();
        pane.Hover = hover.Add;

        async Task<string?> Over(MenuPage page, int row)
        {
            string? text = "unset";
            await WhileOpen(pane, input, keys, page, _ =>
            {
                Tick();
                pane.PointerMoved(3, pane.OverlayTopRow + 2 + row);
                Tick();
                text = pane.HoverTargetNow?.Text;
            });
            Tick();
            return text;
        }

        Assert.Null(await Over(Page("+ new", MenuFilter.NoMatchRow("zz")) with { Filter = "zz" }, 1));
        Assert.Equal(Theme.DimMarkup("off tool"), await Over(Page("on tool", Theme.DimMarkup("off tool")), 1));
        hover.Clear();
        await Over(Page(MenuFilter.NoMatchNameRow("zz")) with { Filter = "zz" }, 0);
        await Over(Page(Theme.DimMarkup("Nothing saved yet.")) with { Placeholder = true }, 0);
        Assert.Empty(hover);
    }

    /// <summary>
    /// A page's one row that is dim but real (2026-11-07, the second code review: /process with one ended process) is a target, so the pointer's
    /// moves are asked for; it holds the cursor, whose row keeps its look. Only a page or tab the host marks <see cref="MenuPage.Placeholder"/>
    /// stands in for rows, and a tab's mark follows a switch to it and away.
    /// </summary>
    [Fact]
    public async Task ALoneDimRow_IsATarget_OnlyAPlaceholderPageIsNot()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var hover = new List<bool>();
        pane.Hover = hover.Add;
        await WhileOpen(pane, input, keys, Page(Theme.DimMarkup("1  ended  echo hi")), _ => Tick());
        Tick();
        Assert.Equal([true, false], hover);

        // Tabbed: the first tab real, the second a placeholder, reached by Tab and left by Tab.
        var tabs = new MenuTab[] { new("Real", ["one"]), new("Empty", [Theme.DimMarkup("none")]) { Placeholder = true } };
        Assert.True(MenuPage.Tabbed("T", tabs, 1, "hint").Placeholder);
        hover.Clear();
        await WhileOpen(pane, input, keys, MenuPage.Tabbed("T", tabs, 1, "hint"), _ => Tick());
        Tick();
        Assert.Empty(hover);

        var menu = new MenuPane(pane, keys);
        input.OnWait = () =>
        {
            Tick();
            Assert.Equal([true], hover);
            input.OnWait = () =>
            {
                Tick();
                Assert.Equal([true, false], hover);
                input.OnWait = null;
                input.Push(Keys.Escape);
            };
            input.Push(Keys.Tab);
        };
        await menu.PickAsync(MenuPage.Tabbed("T", tabs, 0, "hint"), 0, CancellationToken.None);
        menu.Close();
    }

    [Fact]
    public async Task NoRedraw_BeforeAnyMove_OrForAMoveWithinOneTarget()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        await WhileOpen(pane, input, keys, Page("one", "two", "three"), _ =>
        {
            Tick();
            int mark = Output.Length;
            Tick();
            Assert.Equal(mark, Output.Length);   // no move yet

            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            Tick();
            Assert.True(Output.Length > mark);   // the slab drawn
            mark = Output.Length;

            pane.PointerMoved(9, pane.OverlayTopRow + 3);
            Tick();
            Assert.Equal(mark, Output.Length);   // the same row
        });
    }

    [Fact]
    public async Task AKey_HidesTheSlab_UntilThePointerNextMoves()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var menu = new MenuPane(pane, keys);
        int waits = 0;
        input.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Tick();
                    pane.PointerMoved(3, pane.OverlayTopRow + 4);   // "three"
                    Tick();
                    Assert.Equal("three", pane.HoverTargetNow?.Text);
                    input.Push(Keys.Down);
                    break;
                case 1:
                    Tick();
                    Assert.Null(pane.HoverTargetNow);
                    pane.PointerMoved(3, pane.OverlayTopRow + 4);
                    Tick();
                    Assert.Equal("three", pane.HoverTargetNow?.Text);
                    input.Push(Keys.Escape);
                    break;
            }
        };

        await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None);
        menu.Close();
    }

    [Fact]
    public async Task LostFocus_HidesTheSlab_AndFocusBackDoesntBringItBackAlone()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        await WhileOpen(pane, input, keys, Page("one", "two", "three"), _ =>
        {
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            Tick();
            Assert.NotNull(pane.HoverTargetNow);

            pane.FocusChanged(false);
            Tick();
            Assert.Null(pane.HoverTargetNow);

            pane.FocusChanged(true);
            Tick();
            Assert.Null(pane.HoverTargetNow);

            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            Tick();
            Assert.NotNull(pane.HoverTargetNow);
        });
    }

    /// <summary>
    /// Focus lost under one pane and never reported back (a terminal may not, while reporting is off): hover asked for afresh by the next pane takes
    /// focus as here, so its slab shows on the first move (2026-11-07, the code review).
    /// </summary>
    [Fact]
    public async Task HoverAskedForAgain_TakesFocusAsHere()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        await WhileOpen(pane, input, keys, Page("one", "two", "three"), _ =>
        {
            Tick();
            pane.FocusChanged(false);
            Tick();
        });
        Tick();

        await WhileOpen(pane, input, keys, Page("one", "two", "three"), _ =>
        {
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            Tick();
            Assert.Equal("two", pane.HoverTargetNow?.Text);
        });
    }

    /// <summary>A resize draws the pane again with no slab: the old size's target is on another row now (2026-11-07, the code review).</summary>
    [Fact]
    public async Task AResize_DrawsNoSlab_UntilThePointerNextMoves()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        await WhileOpen(pane, input, keys, Page("one", "two", "three"), _ =>
        {
            Tick();
            pane.PointerMoved(3, pane.OverlayTopRow + 3);
            Tick();
            Assert.Equal("two", pane.HoverTargetNow?.Text);

            int mark = Output.Length;
            _console.Profile.Width = 90;
            Tick();
            var back = Theme.MenuHover;
            Assert.DoesNotContain($"48;2;{back.R};{back.G};{back.B}m", Output[mark..], StringComparison.Ordinal);
            Assert.Null(pane.HoverTargetNow);
        });
    }

    [Fact]
    public async Task HoverIsAskedForOnOpen_AndStoppedOnClose()
    {
        var (pane, input, keys) = Rig();
        using var _ = pane;
        var hover = new List<bool>();
        pane.Hover = hover.Add;
        Tick();
        Assert.Empty(hover);

        await WhileOpen(pane, input, keys, Page("one", "two"), _ =>
        {
            Tick();
            Assert.Equal([true], hover);
        });

        Tick();
        Assert.Equal([true, false], hover);
    }

    // ── The slab's splitting, pure ────────────────────────────

    private static readonly Color Back = new(1, 2, 3);

    private static SegmentLine Line(params Segment[] segments)
    {
        var line = new SegmentLine();
        line.AddRange(segments);
        return line;
    }

    [Fact]
    public void Slab_KeepsTheTextsColors_AndPadsToTheSpansEnd()
    {
        var ink = new Style(foreground: Color.Red, decoration: Decoration.Bold);
        var painted = OverlayAim.Slab(Line(new Segment("ab", ink)), 0, 4, Back);

        Assert.Equal("ab   ", string.Concat(painted.Select(s => s.Text)));
        Assert.Equal(new Style(Color.Red, Back, Decoration.Bold), painted[0].Style);
        Assert.Equal(Back, painted[1].Style.Background);
    }

    /// <summary>One cell of a grid: the parts either side stay as they were, and a wide glyph counts as the terminal draws it.</summary>
    [Fact]
    public void Slab_SplitsAtTheSpan_InTheTerminalsCells()
    {
        var painted = OverlayAim.Slab(Line(new Segment("🛠️abcd")), 2, 3, Back);

        Assert.Equal(["🛠️", "ab", "cd"], painted.Select(s => s.Text));
        Assert.Equal(Color.Default, painted[0].Style.Background);
        Assert.Equal(Back, painted[1].Style.Background);
        Assert.Equal(Color.Default, painted[2].Style.Background);
    }

    [Fact]
    public void Slab_LeavesAPartWithItsOwnBackground()
    {
        var marked = new Style(background: Color.Green);
        var painted = OverlayAim.Slab(Line(new Segment("ab"), new Segment("cd", marked)), 0, 3, Back);

        Assert.Equal(Back, painted[0].Style.Background);
        Assert.Equal(Color.Green, painted[1].Style.Background);
    }

    [Fact]
    public void Slab_OnAShortLine_ReachesItsSpan()
    {
        var painted = OverlayAim.Slab(Line(new Segment("a")), 3, 4, Back);

        Assert.Equal(["a", "  ", "  "], painted.Select(s => s.Text));
        Assert.Equal(Color.Default, painted[1].Style.Background);
        Assert.Equal(Back, painted[2].Style.Background);
    }
}
