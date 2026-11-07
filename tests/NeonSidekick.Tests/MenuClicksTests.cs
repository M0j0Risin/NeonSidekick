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

    private (ScreenPane Pane, ScriptedInput Input, KeySource Keys) Rig()
    {
        // The window's top at buffer row 0, so a pointer's row is its screen row (OverlayTopRow's frame).
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100, () => 0), _time) { Hint = () => "idle" };
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
            var back = Theme.MenuHover.Background;
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
