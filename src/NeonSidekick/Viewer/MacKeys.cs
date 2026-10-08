namespace NeonSidekick.Viewer;

/// <summary>
/// A Mac key as the Windows key the app's windows already decide on (2026-10-07, the windows over AppKit): AppKit's hardware
/// key codes (<c>kVK_*</c>, Carbon's Events.h — the key, never the character, which Option changes) mapped to the virtual-key
/// codes <see cref="ViewerState.ActionFor"/> and <see cref="TerminalHandoff.Decide"/> take, so those stay shared and pinned.
/// And the Mac's own extras for the picture viewer (the user's call that day): ⌫ deletes as Del does (twice), ⌘W closes, ⌃⌘F
/// is full screen beside F11 (which macOS keeps for Show Desktop by default: measured that day, F11 pushed every window
/// aside), Option held is never the viewer's (Windows' Alt rule) and Command is only its extras'. Pure.
/// </summary>
public static class MacKeys
{
    /// <summary>NSEvent's modifier flags.</summary>
    public const ulong ShiftFlag = 1UL << 17;
    public const ulong ControlFlag = 1UL << 18;
    public const ulong OptionFlag = 1UL << 19;
    public const ulong CommandFlag = 1UL << 20;

    /// <summary>The Windows virtual key for Backspace — a Mac's delete key.</summary>
    public const int VkBack = 0x08;

    private static readonly Dictionary<ushort, int> s_keys = Build();

    /// <summary>The Windows virtual-key code of Mac key <paramref name="keyCode"/>; 0 for a key the app's windows never name. Pure.</summary>
    public static int ToVirtualKey(ushort keyCode) => s_keys.TryGetValue(keyCode, out int vk) ? vk : 0;

    /// <summary>
    /// What a key does in the picture viewer on a Mac: the Mac extras first (⌘W, ⌃⌘F), nothing else with ⌘ or ⌥ held, else
    /// <see cref="ViewerState.ActionFor"/>'s answer with ⌫ read as Del. Pure.
    /// </summary>
    public static ViewerAction ViewerAction(ushort keyCode, ulong flags, bool fullScreen, bool slideShow)
    {
        int vk = ToVirtualKey(keyCode);
        bool command = (flags & CommandFlag) != 0;
        bool control = (flags & ControlFlag) != 0;
        bool option = (flags & OptionFlag) != 0;
        bool shift = (flags & ShiftFlag) != 0;
        if (command)
        {
            return option ? Viewer.ViewerAction.None
                : vk == 'W' && !control ? Viewer.ViewerAction.Close
                : vk == 'F' && control ? Viewer.ViewerAction.ToggleFullScreen
                : Viewer.ViewerAction.None;
        }

        if (option)
        {
            return Viewer.ViewerAction.None;
        }

        return ViewerState.ActionFor(vk == VkBack ? ViewerState.VkDelete : vk, fullScreen, slideShow, shift);
    }

    /// <summary>
    /// What a key does in the thumbnail browser on a Mac (2026-10-07, phase 2): ⌘W closes, ⌃⌘F is full screen, ⌘= and ⌘− size the tiles
    /// (a Mac's own zoom keys), nothing else with ⌘ or ⌥ held, else <see cref="ThumbsState.ActionFor"/>'s answer with ⌫ read as Del. Pure.
    /// </summary>
    public static ThumbsAction ThumbsAction(ushort keyCode, ulong flags, bool fullScreen)
    {
        int vk = ToVirtualKey(keyCode);
        bool command = (flags & CommandFlag) != 0;
        bool control = (flags & ControlFlag) != 0;
        bool option = (flags & OptionFlag) != 0;
        bool shift = (flags & ShiftFlag) != 0;
        if (command)
        {
            return option ? Viewer.ThumbsAction.None
                : vk == 'W' && !control ? Viewer.ThumbsAction.Close
                : vk == 'F' && control ? Viewer.ThumbsAction.ToggleFullScreen
                : vk is ThumbsState.VkOemPlus or ThumbsState.VkAdd && !control ? Viewer.ThumbsAction.ZoomIn
                : vk is ThumbsState.VkOemMinus or ThumbsState.VkSubtract && !control ? Viewer.ThumbsAction.ZoomOut
                : Viewer.ThumbsAction.None;
        }

        if (option)
        {
            return Viewer.ThumbsAction.None;
        }

        return ThumbsState.ActionFor(vk == VkBack ? ThumbsState.VkDelete : vk, control, shift, fullScreen);
    }

