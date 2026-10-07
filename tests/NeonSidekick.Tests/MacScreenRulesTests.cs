using NeonSidekick.Screen;

namespace NeonSidekick.Tests;

/// <summary>
/// A Mac's screen capture decisions (2026-10-07, <see cref="MacScreenRules"/>), pure and run everywhere: the shared space at the
/// largest scale, the window list's rules (the real list on the user's 5K M4 that day: menu-bar extras on layer 25, the menu bar 24,
/// the Dock 20, Notification Center far below, Terminal and Preview on 0), the terminal's own window, and how an area is drawn.
/// </summary>
public sealed class MacScreenRulesTests
{
    // The 5K panel: 2560x1440 points at 2x.
    private static readonly MacDisplay FiveK = new(2, 0, 0, 2560, 1440, 5120, 2880, Main: true);

    // A 1x 1920x1080 screen to its right, its top 100 points down.
    private static readonly MacDisplay Side = new(7, 2560, 100, 1920, 1080, 1920, 1080, Main: false);

    private static MacWindowEntry Entry(long id, string title, string owner, int pid, int layer = 0, double alpha = 1, bool onScreen = true, int sharing = 1, double x = 10, double y = 40, double w = 800, double h = 600) =>
        new(id, layer, alpha, onScreen, sharing, title, owner, pid, x, y, w, h);

    private static readonly IReadOnlyList<MacWindowEntry> Desktop =
    [
        Entry(23673, "Item-0", "Microsoft Outlook", 61153, layer: 25, w: 38, h: 24),
        Entry(23547, "Menubar", "Window Server", 394, layer: 24, w: 2560, h: 24),
        Entry(6713, "Dock", "Dock", 35287, layer: 20, w: 2560, h: 1440),
        Entry(23005, "NeonSidekick — zsh", "Terminal", 31985, x: 8, y: 33, w: 1267, h: 1328),
        Entry(23010, "Downloads — zsh", "Terminal", 31985, x: 300, y: 300),
        Entry(24302, "Untitled", "Preview", 96022, x: 278, y: 318, w: 708, h: 593),
        Entry(24400, "", "Finder", 500),                                  // no title
        Entry(24401, "Ghost", "Helper", 501, alpha: 0),                   // fully transparent
        Entry(24402, "Secret", "Vault", 502, sharing: 0),                 // kept from capture
        Entry(24403, "Dot", "Helper", 501, w: 1, h: 1),                   // a point
        Entry(18, "Notification Center", "Notification Center", 716, layer: -2147483601, w: 180, h: 180),
    ];

    [Fact]
    public void OneRetinaDisplay_ListsAtItsPixels_AsWindowsWould()
    {
        var monitors = MacScreenRules.Monitors([FiveK]);

        Assert.Equal(2.0, MacScreenRules.Scale([FiveK]));
        Assert.Equal([new ScreenMonitor(1, "display 2", new ScreenRect(0, 0, 5120, 2880), true)], monitors);
        Assert.Equal("monitor 1 (5120x2880, primary)", ScreenText.MonitorDescribed(monitors[0], own: false));
        Assert.Equal(1.0, MacScreenRules.Scale([]));
    }

    [Fact]
    public void MixedScales_ShareTheLargest_ThePlainScreenAtTwiceItsPixels()
    {
        var monitors = MacScreenRules.Monitors([FiveK, Side]);

        Assert.Equal(new ScreenRect(5120, 200, 3840, 2160), monitors[1].Bounds);
        Assert.False(monitors[1].Primary);
        Assert.Equal(new ScreenRect(0, 0, 8960, 2880), ScreenRect.Union(monitors.Select(m => m.Bounds)));
    }

    [Fact]
    public void TheWindowList_KeepsWhatWindowsWould_FrontToBack_InTheSpace()
    {
        var windows = MacScreenRules.Windows(Desktop, 2);

        Assert.Equal([23005, 23010, 24302], windows.Select(w => w.Id));
        Assert.Equal(new ScreenWindow(24302, "Untitled", "Preview", new ScreenRect(556, 636, 1416, 1186)), windows[2]);
        Assert.False(MacScreenRules.Listed(Entry(1, "Off", "App", 9, onScreen: false)));
        Assert.Equal(24302, ScreenAiming.FindWindow("preview", windows).Id);
        Assert.Equal(24302, ScreenAiming.FindWindow("24302", windows).Id);
    }

