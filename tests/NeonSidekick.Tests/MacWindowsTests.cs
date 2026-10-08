using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The Mac windows' pure decisions (2026-10-07, the windows over AppKit): <see cref="MacKeys"/>' key map and the viewer's Mac extras,
/// and <see cref="MacPlacement"/>'s flip, corner and restore. They run on every system; the AppKit layer itself is the smoke's.
/// </summary>
public sealed class MacWindowsTests
{
    private const ushort KeyA = 0, KeyF = 3, KeyW = 13, KeyQ = 12, Key1 = 18, Key0 = 29;
    private const ushort Escape = 53, Left = 123, Right = 124, Up = 126, Down = 125, Home = 115, End = 119;
    private const ushort Backspace = 51, ForwardDelete = 117, Tab = 48, F9 = 101, F10 = 109, F11 = 103, F4 = 118;

    [Theory]
    [InlineData(KeyA, 'A')]
    [InlineData(KeyF, 'F')]
    [InlineData(KeyW, 'W')]
    [InlineData(KeyQ, 'Q')]
    [InlineData(Key1, '1')]
    [InlineData(Key0, '0')]
    [InlineData(Escape, 0x1B)]
    [InlineData(Left, 0x25)]
    [InlineData(Right, 0x27)]
    [InlineData(Up, 0x26)]
    [InlineData(Down, 0x28)]
    [InlineData(Home, 0x24)]
    [InlineData(End, 0x23)]
    [InlineData(ForwardDelete, 0x2E)]
    [InlineData(Backspace, 0x08)]
    [InlineData(Tab, 0x09)]
    [InlineData(F4, 0x73)]
    [InlineData(F9, 0x78)]
    [InlineData(F10, 0x79)]
    [InlineData(F11, 0x7A)]
    [InlineData(200, 0)]
    public void ToVirtualKey_MapsTheKey_NotTheCharacter(ushort keyCode, int vk) => Assert.Equal(vk, MacKeys.ToVirtualKey(keyCode));

    [Fact]
    public void ToVirtualKey_CoversEveryLetterAndDigit_Once()
    {
        var mapped = Enumerable.Range(0, 128).Select(k => MacKeys.ToVirtualKey((ushort)k)).Where(vk => vk is >= 'A' and <= 'Z' or >= '0' and <= '9').ToList();
        Assert.Equal(36, mapped.Count);
        Assert.Equal(36, mapped.Distinct().Count());
    }

    [Fact]
    public void ViewerAction_TheWindowsKeys_AsOnWindows()
    {
        Assert.Equal(ViewerAction.Newer, MacKeys.ViewerAction(Left, 0, fullScreen: false, slideShow: false));
        Assert.Equal(ViewerAction.Older, MacKeys.ViewerAction(Right, 0, false, false));
        Assert.Equal(ViewerAction.Newest, MacKeys.ViewerAction(Home, 0, false, false));
        Assert.Equal(ViewerAction.Oldest, MacKeys.ViewerAction(End, 0, false, false));
        Assert.Equal(ViewerAction.ToggleSlideShow, MacKeys.ViewerAction(F9, 0, false, false));
        Assert.Equal(ViewerAction.ToggleFullScreen, MacKeys.ViewerAction(F11, 0, false, false));
        Assert.Equal(ViewerAction.Close, MacKeys.ViewerAction(Escape, 0, false, false));
        Assert.Equal(ViewerAction.LeaveFullScreen, MacKeys.ViewerAction(Escape, 0, fullScreen: true, slideShow: false));
        Assert.Equal(ViewerAction.StopSlideShow, MacKeys.ViewerAction(Escape, 0, false, slideShow: true));
        Assert.Equal(ViewerAction.Delete, MacKeys.ViewerAction(ForwardDelete, 0, false, false));
        Assert.Equal(ViewerAction.Menu, MacKeys.ViewerAction(F10, MacKeys.ShiftFlag, false, false));
        Assert.Equal(ViewerAction.None, MacKeys.ViewerAction(Tab, 0, false, false));
    }