    /// <summary>
    /// What a key does in the log and process windows on a Mac (2026-10-07, phase 3): ⌘C copies and ⌘A selects all (a Mac's own, so
    /// Ctrl+C and Ctrl+A are not the window's there), ⌘↑ the top and ⌘↓ the bottom beside Windows' Ctrl+Home and Ctrl+End or Ctrl+E,
    /// ⌘W closes, ⌃⌘F is full screen, nothing else with ⌘ or ⌥ held, else <see cref="LogViewState.ActionFor"/>'s answer. Pure.
    /// </summary>
    public static LogViewAction LogAction(ushort keyCode, ulong flags, bool fullScreen)
    {
        int vk = ToVirtualKey(keyCode);
        bool command = (flags & CommandFlag) != 0;
        bool control = (flags & ControlFlag) != 0;
        bool option = (flags & OptionFlag) != 0;
        if (option)
        {
            return LogViewAction.None;
        }

        if (command)
        {
            return (vk, control) switch
            {
                ('W', false) => LogViewAction.Close,
                ('F', true) => LogViewAction.ToggleFullScreen,
                ('C', false) => LogViewAction.Copy,
                ('A', false) => LogViewAction.SelectAll,
                (ViewerState.VkUp, false) => LogViewAction.Top,
                (ViewerState.VkDown, false) => LogViewAction.Bottom,
                _ => LogViewAction.None,
            };
        }

        var action = LogViewState.ActionFor(vk, control, fullScreen);
        return action is LogViewAction.Copy or LogViewAction.SelectAll ? LogViewAction.None : action;
    }

    /// <summary>
    /// What a key does in the video window on a Mac (2026-10-07, YouTube playback on macOS): ⌘W closes and ⌃⌘F is full screen (the
    /// other windows' extras; F11 is macOS's Show Desktop by default), nothing else with ⌘ or ⌥ held, else
    /// <see cref="VideoKeys.ActionFor"/>'s answer — Esc out of full screen and then closing. What is left goes to the terminal when
    /// <see cref="TerminalHandoff.Decide"/> wants it (TAB, a Control chord) and to the player otherwise (Space, the arrows, K, M). Pure.
    /// </summary>
    public static VideoKeyAction VideoAction(ushort keyCode, ulong flags, bool fullScreen)
    {
        int vk = ToVirtualKey(keyCode);
        bool command = (flags & CommandFlag) != 0;
        bool control = (flags & ControlFlag) != 0;
        bool option = (flags & OptionFlag) != 0;
        if (command)
        {
            return option ? VideoKeyAction.None
                : vk == 'W' && !control ? VideoKeyAction.Close
                : vk == 'F' && control ? VideoKeyAction.ToggleFullScreen
                : VideoKeyAction.None;
        }

        return VideoKeys.ActionFor(vk, fullScreen, control, option);
    }

    private static Dictionary<ushort, int> Build()
    {
        var keys = new Dictionary<ushort, int>
        {
            [36] = 0x0D,   // Return
            [48] = 0x09,   // Tab
            [49] = 0x20,   // Space
            [51] = VkBack, // Delete (backspace)
            [53] = 0x1B,   // Escape
            [24] = 0xBB,   // = (+ with Shift)
            [27] = 0xBD,   // -
            [69] = 0x6B,   // keypad +
            [78] = 0x6D,   // keypad -
            [76] = 0x0D,   // keypad Enter
            [115] = 0x24,  // Home
            [116] = 0x21,  // Page Up
            [117] = 0x2E,  // Forward Delete
            [119] = 0x23,  // End
            [121] = 0x22,  // Page Down
            [123] = 0x25,  // ←
            [124] = 0x27,  // →
            [125] = 0x28,  // ↓
            [126] = 0x26,  // ↑
            [122] = 0x70, [120] = 0x71, [99] = 0x72, [118] = 0x73,   // F1–F4
            [96] = 0x74, [97] = 0x75, [98] = 0x76, [100] = 0x77,     // F5–F8
            [101] = 0x78, [109] = 0x79, [103] = 0x7A, [111] = 0x7B,  // F9–F12
        };

        // The ANSI letters and digits: kVK_ANSI_A = 0 … in the keyboard's own order.
        ushort[] letters = [0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6];
        for (int i = 0; i < letters.Length; i++)
        {
            keys[letters[i]] = 'A' + i;
        }

        ushort[] digits = [29, 18, 19, 20, 21, 23, 22, 26, 28, 25];
        for (int i = 0; i < digits.Length; i++)
        {
            keys[digits[i]] = '0' + i;
        }

        return keys;
    }
}
