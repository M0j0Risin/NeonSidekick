using System.Globalization;
using System.Text;

namespace NeonSidekick.UI;

/// <summary>
/// The terminal's input bytes as <see cref="InputEvent"/>s, for the Unix reader (<see cref="UnixConsoleInput"/>, 2026-10-06,
/// the macOS build). Pure: bytes in, events out, so every rule is pinned by tests on any OS. What it reads is what a VT
/// terminal sends in raw mode (no ICANON, ECHO, ISIG, IXON or ICRNL):
///
/// <list type="bullet">
/// <item>UTF-8 text, a character per key, split across reads as it may be. Control bytes are Ctrl+letter keys with the byte
/// as the character, as Windows' console reports them (Ctrl+C is <c>'\x03'</c> with <see cref="ConsoleKey.C"/>); CR is Enter,
/// LF (Ctrl+J, what some terminals send for Ctrl+Enter) is Enter with Control, the app's line break; DEL is Backspace.
/// BS is Ctrl+H (2026-10-07, the user's find: Ctrl+H, <c>/help</c>, did nothing on a Mac): it was Ctrl+Backspace until then, but
/// a Mac terminal's Delete sends DEL and a Mac deletes a word with Option+Delete (ESC DEL, Backspace with Alt), so BS is the
/// chord. GS (Ctrl+]) and US (Ctrl+/) are those keys with Control the same day: they were dropped, so <c>/settings</c>' Ctrl+/
/// never ran, and Ctrl+] is a Mac's <c>/terminal</c> (Ctrl+. sends a bare ".").</item>
/// <item>ESC then a key is that key with Alt (Option as Meta). A lone ESC is ESC, but only once the bytes stop:
/// ESC also opens every sequence below, so the reader calls <see cref="Flush"/> when nothing more came within its timeout.</item>
/// <item>CSI and SS3 keys (arrows, Home/End, Insert/Delete, PageUp/Down, F1–F12, Shift+Tab) with xterm's modifier
/// parameter (1 + Shift 1, Alt 2, Ctrl 4).</item>
/// <item>Bracketed paste (<c>ESC[200~</c> … <c>ESC[201~</c>) as one <see cref="InputEvent.Paste"/>, CR LF and lone CR as
/// <c>'\n'</c>: Cmd+V's text never submits a line, whatever reads it splits.</item>
/// <item>SGR mouse reports (<c>ESC[&lt;b;x;yM</c>/<c>m</c>): left and right presses are clicks, a motion with the left button
/// held a drag, the left release a release, the wheel (64 up, 65 down) a notch; 1-based cells become 0-based.</item>
/// <item>A cursor position report (<c>ESC[row;colR</c>) answers <see cref="CursorReport"/> while one is awaited
/// (<see cref="AwaitingCursor"/>); otherwise it is xterm's modified F3, which shares the form.</item>
/// </list>
///
/// Anything else (focus reports, a kitty key, an unknown CSI) is dropped whole, never typed into the draft.
/// </summary>
public sealed class VtInputParser
{
    private const char Esc = '\x1b';

    /// <summary>The longest sequence kept waiting for its end; past it the bytes are dropped (a stray ESC[ with no final).</summary>
    public const int MaxSequence = 64;

    private readonly Decoder _utf8 = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _pending = new();
    private StringBuilder? _paste;
    private char[] _chars = new char[256];
    private volatile bool _awaitingCursor;

    /// <summary>Set (from the asking thread) while a cursor position query waits; a <c>CSI r;c R</c> is then the answer, not F3.</summary>
    public bool AwaitingCursor
    {
        get => _awaitingCursor;
        set => _awaitingCursor = value;
    }

    /// <summary>Called with the 0-based row and column of a cursor position report while <see cref="AwaitingCursor"/>.</summary>
    public Action<int, int>? CursorReport { get; set; }

    /// <summary>True while bytes wait for the rest of a sequence (an ESC, an unfinished CSI); the reader flushes after its timeout.</summary>
    public bool HasPending => _pending.Length > 0;

    /// <summary>True inside a bracketed paste that has not ended yet.</summary>
    public bool InPaste => _paste is not null;

    /// <summary>Decodes <paramref name="bytes"/> and appends the events they complete to <paramref name="events"/>.</summary>
    public void Feed(ReadOnlySpan<byte> bytes, List<InputEvent> events)
    {
        int needed = _utf8.GetCharCount(bytes, flush: false);
        if (_chars.Length < needed)
        {
            _chars = new char[Math.Max(needed, _chars.Length * 2)];
        }

        int count = _utf8.GetChars(bytes, _chars, flush: false);
        _pending.Append(_chars, 0, count);
        Drain(events, final: false);
    }

    /// <summary>The reader's timeout passed with bytes still waiting: what can stand alone (a lone ESC) is delivered.</summary>
    public void Flush(List<InputEvent> events) => Drain(events, final: true);

