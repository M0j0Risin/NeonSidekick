using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The way back from the app's own windows (2026-10-03, the user's ask): TAB brings the terminal forward, a Ctrl or Alt chord the
/// window has no use for is passed to the chat in the console's own shape, and plain keys, Alt+F4 and Alt+Space stay the window's.
/// </summary>
public sealed class TerminalHandoffTests
{
    /// <summary>
    /// A closing window hands the foreground back (2026-10-13, the user's ask) only when it, or a window it owns, is in front: never
    /// when the terminal or another app is, and never without a terminal or a window.
    /// </summary>
    [Fact]
    public void ShouldReturn_OnlyWhenTheClosingWindowHasTheFocus()
    {
        IntPtr viewer = new(0x100), menu = new(0x200), terminal = new(0x300), other = new(0x400);
        Assert.True(TerminalHandoff.ShouldReturn(viewer, viewer, viewer, terminal));
        Assert.True(TerminalHandoff.ShouldReturn(menu, viewer, viewer, terminal));      // its context menu in front, the viewer its owner
        Assert.False(TerminalHandoff.ShouldReturn(terminal, terminal, viewer, terminal)); // the terminal has it already
        Assert.False(TerminalHandoff.ShouldReturn(other, other, viewer, terminal));      // another app has it: left alone
        Assert.False(TerminalHandoff.ShouldReturn(viewer, viewer, viewer, IntPtr.Zero)); // no terminal found
        Assert.False(TerminalHandoff.ShouldReturn(IntPtr.Zero, IntPtr.Zero, viewer, terminal));
        Assert.False(TerminalHandoff.ShouldReturn(viewer, viewer, IntPtr.Zero, terminal));
    }

    [Theory]
    [InlineData(0x09, false, false, WindowKey.Focus)]   // TAB (Shift+TAB arrives the same: Shift is not looked at)
    [InlineData(0x09, true, false, WindowKey.Pass)]     // Ctrl+TAB is a chord
    [InlineData(0x09, false, true, WindowKey.Pass)]     // Alt+TAB never reaches a window; were it to, a chord
    [InlineData(0x41, false, false, WindowKey.None)]    // a plain A: nothing (only chords go back, the user's pick)
    [InlineData(0x0D, false, false, WindowKey.None)]    // a plain Enter
    [InlineData(0x74, false, false, WindowKey.None)]    // a plain F5
    [InlineData(0x4D, true, false, WindowKey.Pass)]     // Ctrl+M: /model
    [InlineData(0x54, true, true, WindowKey.Pass)]      // Ctrl+Alt+T: /tools
    [InlineData(0x58, false, true, WindowKey.Pass)]     // Alt+X
    [InlineData(0x73, false, true, WindowKey.None)]     // Alt+F4: the window's close
    [InlineData(0x73, true, true, WindowKey.Pass)]      // Ctrl+Alt+F4 is no close
    [InlineData(0x20, false, true, WindowKey.None)]     // Alt+Space: the system menu
    [InlineData(0x11, true, false, WindowKey.None)]     // Ctrl alone
    [InlineData(0x12, false, true, WindowKey.None)]     // Alt alone
    [InlineData(0xA2, true, false, WindowKey.None)]     // left Ctrl
    [InlineData(0xA5, true, true, WindowKey.None)]      // right Alt (AltGr)
    [InlineData(0x10, true, false, WindowKey.None)]     // Shift pressed with Ctrl held
    [InlineData(0x5B, true, false, WindowKey.None)]     // the Windows key
    [InlineData(0x14, true, false, WindowKey.None)]     // Caps Lock
    public void Decide_TabFocuses_ChordsPass_TheRestStay(int key, bool control, bool alt, WindowKey expected) =>
        Assert.Equal(expected, TerminalHandoff.Decide(key, control, alt));

    [Fact]
    public void ToKey_IsTheConsolesShape_SoTheChatReadsItAsTyped()
    {
        Assert.Equal("/log", Keys.ShortcutLine(TerminalHandoff.ToKey((int)ConsoleKey.G, control: true, alt: false, shift: false)));
        Assert.Equal("/comfy view", Keys.ShortcutLine(TerminalHandoff.ToKey((int)ConsoleKey.U, control: true, alt: true, shift: false)));
        Assert.Equal("/model", Keys.ShortcutLine(TerminalHandoff.ToKey((int)ConsoleKey.M, control: true, alt: false, shift: false)));
        Assert.True(Keys.IsKillSwitch(TerminalHandoff.ToKey((int)ConsoleKey.X, control: true, alt: true, shift: false)));
        Assert.True(Keys.IsInterrupt(TerminalHandoff.ToKey((int)ConsoleKey.C, control: true, alt: false, shift: false)));

        var key = TerminalHandoff.ToKey((int)ConsoleKey.T, control: true, alt: true, shift: true);
        Assert.Equal('\0', key.KeyChar);
        Assert.Equal(ConsoleKey.T, key.Key);
        Assert.Equal(ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift, key.Modifiers);
        Assert.Null(Keys.ShortcutLine(key));   // Ctrl+Alt+Shift+T is no chord of the chat's: passed, and nothing happens there
    }
}
