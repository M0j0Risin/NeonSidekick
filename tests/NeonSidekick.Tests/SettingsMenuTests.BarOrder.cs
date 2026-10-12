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
}