    [Fact]
    public void TheOwnWindow_IsTheFirstAncestorsFrontMost_TheTitleWinning_AndBehindIsTheNext()
    {
        int[] ancestors = [9001, 9000, 31985, 1];

        Assert.Equal(23005, MacScreenRules.Own(Desktop, ancestors, null, null));
        Assert.Equal(23010, MacScreenRules.Own(Desktop, ancestors, null, "Downloads"));
        Assert.Equal(23005, MacScreenRules.Own(Desktop, ancestors, null, "no such title"));
        Assert.Equal(23005, MacScreenRules.Own(Desktop, [42], "Apple_Terminal", null));   // the chain broken (tmux): TERM_PROGRAM's owner
        Assert.Null(MacScreenRules.Own(Desktop, [42], "vscode", null));
        Assert.Null(MacScreenRules.Own(Desktop, [], null, null));

        var screen = new RulesScreen(Desktop, ancestors);
        var aim = ScreenAiming.Resolve(new ScreenTarget(ScreenTargetKind.Behind), screen);
        Assert.Equal(23010, aim.Window);
    }

    [Fact]
    public void TheTerminalsName_IsItsWindowsOwner_ElseTermPrograms()
    {
        Assert.Equal("Terminal", MacScreenRules.TerminalName(Desktop, [31985], "iTerm.app"));
        Assert.Equal("iTerm2", MacScreenRules.TerminalName(Desktop, [42], "iTerm.app"));
        Assert.Null(MacScreenRules.TerminalName(Desktop, [42], null));
    }

    [Fact]
    public void TheOwnMonitor_HoldsTheWindowsCentre()
    {
        var monitors = MacScreenRules.Monitors([FiveK, Side]);

        Assert.Equal(1, MacScreenRules.MonitorOf(new ScreenRect(16, 66, 2534, 2656), monitors));
        Assert.Equal(2, MacScreenRules.MonitorOf(new ScreenRect(6000, 400, 800, 600), monitors));
        Assert.Null(MacScreenRules.MonitorOf(new ScreenRect(-5000, 0, 100, 100), monitors));
    }

    [Fact]
    public void APlan_OneDisplayAtItsOwnPixels_AllAsOneCanvas_GapsLeftBlack()
    {
        var monitors = MacScreenRules.Monitors([FiveK, Side]);

        var alone = MacScreenRules.Plan(monitors[1].Bounds, [FiveK, Side])!;
        Assert.Equal(new MacCanvas(1920, 1080, alone.Placements), alone);
        Assert.Equal(new MacPlacement(7, new ScreenRect(0, 0, 1920, 1080)), Assert.Single(alone.Placements));

        var all = MacScreenRules.Plan(ScreenRect.Union(monitors.Select(m => m.Bounds)), [FiveK, Side])!;
        Assert.Equal((8960, 2880), (all.Width, all.Height));
        Assert.Equal([new MacPlacement(2, new ScreenRect(0, 0, 5120, 2880)), new MacPlacement(7, new ScreenRect(5120, 200, 3840, 2160))], all.Placements);

        var part = MacScreenRules.Plan(new ScreenRect(5000, 0, 400, 400), [FiveK, Side])!;
        Assert.Equal([new MacPlacement(2, new ScreenRect(-5000, 0, 5120, 2880)), new MacPlacement(7, new ScreenRect(120, 200, 3840, 2160))], part.Placements);

        Assert.Null(MacScreenRules.Plan(new ScreenRect(-900, -900, 10, 10), [FiveK, Side]));
        Assert.Null(MacScreenRules.Plan(default, [FiveK]));
    }

    [Fact]
    public void ToSpace_RoundsTheEdges_NotTheSize()
    {
        Assert.Equal(new ScreenRect(3, 3, 3, 2), MacScreenRules.ToSpace(1.25, 1.25, 1.5, 1, 2));
        Assert.Equal(1.0, new MacDisplay(1, 0, 0, 0, 0, 0, 0, false).Scale);
    }

    /// <summary><see cref="IScreenSystem"/> over the rules alone, the way <see cref="MacScreenSystem"/> answers, to aim <c>behind</c>.</summary>
    private sealed class RulesScreen(IReadOnlyList<MacWindowEntry> entries, IReadOnlyList<int> ancestors) : IScreenSystem
    {
        public IReadOnlyList<ScreenMonitor> Monitors() => MacScreenRules.Monitors([FiveK]);

        public IReadOnlyList<ScreenWindow> Windows() => MacScreenRules.Windows(entries, 2);

        public long? OwnWindow() => MacScreenRules.Own(entries, ancestors, null, null);

        public int? OwnMonitor() => 1;

        public ScreenFrame CaptureArea(ScreenRect area) => throw new NotSupportedException();

        public ScreenFrame CaptureWindow(long id) => throw new NotSupportedException();
    }
}
