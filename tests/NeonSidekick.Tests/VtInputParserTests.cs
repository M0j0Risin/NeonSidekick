using System.Text;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Unix reader's byte rules (2026-10-06, the macOS build): pure, so they run on every OS.</summary>
public class VtInputParserTests
{
    private static List<InputEvent> Parse(params string[] reads)
    {
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        foreach (var read in reads)
        {
            parser.Feed(Encoding.UTF8.GetBytes(read), events);
        }

        return events;
    }

    private static ConsoleKeyInfo Key(InputEvent e) => Assert.IsType<InputEvent.Key>(e).Info;

    [Fact]
    public void Text_IsOneKeyPerCharacter_WithLettersAndDigitsAsTheirKeys()
    {
        var keys = Parse("aZ5!é").Select(Key).ToList();

        Assert.Equal(new[] { 'a', 'Z', '5', '!', 'é' }, keys.Select(k => k.KeyChar));
        Assert.Equal(ConsoleKey.A, keys[0].Key);
        Assert.Equal(ConsoleKey.Z, keys[1].Key);
        Assert.Equal(ConsoleModifiers.Shift, keys[1].Modifiers);
        Assert.Equal(ConsoleKey.D5, keys[2].Key);
    }

    [Fact]
    public void Utf8_SplitAcrossReads_IsOneCharacter()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("é");
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        parser.Feed(bytes.AsSpan(0, 1), events);
        Assert.Empty(events);
        parser.Feed(bytes.AsSpan(1), events);