    private void Drain(List<InputEvent> events, bool final)
    {
        string s = _pending.ToString();
        int i = 0;
        while (i < s.Length)
        {
            if (_paste is not null)
            {
                int end = s.IndexOf("\x1b[201~", i, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Keep a tail that may be the start of the end marker for the next read.
                    int keep = TailThatMayStartMarker(s, i);
                    _paste.Append(s, i, s.Length - i - keep);
                    i = s.Length - keep;
                    break;
                }

                _paste.Append(s, i, end - i);
                events.Add(new InputEvent.Paste(NormalizePaste(_paste.ToString())));
                _paste = null;
                i = end + 6;
                continue;
            }

            char c = s[i];
            if (c != Esc)
            {
                events.Add(new InputEvent.Key(Plain(c, alt: false)));
                i++;
                continue;
            }

            int used = TrySequence(s, i, events, final);
            if (used == 0)
            {
                break;
            }

            i += used;
        }

        _pending.Remove(0, i);
        if (final && _pending.Length > 0 && _paste is null)
        {
            // Nothing completes it: an unfinished sequence is dropped rather than typed.
            _pending.Clear();
        }
    }

    // How many characters at s[i..] a sequence used, or 0 to wait for more (never 0 when final).
    private int TrySequence(string s, int i, List<InputEvent> events, bool final)
    {
        if (i + 1 >= s.Length)
        {
            if (!final)
            {
                return 0;
            }

            events.Add(new InputEvent.Key(new ConsoleKeyInfo(Esc, ConsoleKey.Escape, false, false, false)));
            return 1;
        }

        char next = s[i + 1];
        if (next == '[')
        {
            return Csi(s, i, events, final);
        }

        if (next == 'O')
        {
            if (i + 2 >= s.Length)
            {
                if (!final)
                {
                    return 0;
                }

                // ESC O alone: Alt+Shift+O.
                events.Add(new InputEvent.Key(Plain('O', alt: true)));
                return 2;
            }

            if (Ss3Key(s[i + 2]) is { } ss3)
            {
                events.Add(new InputEvent.Key(ss3));
                return 3;
            }

            events.Add(new InputEvent.Key(Plain('O', alt: true)));
            return 2;
        }

        if (next == Esc)
        {
            // ESC ESC: the first is a lone ESC, the second starts over.
            events.Add(new InputEvent.Key(new ConsoleKeyInfo(Esc, ConsoleKey.Escape, false, false, false)));
            return 1;
        }

        // ESC then a key: that key with Alt.
        events.Add(new InputEvent.Key(Plain(next, alt: true)));
        return 2;
    }

    private int Csi(string s, int i, List<InputEvent> events, bool final)
    {
        // Parameters and intermediates run to a final byte in @..~.
        int j = i + 2;
        while (j < s.Length && (s[j] < '@' || s[j] > '~'))
        {
            j++;
        }

        if (j >= s.Length)
        {
            if (j - i > MaxSequence || final)
            {
                return s.Length - i;
            }

            return 0;
        }

        string body = s.Substring(i + 2, j - i - 2);
        char fin = s[j];
        int used = j - i + 1;

        if (body.StartsWith('<') && (fin == 'M' || fin == 'm'))
        {
            if (Mouse(body.AsSpan(1), fin == 'M') is { } mouse)
            {
                events.Add(mouse);
            }

            return used;
        }

        if (fin == '~' && body == "200")
        {
            _paste = new StringBuilder();
            return used;
        }

        if (fin == '~' && body == "201")
        {
            return used;
        }

        var parts = body.Split(';');
        int p0 = Number(parts, 0, 1);
        int mods = Number(parts, 1, 1);

        if (fin == 'R' && parts.Length == 2 && AwaitingCursor)
        {
            AwaitingCursor = false;
            CursorReport?.Invoke(p0 - 1, mods - 1);
            return used;
        }

        ConsoleKey? key = fin switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            'P' => ConsoleKey.F1,
            'Q' => ConsoleKey.F2,
            'R' => ConsoleKey.F3,
            'S' => ConsoleKey.F4,
            'Z' => ConsoleKey.Tab,
            '~' => Tilde(p0),
            _ => null,
        };

        if (key is { } k)
        {
            bool shift = fin == 'Z' || ((mods - 1) & 1) != 0;
            bool alt = ((mods - 1) & 2) != 0;
            bool control = ((mods - 1) & 4) != 0;
            char ch = k == ConsoleKey.Tab ? '\t' : '\0';
            events.Add(new InputEvent.Key(new ConsoleKeyInfo(ch, k, shift, alt, control)));
        }

