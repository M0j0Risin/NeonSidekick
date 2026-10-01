using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class MenuPaneTests : IDisposable
{
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();
    private readonly KeySource _keys;

    public MenuPaneTests()
    {
        _console.Profile.Width = 40;
        _console.Profile.Height = 12;
        _keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
    }

    public void Dispose() => _console.Dispose();

    private ScreenPane Pane(bool geometry = true) =>
        new(_console, geometry ? new ScreenGeometry(() => null) : null, _time) { Hint = () => "idle" };

    private static MenuPage Page(params string[] rows) => new("Settings", rows, "Enter = pick · ESC = back");

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    /// <summary>A title or strip row as the pane prints it since 2026-09-18: the text, then the × close glyph in column width − 2.</summary>
    private static string Titled(string row, int width = 40) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    private string Output => _console.Output;

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    [Fact]
    public async Task Enter_ReturnsTheRow_UpAndDownWrap_AndThePaneStaysOpen()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Up, Keys.Enter);

        MenuPick? picked = await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None);

        Assert.Equal(new MenuPick(0, 2), picked);
        Assert.True(menu.IsOpen);
        Assert.True(pane.OverlayOpen);
        // The first draw: the title, the spacer, the rows with the cursor on the first.
        Assert.Contains(Rule(40) + "\n" + Titled("Settings") + "\n \n▸ one\n  two\n  three\n" + Rule(40) + "\nEnter = pick · ESC = back\n", Output);
        // Down three times wraps to the first; Up from there is the last.
        Assert.Contains("\n  one\n  two\n▸ three\n", Output);

        menu.Close();
        Assert.False(menu.IsOpen);
        Assert.False(pane.OverlayOpen);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    [Fact]
    public async Task Esc_ReturnsNull_WithThePaneStillOpen()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Down, Keys.Escape);

        Assert.Null(await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None));

        Assert.True(pane.OverlayOpen);
        menu.Close();
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task CtrlC_ReturnsNull_LikeEsc()
    {
        // Ctrl+C backs out of a menu as ESC does (2026-09-17): nothing picked.
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Down, Keys.CtrlC);

        Assert.Null(await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None));

        Assert.True(pane.OverlayOpen);
        menu.Close();
    }

    [Fact]
    public async Task EditAsync_CtrlC_WithoutASelection_CancelsLikeEsc_WithOne_CopiesAndKeepsTheSlot()
    {
        // A settings field passes no interrupt hook: Ctrl+C is ESC there — unless text is selected,
        // then it is the copy and the field stays.
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        var copied = new List<string>();
        var input = new InputLine(pane, _keys, copyToClipboard: text => { copied.Add(text); return true; });
        var page = new MenuPage("Settings", ["one", "two"], "Enter = save · ESC = back");
        Push(Keys.CtrlC);

        Assert.IsType<InputResult.Cancelled>(await menu.EditAsync(page, 1, input, "old", allowEmpty: false, CancellationToken.None));
        Assert.Empty(copied);
        Assert.True(menu.IsOpen);

        Push(Keys.Ctrl(ConsoleKey.A), Keys.CtrlC, Keys.Enter);
        var submitted = await menu.EditAsync(page, 1, input, "old", allowEmpty: false, CancellationToken.None);

        Assert.Equal(["old"], copied);
        Assert.Equal("old", Assert.IsType<InputResult.Submitted>(submitted).Text);
        menu.Close();
    }

    [Fact]
    public async Task HomeEndAndPageKeys_MoveTheCursor_OtherKeysAreSwallowed()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        var rows = Enumerable.Range(1, 10).Select(i => "row " + i).ToArray();

        Push(Keys.End, Keys.Enter);
        Assert.Equal(new MenuPick(0, 9), await menu.PickAsync(Page(rows), 0, CancellationToken.None));

        Push(Keys.Home, Keys.Enter);
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(Page(rows), 9, CancellationToken.None));

        // A page is what the viewport shows (12 rows: 8 for the overlay, 2 for the title and the spacer, one for the more row = 5).
        Push(Keys.PageDown, Keys.Enter);
        Assert.Equal(new MenuPick(0, 5), await menu.PickAsync(Page(rows), 0, CancellationToken.None));

        Push(Keys.PageUp, Keys.Enter);
        Assert.Equal(new MenuPick(0, 2), await menu.PickAsync(Page(rows), 7, CancellationToken.None));

        Push(Keys.Char('x'), Keys.F4, Keys.Right, Keys.Tab, Keys.Enter);
        Assert.Equal(new MenuPick(0, 3), await menu.PickAsync(Page(rows), 3, CancellationToken.None));
        menu.Close();
    }

    [Fact]
    public async Task AHotkey_MovesTheCursorToItsRow_EnterStillPicks_RepeatsAndStrangersDoNothing()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        var rows = new[] { "No", "Yes", "Maybe" };
        var page = Page(rows) with { Hotkeys = new Dictionary<char, int> { ['n'] = 0, ['y'] = 1, ['q'] = 9 } };
        var highlighted = new List<int>();

        Push(Keys.Char('Y'), Keys.Enter);
        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(page, 0, CancellationToken.None, highlighted.Add));
        Assert.Equal([1], highlighted);

        // A repeated hotkey, a stranger, a control chord and one whose row is off the page leave the cursor where it is.
        highlighted.Clear();
        Push(Keys.Char('y'), Keys.Char('y'), Keys.Char('x'), Keys.Ctrl(ConsoleKey.Y), Keys.Char('q'), Keys.Enter);
        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(page, 1, CancellationToken.None, highlighted.Add));
        Assert.Empty(highlighted);

        Push(Keys.Char('y'), Keys.Char('n'), Keys.Enter);
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(page, 2, CancellationToken.None, highlighted.Add));
        Assert.Equal([1, 0], highlighted);

        // Without hotkeys the same characters are swallowed.
        Push(Keys.Char('y'), Keys.Enter);
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(Page(rows), 0, CancellationToken.None));
        menu.Close();
    }

    [Fact]
    public async Task Backspace_MovesTheCursorToTheBackspaceRow_EnterStillPicks_ARepeatAndAPageWithoutOneDoNothing()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        var rows = new[] { "(none)", "af_heart", "af_bella", "af_sky" };
        var page = Page(rows) with { BackspaceRow = 0 };
        var highlighted = new List<int>();

        Push(Keys.Backspace, Keys.Enter);
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(page, 3, CancellationToken.None, highlighted.Add));
        Assert.Equal([0], highlighted);
        Assert.Contains("▸ (none)", _console.Output);

        // On the row already: swallowed, no move reported.
        highlighted.Clear();
        Push(Keys.Backspace, Keys.Backspace, Keys.Enter);
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(page, 0, CancellationToken.None, highlighted.Add));
        Assert.Empty(highlighted);

        // A row off the page is ignored; without the opt-in Backspace is swallowed like any other key.
        Push(Keys.Backspace, Keys.Enter);
        Assert.Equal(new MenuPick(0, 2), await menu.PickAsync(page with { BackspaceRow = 9 }, 2, CancellationToken.None, highlighted.Add));
        Assert.Empty(highlighted);

        Push(Keys.Backspace, Keys.Enter);
        Assert.Equal(new MenuPick(0, 2), await menu.PickAsync(Page(rows), 2, CancellationToken.None, highlighted.Add));
        Assert.Empty(highlighted);
        menu.Close();
    }

    [Fact]
    public async Task ALongList_ScrollsBehindTheMoreRow()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        var rows = Enumerable.Range(1, 10).Select(i => "row " + i).ToArray();
        Push(Keys.Up, Keys.Enter);   // wraps to the last row

        Assert.Equal(new MenuPick(0, 9), await menu.PickAsync(Page(rows), 0, CancellationToken.None));

        // Opened: the first five rows and the more row, the eighth content row of a 12-row window.
        Assert.Contains("\n▸ row 1\n  row 2\n  row 3\n  row 4\n  row 5\n  " + MenuPane.MoreHint + "\n" + Rule(40), Output);
        // On the last row: the viewport moved just far enough, the more row still there.
        Assert.Contains("\n  row 6\n  row 7\n  row 8\n  row 9\n▸ row 10\n  " + MenuPane.MoreHint + "\n", Output);
        Assert.Equal(8, pane.OverlayRows);
        menu.Close();
    }

    [Fact]
    public void Viewport_IsPinned()
    {
        Assert.Equal((0, 3), MenuPane.Viewport(3, 2, 6, 0));       // fits: everything
        Assert.Equal((0, 6), MenuPane.Viewport(6, 0, 6, 0));
        Assert.Equal((0, 5), MenuPane.Viewport(10, 0, 6, 0));      // cut: one row for the more line
        Assert.Equal((0, 5), MenuPane.Viewport(10, 4, 6, 0));      // the cursor still in view
        Assert.Equal((1, 5), MenuPane.Viewport(10, 5, 6, 0));      // moved just far enough
        Assert.Equal((5, 5), MenuPane.Viewport(10, 9, 6, 0));
        Assert.Equal((3, 5), MenuPane.Viewport(10, 3, 6, 5));      // back up to the cursor
        Assert.Equal((2, 5), MenuPane.Viewport(10, 4, 6, 2));      // in view: the viewport stays
        Assert.Equal((5, 5), MenuPane.Viewport(10, 9, 6, 8));      // clamped to the end
        Assert.Equal((0, 0), MenuPane.Viewport(0, 0, 6, 0));
        Assert.Equal((0, 0), MenuPane.Viewport(3, 0, 0, 0));
        Assert.Equal((2, 1), MenuPane.Viewport(3, 2, 1, 0));       // one row of capacity: the cursor's
    }

    [Fact]
    public async Task Notices_ShowOnTheStatusLine_AndEnterClearsThem()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Escape);
        await menu.PickAsync(Page("one", "two"), 1, CancellationToken.None);
        int mark = Output.Length;

        menu.Notice("saved");
        menu.Warning("overridden");
        menu.Error("bad");

        // Nothing drawn yet: the next page carries them (a stale row is never shown with a fresh notice).
        Assert.Equal(mark, Output.Length);
        Assert.Equal(3, menu.Status.Count);

        // All three under the title, in the transcript's shapes, the spacer gone; the list under them. ESC keeps them.
        Push(Keys.Escape);
        await menu.PickAsync(Page("one", "two"), 1, CancellationToken.None);
        Assert.Contains("\n" + Titled("Settings") + "\n  · saved\n  ! overridden\n  ✗ bad\n  one\n▸ two\n", Output[mark..]);
        Assert.Equal(3, menu.Status.Count);

        // The next pick starts a new action: Enter clears the status.
        Push(Keys.Enter);
        await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None);
        Assert.Empty(menu.Status);

        menu.Close();
        Assert.Empty(menu.Status);
    }

    [Fact]
    public async Task EditAsync_DrawsTheSlotUnderTheList_OneEscCancels_EnterSubmitsOutsideTheFlow()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        var input = new InputLine(pane, _keys);
        var page = new MenuPage("Settings", ["one", "two"], "Enter = save · ESC = back");
        _console.Input.PushText("x");
        Push(Keys.Escape);

        var cancelled = await menu.EditAsync(page, 1, input, "old", allowEmpty: false, CancellationToken.None);

        Assert.IsType<InputResult.Cancelled>(cancelled);
        Assert.True(menu.IsOpen);
        Assert.True(pane.OverlayOpen);
        // The list with the edited row marked, the slot pre-filled under it, the edit hint.
        Assert.Contains(Rule(40) + "\n" + Titled("Settings") + "\n \n  one\n▸ two\n› \n" + Rule(40) + "\nEnter = save · ESC = back", Output);
        Assert.Contains("old", Output);
        Assert.Contains("oldx", Output);

        _console.Input.PushText("new");
        Push(Keys.Enter);
        int mark = Output.Length;
        var submitted = await menu.EditAsync(page, 1, input, "", allowEmpty: false, CancellationToken.None);

        Assert.Equal("new", Assert.IsType<InputResult.Submitted>(submitted).Text);
        Assert.DoesNotContain("› new", Output[mark..]);
        Assert.DoesNotContain(Theme.ToHex(Theme.Secondary) + " bold", Output[mark..]);

        menu.Close();
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    // ── Tabs ────────────────────────────────────────────────────────────────

    private static MenuPage Tabbed(int tab = 0) => MenuPage.Tabbed(
        "Settings",
        [new MenuTab("One", ["a", "b"]), new MenuTab("Two", ["c"]), new MenuTab("Three", ["d", "e", "f"])],
        tab,
        "Enter = pick · ←/→ tabs · ESC = back");

    [Fact]
    public void Tabbed_ShowsTheTabsRows_AndClampsTheTab()
    {
        var page = Tabbed(1);
        Assert.Equal(["c"], page.Rows);
        Assert.Equal(1, page.Tab);
        Assert.Equal(3, page.Tabs!.Count);
        Assert.Equal(["d", "e", "f"], Tabbed(7).Rows);
        Assert.Equal(0, Tabbed(-1).Tab);
        Assert.Throws<ArgumentException>(() => MenuPage.Tabbed("t", [], 0, "h"));
        Assert.Null(Page("one").Tabs);
    }

    [Fact]
    public async Task ATabbedPage_DrawsTheStrip_AndRightGoesForward_TabTooAndWraps()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Down, Keys.Right, Keys.Tab, Keys.Tab, Keys.Enter);

        MenuPick? picked = await menu.PickAsync(Tabbed(), 0, CancellationToken.None);

        // Right from One (on "b") lands on Two with the cursor on its first row; Tab twice wraps past Three to One.
        Assert.Equal(new MenuPick(0, 0), picked);
        Assert.Contains(Rule(40) + "\n" + Titled("Settings   One    Two    Three ") + "\n \n▸ a\n  b\n \n" + Rule(40) + "\nEnter = pick · ←/→ tabs · ESC = back\n", Output);   // padded to Three's height (2026-10-01)
        Assert.Contains("\n" + Titled("Settings   One    Two    Three ") + "\n \n  a\n▸ b\n", Output);
        Assert.Contains("\n" + Titled("Settings   One    Two    Three ") + "\n \n▸ c\n \n \n" + Rule(40), Output);
        Assert.Contains("\n" + Titled("Settings   One    Two    Three ") + "\n \n▸ d\n  e\n  f\n", Output);
        Assert.True(menu.IsOpen);
        menu.Close();
    }

    [Fact]
    public void TabBodyRows_IsTheTallestTabsCaptionAndRows_NoneForAOneListPage()
    {
        Assert.Equal(3, MenuPane.TabBodyRows(Tabbed(), 40));
        // A caption counts: the question pane's tabs differ by theirs (a word per row at width 5).
        var captioned = MenuPage.Tabbed("Q", [new MenuTab("A", ["x"]) { Caption = "one two" }, new MenuTab("B", ["y", "z"])], 0, "h");
        Assert.Equal(3, MenuPane.TabBodyRows(captioned, 5));
        Assert.Equal(2, MenuPane.TabBodyRows(captioned, 40));
        Assert.Equal(0, MenuPane.TabBodyRows(Page("one", "two"), 40));
    }

    [Fact]
    public async Task EveryTab_KeepsTheTallestTabsHeight_TheBlankUnderTheList()
    {
        // 2026-10-01, the user's ask: the pane jumped up and down as one tabbed through /settings.
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Right, Keys.Right, Keys.Escape);

        Assert.Null(await menu.PickAsync(Tabbed(), 0, CancellationToken.None));

        string strip = Titled("Settings   One    Two    Three ");
        Assert.Contains("\n" + strip + "\n \n▸ a\n  b\n \n" + Rule(40), Output);       // One: two rows and a blank
        Assert.Contains("\n" + strip + "\n \n▸ c\n \n \n" + Rule(40), Output);         // Two: one row and two
        Assert.Contains("\n" + strip + "\n \n▸ d\n  e\n  f\n" + Rule(40), Output);     // Three, the tallest: none
        menu.Close();
    }

    [Fact]
    public async Task APage_WithoutTabs_IsNotPadded()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Escape);

        Assert.Null(await menu.PickAsync(Page("one"), 0, CancellationToken.None));

        Assert.Contains("\n" + Titled("Settings") + "\n \n▸ one\n" + Rule(40), Output);
        menu.Close();
    }

    private static string[] Numbered(int count) => Enumerable.Range(0, count).Select(i => "r" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    [Theory]
    // Menus max height on a 24-row window (2026-10-01): the title and the spacer, then the rest of the content rows the
    // height leaves — half 12 − 3 = 9, three-quarters 18 − 3 = 15, full-screen 24 − 4 = 20 — the more row the last.
    [InlineData("half-screen", 6)]
    [InlineData("three-quarters", 12)]
    [InlineData("full-screen", 17)]
    public async Task MenusMaxHeight_CapsThePane_AndTheListScrollsInside(string height, int shown)
    {
        _console.Profile.Height = 24;
        using var pane = Pane();
        pane.MenuHeight = () => height;
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Escape);

        Assert.Null(await menu.PickAsync(Page(Numbered(30)), 0, CancellationToken.None));

        string rows = string.Concat(Numbered(shown).Select((r, i) => (i == 0 ? "▸ " : "  ") + r + "\n"));
        Assert.Contains("\n" + Titled("Settings") + "\n \n" + rows + "  " + MenuPane.MoreHint + "\n" + Rule(40), Output);
        menu.Close();
    }

    [Fact]
    public async Task ATabTallerThanTheCap_Scrolls_AndTheOthersFillTheCap()
    {
        _console.Profile.Height = 24;
        using var pane = Pane();
        pane.MenuHeight = () => "half-screen";
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Right, Keys.Escape);
        var page = MenuPage.Tabbed("Settings", [new MenuTab("Long", Numbered(30)), new MenuTab("Short", ["c"])], 0, "hint");

        Assert.Null(await menu.PickAsync(page, 0, CancellationToken.None));

        // Half of 24 less the rules and hint: 9 content rows, the strip and the spacer two of them, so seven under them.
        string strip = Titled("Settings   Long    Short ");
        Assert.Contains("\n" + strip + "\n \n▸ r0\n  r1\n  r2\n  r3\n  r4\n  r5\n  " + MenuPane.MoreHint + "\n" + Rule(40), Output);
        Assert.Contains("\n" + strip + "\n \n▸ c\n \n \n \n \n \n \n" + Rule(40), Output);
        menu.Close();
    }

    [Fact]
    public async Task LeftAndShiftTab_GoBack_AndWrap()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Left, Keys.Down, Keys.ShiftTab, Keys.Enter);

        MenuPick? picked = await menu.PickAsync(Tabbed(), 1, CancellationToken.None);

        // Left from One wraps to Three; Down there; Shift+Tab back to Two, its first row.
        Assert.Equal(new MenuPick(1, 0), picked);
        Assert.Contains("\n▸ d\n  e\n  f\n", Output);
        Assert.Contains("\n  d\n▸ e\n  f\n", Output);
        Assert.Contains("\n▸ c\n \n \n" + Rule(40), Output);
        menu.Close();
    }

    [Fact]
    public async Task ATabbedPage_ReturnsTheTabItEndedOn_WithTheRowWithinIt()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Right, Keys.Right, Keys.End, Keys.Enter);

        Assert.Equal(new MenuPick(2, 2), await menu.PickAsync(Tabbed(), 0, CancellationToken.None));

        // Opened on the tab asked for, with the cursor where the caller left it.
        Push(Keys.Enter);
        Assert.Equal(new MenuPick(2, 1), await menu.PickAsync(Tabbed(2), 1, CancellationToken.None));
        Assert.Contains("\n  d\n▸ e\n  f\n", Output);
        menu.Close();
    }

    [Fact]
    public async Task ASingleTab_IgnoresTheTabKeys_AndTheStripStillShows()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        var page = MenuPage.Tabbed("Settings", [new MenuTab("Only", ["a", "b"])], 0, "hint");
        Push(Keys.Down, Keys.Right, Keys.Tab, Keys.Left, Keys.ShiftTab, Keys.Enter);

        int mark = Output.Length;
        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(page, 0, CancellationToken.None));

        Assert.Contains("\n" + Titled("Settings   Only ") + "\n \n▸ a\n  b\n", Output);
        // Two draws: the open and the Down; none of the four tab keys redrew.
        Assert.Equal(2, CountOf(Output[mark..], "\n" + Titled("Settings   Only ") + "\n"));
        menu.Close();
    }

    [Fact]
    public async Task SwitchingTabs_DropsTheStatusLines()
    {
        // A tab switch ends the action that wrote the status (2026-09-20, the user's ask): the
        // other tab draws with the spacer, and a click on a tab title drops it the same way.
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        menu.Notice("saved");
        Push(Keys.Right, Keys.Enter);

        Assert.Equal(new MenuPick(1, 0), await menu.PickAsync(Tabbed(), 0, CancellationToken.None));

        Assert.Contains("\n" + Titled("Settings   One    Two    Three ") + "\n  · saved\n▸ a\n  b\n", Output);
        Assert.Contains("\n" + Titled("Settings   One    Two    Three ") + "\n \n▸ c\n", Output);
        Assert.DoesNotContain("  · saved\n▸ c\n", Output);
        Assert.Empty(menu.Status);
        menu.Close();
    }

    [Fact]
    public async Task EditAsync_OnATabbedPage_KeepsTheStrip()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        var input = new InputLine(pane, _keys);
        Push(Keys.Char('x'), Keys.Enter);

        var result = await menu.EditAsync(Tabbed(2) with { Hint = "Enter = save · ESC = back" }, 1, input, "", allowEmpty: false, CancellationToken.None);

        Assert.Equal("x", Assert.IsType<InputResult.Submitted>(result).Text);
        Assert.Contains(Rule(40) + "\n" + Titled("Settings   One    Two    Three ") + "\n \n  d\n▸ e\n  f\n› \n" + Rule(40) + "\nEnter = save · ESC = back", Output);
        menu.Close();
    }

    /// <summary>"Settings   One    Two    Three ": One is columns 10–14, Two 17–21, Three 24–32; the strip is buffer row 100.</summary>
    [Fact]
    public async Task AClickOnATabTitle_SwitchesToIt_TheLabelAndTheGapsDoNothing()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.Push(Keys.Down);                           // "b" on One
        input.PushClick(3, 100);                         // the label
        input.PushClick(22, 100);                        // the gap after Two
        input.PushClick(18, 100, MouseButton.Right);     // a right click on Two
        input.PushClick(18, 100);                        // Two: its first row
        input.PushClick(18, 100);                        // again: the active tab, nothing to draw
        input.PushClick(28, 100);                        // Three
        input.PushClick(0, 103);                         // "e": the rows still start under the spacer
        input.Push(Keys.Enter);

        int mark = Output.Length;
        Assert.Equal(new MenuPick(2, 1), await menu.PickAsync(Tabbed(), 0, CancellationToken.None));

        // Five draws: the open, Down, Two, Three, the row click.
        Assert.Equal(5, CountOf(Output[mark..], "\n" + Titled("Settings   One    Two    Three ") + "\n"));
        Assert.Contains("\n  a\n▸ b\n", Output);
        Assert.Contains("\n \n▸ c\n \n \n" + Rule(40), Output);
        Assert.Contains("\n \n▸ d\n  e\n  f\n", Output);
        Assert.Contains("\n  d\n▸ e\n  f\n", Output);
        menu.Close();
    }

    /// <summary>
    /// At 30 columns "Settings   One    Two    Three " does not fit (2026-09-27): Three takes a second strip
    /// row at column 10, under One; the × stays on the first; the spacer moves to 102 and the rows start at
    /// 103, where a click still lands on the row it shows.
    /// </summary>
    [Fact]
    public async Task AStripOnTwoRows_LinesUpUnderTheFirstTitle_AndBothRowsAndTheListTakeClicks()
    {
        _console.Profile.Width = 30;
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.PushClick(4, 101);                         // the indent under the label: nothing
        input.PushClick(12, 101);                        // Three
        input.PushClick(0, 104);                         // "e"
        input.Push(Keys.Enter);

        int mark = Output.Length;
        Assert.Equal(new MenuPick(2, 1), await menu.PickAsync(Tabbed(), 0, CancellationToken.None));

        string strip = "\n" + Titled("Settings   One    Two ", 30) + "\n           Three \n \n";
        Assert.Equal(3, CountOf(Output[mark..], strip));     // the open, Three, the row click
        Assert.Contains(strip + "▸ d\n  e\n  f\n", Output);
        Assert.Contains(strip + "  d\n▸ e\n  f\n", Output);
        menu.Close();
    }

    [Fact]
    public async Task AClickOnAFlatPagesTitle_ChangesNothing()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 100);
        input.Push(Keys.Enter);

        int mark = Output.Length;
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None));
        Assert.Equal(1, CountOf(Output[mark..], "\n" + Titled("Settings") + "\n"));
        menu.Close();
    }

    /// <summary>
    /// A one-list page's buttons (2026-09-21): drawn after the title as a strip nobody is on
    /// ("Settings   ⊠ clear    ✎ edit "; ⊠ clear is columns 11–19, ✎ edit 22–29); a click on one
    /// returns the pick with Button set and the cursor's row, its key (either case) the same, the
    /// status cleared; the label, a gap and a keyless button's letter are nothing. A tabbed page
    /// ignores them.
    /// </summary>
    [Fact]
    public async Task AClickOnAButton_OrItsKey_ReturnsTheButton_TheLabelAndTheGapsDoNothing()
    {
        var page = Page("one", "two") with { Buttons = [new MenuButton("⊠ clear", 'c'), new MenuButton("✎ edit", null)] };
        Assert.Equal(InfoPane.TabStripMarkup("Settings", ["⊠ clear", "✎ edit"], -1), MenuPane.TopMarkup(page));
        Assert.Equal(MenuPane.TitleMarkup("Settings"), MenuPane.TopMarkup(Page("one")));
        Assert.Equal(MenuPane.TitleMarkup("Settings"), MenuPane.TopMarkup(Page("one") with { Buttons = [] }));
        Assert.Equal(0, MenuPane.ButtonFor(page.Buttons!, 'c'));
        Assert.Equal(0, MenuPane.ButtonFor(page.Buttons!, 'C'));
        Assert.Null(MenuPane.ButtonFor(page.Buttons!, 'e'));

        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        menu.Notice("a status");
        input.Push(Keys.Down);                           // the cursor on "two"
        input.PushClick(3, 100);                         // the label
        input.PushClick(20, 100);                        // the gap
        input.Push(Keys.Char('e'));                      // a keyless button's letter: swallowed
        input.PushClick(25, 100);                        // ✎ edit

        int mark = Output.Length;
        Assert.Equal(new MenuPick(0, 1, Button: 1), await menu.PickAsync(page, 0, CancellationToken.None));
        Assert.Contains("\n" + Titled("Settings   ⊠ clear    ✎ edit ") + "\n  · a status\n  one\n▸ two\n", Output[mark..]);
        Assert.Empty(menu.Status);

        input.PushClick(12, 100);                        // ⊠ clear
        Assert.Equal(new MenuPick(0, 1, Button: 0), await menu.PickAsync(page, 1, CancellationToken.None));
        input.Push(Keys.Char('C'));
        Assert.Equal(new MenuPick(0, 0, Button: 0), await menu.PickAsync(page, 0, CancellationToken.None));

        // A tabbed page: the strip is the tabs', the key a plain swallow, Enter the row.
        input.Push(Keys.Char('c'), Keys.Enter);
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(Tabbed() with { Buttons = page.Buttons }, 0, CancellationToken.None));
        menu.Close();
    }

    /// <summary>The × at the first row's right edge (columns 37–39 at width 40) is ESC: null, the pane still open; a click just left of it is the title, nothing (2026-09-18).</summary>
    [Fact]
    public async Task AClickOnTheCloseGlyph_IsEsc_OnAFlatAndATabbedPage()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.PushClick(36, 100);                        // the last cell of the padding: nothing
        input.PushClick(38, 100);                        // the glyph
        input.Push(Keys.Enter);                          // never read

        Assert.Null(await menu.PickAsync(Page("one", "two"), 1, CancellationToken.None));
        Assert.True(pane.OverlayOpen);
        Assert.True(input.IsAvailable);
        input.Read();                                    // the Enter

        input.Push(Keys.Down);
        input.PushClick(39, 100);                        // the last column counts too
        Assert.Null(await menu.PickAsync(Tabbed(), 0, CancellationToken.None));
        Assert.Contains("\n" + Titled("Settings   One    Two    Three ") + "\n \n  a\n▸ b\n", Output);
        Assert.True(pane.OverlayOpen);
        menu.Close();
        Assert.False(pane.OverlayOpen);
    }

    /// <summary>Under a typed edit the line reads the clicks: the × is the ESC key there too — Cancelled, the saved value kept (2026-09-18).</summary>
    [Fact]
    public async Task EditAsync_AClickOnTheCloseGlyph_CancelsLikeEsc()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var line = new InputLine(pane, keys);
        // The overlay's four rows (title, spacer, one, two) sit above the input row the cursor is on: the title at 96.
        input.Push(Keys.Char('x'));
        input.PushClick(38, 96);
        input.Push(Keys.Enter);                          // never read

        Assert.IsType<InputResult.Cancelled>(await menu.EditAsync(Page("one", "two"), 1, line, "old", allowEmpty: false, CancellationToken.None));
        Assert.True(menu.IsOpen);
        Assert.True(input.IsAvailable);
        input.Read();

        // A click on the title's text, or on the row above the slot, is not the glyph: the edit goes on and Enter submits.
        input.PushClick(3, 96);
        input.PushClick(38, 99);
        input.Push(Keys.Enter);
        Assert.Equal("old", Assert.IsType<InputResult.Submitted>(await menu.EditAsync(Page("one", "two"), 1, line, "old", allowEmpty: false, CancellationToken.None)).Text);
        menu.Close();
    }

    // ── The mouse ───────────────────────────────────────────────────────────

    /// <summary>A pane whose cursor row the geometry reports (the click's frame) over a scripted source that carries clicks.</summary>
    private (ScreenPane Pane, ScriptedInput Input, KeySource Keys) ClickablePane(int cursorTop)
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => cursorTop), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        return (pane, input, new KeySource(input, TimeSpan.FromMilliseconds(1)));
    }

    /// <summary>The overlay's first row is the cursor's (buffer row 100): title 100, spacer 101, the rows from 102.</summary>
    [Fact]
    public async Task ALeftClickOnARow_MovesTheCursorThere_EnterPicksIt()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.PushClick(7, 104);
        input.Push(Keys.Enter);

        MenuPick? picked = await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None);

        Assert.Equal(new MenuPick(0, 2), picked);
        Assert.Contains("\n▸ one\n  two\n  three\n", Output);
        Assert.Contains("\n  one\n  two\n▸ three\n", Output);
        menu.Close();
    }

    /// <summary>The highlight hook hears every row move that lands somewhere new: the keys and a click on another row; never the opening row, Enter, a click on the cursor's own row or a wrap onto the same one-row list.</summary>
    [Fact]
    public async Task TheHighlightHook_HearsEveryRowMove_NotTheOpeningRowOrEnter()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var heard = new List<int>();
        input.Push(Keys.Down, Keys.Down, Keys.Up, Keys.End);
        input.PushClick(0, 102);                         // "one": another row
        input.OnWait = () =>
        {
            // Well past DoubleClick.Interval: the cursor's own row again is nothing, not the pair.
            _time.Advance(TimeSpan.FromSeconds(1));
            input.PushClick(0, 102);
            input.Push(Keys.Enter);
            input.OnWait = null;
        };

        MenuPick? picked = await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None, heard.Add);

        Assert.Equal(new MenuPick(0, 0), picked);
        Assert.Equal([1, 2, 1, 2, 0], heard);

        heard.Clear();
        input.Push(Keys.Down, Keys.Up, Keys.Escape);
        Assert.Null(await menu.PickAsync(Page("only"), 0, CancellationToken.None, heard.Add));
        Assert.Empty(heard);
        menu.Close();
    }

    /// <summary>The title, the spacer, the rules, a right click, a drag and a paste are nothing; a click on the cursor's own row draws nothing new. (A rule click is off the pane: the right click between the two keeps them from pairing, 2026-09-18.)</summary>
    [Fact]
    public async Task AClickOffTheRows_ARightClick_ADrag_APaste_ChangeNothing()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.PushClick(0, 100);                         // the title
        input.PushClick(0, 101);                         // the spacer
        input.PushClick(0, 99);                          // the upper rule
        input.PushClick(0, 104, MouseButton.Right);      // a right click on "three"
        input.PushClick(0, 105);                         // the lower rule
        input.PushDrag(0, 104);                          // a drag over "three"
        input.PushPaste("two\r\nthree\r\n");                // a paste has no slot here: the menu stays, nothing is picked
        input.PushClick(0, 102);                         // the cursor's own row
        input.Push(Keys.Enter);

        int mark = Output.Length;
        MenuPick? picked = await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None);

        Assert.Equal(new MenuPick(0, 0), picked);
        // One draw (the open), nothing redrawn for any of the eight events.
        Assert.Equal(1, CountOf(Output[mark..], "\n▸ one\n"));
        Assert.DoesNotContain("▸ three", Output);
        menu.Close();
    }

    /// <summary>A cut list: the click names the shown row, which is the viewport's row, not the list's.</summary>
    [Fact]
    public async Task AClickOnAScrolledList_PicksTheRowInView()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var rows = Enumerable.Range(1, 10).Select(i => "row " + i).ToArray();
        // Opened on row 10: the viewport shows rows 6–10 (buffer rows 102–106), the more row at 107.
        input.PushClick(0, 103);                         // the second shown row = "row 7"
        input.PushClick(0, 107);                         // the more row: nothing
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 6), await menu.PickAsync(Page(rows), 9, CancellationToken.None));
        Assert.Contains("\n  row 6\n▸ row 7\n  row 8\n", Output);
        menu.Close();
    }

    /// <summary>Two clicks on the same row within DoubleClick.Interval are Enter: the first moves the highlight (reported once), the second picks — Enter's pick, never Space's toggle, the pane still open (2026-09-18).</summary>
    [Fact]
    public async Task ADoubleClickOnARow_PicksIt_AsEnterWould()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var heard = new List<int>();
        input.PushClick(7, 104);                         // "three": the highlight moves
        input.PushClick(9, 104);                         // again, a few cells over: the pair
        input.Push(Keys.Escape);                         // never read: the pick ends the read

        MenuPick? picked = await menu.PickAsync(Page("one", "two", "three") with { SpaceToggles = true }, 0, CancellationToken.None, heard.Add);

        Assert.Equal(new MenuPick(0, 2), picked);
        Assert.Equal([2], heard);
        Assert.True(pane.OverlayOpen);
        Assert.Contains("\n  one\n  two\n▸ three\n", Output);
        Assert.True(input.IsAvailable);                  // the ESC still queued
        menu.Close();
    }

    /// <summary>A second click past the interval, on another row, or with a key between the two is a first again: the highlight moves, Enter still picks.</summary>
    [Fact]
    public async Task TwoClicksApart_OnDifferentRows_OrAroundAKey_OnlyMoveTheCursor()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var heard = new List<int>();
        input.PushClick(0, 103);                         // "two"
        input.OnWait = () =>
        {
            _time.Advance(DoubleClick.Interval + TimeSpan.FromMilliseconds(1));
            input.PushClick(0, 103);                     // too late: a first again
            input.PushClick(0, 104);                     // "three": another row
            input.PushClick(0, 103);                     // "two": another row again
            input.Push(Keys.Up);                         // "one": a key ends the pair
            input.PushClick(0, 103);                     // "two": a first
            input.Push(Keys.Enter);
            input.OnWait = null;
        };

        MenuPick? picked = await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None, heard.Add);

        Assert.Equal(new MenuPick(0, 1), picked);
        Assert.Equal([1, 2, 1, 0, 1], heard);
        menu.Close();
    }

    /// <summary>Two left clicks off the pane — the upper rule then the hint row — within the interval dismiss it (later on 2026-09-18): null with the pane still drawn, ScreenPane.Dismissed set, the next PickAsync null at once without a draw or a read, and Close clearing it so a fresh visit reads again.</summary>
    [Fact]
    public async Task TwoClicksOffThePane_WithinTheInterval_DismissIt_TheNextPickIsNullWithoutADraw_AndCloseClearsIt()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.PushClick(3, 99);                          // the upper rule
        input.PushClick(20, 106);                        // the hint row: another part (later on 2026-09-21), no pair
        input.PushClick(20, 106);                        // the hint row again: the pair
        input.Push(Keys.Escape);                         // never read

        Assert.Null(await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        Assert.True(pane.Dismissed);
        Assert.True(pane.OverlayOpen);
        Assert.True(input.IsAvailable);
        // The dismissing click's part, once: the hint row's blanks (nothing under the hint's zones: no trailer drawn).
        Assert.Equal(new ScreenPane.OffPaneHit(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1), null), pane.TakeDismissHit());
        Assert.Null(pane.TakeDismissHit());

        // A host re-showing its parent list finds the signal: null, no draw, the ESC still queued.
        int mark = Output.Length;
        Assert.Null(await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None));
        Assert.Equal(mark, Output.Length);
        Assert.True(input.IsAvailable);

        menu.Close();
        Assert.False(pane.Dismissed);
        Assert.False(pane.OverlayOpen);

        // A fresh visit reads again.
        input.Read();                                    // the ESC
        input.Push(Keys.Enter);
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None));
        menu.Close();
    }

    /// <summary>A click off the pane pairs with nothing else: one on the transcript then one on a row moves the highlight, one past the interval or around a key is a first again; a single one is nothing.</summary>
    [Fact]
    public async Task AClickOffThePane_ThenOneOnARow_PastTheInterval_OrAroundAKey_IsNoPair()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var heard = new List<int>();
        input.PushClick(5, 50);                          // the transcript: a first
        input.PushClick(0, 104);                         // "three": a row, no pair
        input.PushClick(5, 50);                          // a first again
        input.OnWait = () =>
        {
            _time.Advance(DoubleClick.Interval + TimeSpan.FromMilliseconds(1));
            input.PushClick(5, 50);                      // too late: a first
            input.Push(Keys.Up);                         // "two": a key ends the pair
            input.PushClick(5, 50);                      // a first
            input.Push(Keys.Enter);
            input.OnWait = null;
        };

        MenuPick? picked = await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None, heard.Add);

        Assert.Equal(new MenuPick(0, 1), picked);
        Assert.Equal([2, 1], heard);
        Assert.False(pane.Dismissed);
        menu.Close();
    }

    /// <summary>Under a typed edit the line reads the clicks: two off the pane are the dismissal there too — Cancelled with Dismissed set, so the host above backs out (later on 2026-09-18); a single one is nothing.</summary>
    [Fact]
    public async Task EditAsync_TwoClicksOffThePane_CancelAndDismiss()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var line = new InputLine(pane, keys);
        // The overlay's four rows sit above the input row the cursor is on: the title at 96, the upper rule at 95, the hint row at 102.
        input.PushClick(3, 95);
        input.Push(Keys.Enter);                          // a single click: the edit goes on
        Assert.Equal("old", Assert.IsType<InputResult.Submitted>(await menu.EditAsync(Page("one", "two"), 1, line, "old", allowEmpty: false, CancellationToken.None)).Text);
        Assert.False(pane.Dismissed);

        input.PushClick(30, 102);                        // the hint row: its own part (later on 2026-09-21)
        input.PushClick(3, 40);                          // the transcript: another part, no pair — a first
        input.PushClick(3, 40);                          // the transcript again: the pair
        input.Push(Keys.Enter);                          // never read
        Assert.IsType<InputResult.Cancelled>(await menu.EditAsync(Page("one", "two"), 1, line, "old", allowEmpty: false, CancellationToken.None));
        Assert.True(pane.Dismissed);
        Assert.Null(pane.TakeDismissHit());              // the transcript names no part
        Assert.True(input.IsAvailable);
        Assert.Null(await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None));
        menu.Close();
        Assert.False(pane.Dismissed);
    }

    /// <summary>A command chord in the list (2026-10-01, the user's ask): null with every level dismissed and the chord kept for the screen, the keys after it unread.</summary>
    [Fact]
    public async Task AChord_DismissesThePane_AndKeepsItsCommandForTheScreen()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.Push(Keys.Down, Keys.CtrlAltS, Keys.Enter);

        Assert.Null(await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        Assert.True(pane.Dismissed);
        Assert.Equal("/skills", pane.TakeDismissChord());
        Assert.Null(pane.TakeDismissHit());               // a chord is no click
        Assert.True(input.IsAvailable);                  // the Enter, never read
        Assert.Null(await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None));
        menu.Close();
        Assert.False(pane.Dismissed);
    }

    /// <summary>
    /// The chord outlives the next overlay (2026-10-01, the review's finding: a handler that showed another pane after the
    /// first closed dropped it, the screen then running nothing), where a click's part does not; taken once.
    /// </summary>
    [Fact]
    public async Task AChord_OutlivesTheNextPane_AndIsTakenOnce()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.Push(Keys.CtrlAltS);
        Assert.Null(await menu.PickAsync(Page("one"), 0, CancellationToken.None));
        menu.Close();

        input.Push(Keys.Enter);                          // the handler's next pane, answered
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(Page("two"), 0, CancellationToken.None));
        menu.Close();

        Assert.Equal("/skills", pane.TakeDismissChord());
        Assert.Null(pane.TakeDismissChord());
    }

    /// <summary>The pane read with no overlay open (2026-10-01): a chord passes as read, nothing dismissed or kept.</summary>
    [Fact]
    public async Task ReadPaneInputAsync_WithNoOverlay_PassesAChordAsRead()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        input.Push(Keys.CtrlAltC);

        Assert.Equal(new InputEvent.Key(Keys.CtrlAltC), await keys.ReadPaneInputAsync(pane, CancellationToken.None));
        Assert.False(pane.Dismissed);
        Assert.Null(pane.TakeDismissChord());
    }

    /// <summary>The kill switch in a list (2026-10-01): the hook runs and the list reads on, nothing dismissed, no chord kept.</summary>
    [Fact]
    public async Task TheKillSwitch_LeavesTheListReading()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        int kills = 0;
        keys.KillSwitch = () => kills++;
        input.Push(Keys.CtrlAlt(ConsoleKey.X), Keys.Down, Keys.Enter);

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        Assert.Equal(1, kills);
        Assert.False(pane.Dismissed);
        Assert.Null(pane.TakeDismissChord());
        menu.Close();
    }

    /// <summary>A chord the screen does in place (<c>/perf</c>, <c>/tb</c>) leaves the list reading; under a tool's question every chord is nobody's.</summary>
    [Fact]
    public async Task AChord_DoneInPlace_OrSuppressed_LeavesTheListReading()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var done = new List<string>();
        pane.ChordInPlace = line =>
        {
            done.Add(line);
            return line == "/perf";
        };
        input.Push(Keys.CtrlF, Keys.Down, Keys.Enter);

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None));
        Assert.Equal(["/perf"], done);
        Assert.False(pane.Dismissed);

        using (pane.SuppressChords())
        {
            input.Push(Keys.CtrlAltC, Keys.CtrlF, Keys.Enter);
            Assert.Equal(new MenuPick(0, 2), await menu.PickAsync(Page("one", "two", "three"), 2, CancellationToken.None));
        }

        Assert.Equal(["/perf"], done);                   // never asked while suppressed
        Assert.False(pane.Dismissed);
        Assert.Null(pane.TakeDismissChord());
        menu.Close();
    }

    /// <summary>A chord under a typed edit: Cancelled with every level dismissed and the chord kept, as the double-click off the pane.</summary>
    [Fact]
    public async Task EditAsync_AChord_CancelsAndDismisses_KeepingIt()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var line = new InputLine(pane, keys);
        input.Push(Keys.Char('x'), Keys.CtrlAltN, Keys.Enter);

        Assert.IsType<InputResult.Cancelled>(await menu.EditAsync(Page("one", "two"), 1, line, "old", allowEmpty: false, CancellationToken.None));
        Assert.True(pane.Dismissed);
        Assert.Equal("/new", pane.TakeDismissChord());
        Assert.True(input.IsAvailable);                  // the Enter, never read
        menu.Close();
    }

    /// <summary>A double-click on a tab's title switches once and picks nothing; a click on the strip then one on a row are no pair either.</summary>
    [Fact]
    public async Task ADoubleClickOnATabTitle_SwitchesOnce_AndPicksNothing()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        input.PushClick(18, 100);                        // Two
        input.PushClick(18, 100);                        // again: nothing
        input.PushClick(0, 102);                         // "c": the highlight's own row, a first
        input.Push(Keys.Escape);

        int mark = Output.Length;
        Assert.Null(await menu.PickAsync(Tabbed(), 0, CancellationToken.None));

        // Two draws: the open and Two.
        Assert.Equal(2, CountOf(Output[mark..], "\n" + Titled("Settings   One    Two    Three ") + "\n"));
        Assert.Contains("\n \n▸ c\n \n \n" + Rule(40), Output);
        menu.Close();
    }

    /// <summary>A notch on the list is the arrow's move — down a row for a notch towards the user — reported like one, and it never wraps; Enter picks where it stopped.</summary>
    [Fact]
    public async Task TheWheel_MovesTheCursorARowANotch_AndStopsAtTheEnds()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var menu = new MenuPane(pane, keys);
        var highlighted = new List<int>();
        // The overlay's rows are 100 (the title) to 104; a notch anywhere on them is the list's (2026-09-26: off them it scrolls the transcript).
        input.PushWheel(1, 0, 102);     // at the top: nothing, no wrap
        input.PushWheel(-1, 30, 100);   // "two", from the title row too
        input.PushWheel(-3, 0, 104);    // three notches, clamped at "three"
        input.PushWheel(-1, 0, 102);    // at the end: nothing
        input.PushWheel(1, 0, 103);     // back to "two"
        input.Push(Keys.Enter);

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 0, CancellationToken.None, highlighted.Add));
        Assert.Equal([1, 2, 1], highlighted);
        Assert.Contains("\n  one\n  two\n▸ three\n", Output);
        menu.Close();

        // An empty page: a notch is nothing, and ESC still leaves.
        input.PushWheel(-1, 0, 100).Push(Keys.Escape);
        Assert.Null(await menu.PickAsync(Page(), 0, CancellationToken.None, highlighted.Add));
        Assert.Equal([1, 2, 1], highlighted);
        menu.Close();
    }

    /// <summary>
    /// A notch off the pane — over the transcript above it (2026-09-26, the user's ask: the chat read back while the
    /// plan's approval waits) — scrolls the transcript, never the list: no row move is reported, and Enter picks the opening row.
    /// </summary>
    [Fact]
    public async Task TheWheel_OffThePane_ScrollsTheTranscript_AndLeavesTheCursor()
    {
        _console.Profile.Height = 20;
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Open();
        pane.Show();
        for (int i = 1; i <= 40; i++)
        {
            pane.Write(new Markup("L" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine));
        }

        var menu = new MenuPane(pane, keys);
        var highlighted = new List<int>();
        int below = -1;
        input.PushWheel(2, 5, 90);      // two notches away from the user, over the transcript: up six rows
        input.OnWait = () =>
        {
            if (below < 0)
            {
                below = pane.RowsBelow;
                input.PushWheel(-2, 5, 99);   // back down, from the upper rule: the bottom again
                input.OnWait = () => input.Push(Keys.Enter);
            }
        };

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two", "three"), 1, CancellationToken.None, highlighted.Add));
        Assert.Equal(2 * ScreenPane.WheelRows, below);
        Assert.False(pane.Scrolled);
        Assert.Empty(highlighted);
        menu.Close();
    }

    /// <summary>The list takes the mouse on every pick and hands it back on Close; an edit between two picks takes nothing itself (the input line owns the mouse by its draft).</summary>
    [Fact]
    public async Task TheMouse_IsTakenPerPick_AndHandedBackOnClose()
    {
        var (pane, input, keys) = ClickablePane(cursorTop: 100);
        using var _ = pane;
        pane.Show();
        var owned = new List<bool>();
        var menu = new MenuPane(pane, keys, owned.Add);
        var line = new InputLine(pane, keys);
        var page = Page("one", "two");

        input.Push(Keys.Enter);
        Assert.Equal(new MenuPick(0, 0), await menu.PickAsync(page, 0, CancellationToken.None));
        Assert.Equal(new[] { true }, owned);

        input.Push(Keys.Char('x'), Keys.Enter);
        Assert.IsType<InputResult.Submitted>(await menu.EditAsync(page, 0, line, "", allowEmpty: false, CancellationToken.None));
        Assert.Equal(new[] { true }, owned);   // the line has no hook of its own (the screen holds the mouse, 2026-09-17)

        input.Push(Keys.Escape);
        Assert.Null(await menu.PickAsync(page, 0, CancellationToken.None));
        Assert.Equal(new[] { true, true }, owned);   // the list takes it again

        menu.Close();
        Assert.Equal(new[] { true, true, false }, owned);
        menu.Close();   // a second Close is nothing
        Assert.Equal(3, owned.Count);
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task NoKeyboard_ReturnsNull()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);

        // A drained TestConsoleInput throws: the read ran dry, nothing is thrown on.
        Assert.Null(await menu.PickAsync(Page("one"), 0, CancellationToken.None));
        menu.Close();
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task ACancelledToken_ReturnsNull()
    {
        using var pane = Pane();
        pane.Show();
        var input = new ScriptedInput();
        var menu = new MenuPane(pane, new KeySource(input, TimeSpan.FromMilliseconds(1)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await menu.PickAsync(Page("one"), 0, cts.Token));
        menu.Close();
    }

    [Fact]
    public async Task ADisabledPane_HostsNothing()
    {
        using var pane = Pane(geometry: false);
        var menu = new MenuPane(pane, _keys);
        Assert.False(menu.Enabled);
        Push(Keys.Enter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => menu.PickAsync(Page("one"), 0, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => menu.EditAsync(Page("one"), 0, new InputLine(pane, _keys), "", false, CancellationToken.None));
        Assert.True(_keys.IsKeyAvailable());
        Assert.Equal("", Output);
        menu.Close();   // nothing to close: no throw
    }

    // ── The question pane's additions (2026-09-15): a caption, Space, per-tab cursors, hints and captions ──

    private static MenuPage Questions(int tab = 0) => MenuPage.Tabbed(
        "Questions",
        [
            new MenuTab("One", ["( ) red", "( ) blue"]) { Caption = "Which colour do you like best of all?", Hint = "Enter = choose" },
            new MenuTab("Two", [Markup.Escape("[ ] a"), Markup.Escape("[ ] b"), Markup.Escape("[ ] c")]) { Caption = "Toppings?" },
            new MenuTab("Submit", ["One — (no answer)", "Submit"]),
        ],
        tab,
        "Enter = pick · ←/→ tabs · ESC = back") with { SpaceToggles = true, TabCursors = [1, 2, 1] };

    [Fact]
    public void Tabbed_TakesTheTabsCaptionAndHint_TheBaseHintKept()
    {
        var page = Questions();
        Assert.Equal("Which colour do you like best of all?", page.Caption);
        Assert.Equal("Enter = choose", page.Hint);
        Assert.Equal("Enter = pick · ←/→ tabs · ESC = back", page.BaseHint);
        Assert.Equal("Toppings?", Questions(1).Caption);
        Assert.Equal("Enter = pick · ←/→ tabs · ESC = back", Questions(1).Hint);
        Assert.Null(Questions(2).Caption);
        Assert.Null(Tabbed().Caption);
        Assert.Null(Page("one").BaseHint);
        Assert.False(Tabbed().SpaceToggles);
        Assert.Null(Tabbed().TabCursors);
        Assert.Equal(new MenuPick(1, 2), new MenuPick(1, 2, Toggle: false));
        Assert.False(new MenuPick(0, 0).Toggle);
    }

    [Fact]
    public async Task Caption_IsDrawnUnderTheStrip_WrappedAndCountedInTheHeader_AndFollowsATabSwitch()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Right, Keys.Enter);

        Assert.Equal(new MenuPick(1, 2), await menu.PickAsync(Questions(), 0, CancellationToken.None));

        // The strip, the caption row (37 cells fit the 40), the spacer, the rows, the tab's own hint.
        Assert.Contains(Rule(40) + "\n" + Titled("Questions   One    Two    Submit ") + "\nWhich colour do you like best of all?\n \n▸ ( ) red\n  ( ) blue\n \n" + Rule(40) + "\nEnter = choose\n", Output);
        // The switch: the other tab's caption, its cursor on the page's row for it (2), the base hint.
        Assert.Contains("\n" + Titled("Questions   One    Two    Submit ") + "\nToppings?\n \n  [ ] a\n  [ ] b\n▸ [ ] c\n" + Rule(40) + "\nEnter = pick · ←/→ tabs · ESC = back", Output);
        menu.Close();
    }

    [Fact]
    public async Task Space_WithSpaceToggles_ReturnsAToggle_ClearsTheStatus_AndTheSameTabKeepsItsViewport()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        menu.Notice("saved");
        Push(Keys.Down, Keys.Char(' '));

        Assert.Equal(new MenuPick(0, 1, Toggle: true), await menu.PickAsync(Questions(), 0, CancellationToken.None));
        Assert.Empty(menu.Status);
        Assert.Contains("\n  · saved\n", Output);

        // The host re-shows the same tab with the row marked: no tab change, Enter picks as ever.
        Push(Keys.Enter);
        var marked = Questions() with { Rows = ["( ) red", "(x) blue"] };
        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(marked, 1, CancellationToken.None));
        Assert.Contains("\n  ( ) red\n▸ (x) blue\n", Output);
        menu.Close();
    }

    [Fact]
    public async Task Space_WithoutSpaceToggles_IsSwallowed()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Char(' '), Keys.Down, Keys.Enter);

        Assert.Equal(new MenuPick(0, 1), await menu.PickAsync(Page("one", "two"), 0, CancellationToken.None));
        menu.Close();
    }

    [Fact]
    public async Task ATabSwitch_KeepsTheToggleAndTheCursors_TheSubmitTabLandsOnItsRow()
    {
        using var pane = Pane();
        pane.Show();
        var menu = new MenuPane(pane, _keys);
        Push(Keys.Left, Keys.Char(' '));

        // Left from the first tab wraps to Submit, whose cursor is 1; Space still toggles after the switch.
        Assert.Equal(new MenuPick(2, 1, Toggle: true), await menu.PickAsync(Questions(), 0, CancellationToken.None));
        Assert.Contains("\n  One — (no answer)\n▸ Submit\n \n \n" + Rule(40) + "\nEnter = pick · ←/→ tabs · ESC = back", Output);
        menu.Close();
    }

    [Fact]
    public void CaptionRows_WrapOnWords_CutAnOverWideWord_AndJoinTheRestIntoTheLastRow()
    {
        Assert.Equal(["Which colour do you", "like best?"], MenuPane.CaptionRows("Which colour  do you\nlike best?", 20, 3));
        Assert.Equal(["short"], MenuPane.CaptionRows("short", 20, 3));
        Assert.Empty(MenuPane.CaptionRows("   ", 20, 3));
        Assert.Empty(MenuPane.CaptionRows("x", 0, 3));
        Assert.Empty(MenuPane.CaptionRows("x", 20, 0));
        Assert.Equal(["abcdefghi…", "next"], MenuPane.CaptionRows("abcdefghijkl next", 10, 3));
        Assert.Equal(["one two", "three fo…"], MenuPane.CaptionRows("one two three four five six", 9, 2));
        Assert.Equal(3, MenuPane.CaptionMaxRows);
    }

    [Fact]
    public void Markups_ArePinned()
    {
        Assert.Equal($"[{Theme.Label.ToMarkup()}]Settings › TTS voice[/]", MenuPane.TitleMarkup("Settings › TTS voice"));
        Assert.Equal("a[b]", Markup.Remove(MenuPane.TitleMarkup("a[b]")));   // escaped, not parsed
        Assert.Equal($"[{Theme.MenuHighlight.ToMarkup()}]▸ [#EFE6FF]on[/][/]", MenuPane.RowMarkup("[#EFE6FF]on[/]", active: true));
        Assert.Equal("  [#EFE6FF]on[/]", MenuPane.RowMarkup("[#EFE6FF]on[/]", active: false));
        Assert.Equal(TranscriptRenderer.NoticeMarkup("saved"), MenuPane.StatusMarkup(MenuPane.NoticeKind.Notice, "saved"));
        Assert.Equal(TranscriptRenderer.WarningMarkup("w"), MenuPane.StatusMarkup(MenuPane.NoticeKind.Warning, "w"));
        Assert.Equal(TranscriptRenderer.ErrorMarkup("e"), MenuPane.StatusMarkup(MenuPane.NoticeKind.Error, "e"));
    }
}