    [Fact]
    public void ViewerAction_TheMacExtras()
    {
        Assert.Equal(ViewerAction.Delete, MacKeys.ViewerAction(Backspace, 0, false, false));   // ⌫ is Del
        Assert.Equal(ViewerAction.Close, MacKeys.ViewerAction(KeyW, MacKeys.CommandFlag, false, false));   // ⌘W
        Assert.Equal(ViewerAction.ToggleFullScreen, MacKeys.ViewerAction(KeyF, MacKeys.CommandFlag | MacKeys.ControlFlag, false, false));   // ⌃⌘F
        Assert.Equal(ViewerAction.None, MacKeys.ViewerAction(KeyF, MacKeys.CommandFlag, false, false));   // ⌘F alone: nothing
        Assert.Equal(ViewerAction.None, MacKeys.ViewerAction(Left, MacKeys.CommandFlag, false, false));   // ⌘ is only the extras'
        Assert.Equal(ViewerAction.None, MacKeys.ViewerAction(KeyW, MacKeys.CommandFlag | MacKeys.OptionFlag, false, false));
        Assert.Equal(ViewerAction.None, MacKeys.ViewerAction(Left, MacKeys.OptionFlag, false, false));    // ⌥ held: never the viewer's
        Assert.Equal(ViewerAction.Newer, MacKeys.ViewerAction(Left, MacKeys.ControlFlag, false, false));  // ⌃ is ignored, as Ctrl is on Windows
    }

    [Fact]
    public void TheMacWords_AreTheirOwn_AndWindowsKeepsItsOwn()
    {
        Assert.Equal("(← or wheel up newer · → or wheel down older · Home newest · End oldest · right-click picture menu · F9 slide show · F10 random · ↑ ↓ slide time · ⌃⌘F full screen · ⌫ twice delete · TAB terminal · ESC or ⌘W close)", ViewerText.KeysMac);
        Assert.Equal("The picture viewer needs the Mac's desktop: there is no window server here (over SSH, say).", ViewerText.UnavailableMac);
        Assert.Equal(OperatingSystem.IsMacOS() ? ViewerText.KeysMac : ViewerText.Keys, ViewerText.KeysHere);
        Assert.Equal(OperatingSystem.IsMacOS() ? ViewerText.UnavailableMac : ViewerText.Unavailable, ViewerText.UnavailableHere);
    }

    [Fact]
    public void ThumbsAction_TheWindowsKeys_AndTheMacExtras()
    {
        const ushort Return = 36, Equal = 24, Minus = 27, KeypadPlus = 69, KeypadMinus = 78, F5 = 96;
        Assert.Equal(ThumbsAction.Left, MacKeys.ThumbsAction(Left, 0, fullScreen: false));
        Assert.Equal(ThumbsAction.Down, MacKeys.ThumbsAction(Down, 0, false));
        Assert.Equal(ThumbsAction.Open, MacKeys.ThumbsAction(Return, 0, false));
        Assert.Equal(ThumbsAction.Refresh, MacKeys.ThumbsAction(F5, 0, false));
        Assert.Equal(ThumbsAction.ZoomIn, MacKeys.ThumbsAction(Equal, MacKeys.ShiftFlag, false));     // + is Shift+=
        Assert.Equal(ThumbsAction.ZoomIn, MacKeys.ThumbsAction(KeypadPlus, 0, false));
        Assert.Equal(ThumbsAction.ZoomOut, MacKeys.ThumbsAction(Minus, 0, false));
        Assert.Equal(ThumbsAction.ZoomOut, MacKeys.ThumbsAction(KeypadMinus, 0, false));
        Assert.Equal(ThumbsAction.Menu, MacKeys.ThumbsAction(F10, MacKeys.ShiftFlag, false));
        Assert.Equal(ThumbsAction.Close, MacKeys.ThumbsAction(Escape, 0, false));
        Assert.Equal(ThumbsAction.LeaveFullScreen, MacKeys.ThumbsAction(Escape, 0, fullScreen: true));
        Assert.Equal(ThumbsAction.Delete, MacKeys.ThumbsAction(Backspace, 0, false));                 // ⌫ is Del
        Assert.Equal(ThumbsAction.Delete, MacKeys.ThumbsAction(ForwardDelete, 0, false));
        Assert.Equal(ThumbsAction.Close, MacKeys.ThumbsAction(KeyW, MacKeys.CommandFlag, false));     // ⌘W
        Assert.Equal(ThumbsAction.ToggleFullScreen, MacKeys.ThumbsAction(KeyF, MacKeys.CommandFlag | MacKeys.ControlFlag, false));
        Assert.Equal(ThumbsAction.ZoomIn, MacKeys.ThumbsAction(Equal, MacKeys.CommandFlag, false));   // ⌘=
        Assert.Equal(ThumbsAction.ZoomOut, MacKeys.ThumbsAction(Minus, MacKeys.CommandFlag, false));  // ⌘−
        Assert.Equal(ThumbsAction.None, MacKeys.ThumbsAction(Left, MacKeys.CommandFlag, false));
        Assert.Equal(ThumbsAction.None, MacKeys.ThumbsAction(Left, MacKeys.OptionFlag, false));
        Assert.Equal(ThumbsAction.None, MacKeys.ThumbsAction(Left, MacKeys.ControlFlag, false));      // a Ctrl chord is never the window's
    }

