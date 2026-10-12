using NeonSidekick.App;
using NeonSidekick.Settings;
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
        Assert.Equal([ToolbarItems.Settings, ToolbarItems.Path], order[^2..]);
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
        Assert.Contains("  · Show performance bar: RAM, GPU, VRAM · gauge", Output);   // the look set before the page opened
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
        Assert.Contains("Home bar         " + PaneBars.OffMark + "Show home bar", Output);
        Assert.Contains("  · Toolbar order: Toolbar, Performance bar, Hint bar, Home bar", Output);
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
}
