namespace NeonSidekick.UI;

/// <summary>
/// The key contract, in one place: <b>ESC cancels or backs out and never quits.</b> No single key quits: <c>/exit</c>,
/// Ctrl+C twice within two seconds at an idle line (<see cref="IsInterrupt"/>, since 2026-09-17)
/// and the app token (Ctrl+Break) do. The factories exist so tests and the input line build the
/// same <see cref="ConsoleKeyInfo"/> shapes.
/// </summary>
public static class Keys
{
    /// <summary>ESC: stop the speech, clear the line, cancel the turn, back out of a menu.</summary>
    public static bool IsCancel(ConsoleKeyInfo key) => key.Key == ConsoleKey.Escape;

    /// <summary>
    /// Ctrl+C: copy the line's selection, else stop the speech, else cancel the turn, else back out
    /// of a menu — and at an idle line with nothing to do, twice to exit, never once. Control held,
    /// Alt not (Ctrl+Alt+C is AltGr+C, a character on some layouts), Shift not looked at, and no
    /// character but the console's own ETX (<c>'\x03'</c>; a test <see cref="Ctrl"/> builds <c>'\0'</c>):
    /// a key that types a character is never the chord — Spectre's test input marks every
    /// upper-case letter with Control, and a typed "C" must stay a "C".
    /// </summary>
    public static bool IsInterrupt(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.C
        && key.KeyChar is '\0' or '\x03'
        && (key.Modifiers & ConsoleModifiers.Control) != 0
        && (key.Modifiers & ConsoleModifiers.Alt) == 0;

    /// <summary>
    /// Ctrl+Enter: a line break typed into the chat line's draft, never a send (2026-09-22, the
    /// user's call). The console reports it as Enter with Control held (the character is the LF the
    /// record carries, not looked at); a plain Enter sends. The type-ahead under a reply
    /// (<see cref="KeySource"/>) ends a line only on a plain Enter for the same reason, and a field
    /// that is not multi-line (a settings slot) takes Ctrl+Enter as Enter.
    /// </summary>
    public static bool IsLineBreak(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.Enter && (key.Modifiers & ConsoleModifiers.Control) != 0;

    /// <summary>
    /// Ctrl+O: every tool run in the transcript unfolded, or folded again (2026-09-22, the user's
    /// ask) — on the idle line and under a reply. <see cref="IsInterrupt"/>'s shape: Control held,
    /// Alt not, and no character but the console's own SI (<c>'\x0f'</c>; a test <see cref="Ctrl"/>
    /// builds <c>'\0'</c>), so a typed "O" stays an "O".
    /// </summary>
    public static bool IsToolToggle(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.O
        && key.KeyChar is '\0' or '\x0f'
        && (key.Modifiers & ConsoleModifiers.Control) != 0
        && (key.Modifiers & ConsoleModifiers.Alt) == 0;

    /// <summary>Ctrl+O as the console delivers it: the SI character with the key and Control.</summary>
    public static ConsoleKeyInfo CtrlO => new('\x0f', ConsoleKey.O, false, false, true);

    /// <summary>Ctrl+E as the console delivers it: the ENQ character with the key and Control (<c>/explore</c>, <see cref="ShortcutLine"/>).</summary>
    public static ConsoleKeyInfo CtrlE => new('\x05', ConsoleKey.E, false, false, true);

    /// <summary>
    /// The chat line's command chords (2026-09-30, the user's ask): Ctrl+Alt+C is <c>/clear</c>, Ctrl+Alt+N <c>/new</c> and
    /// Ctrl+Alt+S <c>/splash</c> — at the idle line and under a reply, run as the typed command would be. The user asked for
    /// Ctrl+Shift first; Windows Terminal keeps Ctrl+Shift+N (a new window) and Ctrl+Shift+C (its copy) by default, and leaves
    /// Ctrl+Alt with these letters alone, so all three moved to Ctrl+Alt. Control and Alt held, Shift not, and no character but
    /// the console's own control one for the letter (ETX, SO, DC3; a test builds <c>'\0'</c>): AltGr is Ctrl+Alt, and an AltGr
    /// key that types a character (ć, ń, ś on some layouts) stays that character. Null for every other key.
    /// Eleven more came later on 2026-09-30 (the user's ask), each its bare command as typed: Ctrl+Alt+T <c>/tools</c>, K
    /// <c>/skills</c>, P <c>/profile</c>, Y <c>/sys</c>, G <c>/usage</c>, E <c>/perf</c> (the performance bar shown or hidden),
    /// M <c>/memory</c>, D <c>/mcp</c>, L <c>/cmdlist</c>, O <c>/police</c> (the Shell police page) and B <c>/tb</c> (the
    /// toolbar shown or hidden). Under a reply each goes where its typed line would: a pane over the reply, <c>/perf</c> and
    /// <c>/tb</c> at once, <c>/profile</c> left for the idle line. Their control characters (DC4, VT, DLE, EM, BEL, ENQ, CR,
    /// EOT, FF, SI, STX) count as no character, as ETX does for C; an AltGr key that types one (€ on AltGr+E, ł, ó) is still
    /// that character.
    /// Ctrl+Alt+H <c>/help</c> came on 2026-10-01 (the user's ask), its BS (<c>'\x08'</c>) no character as the others' are;
    /// Backspace is its own key, never <see cref="ConsoleKey.H"/>, so the two do not meet.
    /// In a pane too since 2026-10-01 (the user's ask: "operate the same there as everywhere"): every pane reader hands the
    /// chord to <see cref="ScreenPane.Chord"/>, which closes the stack for the screen to run it, or toggles the bar in place
    /// (<c>/perf</c>, <c>/tb</c>), or ignores it under a tool's question.
    /// Ctrl+E <c>/explore</c> came later on 2026-10-01 (the user's ask), the one plain-Ctrl chord: Control held, Alt and Shift
    /// not, and no character but the console's own ENQ (<c>'\x05'</c>; a test <see cref="Ctrl"/> builds <c>'\0'</c>) — the
    /// <see cref="IsToolToggle"/> shape, so a typed "E" stays an "E" and Ctrl+Alt+E is still <c>/perf</c>.
    /// </summary>
    public static string? ShortcutLine(ConsoleKeyInfo key)
    {
        var held = key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift);
        if (held == ConsoleModifiers.Control)
        {
            return (key.Key, key.KeyChar) is (ConsoleKey.E, '\0' or '\x05') ? "/explore" : null;
        }

        if (held != (ConsoleModifiers.Control | ConsoleModifiers.Alt))
        {
            return null;
        }

        return (key.Key, key.KeyChar) switch
        {
            (ConsoleKey.C, '\0' or '\x03') => "/clear",
            (ConsoleKey.N, '\0' or '\x0e') => "/new",
            (ConsoleKey.S, '\0' or '\x13') => "/splash",
            (ConsoleKey.T, '\0' or '\x14') => "/tools",
            (ConsoleKey.K, '\0' or '\x0b') => "/skills",
            (ConsoleKey.P, '\0' or '\x10') => "/profile",
            (ConsoleKey.Y, '\0' or '\x19') => "/sys",
            (ConsoleKey.G, '\0' or '\x07') => "/usage",
            (ConsoleKey.H, '\0' or '\x08') => "/help",
            (ConsoleKey.E, '\0' or '\x05') => "/perf",
            (ConsoleKey.M, '\0' or '\r') => "/memory",
            (ConsoleKey.D, '\0' or '\x04') => "/mcp",
            (ConsoleKey.L, '\0' or '\x0c') => "/cmdlist",
            (ConsoleKey.O, '\0' or '\x0f') => "/police",
            (ConsoleKey.B, '\0' or '\x02') => "/tb",
            _ => null,
        };
    }

    /// <summary>
    /// Ctrl+Alt+X, the embedded model's kill switch (2026-10-01, the user's ask: "immediately unload an embedded model if one
    /// is loaded", nothing with any other server). A chord with no command, so never a <see cref="ShortcutLine"/>: the chord
    /// lines run through the dispatch, the mid-turn hook and the panes as typed commands, and a spinner's watch holds them for
    /// the idle line — a kill switch cannot wait for any of that. <see cref="KeySource.KillSwitch"/> spends it wherever the
    /// key is read instead. <see cref="ShortcutLine"/>'s shape: Control and Alt held, Shift not, and no character but the
    /// console's own CAN (<c>'\x18'</c>; a test builds <c>'\0'</c>), so an AltGr+X that types a character stays that character.
    /// </summary>
    public static bool IsKillSwitch(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.X
        && key.KeyChar is '\0' or '\x18'
        && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift)) == (ConsoleModifiers.Control | ConsoleModifiers.Alt);

    /// <summary>Ctrl+Alt+C, Ctrl+Alt+N and Ctrl+Alt+S as a US layout delivers them: no character, the key with Control and Alt.</summary>
    public static ConsoleKeyInfo CtrlAltC => new('\0', ConsoleKey.C, false, true, true);

    public static ConsoleKeyInfo CtrlAltN => new('\0', ConsoleKey.N, false, true, true);

    public static ConsoleKeyInfo CtrlAltS => new('\0', ConsoleKey.S, false, true, true);

    /// <summary>The Ctrl+Alt chord for <paramref name="key"/> as a US layout delivers it (later on 2026-09-30): no character, Control and Alt.</summary>
    public static ConsoleKeyInfo CtrlAlt(ConsoleKey key) => new('\0', key, false, true, true);

    /// <summary>A plain Enter (or Shift+Enter, Alt+Enter): the key that sends a line and ends a type-ahead line; Ctrl+Enter is <see cref="IsLineBreak"/>.</summary>
    public static bool IsSend(ConsoleKeyInfo key) => key.Key == ConsoleKey.Enter && !IsLineBreak(key);

    /// <summary>A printable character with no <see cref="ConsoleKey"/> (what a pasted or typed glyph looks like).</summary>
    public static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.None, false, false, false);

