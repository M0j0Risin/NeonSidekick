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
        Assert.Equal("/header", Keys.ShortcutLine(keys[3]));
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
}
