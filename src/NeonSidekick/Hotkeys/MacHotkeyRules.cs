namespace NeonSidekick.Hotkeys;

/// <summary>One of macOS's own shortcuts as Carbon's <c>CopySymbolicHotKeys</c> lists it: the key code, Carbon's modifiers, on or off.</summary>
public readonly record struct SymbolicHotKey(ushort Code, uint Modifiers, bool Enabled);

/// <summary>What the terminal makes of the Option key: Alt (ESC then the key, which the app reads), characters, or not known.</summary>
public enum OptionKey
{
    Unknown,
    Alt,
    Types,
}

/// <summary>
/// A Mac's <c>/keycheck</c> decisions (2026-10-11, the user's pick after the spike of that day, <c>.docs/mac_keycheck_spike.md</c>):
/// pure, so they are pinned on every OS; <see cref="MacHotkeyProbe"/> only reads the system and hands the facts here. A chord is
/// held by macOS when an enabled entry of <c>CopySymbolicHotKeys</c> names it (no id or name comes with an entry, so the row says
/// only "macOS shortcut"); else by another app when Carbon's <c>RegisterEventHotKey</c> with <c>kEventHotKeyExclusive</c> answers
/// <c>eventHotKeyExistsErr</c> (measured: it sees only exclusive registrations, the system's Ctrl+arrows among them; most apps
/// register the shared way and answer free, a blind spot the caveat names); else, for an Option chord, by the terminal when its
/// Option key types characters. The key codes are <c>kVK_*</c> (Carbon's Events.h), a table of its own: <c>Viewer/MacKeys</c> goes
/// the other way and folds keypad Enter into Return.
/// </summary>
public static class MacHotkeyRules
{
    /// <summary>Carbon's modifier bits (Events.h): ⌘, ⇧, ⌥, ⌃, and fn/Globe, which the arrows and F-keys carry of themselves.</summary>
    public const uint CmdKey = 0x0100;
    public const uint ShiftKey = 0x0200;
    public const uint OptionKeyBit = 0x0800;
    public const uint ControlKey = 0x1000;
    public const uint FnKey = 0x20000;

    /// <summary>The four modifiers a chord is matched on; caps lock and fn left out.</summary>
    public const uint ChordModifiers = CmdKey | ShiftKey | OptionKeyBit | ControlKey;

    /// <summary>An entry with no key (macOS lists them for shortcuts with none set).</summary>
    public const ushort NoCode = 0xFFFF;

    /// <summary><c>kEventHotKeyExclusive</c>: measured 1 (every other bit answers <c>paramErr</c>, -50).</summary>
    public const uint Exclusive = 1;

    /// <summary><c>noErr</c> and <c>eventHotKeyExistsErr</c>: the exclusive register took (free), or another holds it exclusively.</summary>
    public const int NoErr = 0;
    public const int HotKeyExists = -9878;

    /// <summary>The <c>TERM_PROGRAM</c> values of the two terminals whose Option setting the app reads.</summary>
    public const string TerminalApp = "Apple_Terminal";
    public const string Iterm = "iTerm.app";

    /// <summary>The preference domains (bundle ids) the two terminals keep their settings under.</summary>
    public const string TerminalAppDomain = "com.apple.Terminal";
    public const string ItermDomain = "com.googlecode.iterm2";

    /// <summary>iTerm2's <c>Option Key Sends</c> value for Esc+, the only one the app reads as Alt (1, Meta, sets the high bit: no ESC).</summary>
    public const int ItermEscPlus = 2;

    /// <summary>The chord's modifiers as Carbon's bits (Ctrl ⌃, Alt ⌥, Shift ⇧; a chord never has ⌘).</summary>
    public static uint ModifiersOf(KeyChord chord) =>
        (chord.Ctrl ? ControlKey : 0) | (chord.Alt ? OptionKeyBit : 0) | (chord.Shift ? ShiftKey : 0);

    // kVK_ANSI_A … kVK_ANSI_Z and kVK_ANSI_0 … kVK_ANSI_9, in the keyboard's own order.
    private static readonly ushort[] s_letters = [0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6];
    private static readonly ushort[] s_digits = [29, 18, 19, 20, 21, 23, 22, 26, 28, 25];

