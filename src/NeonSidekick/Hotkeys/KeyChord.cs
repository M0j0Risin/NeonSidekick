namespace NeonSidekick.Hotkeys;

/// <summary>
/// One key with its modifiers, as <c>/keycheck</c> asks Windows about it (2026-10-04, the user's ask: the NVIDIA overlay had
/// held Ctrl+Alt+M and Ctrl+Alt+R, so <c>/memory</c>'s chord never reached the app). <see cref="Key"/>'s number is the Windows
/// virtual-key code: <see cref="ConsoleKey"/>'s values are those codes (<c>.</c> is OemPeriod 0xBE, <c>/</c> Oem2 0xBF). Pure
/// and portable; only <see cref="WindowsHotkeyProbe"/> asks Windows.
/// </summary>
public readonly record struct KeyChord(bool Ctrl, bool Alt, bool Shift, ConsoleKey Key)
{
    /// <summary>RegisterHotKey's modifier bits.</summary>
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    /// <summary>At least one modifier is held: a chord, not a bare key.</summary>
    public bool HasModifier => Ctrl || Alt || Shift;

    /// <summary>The modifiers as RegisterHotKey's bits.</summary>
    public uint Modifiers => (Ctrl ? ModControl : 0) | (Alt ? ModAlt : 0) | (Shift ? ModShift : 0);

    /// <summary>The key as a Windows virtual-key code.</summary>
    public uint VirtualKey => (uint)Key;

    /// <summary>
    /// A key label as <c>/help</c>'s Keys tab writes it (<c>ChatScreen.KeyRows</c>): <c>Ctrl+Alt+M</c>, <c>Ctrl+.</c>, <c>Ctrl+/</c>,
    /// <c>Ctrl+Home</c>, <c>Ctrl+Enter</c>, <c>Alt+V</c>, <c>F4</c> — modifiers then one key, joined by <c>+</c>, any case. False for
    /// anything else (<c>ESC ESC</c>, <c>Up / Down</c>, a spoken phrase). Hand-written, no enum parsing, so AOT keeps it as is.
    /// </summary>
    public static bool TryParse(string? label, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        string text = label.Trim();
        bool ctrl = false, alt = false, shift = false;
        while (true)
        {
            int plus = text.IndexOf('+', StringComparison.Ordinal);
            if (plus <= 0 || plus == text.Length - 1)
            {
                break;
            }

            string modifier = text[..plus];
            if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
            }
            else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
            else
            {
                return false;
            }

            text = text[(plus + 1)..];
        }

        if (KeyOf(text) is not { } key)
        {
            return false;
        }

        chord = new KeyChord(ctrl, alt, shift, key);
        return true;
    }

    /// <summary>The key a label's last part names, or null.</summary>
    private static ConsoleKey? KeyOf(string name)
    {
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            return c switch
            {
                >= 'A' and <= 'Z' => (ConsoleKey)c,
                >= '0' and <= '9' => (ConsoleKey)c,
                '.' => ConsoleKey.OemPeriod,
                '/' => ConsoleKey.Oem2,
                ',' => ConsoleKey.OemComma,
                '-' => ConsoleKey.OemMinus,
                _ => null,
            };
        }

        if (name.Length is 2 or 3 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            return (ConsoleKey)((int)ConsoleKey.F1 + f - 1);
        }

        return name.ToUpperInvariant() switch
        {
            "HOME" => ConsoleKey.Home,
            "END" => ConsoleKey.End,
            "ENTER" => ConsoleKey.Enter,
            "TAB" => ConsoleKey.Tab,
            "SPACE" or "SPACEBAR" => ConsoleKey.Spacebar,
            "ESC" or "ESCAPE" => ConsoleKey.Escape,
            "INSERT" or "INS" => ConsoleKey.Insert,
            "DELETE" or "DEL" => ConsoleKey.Delete,
            "BACKSPACE" => ConsoleKey.Backspace,
            "PGUP" or "PAGEUP" => ConsoleKey.PageUp,
            "PGDN" or "PAGEDOWN" => ConsoleKey.PageDown,
            "UP" or "UPARROW" => ConsoleKey.UpArrow,
            "DOWN" or "DOWNARROW" => ConsoleKey.DownArrow,
            "LEFT" or "LEFTARROW" => ConsoleKey.LeftArrow,
            "RIGHT" or "RIGHTARROW" => ConsoleKey.RightArrow,
            "PAUSE" => ConsoleKey.Pause,
            _ => null,
        };
    }
}
