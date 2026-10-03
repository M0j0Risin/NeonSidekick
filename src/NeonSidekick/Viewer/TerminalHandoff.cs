using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>What a key a window of the app's own has no use for does instead (<see cref="TerminalHandoff.Decide"/>).</summary>
public enum WindowKey
{
    /// <summary>Nothing: the window's default handling (a plain key, Alt+F4's close, Alt+Space's system menu).</summary>
    None,

    /// <summary>TAB: the terminal brought to the front.</summary>
    Focus,

    /// <summary>A Ctrl or Alt chord: handed to the chat as if pressed there, the keyboard left where it is.</summary>
    Pass,
}

/// <summary>
/// The way back from the picture viewer, the camera's window and the log window to the terminal (2026-10-03, the user's ask):
/// a Ctrl or Alt chord the window does not use itself (Ctrl+Alt+T, Ctrl+M, Ctrl+Alt+X…) is handed to the chat through
/// <see cref="Passed"/> — <c>WindowsConsoleInput.Inject</c>, so it is read as though typed in the terminal and
/// <c>ChatScreen.CloseByChord</c> closes the window its own chord opened — and TAB brings the terminal forward. The user's
/// picks: only chords (a plain key, Enter or a letter still does nothing in a window), and the keyboard stays in the window
/// after a chord, TAB being the way back. A key with Alt held is never the window's own (F10, which Windows sends as a
/// system key without Alt, aside), so Ctrl+Alt+C is <c>/clear</c> even in the log window, whose Ctrl+C copies. Ctrl+C itself
/// is passed from the viewer and the camera's window, which have no copy: there it cancels a reply as in the terminal.
///
/// <para>The terminal's window is found once, at startup (<see cref="Remember"/>): the console window's root owner — under
/// Windows Terminal the console window is ConPTY's hidden pseudo-window, which the terminal owns; under conhost it is the
/// console window itself — or, when that leads to nothing visible, the window in front as the app started (the terminal
/// that launched it). <see cref="Decide"/> and <see cref="ToKey"/> are pure and pinned; the rest runs on a window's thread.</para>
/// </summary>
public static class TerminalHandoff
{
    private const int VkTab = 0x09;
    private const int VkSpace = 0x20;
    private const int VkF4 = 0x73;

    private static IntPtr s_terminal;

    /// <summary>Where a passed chord goes: set by <c>Program</c> to the console input's <c>Inject</c>. Called on a window's thread; must not block.</summary>
    public static Action<ConsoleKeyInfo>? Passed { get; set; }

    /// <summary>
    /// What a key the window has no use for does: TAB with neither Ctrl nor Alt (Shift does not matter) brings the terminal forward;
    /// a key with Ctrl or Alt held is passed, unless it is a modifier or lock key itself, Alt+F4 (the window's close) or
    /// Alt+Space (its system menu); anything else is nothing. Pure.
    /// </summary>
    public static WindowKey Decide(int virtualKey, bool control, bool alt)
    {
        if (virtualKey == VkTab && !control && !alt)
        {
            return WindowKey.Focus;
        }

        if ((!control && !alt) || IsModifier(virtualKey))
        {
            return WindowKey.None;
        }

        if (alt && !control && virtualKey is VkF4 or VkSpace)
        {
            return WindowKey.None;
        }

        return WindowKey.Pass;
    }

    /// <summary>
    /// The chord as the console would deliver it, with no character — the shape <c>Keys.ShortcutLine</c>, <c>Keys.IsKillSwitch</c>
    /// and <c>Keys.IsInterrupt</c> all accept (a window's virtual-key code is the console's <see cref="ConsoleKey"/>). Pure.
    /// </summary>
    public static ConsoleKeyInfo ToKey(int virtualKey, bool control, bool alt, bool shift) =>
        new('\0', (ConsoleKey)virtualKey, shift, alt, control);

    /// <summary>The terminal's window found and kept (once, at startup, before any window opens). Windows only; nothing elsewhere.</summary>
    public static void Remember()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IntPtr console = GetConsoleWindow();
        IntPtr root = console == IntPtr.Zero ? IntPtr.Zero : GetAncestor(console, GaRootOwner);
        s_terminal = root != IntPtr.Zero && IsWindowVisible(root) ? root : GetForegroundWindow();
        DiagnosticLog.Debug("Viewer", s_terminal == IntPtr.Zero
            ? "No terminal window found: TAB in the app's windows does nothing."
            : $"Terminal window 0x{s_terminal:X} ({(s_terminal == root ? "the console's root owner" : "in front at startup")}).");
    }

    /// <summary>
    /// A key the window had no use for, on its thread: TAB brings the terminal forward, a chord goes to <see cref="Passed"/>.
    /// The modifiers are read as the key arrived. True when it was taken (the window returns 0, no default handling).
    /// </summary>
    internal static bool Take(int virtualKey)
    {
        bool control = GetKeyState(VkControl) < 0;
        bool alt = GetKeyState(VkMenu) < 0;
        bool shift = GetKeyState(VkShift) < 0;
        switch (Decide(virtualKey, control, alt))
        {
            case WindowKey.Focus:
                FocusTerminal();
                return true;
            case WindowKey.Pass when Passed is { } passed:
                passed(ToKey(virtualKey, control, alt, shift));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Whether Alt is held right now, on a window's thread: a key with Alt held is never the window's own.</summary>
    internal static bool AltHeld() => GetKeyState(VkMenu) < 0;

    // The terminal in front. The window the key came to is the foreground one, so Windows lets its thread hand the foreground on.
    private static void FocusTerminal()
    {
        IntPtr terminal = s_terminal;
        if (terminal == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(terminal))
        {
            ShowWindow(terminal, SwRestore);
        }

        if (!SetForegroundWindow(terminal))
        {
            DiagnosticLog.Debug("Viewer", "The terminal could not be brought forward.");
        }
    }

    private static bool IsModifier(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12          // Shift, Ctrl, Alt
            or >= 0xA0 and <= 0xA5                  // their left and right keys
            or 0x5B or 0x5C                         // the Windows keys
            or 0x14 or 0x90 or 0x91;                // Caps, Num and Scroll Lock
}