    // kVK_F1 … kVK_F20 (a Mac keyboard has no F21–F24).
    private static readonly ushort[] s_fkeys = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90];

    /// <summary>The Mac key code of <paramref name="key"/>; null for a key a Mac keyboard does not have (F21–F24, Pause). Pure.</summary>
    public static ushort? KeyCodeOf(ConsoleKey key) => key switch
    {
        >= ConsoleKey.A and <= ConsoleKey.Z => s_letters[key - ConsoleKey.A],
        >= ConsoleKey.D0 and <= ConsoleKey.D9 => s_digits[key - ConsoleKey.D0],
        >= ConsoleKey.F1 and <= ConsoleKey.F20 => s_fkeys[key - ConsoleKey.F1],
        ConsoleKey.Enter => 36,
        ConsoleKey.Tab => 48,
        ConsoleKey.Spacebar => 49,
        ConsoleKey.Backspace => 51,     // the Mac's Delete
        ConsoleKey.Escape => 53,
        ConsoleKey.OemPeriod => 47,
        ConsoleKey.Oem2 => 44,          // /
        ConsoleKey.Oem6 => 30,          // ]
        ConsoleKey.OemComma => 43,
        ConsoleKey.OemMinus => 27,
        ConsoleKey.Insert => 114,       // Help, where a Mac keyboard has one
        ConsoleKey.Home => 115,
        ConsoleKey.PageUp => 116,
        ConsoleKey.Delete => 117,       // forward delete
        ConsoleKey.End => 119,
        ConsoleKey.PageDown => 121,
        ConsoleKey.LeftArrow => 123,
        ConsoleKey.RightArrow => 124,
        ConsoleKey.DownArrow => 125,
        ConsoleKey.UpArrow => 126,
        _ => null,
    };

    /// <summary>
    /// A key that carries fn of itself (the F-keys; Help, Home, PgUp, forward delete, End, PgDn and the arrows, 114–126): its entries
    /// hold fn whether or not fn is pressed, so fn is no part of the match. On any other key fn means the Globe key (0x21000 on C, F,
    /// R is macOS 15's Globe+Ctrl tiling, not Ctrl+C/F/R). Pure.
    /// </summary>
    public static bool CarriesFn(ushort code) => code is >= 114 and <= 126 || Array.IndexOf(s_fkeys, code) >= 0;

    /// <summary>Whether <paramref name="entry"/> is on and is the chord <paramref name="code"/> with Carbon modifiers <paramref name="modifiers"/>. Pure.</summary>
    public static bool Matches(SymbolicHotKey entry, ushort code, uint modifiers) =>
        entry.Enabled
        && entry.Code != NoCode
        && entry.Code == code
        && (entry.Modifiers & ChordModifiers) == (modifiers & ChordModifiers)
        && (CarriesFn(code) || (entry.Modifiers & FnKey) == 0);

    /// <summary>
    /// The chord's answer: macOS's list first, then the exclusive register (<paramref name="register"/>, the code and modifiers to an
    /// OSStatus), then the terminal for an Option chord. A key with no Mac code is not asked. Pure but for <paramref name="register"/>.
    /// </summary>
    public static HotkeyProbeResult Decide(KeyChord chord, IReadOnlyList<SymbolicHotKey> symbolic, Func<ushort, uint, int> register, OptionKey option)
    {
        ArgumentNullException.ThrowIfNull(symbolic);
        ArgumentNullException.ThrowIfNull(register);
        if (KeyCodeOf(chord.Key) is not { } code)
        {
            return new HotkeyProbeResult(HotkeyStatus.Unknown, 0, HotkeyCause.NoKeyCode);
        }

        uint modifiers = ModifiersOf(chord);
        if (symbolic.Any(entry => Matches(entry, code, modifiers)))
        {
            return new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.System);
        }

        int status = register(code, modifiers);
        if (status == HotKeyExists)
        {
            return new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.OtherApp);
        }

        if (status != NoErr)
        {
            return new HotkeyProbeResult(HotkeyStatus.Unknown, status);
        }

        return chord.Alt && option == OptionKey.Types
            ? new HotkeyProbeResult(HotkeyStatus.Held, 0, HotkeyCause.Terminal)
            : new HotkeyProbeResult(HotkeyStatus.Free);
    }

    /// <summary>Terminal.app by its default profile; <paramref name="meta"/> null (never set) is Terminal's default, off. Null profile: unreadable.</summary>
    public static (OptionKey Option, string Note) TerminalAppOption(string? profile, bool? meta)
    {
        if (string.IsNullOrEmpty(profile))
        {
            return (OptionKey.Unknown, KeyCheckText.UnknownOption("Terminal"));
        }

        bool on = meta ?? false;
        return (on ? OptionKey.Alt : OptionKey.Types, KeyCheckText.TerminalAppOption(profile, on));
    }

    /// <summary>iTerm2 by a profile's two settings (never set is 0, Normal); Alt arrives when either key sends Esc+. Null profile: none found.</summary>
    public static (OptionKey Option, string Note) ItermOption(string? profile, int? left, int? right)
    {
        if (string.IsNullOrEmpty(profile))
        {
            return (OptionKey.Unknown, KeyCheckText.UnknownOption("iTerm2"));
        }

        bool l = left == ItermEscPlus, r = right == ItermEscPlus;
        return (l || r ? OptionKey.Alt : OptionKey.Types, KeyCheckText.ItermOption(profile, l, r));
    }

    /// <summary>
    /// Which of iTerm2's profiles (name, guid) this session has: the one <c>ITERM_PROFILE</c> names (iTerm2 sets it to the profile's
    /// name), else the default by guid; -1 for none. Pure.
    /// </summary>
    public static int ItermProfileIndex(IReadOnlyList<(string? Name, string? Guid)> profiles, string? sessionProfile, string? defaultGuid)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (!string.IsNullOrEmpty(sessionProfile))
        {
            for (int i = 0; i < profiles.Count; i++)
            {
                if (string.Equals(profiles[i].Name, sessionProfile, StringComparison.Ordinal))
                {
                    return i;
                }
            }
        }

        for (int i = 0; i < profiles.Count; i++)
        {
            if (defaultGuid is not null && string.Equals(profiles[i].Guid, defaultGuid, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
