using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

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

    /// <summary>
    /// The Tool bar checklist with a Mac's answer (2026-10-22, the user's ask): no 🧩 or 📅 row, so Network a place down lands after Directory;
    /// the hidden two keep their checks and stay right after Network, where they were.
    /// </summary>
    [Fact]
    public async Task OnThePane_ShowToolbar_WithTheMacsAnswer_ListsNoServicesOrTasks_AndAMoveKeepsThemInPlace()
    {
        _settings.Update(d => d.ToolbarItems = [.. ToolbarItems.Names]);
        var (menu, pane) = PaneMenu();
        menu.ToolbarItemHere = id => ToolbarItems.IsHere(id, windows: false);
        bool mac(string id) => ItemOrder.IsDivider(id) || ToolbarItems.IsHere(id, windows: false);
        int network = ItemOrder.Only(ToolbarItems.Sides(null, null), mac).Left.ToList().IndexOf(ToolbarItems.Network);
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ToolbarItems));
        Push(Keys.Enter);
        Push([.. Enumerable.Repeat(Keys.Down, network)]);
        Push(ShiftDown);                        // Network a place down: past Directory, the next row here
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var order = _settings.Current.ToolbarOrder!.ToList();
        Assert.Equal(order.IndexOf(ToolbarItems.Directory) + 1, order.IndexOf(ToolbarItems.Network));
        Assert.Equal(order.IndexOf(ToolbarItems.Network) + 1, order.IndexOf(ToolbarItems.Services));
        Assert.Equal(order.IndexOf(ToolbarItems.Services) + 1, order.IndexOf(ToolbarItems.Tasks));
        Assert.Contains(ToolbarItems.Services, _settings.Current.ToolbarItems!);   // its check kept
        Assert.DoesNotContain(NeonSidekick.Services.ServiceText.Glyph, Output);
        Assert.DoesNotContain(NeonSidekick.Tasks.TaskText.Glyph, Output);
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
        Assert.Contains("  · Performance bar: RAM, GPU, " + Perf.PerfText.VramLabel + " · gauge", Output);   // the look set before the page opened; GMEM on a Mac (2026-10-17, the Mac pass)
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
    /// Toolbar order (2026-10-12, the user's ask): the Chat tab's row after Show home bar; its page lists the bars in their
    /// order (five since 2026-10-14, the Hints bar first), a bar that is off marked so; the move keys save at once; Enter, Space and Delete save nothing; default (D, the
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
        Push(Keys.Down);                        // past the Hints bar to the Status bar
        Push(ShiftDown);                        // the Status bar under the toolbar
        Push(ShiftDown);                        // and under the performance bar
        Push(Keys.Enter, Keys.Char(' '), DeleteKey);   // nothing to check: nothing saved
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([PaneBars.Hints, PaneBars.Toolbar, PaneBars.Perf, PaneBars.Status, PaneBars.Home], _settings.Current.BarOrder);
        Assert.Contains(SettingsMenu.BarOrderKeys, Output);
        Assert.Contains(SettingsMenu.DefaultsButton, Output);
        Assert.Contains("Home bar         " + PaneBars.OffMark + PaneBars.Describe(PaneBars.Home), Output);
        Assert.Contains("  · Toolbar order: Hints bar, Tool bar, Performance bar, Status bar, Home bar", Output);   // "Toolbar" until later on 2026-10-12
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
    /// The Tool bars section (2026-10-12, the user's ask): under Screen's rows, the bars by their new names — Hints bar and Status bar
    /// (2026-10-14; Hint bar from later on 2026-10-12), Tool bar ("Toolbar" until later that day), Performance bar, Home bar and Order, the last <c>Toolbar order</c> everywhere but
    /// here — and Order's page under that name.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheToolBarsSection_HoldsTheBarRows_OrderShortOnThePane()
    {
        var fields = SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList();
        Assert.Equal(fields.IndexOf(SettingsField.MenuMaxHeight) + 1, fields.IndexOf(SettingsField.HintsBar));
        Assert.Equal(
            [SettingsField.HintsBar, SettingsField.StatusBarItems, SettingsField.ToolbarItems, SettingsField.ShowPerformanceBar, SettingsField.ShowHomeBar, SettingsField.BarOrder],
            fields.Skip(fields.IndexOf(SettingsField.HintsBar)).Take(6));

        var (menu, pane) = PaneMenu();
        Down(fields.IndexOf(SettingsField.BarOrder));
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("  Menus max height             full\n" + MenuLayout.Heading("Tool bars", 120) + "\n  Hints bar                    " + HintsBarMode.Default + "\n  Status bar                   " + StatusBarItems.DefaultValue + "\n  Tool bar                     ", Output);
        Assert.Contains("\n  Performance bar              ", Output);
        Assert.Contains("\n  Home bar                     ", Output);
        Assert.Contains("\n▸ Order                        " + PaneBars.DefaultValue + "\n" + MenuLayout.Heading("Outside apps", 120), Output);
        Assert.Contains(GroupBreadcrumb(SettingsTab.Chat, SettingsMenu.BarOrderPaneName) + " ", Output);   // its page under the same name (the default button after it)
        Assert.DoesNotContain("Show toolbar", Output);
        Assert.DoesNotContain("Toolbar order", Output);   // the pane's own words: nothing saved, no notice
        pane.Dispose();
    }

    /// <summary>
    /// Hints bar (2026-10-14, the user's ask): a picker of auto, always and off, the word in force under the cursor; auto saves as
    /// null (the default); off marks the bar <c>off · </c> on the Order page, first there, where it keeps its place.
    /// </summary>
    [Fact]
    public async Task OnThePane_HintsBar_PicksItsMode_OffMarksItOnTheOrderPage()
    {
        var fields = SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList();
        var (menu, pane) = PaneMenu();
        Down(fields.IndexOf(SettingsField.HintsBar));
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // auto under the cursor: always
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(HintsBarMode.Always, _settings.Current.HintsBar);
        Assert.Contains(Markup.Remove(SettingsMenu.HintsBarLabel(HintsBarMode.Off)), Output);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(fields.IndexOf(SettingsField.HintsBar));
        Push(Keys.Enter, Keys.Down, Keys.Enter);   // always under the cursor: off
        Down(fields.IndexOf(SettingsField.BarOrder) - fields.IndexOf(SettingsField.HintsBar));
        Push(Keys.Enter, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(HintsBarMode.Off, _settings.Current.HintsBar);
        Assert.Contains("▸ " + "Hints bar".PadRight(PaneBars.TitleWidth) + PaneBars.OffMark + PaneBars.Describe(PaneBars.Hints), Output);   // first on the page, the cursor on it
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(fields.IndexOf(SettingsField.HintsBar));
        Push(Keys.Enter, Keys.Up, Keys.Up, Keys.Enter, Keys.Escape);   // off under the cursor: auto

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.HintsBar);   // the default saves as null
        pane.Dispose();
    }


    /// <summary>
    /// Status bar (Hint bar until 2026-10-14, its Keys gone to the Hints bar; 2026-10-12, the user's ask: the other bars' checklist for
    /// the hint bar): its sections in the row's order, Shift+End
    /// moves one to the bottom with the cursor, Space unchecks it there, each saved at once; <c>d</c> puts every section and the
    /// order back (null both), a second <c>d</c> saves nothing.
    /// </summary>
    [Fact]
    public async Task OnThePane_StatusBar_ChecksAndMovesItsSections_DefaultPutsThemBack()
    {
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.StatusBarItems));
        Push(Keys.Enter);                       // the checklist, on Status (the left group's first; its heading above is no stop)
        Push(ShiftEnd);                         // Status to the bottom of its group, the cursor with it
        Push(Keys.Char(' '));                   // and off
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["spinner", "queued", "timers", "reading", "usage", "status", "model", "cwd"], _settings.Current.StatusBarOrder);   // the left group's bottom (2026-10-13)
        Assert.Equal(["spinner", "queued", "timers", "reading", "usage", "model"], _settings.Current.StatusBarItems);
        Assert.Null(_settings.Current.StatusBarRight);
        Assert.Contains("[x] Status    ", Output);
        Assert.Contains("[ ] Status    ", Output);
        Assert.Contains(SettingsMenu.PinnedLeftHeading, Output);
        Assert.Contains(SettingsMenu.PinnedRightHeading, Output);
        Assert.Contains(SettingsMenu.StatusBarToggleKeys, Output);
        Assert.Contains(GroupBreadcrumb(SettingsTab.Chat, "Status bar") + " ", Output);
        Assert.Contains("  · Status bar: Queued, Timers, Reading, Usage | Model", Output);
        pane.Dispose();

        // Shift+→ pins the cursor's section right, at the right group's start; Shift+← back to the left group's end (2026-10-13).
        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.StatusBarItems));
        Push(Keys.Enter);                       // on Spinner
        Push(Keys.Down);                        // Queued
        Push(ShiftRight);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["queued", "model", "cwd"], _settings.Current.StatusBarRight);
        Assert.Equal(["spinner", "timers", "reading", "usage", "status", "queued", "model", "cwd"], _settings.Current.StatusBarOrder);
        Assert.Contains("  · Status bar: Timers, Reading, Usage | Queued, Model", Output);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.StatusBarItems));
        Push(Keys.Enter);                       // on Spinner
        Push(Keys.Down);                        // Timers
        Push(ShiftEnd, ShiftDown);              // the left group's bottom, then over the gap: the right group's first
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["queued", "timers", "model", "cwd"], _settings.Current.StatusBarRight);   // saved in the sections' own order
        Assert.Equal(["spinner", "reading", "usage", "status", "timers", "queued", "model", "cwd"], _settings.Current.StatusBarOrder);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.StatusBarItems));
        Push(Keys.Enter);
        Push(Keys.Char('d'));                   // the default sections, the row's own order
        Push(Keys.Char('d'));                   // again: nothing to save
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.StatusBarItems);
        Assert.Null(_settings.Current.StatusBarOrder);
        Assert.Null(_settings.Current.StatusBarRight);   // the sides back too
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Output, "  · Status bar: " + StatusBarItems.DefaultValue));
        pane.Dispose();
    }

    /// <summary>
    /// The spinner on the Status bar's checklist (the hint bar's until 2026-10-14; 2026-10-13, the user's ask: "mandatory and can't be disabled, but it can be moved"):
    /// checked, first; Space, Enter and Delete leave it on with the notice; select none keeps it; Shift+→ pins it right.
    /// </summary>
    [Fact]
    public async Task OnThePane_StatusBar_TheSpinnerStaysOn_ButMoves()
    {
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.StatusBarItems));
        Push(Keys.Enter, Keys.Down);            // on Spinner, after Status
        Push(Keys.Char(' '), Keys.Enter, DeleteKey);   // off, thrice: it stays
        Push(Keys.Char('n'));                   // select none: every section but the spinner
        Push(ShiftRight);                       // the spinner pinned right, the right group's first
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["spinner"], _settings.Current.StatusBarItems);
        Assert.Equal(["spinner", "model", "cwd"], _settings.Current.StatusBarRight);
        Assert.Equal(3, Output.Split(StatusBarItems.SpinnerFixedNotice).Length - 1);
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
    /// The performance bar's checklist in three groups (later on 2026-10-13, the user's ask: the home bar's pinning): every meter
    /// center by default; Shift+→ pins one a group right, Shift+← a group left, one group a press; the row's value lists left, center,
    /// right; default puts the sides back with the order.
    /// </summary>
    [Fact]
    public async Task OnThePane_PerformanceBar_ThreeGroups_ShiftArrowsPinAGroupOver_DefaultPutsTheSidesBack()
    {
        var shiftLeft = new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: true, alt: false, control: false);
        var (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ShowPerformanceBar));
        Push(Keys.Enter);                       // on CPU, the center's first
        Push(ShiftRight);                       // CPU pinned right
        Push(shiftLeft, shiftLeft);             // back to the center's end, then to the left
        Push(Keys.Down, ShiftRight);            // past the center's heading to RAM, pinned right
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["cpu"], _settings.Current.PerformanceBarLeft);
        Assert.Equal(["ram"], _settings.Current.PerformanceBarRight);
        Assert.Equal(["cpu", "gpu", "vram", "net", "netdown", "netup", "proc", "ram"], _settings.Current.PerformanceBarOrder);
        Assert.Contains(SettingsMenu.PinnedLeftHeading, Output);
        Assert.Contains(SettingsMenu.PinnedCenterHeading, Output);
        Assert.Contains(SettingsMenu.PinnedRightHeading, Output);
        Assert.Contains("  · Performance bar: CPU, GPU, " + Perf.PerfText.VramLabel + ", RAM · ", Output);
        pane.Dispose();

        (menu, pane) = PaneMenu();
        Down(SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList().IndexOf(SettingsField.ShowPerformanceBar));
        Push(Keys.Enter);
        Push(Keys.Char('d'));
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Null(_settings.Current.PerformanceBarLeft);
        Assert.Null(_settings.Current.PerformanceBarRight);
        Assert.Null(_settings.Current.PerformanceBarOrder);
        pane.Dispose();
    }

    /// <summary>
    /// The tool bar's dividers on the checklist (2026-10-13, the user's ask): <c>+</c> puts one after the cursor's item, Delete on it
    /// takes it out, Shift+→ pins it right like any item, and <c>d</c> puts the default order (with its three) back.
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

        Assert.Equal(["settings", "divider:4", "profile"], _settings.Current.ToolbarOrder!.Take(3));   // 1 to 3 are the default's (1 and 2 until later still on 2026-10-13)
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
        // The default order's dividers lie between ⚙️ and 🌐 (three since later still that day) with nothing drawn between them: one │ (2026-10-13).
        Assert.Equal([ChatScreen.SettingsToolGlyph, ScreenPane.DividerGlyph, ChatScreen.WebToolGlyph, "llama"], parts.Left.Select(i => i.Text));
        Assert.Equal(ScreenPane.ToolbarItemKind.Divider, parts.Left[1].Kind);
        Assert.True(parts.Left[2].Off);
        Assert.Equal([ChatScreen.ToolsToolGlyph, @"D:\x"], parts.Right.Select(i => i.Text));
        Assert.Equal(ScreenPane.ToolbarItemKind.Path, parts.Right[1].Kind);
    }
}