    [Fact]
    public void TheThumbsMacWords_AreTheirOwn()
    {
        Assert.Equal("(click shows a picture in the viewer · double-click or Enter opens it · arrows move · right-click for the picture menu · + − ⌘+wheel or pinch size · ⌫ twice deletes · F5 refresh · ⌃⌘F full screen · TAB terminal · ESC or ⌘W close)", ThumbsText.KeysMac);
        Assert.Equal("The thumbnail browser needs the Mac's desktop: there is no window server here (over SSH, say).", ThumbsText.UnavailableMac);
        Assert.Equal(OperatingSystem.IsMacOS() ? ThumbsText.KeysMac : ThumbsText.Keys, ThumbsText.KeysHere);
        Assert.Equal("Show in Finder", PictureMenuText.ShowInFinder);
    }

    [Fact]
    public void LogAction_TheMacsCopyAndEnds_AndWindowsOwnKeys()
    {
        const ushort KeyC = 8, KeyE = 14, PageUp = 116, PageDown = 121;
        Assert.Equal(LogViewAction.Copy, MacKeys.LogAction(KeyC, MacKeys.CommandFlag, fullScreen: false));     // ⌘C
        Assert.Equal(LogViewAction.SelectAll, MacKeys.LogAction(KeyA, MacKeys.CommandFlag, false));          // ⌘A
        Assert.Equal(LogViewAction.None, MacKeys.LogAction(KeyC, MacKeys.ControlFlag, false));               // Ctrl+C is not the window's on a Mac
        Assert.Equal(LogViewAction.None, MacKeys.LogAction(KeyA, MacKeys.ControlFlag, false));
        Assert.Equal(LogViewAction.Top, MacKeys.LogAction(Up, MacKeys.CommandFlag, false));                  // ⌘↑
        Assert.Equal(LogViewAction.Bottom, MacKeys.LogAction(Down, MacKeys.CommandFlag, false));             // ⌘↓
        Assert.Equal(LogViewAction.Bottom, MacKeys.LogAction(KeyE, MacKeys.ControlFlag, false));             // Ctrl+E, as on Windows
        Assert.Equal(LogViewAction.Top, MacKeys.LogAction(Home, MacKeys.ControlFlag, false));
        Assert.Equal(LogViewAction.Bottom, MacKeys.LogAction(End, MacKeys.ControlFlag, false));
        Assert.Equal(LogViewAction.LineUp, MacKeys.LogAction(Up, 0, false));
        Assert.Equal(LogViewAction.PageDown, MacKeys.LogAction(PageDown, 0, false));
        Assert.Equal(LogViewAction.PageUp, MacKeys.LogAction(PageUp, 0, false));
        Assert.Equal(LogViewAction.Close, MacKeys.LogAction(KeyW, MacKeys.CommandFlag, false));
        Assert.Equal(LogViewAction.Close, MacKeys.LogAction(Escape, 0, false));
        Assert.Equal(LogViewAction.LeaveFullScreen, MacKeys.LogAction(Escape, 0, fullScreen: true));
        Assert.Equal(LogViewAction.ToggleFullScreen, MacKeys.LogAction(KeyF, MacKeys.CommandFlag | MacKeys.ControlFlag, false));
        Assert.Equal(LogViewAction.None, MacKeys.LogAction(KeyC, MacKeys.OptionFlag | MacKeys.CommandFlag, false));
        Assert.Equal(LogViewAction.None, MacKeys.LogAction(KeyQ, MacKeys.CommandFlag, false));
    }

    [Fact]
    public void TheLineWindowsMacWords_AreTheirOwn()
    {
        Assert.Equal("The log window needs the Mac's desktop (there is no window server here, over SSH, say); start the app with --log <path> and use /log --file to read the log in your editor.", LogViewText.UnavailableMac);
        Assert.Equal("The process window needs the Mac's desktop (there is no window server here, over SSH, say); the model's process tool can still read a process's output.", ProcessWindowText.UnavailableMac);
        Assert.Equal(OperatingSystem.IsMacOS() ? LogViewText.UnavailableMac : LogViewText.Unavailable, LogViewText.UnavailableHere);
        Assert.Equal(OperatingSystem.IsMacOS() ? ProcessWindowText.UnavailableMac : ProcessWindowText.Unavailable, ProcessWindowText.UnavailableHere);
    }