    /// <summary>A bare special key.</summary>
    public static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    /// <summary>A key with Control held.</summary>
    public static ConsoleKeyInfo Ctrl(ConsoleKey key) => new('\0', key, false, false, true);

    /// <summary>A key with Shift held (Shift+arrow extends the input line's selection).</summary>
    public static ConsoleKeyInfo Shift(ConsoleKey key) => new('\0', key, true, false, false);

    public static ConsoleKeyInfo Enter => Key(ConsoleKey.Enter);

    /// <summary>Ctrl+Enter as the console delivers it: the LF character with the key and Control (a line break in the draft).</summary>
    public static ConsoleKeyInfo CtrlEnter => new('\n', ConsoleKey.Enter, false, false, true);
    public static ConsoleKeyInfo Escape => Key(ConsoleKey.Escape);
    public static ConsoleKeyInfo Backspace => Key(ConsoleKey.Backspace);
    public static ConsoleKeyInfo Delete => Key(ConsoleKey.Delete);
    public static ConsoleKeyInfo Left => Key(ConsoleKey.LeftArrow);
    public static ConsoleKeyInfo Right => Key(ConsoleKey.RightArrow);
    public static ConsoleKeyInfo Up => Key(ConsoleKey.UpArrow);
    public static ConsoleKeyInfo Down => Key(ConsoleKey.DownArrow);
    public static ConsoleKeyInfo Home => Key(ConsoleKey.Home);
    public static ConsoleKeyInfo End => Key(ConsoleKey.End);
    public static ConsoleKeyInfo PageUp => Key(ConsoleKey.PageUp);
    public static ConsoleKeyInfo PageDown => Key(ConsoleKey.PageDown);

    /// <summary>The default push-to-talk key, as a terminal delivers it: no character.</summary>
    public static ConsoleKeyInfo F4 => Key(ConsoleKey.F4);

    /// <summary>Ctrl+C as the console delivers it: the ETX character with the key and Control.</summary>
    public static ConsoleKeyInfo CtrlC => new('\x03', ConsoleKey.C, false, false, true);

    /// <summary>Tab, as a terminal delivers it: the tab character with the key (the info pane's next tab).</summary>
    public static ConsoleKeyInfo Tab => new('\t', ConsoleKey.Tab, false, false, false);

    /// <summary>Shift+Tab (the info pane's previous tab).</summary>
    public static ConsoleKeyInfo ShiftTab => new('\t', ConsoleKey.Tab, true, false, false);
}
