using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The toolbar's and the performance bar's checklists take the home bar picker's move keys (2026-10-12, the user's ask):
/// Shift+↑↓ and Shift+Home/End change the order, Delete unchecks, and default puts the order back with the checks.
/// </summary>
public partial class SettingsMenuTests
{
    private static readonly ConsoleKeyInfo ShiftDown = new('\0', ConsoleKey.DownArrow, shift: true, alt: false, control: false);
    private static readonly ConsoleKeyInfo ShiftUp = new('\0', ConsoleKey.UpArrow, shift: true, alt: false, control: false);
    private static readonly ConsoleKeyInfo ShiftEnd = new('\0', ConsoleKey.End, shift: true, alt: false, control: false);
    private static readonly ConsoleKeyInfo ShiftRight = new('\0', ConsoleKey.RightArrow, shift: true, alt: false, control: false);
    private static readonly ConsoleKeyInfo DeleteKey = new('\0', ConsoleKey.Delete, shift: false, alt: false, control: false);

    [Fact]
    public async Task OnThePane_ShowToolbar_TheMoveKeysOrderIt_ThePathStaysLast_DeleteUnchecks_DefaultPutsItBack()
    {
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);
        Push(ShiftDown);                        // Settings a place down
        Push(ShiftEnd);                         // then to the bottom: above the path
        Push(Keys.Down, ShiftUp);               // the path never moves
        Push(Keys.Up, DeleteKey);               // Settings unchecked
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var order = _settings.Current.ToolbarOrder!;
        Assert.Equal(ToolbarItems.Profile, order[0]);
        Assert.Equal([ToolbarItems.Settings, ToolbarItems.Path, ToolbarItems.Model], order[^3..]);   // the model's name after the path (2026-10-12)
        Assert.DoesNotContain(ToolbarItems.Settings, _settings.Current.ToolbarItems!);
        Assert.Contains(ToolbarItems.Tools, _settings.Current.ToolbarItems!);
        Assert.Contains(SettingsMenu.ToolbarToggleKeys, Output);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);
        Push(Keys.Char('d'));                   // the default checks and order
        Push(Keys.Char('d'));                   // again: nothing to save
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.ToolbarItems);
        Assert.Null(_settings.Current.ToolbarOrder);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ShowPerformanceBar_TheMoveKeysOrderIt_DefaultPutsItBack_TheLookStays()
    {
        _settings.Update(d => d.PerformanceBarLook = "gauge");   // /perfbar's since 2026-10-12, the page's look buttons gone
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ShowPerformanceBar));
        Push(Keys.Enter);
        Push(ShiftDown);                        // CPU after RAM
        Push(DeleteKey);                        // CPU unchecked (the cursor followed it)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["ram", "cpu", "gpu", "vram", "net", "netdown", "netup", "proc"], _settings.Current.PerformanceBarOrder);
        Assert.Equal(["ram", "gpu", "vram"], _settings.Current.PerformanceBarItems);
        Assert.Equal("gauge", _settings.Current.PerformanceBarLook);
        Assert.Contains(SettingsMenu.PerfBarToggleKeys, Output);
        Assert.Contains("  · Performance bar: RAM, GPU, VRAM · gauge", Output);   // the look set before the page opened
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ShowPerformanceBar));
        Push(Keys.Enter);
        Push(Keys.Char('d'));                   // the default meters and order; the look stays
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(PerfBarItems.Defaults, _settings.Current.PerformanceBarItems);
        Assert.Null(_settings.Current.PerformanceBarOrder);
        Assert.Equal("gauge", _settings.Current.PerformanceBarLook);
        pane.Dispose();
    }

    /// <summary>
    /// Toolbar order (2026-10-12, the user's ask): the Chat tab's row after Show home bar; its page lists the four bars in their
    /// order, a bar that is off marked so; the move keys save at once; Enter, Space and Delete save nothing; default (D, the
    /// title row's one button — the user's ask: a way back on the page) puts the order back, and again saves nothing.
    /// </summary>
    [Fact]
    public async Task OnThePane_ToolbarOrder_AfterShowHomeBar_TheMoveKeysOrderTheBars_DefaultPutsThemBack()
    {
        var fields = SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList();
        Assert.Equal(fields.IndexOf(SettingsField.ShowHomeBar) + 1, fields.IndexOf(SettingsField.BarOrder));
        Assert.Equal("Toolbar order", SettingsMenu.FieldName(SettingsField.BarOrder));
        _settings.Update(d => d.HomeBarItems = null);   // no home bar: its row says off

        var (menu, pane) = PaneMenu();
        Down(fields.IndexOf(SettingsField.BarOrder));
        Push(Keys.Enter);
        Push(ShiftDown);                        // the hint bar under the toolbar
        Push(ShiftDown);                        // and under the performance bar
        Push(Keys.Enter, Keys.Char(' '), DeleteKey);   // nothing to check: nothing saved
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([PaneBars.Toolbar, PaneBars.Perf, PaneBars.Hint, PaneBars.Home], _settings.Current.BarOrder);
        Assert.Contains(SettingsMenu.BarOrderKeys, Output);
        Assert.Contains(SettingsMenu.DefaultsButton, Output);
        Assert.Contains("Home bar         " + PaneBars.OffMark + PaneBars.Describe(PaneBars.Home), Output);
        Assert.Contains("  · Toolbar order: Tool bar, Performance bar, Hint bar, Home bar", Output);   // "Toolbar" until later on 2026-10-12
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(fields.IndexOf(SettingsField.BarOrder));
        Push(Keys.Enter);
        Push(Keys.Char('d'));                   // the default order
        Push(Keys.Char('d'));                   // again: nothing to save
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.BarOrder);
        Assert.Contains("  · Toolbar order: " + PaneBars.DefaultValue, Output);
        pane.Dispose();
    }

    /// <summary>
    /// The Tool bars section (2026-10-12, the user's ask): under Screen's rows, the bars by their new names — Hint bar (later that day),
    /// Tool bar ("Toolbar" until later that day), Performance bar, Home bar and Order, the last <c>Toolbar order</c> everywhere but
    /// here — and Order's page under that name.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheToolBarsSection_HoldsTheFourRows_OrderShortOnThePane()
    {
        var fields = SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList();
        Assert.Equal(fields.IndexOf(SettingsField.MenuMaxHeight) + 1, fields.IndexOf(SettingsField.HintBarItems));
        Assert.Equal(
            [SettingsField.HintBarItems, SettingsField.ToolbarItems, SettingsField.ShowPerformanceBar, SettingsField.ShowHomeBar, SettingsField.BarOrder],
            fields.Skip(fields.IndexOf(SettingsField.HintBarItems)).Take(5));

        var (menu, pane) = PaneMenu();
        Down(fields.IndexOf(SettingsField.BarOrder));
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  Menus max height             full-screen\n" + MenuLayout.Heading("Tool bars", 120) + "\n  Hint bar                     " + HintBarItems.DefaultValue + "\n  Tool bar                     ", Output);
        Assert.Contains("\n  Performance bar              ", Output);
        Assert.Contains("\n  Home bar                     ", Output);
        Assert.Contains("\n▸ Order                        " + PaneBars.DefaultValue + "\n" + MenuLayout.Heading("Outside apps", 120), Output);
        Assert.Contains(GroupBreadcrumb(SettingsTab.Chat, SettingsMenu.BarOrderPaneName) + " ", Output);   // its page under the same name (the default button after it)
        Assert.DoesNotContain("Show toolbar", Output);
        Assert.DoesNotContain("Toolbar order", Output);   // the pane's own words: nothing saved, no notice
        pane.Dispose();
    }


    /// <summary>
    /// Hint bar (2026-10-12, the user's ask: the other bars' checklist for the hint bar): its sections in the row's order, Shift+End
    /// moves one to the bottom with the cursor, Space unchecks it there, each saved at once; <c>d</c> puts every section and the
    /// order back (null both), a second <c>d</c> saves nothing.
    /// </summary>
    [Fact]
    public async Task OnThePane_HintBar_ChecksAndMovesItsSections_DefaultPutsThemBack()
    {
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.HintBarItems));
        Push(Keys.Enter);                       // the checklist, on Status (the left group's first; its heading above is no stop)
        Push(ShiftEnd);                         // Status to the bottom of its group, the cursor with it
        Push(Keys.Char(' '));                   // and off
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["spinner", "queued", "keys", "timers", "reading", "usage", "status", "model", "cwd"], _settings.Current.HintBarOrder);   // the left group's bottom (2026-10-13)
        Assert.Equal(["spinner", "queued", "keys", "timers", "reading", "usage", "model"], _settings.Current.HintBarItems);
        Assert.Null(_settings.Current.HintBarRight);
        Assert.Contains("[x] Status    ", Output);
        Assert.Contains("[ ] Status    ", Output);
        Assert.Contains(SettingsMenu.PinnedLeftHeading, Output);
        Assert.Contains(SettingsMenu.PinnedRightHeading, Output);
        Assert.Contains(SettingsMenu.HintBarToggleKeys, Output);
        Assert.Contains(GroupBreadcrumb(SettingsTab.Chat, "Hint bar") + " ", Output);
        Assert.Contains("  · Hint bar: Queued, Keys, Timers, Reading, Usage | Model", Output);
        pane.Dispose();

        // Shift+→ pins the cursor's section right, at the right group's start; Shift+← back to the left group's end (2026-10-13).
        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.HintBarItems));
        Push(Keys.Enter);                       // on Spinner
        Push(Keys.Down);                        // Queued
        Push(ShiftRight);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["queued", "model", "cwd"], _settings.Current.HintBarRight);
        Assert.Equal(["spinner", "keys", "timers", "reading", "usage", "status", "queued", "model", "cwd"], _settings.Current.HintBarOrder);
        Assert.Contains("  · Hint bar: Keys, Timers, Reading, Usage | Queued, Model", Output);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.HintBarItems));
        Push(Keys.Enter);                       // on Spinner
        Push(Keys.Down);                        // Keys
        Push(ShiftEnd, ShiftDown);              // the left group's bottom, then over the gap: the right group's first
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["queued", "keys", "model", "cwd"], _settings.Current.HintBarRight);   // saved in the sections' own order
        Assert.Equal(["spinner", "timers", "reading", "usage", "status", "keys", "queued", "model", "cwd"], _settings.Current.HintBarOrder);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.HintBarItems));
        Push(Keys.Enter);
        Push(Keys.Char('d'));                   // the default sections, the row's own order
        Push(Keys.Char('d'));                   // again: nothing to save
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.HintBarItems);
        Assert.Null(_settings.Current.HintBarOrder);
        Assert.Null(_settings.Current.HintBarRight);   // the sides back too
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Output, "  · Hint bar: " + HintBarItems.DefaultValue));
        pane.Dispose();
    }

    /// <summary>
    /// The spinner on the hint bar's checklist (2026-10-13, the user's ask: "mandatory and can't be disabled, but it can be moved"):
    /// checked, first; Space, Enter and Delete leave it on with the notice; select none keeps it; Shift+→ pins it right.
    /// </summary>
    [Fact]
    public async Task OnThePane_HintBar_TheSpinnerStaysOn_ButMoves()
    {
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.HintBarItems));
        Push(Keys.Enter, Keys.Down);            // on Spinner, after Status
        Push(Keys.Char(' '), Keys.Enter, DeleteKey);   // off, thrice: it stays
        Push(Keys.Char('n'));                   // select none: every section but the spinner
        Push(ShiftRight);                       // the spinner pinned right, the right group's first
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["spinner"], _settings.Current.HintBarItems);
        Assert.Equal(["spinner", "model", "cwd"], _settings.Current.HintBarRight);
        Assert.Equal(3, Output.Split(HintBarItems.SpinnerFixedNotice).Length - 1);
        Assert.Contains("[x] Spinner   ", Output);
        Assert.DoesNotContain("[ ] Spinner", Output);
        pane.Dispose();
    }

    /// <summary>
    /// The two groups' rows (2026-10-13, the user's ask): a heading per side counting its items, an empty side's heading saying how to
    /// fill it, the rows mapped to the items and back; Shift/Alt+←/→ are the side keys, Ctrl and the bare arrows not.
    /// </summary>
    [Fact]
    public void TheSidedChecklist_RowsHeadingsAndKeys_ArePinned()
    {
        var sides = ItemOrder.Split(["a", "b", "c"], new HashSet<string> { "c" });
        var (rows, headings) = SettingsMenu.SidedRows(sides, id => id);
        Assert.Equal(5, rows.Count);
        Assert.Equal([0, 3], headings.Order());
        Assert.Equal(["a", "b", "c"], [rows[1], rows[2], rows[4]]);
        Assert.Contains(SettingsMenu.PinnedLeftHeading, rows[0]);
        Assert.Contains(SettingsMenu.PinnedRightHeading, rows[3]);
        Assert.Equal([-1, 0, 1, -1, 2], Enumerable.Range(0, 5).Select(r => SettingsMenu.SidedItemAt(sides, r)));
        Assert.Equal([1, 2, 4], Enumerable.Range(0, 3).Select(i => SettingsMenu.SidedRowOf(sides, i)));

        var noneRight = ItemOrder.Split(["a"], new HashSet<string>());
        Assert.Contains(SettingsMenu.EmptySideNote(right: true), SettingsMenu.SidedRows(noneRight, id => id).Rows[^1]);
        Assert.Equal("none: Shift+→ pins the item under the cursor here", SettingsMenu.EmptySideNote(right: true));

        Assert.Equal(-1, MenuPane.SideOf(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: true, alt: false, control: false)));
        Assert.Equal(1, MenuPane.SideOf(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: true, control: false)));
        Assert.Null(MenuPane.SideOf(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: false, alt: false, control: false)));
        Assert.Null(MenuPane.SideOf(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: true, alt: false, control: true)));
    }

    /// <summary>The tool bar's checklist: Shift+→ pins the path's neighbour right, default puts the sides back (2026-10-13).</summary>
    [Fact]
    public async Task OnThePane_ToolBar_ShiftRightPinsAGlyphRight_DefaultPutsTheSidesBack()
    {
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);                       // on Settings, the left group's first
        Push(ShiftRight);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["settings", "path", "model"], _settings.Current.ToolbarRight);
        Assert.Equal(ToolbarItems.DefaultOrder.Where(n => n is not ("settings" or "path" or "model")).Concat(["settings", "path", "model"]), _settings.Current.ToolbarOrder);   // the default dividers kept (2026-10-13)
        Assert.Contains(SettingsMenu.ToolbarToggleKeys, Output);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);
        Push(Keys.Char('d'));
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.ToolbarRight);
        Assert.Null(_settings.Current.ToolbarOrder);
        pane.Dispose();
    }

    /// <summary>
    /// The tool bar's dividers on the checklist (2026-10-13, the user's ask): <c>+</c> puts one after the cursor's item, Delete on it
    /// takes it out, Shift+→ pins it right like any item, and <c>d</c> puts the default order (with its two) back.
    /// </summary>
    [Fact]
    public async Task OnThePane_ToolBar_AddsMovesAndDeletesDividers()
    {
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);                       // on Settings
        Push(Keys.Char('+'));                   // a divider after Settings, the cursor on it
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["settings", "divider:3", "profile"], _settings.Current.ToolbarOrder!.Take(3));   // 1 and 2 are the default's
        Assert.Contains(SettingsMenu.DividerButton, Output);
        Assert.Contains("    │   Divider", Output);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter, Keys.Down);            // the new divider
        Push(Keys.Char(' '), Keys.Enter);       // nothing: a divider has no check
        Push(DeleteKey);                        // out
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.ToolbarOrder);   // the default order again
        Assert.Null(_settings.Current.ToolbarItems);
        pane.Dispose();
    }

    /// <summary>The screen's toolbar groups (2026-10-13): each side's checked items in its order, the lock where the policy draws it, an off switch on the slab.</summary>
    [Fact]
    public void ToolbarPartsFor_PutsEachCheckedItemOnItsSide()
    {
        var shown = new AppSettingsData { ToolbarRight = ["tools", "path"], WebTools = false };
        var parts = ChatScreen.ToolbarPartsFor(ToolbarItems.Resolve(["settings", "tools", "web", "path", "model"]), shown, @"D:\x", "llama", "");
        // The default order's two dividers lie between ⚙️ and 🌐 with nothing drawn between them: one │ (2026-10-13).
        Assert.Equal([ChatScreen.SettingsToolGlyph, ScreenPane.DividerGlyph, ChatScreen.WebToolGlyph, "llama"], parts.Left.Select(i => i.Text));
        Assert.Equal(ScreenPane.ToolbarItemKind.Divider, parts.Left[1].Kind);
        Assert.True(parts.Left[2].Off);
        Assert.Equal([ChatScreen.ToolsToolGlyph, @"D:\x"], parts.Right.Select(i => i.Text));
        Assert.Equal(ScreenPane.ToolbarItemKind.Path, parts.Right[1].Kind);
    }
}