        Assert.Equal('é', Key(Assert.Single(events)).KeyChar);
    }

    [Fact]
    public void ControlBytes_AreCtrlKeys_AsWindowsReportsThem()
    {
        var keys = Parse("\x03\x13\x0f\r\n\x7f\b\t").Select(Key).ToList();

        Assert.Equal(new ConsoleKeyInfo('\x03', ConsoleKey.C, false, false, true), keys[0]);
        Assert.Equal(new ConsoleKeyInfo('\x13', ConsoleKey.S, false, false, true), keys[1]);
        Assert.Equal(new ConsoleKeyInfo('\x0f', ConsoleKey.O, false, false, true), keys[2]);
        Assert.Equal(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), keys[3]);
        Assert.True(Keys.IsLineBreak(keys[4]));
        Assert.Equal(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false), keys[5]);
        Assert.Equal(new ConsoleKeyInfo('\b', ConsoleKey.H, false, false, true), keys[6]);   // Ctrl+H since 2026-10-07, not Ctrl+Backspace
        Assert.Equal(ConsoleKey.Tab, keys[7].Key);
    }

    /// <summary>
    /// The chords a Mac terminal sends as control bytes past Ctrl+Z (2026-10-07, the user's finds): BS is Ctrl+H (<c>/help</c>),
    /// US Ctrl+/ (<c>/settings</c>), GS Ctrl+] (a Mac's <c>/terminal</c>), each with Alt after an ESC; DEL stays Backspace and
    /// ESC DEL (Option+Delete) Backspace with Alt. The chords' lines are what <see cref="Keys.ShortcutLine"/> makes of them.
    /// </summary>
    [Fact]
    public void BsGsAndUs_AreCtrlH_CtrlBracket_AndCtrlSlash()
    {
        var keys = Parse("\b\x1f\x1d\x1b\b\x1b\x7f").Select(Key).ToList();

        Assert.Equal(new ConsoleKeyInfo('\b', ConsoleKey.H, false, false, true), keys[0]);
        Assert.Equal(new ConsoleKeyInfo('\x1f', ConsoleKey.Oem2, false, false, true), keys[1]);
        Assert.Equal(new ConsoleKeyInfo('\x1d', ConsoleKey.Oem6, false, false, true), keys[2]);
        Assert.Equal(new ConsoleKeyInfo('\b', ConsoleKey.H, false, true, true), keys[3]);
        Assert.Equal(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, true, false), keys[4]);
        Assert.Equal("/help", Keys.ShortcutLine(keys[0]));
        Assert.Equal("/settings", Keys.ShortcutLine(keys[1]));
        Assert.Null(Keys.ShortcutLine(keys[3]));   // Ctrl+Alt+H, /header until 2026-10-18 (the bar chords went)
        // A Mac terminal sends Ctrl+Shift+F as Ctrl+F's ACK: /find since 2026-10-18, either way (/perfbar until then).
        Assert.Equal("/find", Keys.ShortcutLine(Parse("\x06").Select(Key).Single()));
        Assert.Equal(OperatingSystem.IsMacOS() ? "/terminal" : null, Keys.ShortcutLine(keys[2]));
    }

    /// <summary>
    /// The Mac's stand-ins for chords its terminals cannot send (2026-10-07, the user's picks): Ctrl+] for Ctrl+. (<c>/terminal</c>),
    /// Ctrl+D for Ctrl+M (<c>/model</c>, CR being Enter) and Ctrl+Option+A for Ctrl+Alt+M (<c>/memory</c>, ESC CR being Alt+Enter),
    /// only on a Mac; plain Ctrl+A is still no chord (the line's select-all).
    /// </summary>
    [Fact]
    public void TheMacStandIns_AreChordsOnAMacOnly()
    {
        var keys = Parse("\x1d\x04\x1b\x01\x01").Select(Key).ToList();
        bool mac = OperatingSystem.IsMacOS();

        Assert.Equal(mac ? "/terminal" : null, Keys.ShortcutLine(keys[0]));
        Assert.Equal(mac ? "/model" : null, Keys.ShortcutLine(keys[1]));
        Assert.Equal(new ConsoleKeyInfo('\x01', ConsoleKey.A, false, true, true), keys[2]);
        Assert.Equal(mac ? "/memory" : null, Keys.ShortcutLine(keys[2]));
        Assert.Null(Keys.ShortcutLine(keys[3]));
        Assert.Equal("/model", Keys.ShortcutLine(Keys.Ctrl(ConsoleKey.M)));   // Windows' own chords stay, everywhere
        Assert.Equal("/terminal", Keys.ShortcutLine(Keys.CtrlPeriod));
    }

    [Fact]
    public void LoneEscape_WaitsForTheTimeout_ThenIsEscape()
    {
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        parser.Feed("\x1b"u8, events);
        Assert.Empty(events);
        Assert.True(parser.HasPending);

        parser.Flush(events);

        Assert.Equal(ConsoleKey.Escape, Key(Assert.Single(events)).Key);
        Assert.False(parser.HasPending);
    }

    // Terminal.app's Option+Up (2026-11-01, message jumps): ESC as a meta prefix ahead of a whole arrow is that arrow with Alt, never an ESC
    // (under a reply a lone ESC cancels it), CSI or SS3 alike; xterm's Meta bit (8, kitty's Super) reads as Alt too (2026-11-02).
    [Fact]
    public void EscapeEscapeArrow_IsTheArrowWithAlt()
    {
        var up = Key(Assert.Single(Parse("\u001b\u001b[A")));
        Assert.Equal((ConsoleKey.UpArrow, ConsoleModifiers.Alt), (up.Key, up.Modifiers));
        var down = Key(Assert.Single(Parse("\u001b\u001b[1;2B")));
        Assert.Equal((ConsoleKey.DownArrow, ConsoleModifiers.Alt | ConsoleModifiers.Shift), (down.Key, down.Modifiers));
        var ss3 = Key(Assert.Single(Parse("\u001b\u001bOA")));
        Assert.Equal((ConsoleKey.UpArrow, ConsoleModifiers.Alt), (ss3.Key, ss3.Modifiers));
        var meta = Key(Assert.Single(Parse("\u001b[1;9A")));
        Assert.Equal((ConsoleKey.UpArrow, ConsoleModifiers.Alt), (meta.Key, meta.Modifiers));
    }

    // Waiting across reads (2026-11-01; kept at the 2026-11-02 merge with the Mac pass, which found Terminal.app writes the ESC and the
    // arrow apart): ESC ESC with the arrow still to come waits, as a lone ESC does, and is the arrow with Alt once it comes.
    [Theory]
    [InlineData("\u001b", "\u001b[A")]
    [InlineData("\u001b\u001b", "[A")]
    [InlineData("\u001b\u001b[", "A")]
    public void EscapeEscapeArrow_SplitOverTwoReads_WaitsThenIsTheArrowWithAlt(string first, string second)
    {
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        parser.Feed(System.Text.Encoding.ASCII.GetBytes(first), events);
        Assert.Empty(events);
        Assert.True(parser.HasPending);
        parser.Feed(System.Text.Encoding.ASCII.GetBytes(second), events);

        var up = Key(Assert.Single(events));
        Assert.Equal((ConsoleKey.UpArrow, ConsoleModifiers.Alt), (up.Key, up.Modifiers));
    }

    // What Terminal.app really sends to the app (the Mac pass, later on 2026-11-01): in application cursor mode, which .NET turns on at
    // start, Option+Up is ESC then ESC O A, written apart; before, the parser read it as an ESC and a plain Up (the rewind, or a reply cancelled).
    [Fact]
    public void EscapeEscapeSs3Arrow_IsTheArrowWithAlt()
    {
        foreach (var (bytes, key) in new[] { ("\u001b\u001bOA", ConsoleKey.UpArrow), ("\u001b\u001bOB", ConsoleKey.DownArrow), ("\u001b\u001bOC", ConsoleKey.RightArrow), ("\u001b\u001bOD", ConsoleKey.LeftArrow) })
        {
            var arrow = Key(Assert.Single(Parse(bytes)));
            Assert.Equal((key, ConsoleModifiers.Alt), (arrow.Key, arrow.Modifiers));
        }
    }

    [Theory]
    [InlineData("\u001b", "\u001bOA")]     // Terminal.app's two writes
    [InlineData("\u001b\u001b", "OA")]
    [InlineData("\u001b\u001bO", "A")]
    public void EscapeEscapeSs3Arrow_SplitOverTwoReads_WaitsThenIsTheArrowWithAlt(string first, string second)
    {
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        parser.Feed(System.Text.Encoding.ASCII.GetBytes(first), events);
        Assert.Empty(events);
        Assert.True(parser.HasPending);
        parser.Feed(System.Text.Encoding.ASCII.GetBytes(second), events);

        var up = Key(Assert.Single(events));
        Assert.Equal((ConsoleKey.UpArrow, ConsoleModifiers.Alt), (up.Key, up.Modifiers));
    }

    [Fact]
    public void EscapeEscapeO_AtTheTimeout_IsAnEscapeThenAltShiftO()
    {
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        parser.Feed("\u001b\u001bO"u8, events);
        Assert.Empty(events);

        parser.Flush(events);

        Assert.Equal(2, events.Count);
        Assert.Equal(ConsoleKey.Escape, Key(events[0]).Key);
        Assert.Equal((ConsoleKey.O, ConsoleModifiers.Alt | ConsoleModifiers.Shift), (Key(events[1]).Key, Key(events[1]).Modifiers));
        Assert.False(parser.HasPending);
    }

    // Terminal.app's Option+←/→ (2026-11-02, the Mac pass): its built-in key map sends ESC b and ESC f, one read each, never an arrow;
    // they are b and f with Alt (readline's meta-b, meta-f), the keys the editor takes as a Mac's word moves (InputLine.Editor.IsMetaWordKey).
    [Fact]
    public void TerminalAppOptionArrows_EscapeBAndEscapeF_AreBAndFWithAlt()
    {
        var events = Parse("\u001bb", "\u001bf");

        Assert.Equal(2, events.Count);
        Assert.Equal(('b', ConsoleKey.B, ConsoleModifiers.Alt), (Key(events[0]).KeyChar, Key(events[0]).Key, Key(events[0]).Modifiers));
        Assert.Equal(('f', ConsoleKey.F, ConsoleModifiers.Alt), (Key(events[1]).KeyChar, Key(events[1]).Key, Key(events[1]).Modifiers));
    }

    [Fact]
    public void EscapeEscapeSs3NotAnArrow_IsAnEscapeThenThatKey()
    {
        var events = Parse("\u001b\u001bOP");
        Assert.Equal([ConsoleKey.Escape, ConsoleKey.F1], events.Select(e => Key(e).Key));
    }

    [Fact]
    public void EscapeEscape_AtTheTimeout_IsTwoEscapes()
    {
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        parser.Feed("\u001b\u001b"u8, events);
        Assert.Empty(events);

        parser.Flush(events);

        Assert.Equal([ConsoleKey.Escape, ConsoleKey.Escape], events.Select(e => Key(e).Key));
        Assert.False(parser.HasPending);
    }

    // What follows ESC ESC is never run twice (2026-11-02, the review): a paste after Esc arrives whole, a cursor report answers once
    // (not also F3), a focus report fires once.
    [Fact]
    public void EscapeEscape_ThenAPasteACursorReportOrAFocusReport_IsAnEscapeThenThatOnce()
    {
        var paste = Parse("\u001b\u001b[200~x\u001b[201~");
        Assert.Equal(ConsoleKey.Escape, Key(paste[0]).Key);
        Assert.Equal("x", Assert.IsType<InputEvent.Paste>(Assert.Single(paste.Skip(1))).Text);

        var parser = new VtInputParser { AwaitingCursor = true };
        int reports = 0;
        parser.CursorReport = (_, _) => reports++;
        var events = new List<InputEvent>();
        parser.Feed("\u001b\u001b[5;10R"u8, events);
        Assert.Equal(1, reports);
        Assert.Equal(ConsoleKey.Escape, Key(Assert.Single(events)).Key);

        int focus = 0;
        var focused = new VtInputParser { FocusChanged = _ => focus++ };
        var more = new List<InputEvent>();
        focused.Feed("\u001b\u001b[I"u8, more);
        Assert.Equal(1, focus);
        Assert.Equal(ConsoleKey.Escape, Key(Assert.Single(more)).Key);
    }

    [Fact]
    public void EscapeEscapeThenAKey_IsAnEscapeThenThatKeyWithAlt()
    {
        var events = Parse("\u001b\u001bx");
        Assert.Equal(2, events.Count);
        Assert.Equal(ConsoleKey.Escape, Key(events[0]).Key);
        Assert.Equal((ConsoleKey.X, ConsoleModifiers.Alt), (Key(events[1]).Key, Key(events[1]).Modifiers));
    }

    [Fact]
    public void EscapeThenKey_IsAlt()
    {
        var key = Key(Assert.Single(Parse("\u001bb")));

        Assert.Equal(ConsoleKey.B, key.Key);
        Assert.Equal(ConsoleModifiers.Alt, key.Modifiers);
    }

    [Fact]
    public void CsiAndSs3_AreTheNavigationKeys_WithXtermModifiers()
    {
        var keys = Parse("\x1b[A\x1bOB\x1b[1;5C\x1b[1;2D\x1b[3~\x1b[5;3~\x1b[H\x1bOF\x1b[15~\x1b[24~\x1bOP\x1b[Z").Select(Key).ToList();

        Assert.Equal(
            new[] { ConsoleKey.UpArrow, ConsoleKey.DownArrow, ConsoleKey.RightArrow, ConsoleKey.LeftArrow, ConsoleKey.Delete, ConsoleKey.PageUp, ConsoleKey.Home, ConsoleKey.End, ConsoleKey.F5, ConsoleKey.F12, ConsoleKey.F1, ConsoleKey.Tab },
            keys.Select(k => k.Key));
        Assert.Equal(ConsoleModifiers.Control, keys[2].Modifiers);
        Assert.Equal(ConsoleModifiers.Shift, keys[3].Modifiers);
        Assert.Equal(ConsoleModifiers.Alt, keys[5].Modifiers);
        Assert.Equal(ConsoleModifiers.Shift, keys[11].Modifiers);
    }

    [Fact]
    public void Sequence_SplitAcrossReads_IsOneKey()
    {
        var events = Parse("\x1b[", "1;5", "A");

        Assert.Equal(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, true), Key(Assert.Single(events)));
    }

    [Fact]
    public void BracketedPaste_IsOnePaste_WithLineBreaksAsNewlines_EvenSplitAcrossReads()
    {
        var events = Parse("x\x1b[200~one\r\ntwo\rthree\x1b[2", "01~y");

        Assert.Equal(3, events.Count);
        Assert.Equal('x', Key(events[0]).KeyChar);
        Assert.Equal("one\ntwo\nthree", Assert.IsType<InputEvent.Paste>(events[1]).Text);
        Assert.Equal('y', Key(events[2]).KeyChar);
    }

    [Fact]
    public void Paste_KeepsEscapesInside_AsText()
    {
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        parser.Feed(Encoding.UTF8.GetBytes("\x1b[200~a\tb"), events);
        Assert.Empty(events);
        Assert.True(parser.InPaste);
        parser.Feed(Encoding.UTF8.GetBytes("c\x1b[201~"), events);

        Assert.Equal("a\tbc", Assert.IsType<InputEvent.Paste>(Assert.Single(events)).Text);
    }

    [Fact]
    public void SgrMouse_IsClicksDragsReleasesAndWheel_ZeroBased()
    {
        var events = Parse("\x1b[<0;10;5M\x1b[<32;11;5M\x1b[<0;12;5m\x1b[<2;3;4M\x1b[<2;3;4m\x1b[<64;1;1M\x1b[<65;1;1M\x1b[<1;1;1M\x1b[<35;2;2M");

        Assert.Equal(
            new InputEvent[]
            {
                new InputEvent.Click(9, 4, MouseButton.Left),
                new InputEvent.Drag(10, 4),
                new InputEvent.Release(11, 4),
                new InputEvent.Click(2, 3, MouseButton.Right),
                new InputEvent.Wheel(0, 0, 1),
                new InputEvent.Wheel(0, 0, -1),
            },
            events);
    }

    [Fact]
    public void AMotionWithNoButton_AndTheFocusReports_GoToTheirCallbacks_NeverTheEvents()
    {
        // 2026-10-20, the welcome splash's robot: any-event tracking's hover and ?1004's focus reports.
        var parser = new VtInputParser();
        var moves = new List<(int, int)>();
        var focus = new List<bool>();
        parser.PointerMoved = (x, y) => moves.Add((x, y));
        parser.FocusChanged = focus.Add;
        var events = new List<InputEvent>();

        parser.Feed(Encoding.UTF8.GetBytes("\x1b[<35;2;3M\x1b[O\x1b[<35;40;12M\x1b[Ia\x1b[<32;5;5M"), events);

        Assert.Equal([(1, 2), (39, 11)], moves);
        Assert.Equal([false, true], focus);
        Assert.Equal(new InputEvent[] { new InputEvent.Key(VtInputParser.Plain('a', alt: false)), new InputEvent.Drag(4, 4) }, events);
    }

    [Fact]
    public void CursorReport_AnswersAnAwaitedQuery_AndIsF3Otherwise()
    {
        var parser = new VtInputParser();
        (int Row, int Column)? answer = null;
        parser.CursorReport = (row, column) => answer = (row, column);
        var events = new List<InputEvent>();

        parser.AwaitingCursor = true;
        parser.Feed("\x1b[12;40R"u8, events);
        Assert.Empty(events);
        Assert.Equal((11, 39), answer);
        Assert.False(parser.AwaitingCursor);

        parser.Feed("\x1b[1;5R"u8, events);
        var f3 = Key(Assert.Single(events));
        Assert.Equal(ConsoleKey.F3, f3.Key);
        Assert.Equal(ConsoleModifiers.Control, f3.Modifiers);
    }

    [Fact]
    public void UnknownSequences_AreDroppedWhole_NeverTyped()
    {
        var events = Parse("\x1b[I\x1b[O\x1b[97;5u\x1b[?1;2cq");

        Assert.Equal('q', Key(Assert.Single(events)).KeyChar);
    }

    [Fact]
    public void UnfinishedSequence_AtTheTimeout_IsDropped()
    {
        var parser = new VtInputParser();
        var events = new List<InputEvent>();
        parser.Feed("\x1b[1;"u8, events);
        parser.Flush(events);

        Assert.Empty(events);
        Assert.False(parser.HasPending);
    }

    [Fact]
    public void Raw_ClearsLineEditingEchoSignalsAndFlowControl()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        RawOnMac();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void RawOnMac()
    {
        var cooked = new TermiosNative.Termios
        {
            IFlag = TermiosNative.Icrnl | TermiosNative.Ixon | TermiosNative.Brkint,
            LFlag = TermiosNative.Echo | TermiosNative.Icanon | TermiosNative.Isig | TermiosNative.Iexten,
        };
        Assert.False(TermiosNative.IsRaw(cooked));
        Assert.True(TermiosNative.IsRaw(TermiosNative.Raw(cooked)));
    }

    /// <summary>The SGR code's Meta (8) or Shift (4) bit on a left press is the link chord on a Mac (2026-11-01); a right press never carries it.</summary>
    [Fact]
    public void AMetaOrShiftLeftPress_IsAnOpenerClick_AndABareOrRightOneIsNot()
    {
        var events = Parse("\x1b[<8;10;5M\x1b[<4;10;5M\x1b[<0;10;5M\x1b[<10;3;4M");

        Assert.Equal(
            new InputEvent[]
            {
                new InputEvent.Click(9, 4, MouseButton.Left, Opener: true),
                new InputEvent.Click(9, 4, MouseButton.Left, Opener: true),
                new InputEvent.Click(9, 4, MouseButton.Left),
                new InputEvent.Click(2, 3, MouseButton.Right),
            },
            events);
    }
}
