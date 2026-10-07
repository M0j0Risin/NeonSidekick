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
        Assert.Equal("(← or wheel up newer · → or wheel down older · Home newest · End oldest · right-click picture menu · F9 slide show · F10 random · ↑ ↓ slide time · ⌃⌘F full screen · ⌫ twice delete · ESC or ⌘W close)", ViewerText.KeysMac);
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
        Assert.Equal("(click shows a picture in the viewer · double-click or Enter opens it · arrows move · right-click for the picture menu · + − ⌘+wheel or pinch size · ⌫ twice deletes · F5 refresh · ⌃⌘F full screen · ESC or ⌘W close)", ThumbsText.KeysMac);
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
}