        return used;
    }

    private static InputEvent? Mouse(ReadOnlySpan<char> body, bool press)
    {
        Span<int> n = stackalloc int[3];
        int field = 0;
        int value = 0;
        bool any = false;
        foreach (char c in body)
        {
            if (c == ';')
            {
                if (field >= 2)
                {
                    return null;
                }

                n[field++] = value;
                value = 0;
                any = false;
            }
            else if (c is >= '0' and <= '9')
            {
                value = (value * 10) + (c - '0');
                any = true;
            }
            else
            {
                return null;
            }
        }

        if (field != 2 || !any)
        {
            return null;
        }

        n[2] = value;
        int b = n[0];
        int x = Math.Max(0, n[1] - 1);
        int y = Math.Max(0, n[2] - 1);
        int button = b & 3;
        bool motion = (b & 32) != 0;
        if ((b & 64) != 0)
        {
            return !press ? null : button switch
            {
                0 => new InputEvent.Wheel(x, y, 1),
                1 => new InputEvent.Wheel(x, y, -1),
                _ => null,
            };
        }

        if (motion)
        {
            return press && button == 0 ? new InputEvent.Drag(x, y) : null;
        }

        if (!press)
        {
            return button == 0 ? new InputEvent.Release(x, y) : null;
        }

        return button switch
        {
            0 => new InputEvent.Click(x, y, MouseButton.Left),
            2 => new InputEvent.Click(x, y, MouseButton.Right),
            _ => null,
        };
    }

    private static ConsoleKey? Tilde(int code) => code switch
    {
        1 or 7 => ConsoleKey.Home,
        2 => ConsoleKey.Insert,
        3 => ConsoleKey.Delete,
        4 or 8 => ConsoleKey.End,
        5 => ConsoleKey.PageUp,
        6 => ConsoleKey.PageDown,
        11 => ConsoleKey.F1,
        12 => ConsoleKey.F2,
        13 => ConsoleKey.F3,
        14 => ConsoleKey.F4,
        15 => ConsoleKey.F5,
        17 => ConsoleKey.F6,
        18 => ConsoleKey.F7,
        19 => ConsoleKey.F8,
        20 => ConsoleKey.F9,
        21 => ConsoleKey.F10,
        23 => ConsoleKey.F11,
        24 => ConsoleKey.F12,
        _ => null,
    };

    private static ConsoleKeyInfo? Ss3Key(char c) => c switch
    {
        'A' => new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false),
        'B' => new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false),
        'C' => new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false),
        'D' => new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false),
        'H' => new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false),
        'F' => new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false),
        'P' => new ConsoleKeyInfo('\0', ConsoleKey.F1, false, false, false),
        'Q' => new ConsoleKeyInfo('\0', ConsoleKey.F2, false, false, false),
        'R' => new ConsoleKeyInfo('\0', ConsoleKey.F3, false, false, false),
        'S' => new ConsoleKeyInfo('\0', ConsoleKey.F4, false, false, false),
        'M' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
        _ => null,
    };

    /// <summary>One character as a key: a control byte as Ctrl+its letter, the rest as typed. Pinned.</summary>
    public static ConsoleKeyInfo Plain(char c, bool alt)
    {
        switch (c)
        {
            case '\r':
                return new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, alt, false);
            case '\n':
                return new ConsoleKeyInfo('\n', ConsoleKey.Enter, false, alt, true);
            case '\t':
                return new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, alt, false);
            case '\x7f':
                return new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, alt, false);
            case '\b':
                return new ConsoleKeyInfo('\b', ConsoleKey.H, false, alt, true);
            case '\x1d':
                return new ConsoleKeyInfo('\x1d', ConsoleKey.Oem6, false, alt, true);
            case '\x1f':
                return new ConsoleKeyInfo('\x1f', ConsoleKey.Oem2, false, alt, true);
            case '\0':
                return new ConsoleKeyInfo('\0', ConsoleKey.Spacebar, false, alt, true);
            case ' ':
                return new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, alt, false);
        }

        if (c is >= '\x01' and <= '\x1a')
        {
            return new ConsoleKeyInfo(c, ConsoleKey.A + (c - '\x01'), false, alt, true);
        }

        if (c is >= 'a' and <= 'z')
        {
            return new ConsoleKeyInfo(c, ConsoleKey.A + (c - 'a'), false, alt, false);
        }

        if (c is >= 'A' and <= 'Z')
        {
            return new ConsoleKeyInfo(c, ConsoleKey.A + (c - 'A'), true, alt, false);
        }

        if (c is >= '0' and <= '9')
        {
            return new ConsoleKeyInfo(c, ConsoleKey.D0 + (c - '0'), false, alt, false);
        }

        return new ConsoleKeyInfo(c, default, false, alt, false);
    }

    private static int Number(string[] parts, int index, int fallback) =>
        index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static int TailThatMayStartMarker(string s, int from)
    {
        const string marker = "\x1b[201~";
        for (int keep = Math.Min(marker.Length - 1, s.Length - from); keep > 0; keep--)
        {
            if (string.CompareOrdinal(s, s.Length - keep, marker, 0, keep) == 0)
            {
                return keep;
            }
        }

        return 0;
    }

    /// <summary>A paste's line breaks as <c>'\n'</c>: CR LF and a lone CR (what a terminal sends for each line) alike. Pinned.</summary>
    public static string NormalizePaste(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