    [Fact]
    public void TerminalPick_TheParentChainFirst_ThenTermProgram()
    {
        // zsh → login → Terminal → launchd, as measured on 2026-10-07: Terminal is the first app in the chain.
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 300, [300] = 200, [200] = 1 };
        var chain = TerminalPick.Ancestors(500, pid => parents.TryGetValue(pid, out int p) ? p : null);
        Assert.Equal([400, 300, 200, 1], chain);
        Assert.Equal(200, TerminalPick.FirstApp(chain, pid => pid == 200));
        Assert.Null(TerminalPick.FirstApp(chain, _ => false));                       // tmux, ssh: no app in the chain
        Assert.Null(TerminalPick.FirstApp([1], _ => true));                          // launchd is never the terminal
        Assert.Equal("com.apple.Terminal", TerminalPick.BundleFor("Apple_Terminal"));
        Assert.Equal("com.googlecode.iterm2", TerminalPick.BundleFor("iTerm.app"));
        Assert.Equal("com.googlecode.iterm2", TerminalPick.BundleFor("ITERM.APP"));
        Assert.Null(TerminalPick.BundleFor("tmux"));
        Assert.Null(TerminalPick.BundleFor(null));
        Assert.Null(TerminalPick.BundleFor(""));
    }

    [Fact]
    public void TerminalPick_Ancestors_StopAtAnUnreadableParent_ALoop_OrTheLimit()
    {
        Assert.Equal([7], TerminalPick.Ancestors(9, pid => pid == 9 ? 7 : null));          // 7's parent unreadable (another user's)
        Assert.Equal([2, 3], TerminalPick.Ancestors(1000, pid => pid switch { 1000 => 2, 2 => 3, _ => 2 }));   // a loop ends it
        Assert.Equal(32, TerminalPick.Ancestors(10_000, pid => pid + 1).Count);
        Assert.Empty(TerminalPick.Ancestors(5, _ => 0));
    }

    [Fact]
    public void Flip_IsItsOwnInverse_AndMeasuresDownFromTheFirstScreensTop()
    {
        var cocoa = new PlaceRect(100, 600, 1024, 768);   // y up: its bottom 600 above the first screen's bottom
        var topLeft = MacPlacement.Flip(cocoa, 1440);
        Assert.Equal(new PlaceRect(100, 72, 1024, 768), topLeft);
        Assert.Equal(cocoa, MacPlacement.Flip(topLeft, 1440));
        Assert.Equal((100, 72), MacPlacement.Corner(topLeft));
        Assert.Equal((-1281, 3), MacPlacement.Corner(new PlaceRect(-1280.6, 2.5, 10, 10)));
    }

    [Fact]
    public void Restore_NothingSaved_IsTheSystemsPlace()
    {
        Assert.Null(MacPlacement.Restore(null, 1024, 768, [new PlaceRect(0, 25, 2560, 1415)]));
    }

    [Fact]
    public void Restore_OnScreen_StaysWhereItWas()
    {
        Assert.Equal(new PlaceRect(200, 100, 1024, 768), MacPlacement.Restore((200, 100), 1024, 768, [new PlaceRect(0, 25, 2560, 1415)]));
    }

    [Fact]
    public void Restore_HalfOff_IsMovedIn_UnderTheMenuBar()
    {
        var screen = new PlaceRect(0, 25, 2560, 1415);
        Assert.Equal(new PlaceRect(1536, 25, 1024, 768), MacPlacement.Restore((2000, -50), 1024, 768, [screen]));
    }

    [Fact]
    public void Restore_OnAScreenUnplugged_IsTheSystemsPlace()
    {
        // Saved on a second screen at the right that is gone now.
        Assert.Null(MacPlacement.Restore((3000, 200), 1024, 768, [new PlaceRect(0, 25, 2560, 1415)]));
    }

    [Fact]
    public void Restore_TwoScreens_GoesToTheOneItOverlapsMost_AndShrinksToFit()
    {
        var main = new PlaceRect(0, 25, 2560, 1415);
        var left = new PlaceRect(-1280, -200, 1280, 800);   // a smaller screen to the left, higher up
        var place = MacPlacement.Restore((-1100, -100), 1600, 1000, [main, left]);
        Assert.Equal(new PlaceRect(-1280, -200, 1280, 800), place);
        Assert.Equal(new PlaceRect(-1024, -168, 1024, 768), MacPlacement.Restore((-1000, 0), 1024, 768, [main, left]));   // moved in whole
    }

    [Fact]
    public void Overlap_IsZeroApart()
    {
        Assert.Equal(0, new PlaceRect(0, 0, 10, 10).Overlap(new PlaceRect(10, 0, 10, 10)));
        Assert.Equal(25, new PlaceRect(0, 0, 10, 10).Overlap(new PlaceRect(5, 5, 10, 10)));
    }

    /// <summary>
    /// <c>/terminal</c>'s <c>open</c> arguments on a Mac (2026-10-07): the first ancestor inside an app bundle by its path when it is
    /// Terminal or iTerm2 (the chains measured that day: zsh → login → Terminal; zsh → login → iTermServer → iTerm2), Terminal for
    /// any other app, then TERM_PROGRAM by bundle id, then Terminal; the folder always its own last argument, spaces and quotes as
    /// they are.
    /// </summary>
    [Fact]
    public void TerminalPick_OpensTheTerminalTheAppRunsIn_ElseTerminal()
    {
        const string folder = "/Users/me/odd dir/ä 'q\"; $x & (y)";
        string?[] terminal = ["/bin/zsh", "/usr/bin/login", "/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal", "/sbin/launchd"];
        string?[] iterm = ["/bin/zsh", "/usr/bin/login", "/Users/me/Library/Application Support/iTerm2/iTermServer-3.7.3", "/Applications/iTerm.app/Contents/MacOS/iTerm2"];
        string?[] vscode = ["/bin/zsh", "/Applications/Visual Studio Code.app/Contents/Frameworks/Code Helper.app/Contents/MacOS/Code Helper", "/Applications/Visual Studio Code.app/Contents/MacOS/Electron"];
        string?[] tmux = ["/bin/zsh", "/opt/homebrew/bin/tmux", null];

        Assert.Equal(["-a", "/System/Applications/Utilities/Terminal.app", folder], TerminalPick.OpenTerminalArguments(terminal, "iTerm.app", folder));
        Assert.Equal(["-a", "/Applications/iTerm.app", folder], TerminalPick.OpenTerminalArguments(iterm, "Apple_Terminal", folder));
        Assert.Equal(["-b", "com.apple.Terminal", folder], TerminalPick.OpenTerminalArguments(vscode, "vscode", folder));
        Assert.Equal(["-b", "com.googlecode.iterm2", folder], TerminalPick.OpenTerminalArguments(tmux, "iTerm.app", folder));
        Assert.Equal(["-b", "com.apple.Terminal", folder], TerminalPick.OpenTerminalArguments(tmux, "tmux", folder));
        Assert.Equal(["-b", "com.apple.Terminal", folder], TerminalPick.OpenTerminalArguments([], null, folder));
        Assert.Equal("/Applications/Visual Studio Code.app/Contents/Frameworks/Code Helper.app", TerminalPick.AppBundleOf(vscode[1]));
        Assert.Null(TerminalPick.AppBundleOf("/usr/bin/login"));
        Assert.Null(TerminalPick.AppBundleOf("Terminal.app/Contents/MacOS/Terminal"));   // not a full path
        Assert.Null(TerminalPick.AppBundleOf(null));
    }

    /// <summary>
    /// /shortcut's .command file opens in /terminal's terminal (2026-10-08, the user's pick): the bundle found among the ancestors by
    /// its path, Terminal for any other app, else TERM_PROGRAM's iTerm2 or Terminal by bundle id.
    /// </summary>
    [Fact]
    public void TerminalPick_AppFor_IsTheTerminalTheAppRunsIn_ByPathOrId()
    {
        string?[] terminal = ["/bin/zsh", "/usr/bin/login", "/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal"];
        string?[] iterm = ["/bin/zsh", "/Applications/iTerm.app/Contents/MacOS/iTerm2"];
        string?[] vscode = ["/bin/zsh", "/Applications/Visual Studio Code.app/Contents/MacOS/Electron"];

        Assert.Equal(new TerminalApp("/System/Applications/Utilities/Terminal.app", TerminalPick.TerminalBundle), TerminalPick.AppFor(terminal, "iTerm.app"));
        Assert.Equal(new TerminalApp("/Applications/iTerm.app", TerminalPick.ITermBundle), TerminalPick.AppFor(iterm, null));
        Assert.Equal(new TerminalApp(null, TerminalPick.TerminalBundle), TerminalPick.AppFor(vscode, "vscode"));
        Assert.Equal(new TerminalApp(null, TerminalPick.ITermBundle), TerminalPick.AppFor(["/bin/zsh", null], "iTerm.app"));
        Assert.Equal(new TerminalApp(null, TerminalPick.TerminalBundle), TerminalPick.AppFor([], null));
    }
}
