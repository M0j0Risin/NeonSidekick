using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class InputLineTests : IDisposable
{
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly InputLine _line;

    public InputLineTests()
    {
        _console.Profile.Width = 40;
        _line = new InputLine(_console, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)));
    }

    public void Dispose()
    {
        _console.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    private void Type(string text) => _console.Input.PushText(text);

    private static ConsoleKeyInfo[] Chars(string text) => text.Select(Keys.Char).ToArray();

    private async Task<string> SubmitAsync()
    {
        var result = await _line.ReadAsync();
        var submitted = Assert.IsType<InputResult.Submitted>(result);
        return submitted.Text;
    }

    /// <summary>A masked read (later on 2026-09-23, the SQL tab's password prompt): the text as typed, drawn one glyph per character, never remembered.</summary>
    [Fact]
    public async Task AMaskedRead_DrawsGlyphs_EditsAsEver_AndRemembersNothing()
    {
        Push([.. Chars("hunter2"), Keys.Backspace, Keys.Char('9'), Keys.Enter]);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(mask: true));

        Assert.Equal("hunter9", submitted.Text);
        Assert.DoesNotContain("hunter", _console.Output);
        Assert.Contains(new string(InputLine.MaskGlyph, 7), _console.Output);
        Assert.Empty(_line.History);
        Assert.Equal('•', InputLine.MaskGlyph);
    }

    /// <summary>The hint-row pair key (2026-09-18): each zone its own, each strip glyph its own past them — the first glyph no longer shares the trailer's.</summary>
    [Fact]
    public void HintPairKey_IsDistinctPerZone_AndPerStripGlyph()
    {
        Assert.Equal(0, InputLine.HintPairKey(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1)));
        Assert.Equal(2, InputLine.HintPairKey(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 30)));
        Assert.Equal(3, InputLine.HintPairKey(new ScreenPane.HintHit(ScreenPane.HintZone.Queued, "", 5)));
        Assert.Equal(8, InputLine.HintPairKey(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0)));
        Assert.Equal(11, InputLine.HintPairKey(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🎤", 3)));
        Assert.Equal(5, InputLine.HintPairKey(new ScreenPane.HintHit(ScreenPane.HintZone.Usage, "", 0)));   // last in the enum (2026-09-21), the keys above unmoved
        var keys = new[] { ScreenPane.HintZone.Row, ScreenPane.HintZone.Strip, ScreenPane.HintZone.Trailer, ScreenPane.HintZone.Queued, ScreenPane.HintZone.Scrolled, ScreenPane.HintZone.Usage }
            .Select(zone => InputLine.HintPairKey(new ScreenPane.HintHit(zone, "", zone == ScreenPane.HintZone.Strip ? 0 : -1))).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task TypeAndEnter_Submits_AndLeavesTheLineInTheTranscript()
    {
        Type("hello");
        Push(Keys.Enter);

        Assert.Equal("hello", await SubmitAsync());

        Assert.EndsWith(InputLine.PromptGlyph + "hello\n", _console.Output);   // TestConsole ends lines with \n
        Assert.Equal(new[] { "hello" }, _line.History);
    }

    [Fact]
    public async Task Enter_OnBlank_KeepsReading()
    {
        Push(Keys.Enter, Keys.Char(' '), Keys.Enter);
        Type("x");
        Push(Keys.Enter);

        Assert.Equal("x", await SubmitAsync());
        Assert.Single(_console.Lines);
    }

    [Fact]
    public async Task TrailingWhitespace_IsTrimmed_LeadingIsKept()
    {
        Type("  a  ");
        Push(Keys.Enter);
        Assert.Equal("  a", await SubmitAsync());
    }

    /// <summary>Ctrl+Enter on the chat line (2026-09-22) types a line break, never a send; the next Enter sends both lines.</summary>
    [Fact]
    public async Task CtrlEnter_OnTheChatLine_BreaksTheLine_AndEnterSendsIt()
    {
        Type("a");
        Push(Keys.CtrlEnter);
        Type("b");
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(multiline: true));
        Assert.Equal("a\nb", submitted.Text);
        Assert.Equal(new[] { "a\nb" }, _line.History);
    }

    [Fact]
    public async Task CtrlEnter_ReplacesTheSelection()
    {
        Type("ab");
        Push(Keys.Shift(ConsoleKey.LeftArrow), Keys.CtrlEnter);
        Type("c");
        Push(Keys.Enter);

        Assert.Equal("a\nc", Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(multiline: true)).Text);
    }

    /// <summary>A field that is not multi-line (a settings slot) takes Ctrl+Enter as Enter.</summary>
    [Fact]
    public async Task CtrlEnter_OnAField_Submits()
    {
        Type("a");
        Push(Keys.CtrlEnter);

        Assert.Equal("a", await SubmitAsync());
    }

    [Fact]
    public async Task Escape_WithText_ClearsAndKeepsReading()
    {
        Type("junk");
        Push(Keys.Escape);
        Type("ok");
        Push(Keys.Enter);

        Assert.Equal("ok", await SubmitAsync());
    }

    [Fact]
    public async Task Escape_OnEmpty_ReturnsCancelled()
    {
        Push(Keys.Escape);
        Assert.IsType<InputResult.Cancelled>(await _line.ReadAsync());
        Assert.EndsWith("\n", _console.Output);
    }

    [Fact]
    public async Task EscapeCancels_ReturnsCancelled_OnTheFirstEscape_DraftOrNot()
    {
        Type("x");
        Push(Keys.Escape);
        Assert.IsType<InputResult.Cancelled>(await _line.ReadAsync("saved value", remember: false, escapeCancels: true));

        // Typing still works before it; Enter still submits.
        Type("!");
        Push(Keys.Enter);
        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync("saved", remember: false, escapeCancels: true));
        Assert.Equal("saved!", submitted.Text);
        Assert.Empty(_line.History);
    }

    [Fact]
    public async Task SoftEscape_ThatTakesTheKey_KeepsTheDraft_AndReadsOn()
    {
        // The chat line over a spoken tail: the first ESC is the screen's (the speech stops),
        // the draft stays and the read goes on; the second is the line's, as ever.
        int asked = 0;
        Type("dra");
        Push(Keys.Escape);
        Type("ft");
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(softEscape: () => ++asked == 1));

        Assert.Equal("draft", submitted.Text);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task EmptyArrow_IsAskedOnAPlainLeftOrRight_OverAnEmptyDraftOnly()
    {
        // The welcome splash's walk (2026-09-19): a plain arrow with nothing on the line is the
        // screen's; with Shift, Ctrl or Alt, or with a draft, the key is the line's as ever.
        var steps = new List<int>();
        Push(Keys.Left, Keys.Right, Keys.Shift(ConsoleKey.LeftArrow), Keys.Ctrl(ConsoleKey.RightArrow), new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, true, false));
        Type("ab");
        Push(Keys.Left, Keys.Right, Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(emptyArrow: step => { steps.Add(step); return true; }));

        Assert.Equal("ab", submitted.Text);
        Assert.Equal([-1, +1], steps);
    }

    [Fact]
    public async Task EmptyEnter_IsAskedOnAnEnterWithNothingToSend_OnlyOverAnEmptyDraft()
    {
        // The picture strip's Enter (later still on 2026-09-24): spent, the read goes on; a row of spaces is not asked, it is cleared as ever.
        int asked = 0;
        Push(Keys.Enter);
        Type("  ");
        Push(Keys.Enter);
        Type("ab");
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(emptyEnter: () => { asked++; return true; }));

        Assert.Equal("ab", submitted.Text);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task EmptyEnter_ThatDeclines_IsTheOldNoOp()
    {
        int asked = 0;
        Push(Keys.Enter);
        Type("x");
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(emptyEnter: () => { asked++; return false; }));

        Assert.Equal("x", submitted.Text);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task EmptyArrow_ThatDeclines_IsTheOldNoOp()
    {
        int asked = 0;
        Push(Keys.Left, Keys.Right);
        Type("x");
        Push(Keys.Left, Keys.Char('a'), Keys.Enter);   // the arrow moves the cursor: 'a' lands ahead of 'x'

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(emptyArrow: _ => { asked++; return false; }));

        Assert.Equal("ax", submitted.Text);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task EmptyDelete_IsAskedOnAPlainDeleteOverAnEmptyDraft_WithWhetherTheLastEventWasOne()
    {
        // The splash's Delete Delete (2026-09-24): the line says "in a row" — any other key between is a first again.
        var repeats = new List<bool>();
        Push(Keys.Delete, Keys.Delete, Keys.Left, Keys.Delete, Keys.Shift(ConsoleKey.Delete), Keys.Delete);
        Type("ab");
        Push(Keys.Home, Keys.Delete, Keys.Enter);   // a draft: the key is the line's, 'a' goes

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(emptyDelete: repeat => { repeats.Add(repeat); return true; }));

        Assert.Equal("b", submitted.Text);
        Assert.Equal([false, true, false, false], repeats);
    }

    [Fact]
    public async Task EmptyDelete_ThatDeclines_NeverCountsAsTheFirstOfAPair()
    {
        var repeats = new List<bool>();
        Push(Keys.Delete, Keys.Delete, Keys.Enter);
        int asked = 0;

        await _line.ReadAsync(allowEmpty: true, emptyDelete: repeat => { repeats.Add(repeat); return asked++ > 0; });

        Assert.Equal([false, false], repeats);
    }

    [Fact]
    public async Task SoftEscape_ThatDeclines_ClearsTheDraft()
    {
        int asked = 0;
        Type("junk");
        Push(Keys.Escape);
        Type("ok");
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(softEscape: () => { asked++; return false; }));

        Assert.Equal("ok", submitted.Text);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task SoftEscape_TakenTwice_ClearsOnTheSecond()
    {
        // Speech → draft → nothing: the hook takes the first press only (the screen's flag), the
        // second clears the draft, the third from the empty row is Cancelled.
        int asked = 0;
        Type("dra");
        Push(Keys.Escape, Keys.Escape);
        Type("x");
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(softEscape: () => ++asked == 1));

        Assert.Equal("x", submitted.Text);
        Assert.Equal(2, asked);

        Push(Keys.Escape);
        Assert.IsType<InputResult.Cancelled>(await _line.ReadAsync(softEscape: () => { asked++; return false; }));
        Assert.Equal(3, asked);
    }

    [Fact]
    public async Task SoftEscape_OnAnEmptyRow_TakenMeansNoCancelled()
    {
        int asked = 0;
        Push(Keys.Escape);
        Type("ok");
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(softEscape: () => ++asked == 1));

        Assert.Equal("ok", submitted.Text);
        Assert.Equal(1, asked);
    }

    // ── Ctrl+C (2026-09-17): copy the selection, else the screen's hook, else exit ──────────

    [Fact]
    public async Task CtrlC_WithASelection_CopiesIt_AndKeepsTheSelection()
    {
        var copied = new List<string>();
        var line = new InputLine(_console, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)), text => { copied.Add(text); return true; });
        int asked = 0;
        Type("abcdef");
        Push(Keys.Home, Keys.Right, ShiftRight, ShiftRight, ShiftRight);   // a[bcd]ef
        Push(Keys.CtrlC);
        Type("X");                                                          // the selection still there: replaced
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(interrupt: () => { asked++; return true; }));

        Assert.Equal(["bcd"], copied);
        Assert.Equal("aXef", submitted.Text);
        Assert.Equal(0, asked);   // a copy is never the hook's press
        Assert.DoesNotContain(InputLine.CopyFailedNotice, _console.Output);
    }

    [Fact]
    public async Task CtrlC_WithASelection_OverAPasteToken_CopiesTheBlock()
    {
        var copied = new List<string>();
        var (line, keys) = PaneLine(copyToClipboard: text => { copied.Add(text); return true; });
        string block = Block(5);
        keys.Push(Chars("q ")).PushPaste(block).Push(Keys.Ctrl(ConsoleKey.A), Keys.CtrlC, Keys.Enter);

        await line.ReadAsync(multiline: true);

        Assert.Equal(["q " + block.Replace("\r\n", "\n")], copied);
    }

    [Fact]
    public async Task CtrlC_WhenTheCopyFails_OrNoWriter_SaysSo()
    {
        var pane = new ScreenPane(_console, null, TimeProvider.System);
        var line = new InputLine(pane, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)), notices: new TranscriptRenderer(pane), copyToClipboard: _ => false);
        Type("ab");
        Push(Keys.Ctrl(ConsoleKey.A), Keys.CtrlC, Keys.Enter);

        Assert.Equal("ab", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.Contains("Could not write the selection", _console.Output);   // the 40-column console wraps the rest

        var without = new InputLine(pane, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)), notices: new TranscriptRenderer(pane));
        int mark = _console.Output.Length;
        Type("cd");
        Push(Keys.Ctrl(ConsoleKey.A), Keys.CtrlC, Keys.Enter);
        Assert.Equal("cd", Assert.IsType<InputResult.Submitted>(await without.ReadAsync()).Text);
        Assert.Contains("Could not write the selection", _console.Output[mark..]);
    }

    // ── Ctrl+X (2026-09-25): cut the selection; nothing without one ────────────────────────

    [Fact]
    public async Task CtrlX_WithASelection_CutsIt()
    {
        var copied = new List<string>();
        var line = new InputLine(_console, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)), text => { copied.Add(text); return true; });
        int asked = 0;
        Type("abcdef");
        Push(Keys.Home, Keys.Right, ShiftRight, ShiftRight, ShiftRight);   // a[bcd]ef
        Push(Keys.Ctrl(ConsoleKey.X));
        Type("X");                                                          // the cursor where the cut was
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(interrupt: () => { asked++; return true; }));

        Assert.Equal(["bcd"], copied);
        Assert.Equal("aXef", submitted.Text);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task CtrlX_OverAPasteToken_CutsTheBlock()
    {
        var copied = new List<string>();
        var (line, keys) = PaneLine(copyToClipboard: text => { copied.Add(text); return true; });
        string block = Block(5);
        keys.Push(Chars("q ")).PushPaste(block).Push(Keys.Ctrl(ConsoleKey.A), Keys.Ctrl(ConsoleKey.X)).Push(Chars("z")).Push(Keys.Enter);

        Assert.Equal("z", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
        Assert.Equal(["q " + block.Replace("\r\n", "\n")], copied);
    }

    [Fact]
    public async Task CtrlX_WhenTheCopyFails_SaysSo_AndKeepsTheText()
    {
        var pane = new ScreenPane(_console, null, TimeProvider.System);
        var line = new InputLine(pane, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)), notices: new TranscriptRenderer(pane), copyToClipboard: _ => false);
        Type("ab");
        Push(Keys.Ctrl(ConsoleKey.A), Keys.Ctrl(ConsoleKey.X), Keys.Enter);

        Assert.Equal("ab", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.Contains("Could not write the selection", _console.Output);
    }

    [Fact]
    public async Task CtrlX_WithoutASelection_DoesNothing()
    {
        var copied = new List<string>();
        var line = new InputLine(_console, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)), text => { copied.Add(text); return true; });
        Type("abc");
        Push(Keys.Ctrl(ConsoleKey.X), Keys.Enter);

        Assert.Equal("abc", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.Empty(copied);
    }

    [Fact]
    public async Task TheCommandChords_EndTheChatLinesRead_WithTheDraft_AndAFieldLeavesThemAlone()
    {
        // 2026-09-30 (the user's ask): Ctrl+Alt+N on the chat line is /new, the draft handed back; a read without shortcuts ignores it.
        Type("keep");
        Push(Keys.CtrlAltN);
        var shortcut = Assert.IsType<InputResult.Shortcut>(await _line.ReadAsync(shortcuts: true));
        Assert.Equal("/new", shortcut.Line);
        Assert.Equal("keep", shortcut.Draft);

        Type("field");
        Push(Keys.CtrlAltC, Keys.CtrlAltS, Keys.Enter);
        Assert.Equal("field", Assert.IsType<InputResult.Submitted>(await _line.ReadAsync()).Text);
    }

    [Fact]
    public async Task CtrlC_WithNoHook_IsEscape()
    {
        // A settings field: the draft cleared, then Cancelled from the empty row; escapeCancels at once.
        Type("junk");
        Push(Keys.CtrlC);
        Type("ok");
        Push(Keys.Enter);
        Assert.Equal("ok", Assert.IsType<InputResult.Submitted>(await _line.ReadAsync()).Text);

        Push(Keys.CtrlC);
        Assert.IsType<InputResult.Cancelled>(await _line.ReadAsync());

        Type("field");
        Push(Keys.CtrlC);
        Assert.IsType<InputResult.Cancelled>(await _line.ReadAsync(escapeCancels: true));
    }

    [Fact]
    public async Task CtrlC_TheHookTakesIt_TheReadGoesOn_DraftKept()
    {
        int asked = 0;
        Type("dra");
        Push(Keys.CtrlC);
        Type("ft");
        Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(interrupt: () => { asked++; return true; }));

        Assert.Equal("draft", submitted.Text);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task CtrlC_TheHookDeclines_ReturnsExit()
    {
        int asked = 0;
        Type("draft");
        Push(Keys.CtrlC, Keys.CtrlC);

        Assert.IsType<InputResult.Exit>(await _line.ReadAsync(interrupt: () => ++asked == 1));

        Assert.Equal(2, asked);
        Assert.Empty(_line.History);   // nothing sent, nothing remembered
    }

    [Fact]
    public async Task CtrlC_WithTheListOpen_LeavesItOpen()
    {
        var (line, keys, pane, _) = MentionLine();
        int asked = 0;
        int waits = 0;
        keys.Push(Chars("@t"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Keys.CtrlC);
                    break;
                case 1:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Keys.Escape);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply, interrupt: () => { asked++; return true; }));

        Assert.Equal("@t", submitted.Text);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task SoftEscape_IsAskedBeforeTheListCloses()
    {
        // ESC's first job is silence, even over an open completion list: the taken press leaves
        // the list open, the next one closes it with the draft kept.
        var (line, keys, pane, _) = MentionLine();
        int asked = 0;
        int waits = 0;
        keys.Push(Chars("@t"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Keys.Escape);
                    break;
                case 1:
                    Assert.True(pane.OverlayOpen);   // the hook took it; the list is still there
                    keys.Push(Keys.Escape);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply, softEscape: () => ++asked == 1));

        Assert.Equal("@t", submitted.Text);
        Assert.Equal(2, asked);
    }

    /// <summary>Ctrl+Q used to quit from the line; now it is a control key the editor ignores.</summary>
    [Fact]
    public async Task CtrlQ_DoesNotQuit_TheLineGoesOn()
    {
        Type("still");
        Push(Keys.Ctrl(ConsoleKey.Q));
        Push(Keys.Enter);
        Assert.Equal("still", Assert.IsType<InputResult.Submitted>(await _line.ReadAsync()).Text);
    }

    [Fact]
    public async Task NoInput_ReturnsEndOfInput()
    {
        Type("dangling");
        Assert.IsType<InputResult.EndOfInput>(await _line.ReadAsync());
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        var console = new TestConsole().Interactive();
        var line = new InputLine(console, new KeySource(new ScriptedInput()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => line.ReadAsync(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task EditingKeys_WorkAtTheCursor()
    {
        Type("helo wrld");
        Push(Keys.Home, Keys.Right, Keys.Right, Keys.Right);   // hel|o wrld
        Type("l");                                             // hell|o wrld
        Push(Keys.End, Keys.Left, Keys.Left, Keys.Left);       // hello w|rld
        Type("o");                                             // hello wo|rld
        Push(Keys.Delete);                                     // hello wo|ld
        Type("r");                                             // hello wor|ld
        Push(Keys.Backspace, Keys.Backspace);                  // hello w|ld
        Type("or");
        Push(Keys.Enter);

        Assert.Equal("hello world", await SubmitAsync());
    }

    [Fact]
    public async Task BackspaceAndLeft_StepOverASurrogatePair()
    {
        // PushText cannot carry a surrogate (it casts the char to ConsoleKey); push the glyph as key events.
        Push(Keys.Char('a'), Keys.Char("😀"[0]), Keys.Char("😀"[1]), Keys.Char('b'));
        Push(Keys.Left, Keys.Backspace);   // removes the emoji as one element
        Push(Keys.Enter);

        Assert.Equal("ab", await SubmitAsync());
    }

    // ── Up/Down over a wrapped draft (2026-09-21) ───────────────────────────

    /// <summary>Twelve words at 37 cells: two rows, the caret at the end of the second.</summary>
    private static string TwoRows => string.Join(' ', Enumerable.Repeat("word", 12));

    [Fact]
    public async Task OnThePane_UpMovesTheCaretARow_AndHistoryOnlyFromTheFirstRow()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("earlier")).Push(Keys.Enter);
        Assert.Equal("earlier", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);

        string text = TwoRows;
        var layout = InputLayout.Wrap(text, text.Length, InputLine.AvailableCells(40));
        Assert.Equal(2, layout.Rows.Count);
        // Up from the end of row 1 lands on row 0 at the same column; X typed there marks it. A second Up, on row 0, is the history.
        int at = layout.IndexAt(0, layout.CursorCol);
        Assert.InRange(at, 1, layout.Starts[1] - 1);
        keys.Push(Chars(text)).Push(Keys.Up).Push(Chars("X")).Push(Keys.Enter);
        Assert.Equal(text[..at] + "X" + text[at..], Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);

        // The row move, then the history: the marked line just sent, then "earlier" (a recalled line walks on).
        keys.Push(Chars(text)).Push(Keys.Up, Keys.Up).Push(Keys.Enter);
        Assert.Equal(text[..at] + "X" + text[at..], Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        keys.Push(Chars(text)).Push(Keys.Up, Keys.Up, Keys.Up).Push(Keys.Enter);
        Assert.Equal("earlier", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task OnThePane_DownMovesTheCaretARow_AndFromTheLastRow_IsHistory()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("earlier")).Push(Keys.Enter);
        await line.ReadAsync();

        string text = TwoRows;
        // Home puts the caret on row 0; Down lands on row 1 at column 0 (the row's start); a second Down at the last row is the history walk — at the draft already, nothing.
        keys.Push(Chars(text)).Push(Keys.Home, Keys.Down).Push(Chars("X")).Push(Keys.Enter);
        var layout = InputLayout.Wrap(text, 0, InputLine.AvailableCells(40));
        Assert.Equal(text[..layout.Starts[1]] + "X" + text[layout.Starts[1]..], Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);

        keys.Push(Chars(text)).Push(Keys.Home, Keys.Down, Keys.Down).Push(Chars("Y")).Push(Keys.Enter);
        Assert.Equal(text[..layout.Starts[1]] + "Y" + text[layout.Starts[1]..], Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);

        // Up from the draft's first row recalls; Down from the recalled line's last row returns to the draft, kept whole.
        keys.Push(Chars(text)).Push(Keys.Home, Keys.Up, Keys.Down).Push(Keys.Enter);
        Assert.Equal(text, Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task OnThePane_ShiftUp_SelectsAcrossTheRows()
    {
        var (line, keys) = PaneLine();
        string text = TwoRows;
        var layout = InputLayout.Wrap(text, text.Length, InputLine.AvailableCells(40));
        int at = layout.IndexAt(0, layout.CursorCol);
        // Shift+Up from the end: row 0 at the caret's column to the end selected; Delete removes it.
        keys.Push(Chars(text)).Push(Keys.Shift(ConsoleKey.UpArrow), Keys.Delete).Push(Keys.Enter);
        Assert.Equal(text[..at], Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task OnThePane_TheGoalColumn_SurvivesAShortRow()
    {
        var (line, keys) = PaneLine();
        string first = new('a', 30);
        string last = new('b', 30);
        // Three rows by hard breaks (one inline paste): 30 a's, "x", 30 b's. From the end (row 2, column 30): Up lands on the short row's end, Up again on row 0 at column 30 — the goal column, not the short row's 1.
        keys.PushPaste(first + "\nx\n" + last).Push(Keys.Up, Keys.Up).Push(Chars("X")).Push(Keys.Enter);
        Assert.Equal(first + "X\nx\n" + last, Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);

        // A Left between the arrows ends the run: the column is the caret's again (the short row's start after the Left).
        keys.PushPaste(first + "\nx\n" + last).Push(Keys.Up, Keys.Left, Keys.Up).Push(Chars("Y")).Push(Keys.Enter);
        Assert.Equal("Y" + first + "\nx\n" + last, Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
    }

    [Fact]
    public async Task OnThePane_ARecalledWrappedLine_KeepsWalkingTheHistory_UntilEdited()
    {
        var (line, keys) = PaneLine();
        string text = TwoRows;
        keys.Push(Chars("first")).Push(Keys.Enter);
        await line.ReadAsync();
        keys.Push(Chars(text)).Push(Keys.Enter);
        await line.ReadAsync();

        // Up recalls the two-row line (the caret on its last row); Up again walks on to "first" rather than climbing a row.
        keys.Push(Keys.Up, Keys.Up).Push(Keys.Enter);
        Assert.Equal("first", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);

        // The two-row line recalled (past "first", sent again just now), then a Left: the walk is over and Up climbs a row.
        var layout = InputLayout.Wrap(text, text.Length - 1, InputLine.AvailableCells(40));
        int at = layout.IndexAt(0, layout.CursorCol);
        keys.Push(Keys.Up, Keys.Up, Keys.Left, Keys.Up).Push(Chars("X")).Push(Keys.Enter);
        Assert.Equal(text[..at] + "X" + text[at..], Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task WithoutThePane_UpIsHistory_OnALongLine()
    {
        // Off the pane the draft is one scrolling row: nothing to climb, Up recalls as ever.
        Type("earlier");
        Push(Keys.Enter);
        await SubmitAsync();
        Type(TwoRows);
        Push(Keys.Up, Keys.Enter);
        Assert.Equal("earlier", await SubmitAsync());
    }

    [Fact]
    public async Task UpDown_WalkHistory_AndRestoreTheDraft()
    {
        Type("first");
        Push(Keys.Enter);
        await SubmitAsync();
        Type("second");
        Push(Keys.Enter);
        await SubmitAsync();

        Type("draft");
        Push(Keys.Up, Keys.Up, Keys.Up);        // second, first, (stays at first)
        Push(Keys.Down, Keys.Down, Keys.Down);  // second, draft, (stays at draft)
        Push(Keys.Enter);
        Assert.Equal("draft", await SubmitAsync());

        Push(Keys.Up, Keys.Up);                 // draft, second
        Push(Keys.Enter);
        Assert.Equal("second", await SubmitAsync());

        Assert.Equal(new[] { "first", "second", "draft", "second" }, _line.History);
    }

    [Fact]
    public async Task ConsecutiveDuplicate_IsNotRepeatedInHistory()
    {
        Type("same");
        Push(Keys.Enter);
        await SubmitAsync();
        Type("same");
        Push(Keys.Enter);
        await SubmitAsync();
        Assert.Equal(new[] { "same" }, _line.History);
    }

    [Fact]
    public async Task InitialText_IsShownAndEditable_AndRememberFalseSkipsHistory()
    {
        Push(Keys.Backspace);
        Type("2");
        Push(Keys.Enter);

        var result = await _line.ReadAsync("http://x:1", remember: false);

        Assert.Equal("http://x:2", Assert.IsType<InputResult.Submitted>(result).Text);
        Assert.Contains("http://x:1", _console.Output);
        Assert.Empty(_line.History);
    }

    [Fact]
    public async Task LongLine_IsShownWholeOnSubmit()
    {
        string text = new('w', 60);   // wider than the 40-cell console
        Type(text);
        Push(Keys.Enter);

        Assert.Equal(text, await SubmitAsync());
        Assert.Contains(text, _console.Output.Replace("\n", ""));
    }

    [Fact]
    public async Task TypeAhead_IsConsumedFirst()
    {
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        var line = new InputLine(_console, keys);
        Type("ahead");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        while (keys.Buffered < 5)
        {
            await Task.Delay(2);
        }

        stop.Cancel();
        await watch;
        Push(Keys.Enter);

        Assert.Equal("ahead", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task PushToTalkKey_OnAnEmptyLine_ReturnsPushToTalk()
    {
        Push(Keys.F4);
        Assert.IsType<InputResult.PushToTalk>(await _line.ReadAsync(pushToTalk: ConsoleKey.F4));
        Assert.EndsWith("\n", _console.Output);
        Assert.Empty(_line.History);
    }

    [Fact]
    public async Task PushToTalkKey_WithTextOnTheLine_IsIgnored()
    {
        Type("draft");
        Push(Keys.F4);
        Type("!");
        Push(Keys.Enter);

        var result = await _line.ReadAsync(pushToTalk: ConsoleKey.F4);

        Assert.Equal("draft!", Assert.IsType<InputResult.Submitted>(result).Text);
    }

    [Fact]
    public async Task PushToTalkKey_NotConfigured_IsIgnored()
    {
        Push(Keys.F4);
        Type("x");
        Push(Keys.Enter);

        Assert.Equal("x", await SubmitAsync());
    }

    [Fact]
    public async Task PushToTalkKey_ADifferentKey_IsIgnored()
    {
        Push(Keys.Key(ConsoleKey.F8));
        Type("x");
        Push(Keys.Enter);

        Assert.Equal("x", Assert.IsType<InputResult.Submitted>(await _line.ReadAsync(pushToTalk: ConsoleKey.F4)).Text);
    }

    // ── The wake token ──────────────────────────────────────────────────────

    [Fact]
    public async Task Wake_OnAnEmptyLine_ReturnsWakeWord_AndEndsTheRow()
    {
        var scripted = new ScriptedInput();   // blocks until cancelled, as the real console does
        var line = new InputLine(_console, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        using var wake = new CancellationTokenSource();
        var read = line.ReadAsync(wake: wake.Token);
        await Task.Delay(20);
        Assert.False(read.IsCompleted);

        wake.Cancel();
        var result = await read.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("", Assert.IsType<InputResult.WakeWord>(result).Draft);
        Assert.EndsWith("\n", _console.Output);
        Assert.Empty(line.History);
    }

    [Fact]
    public async Task Wake_WithTextOnTheLine_ReturnsTheDraft()
    {
        var scripted = new ScriptedInput();
        scripted.Push(Keys.Char('d'), Keys.Char('r'), Keys.Char('a'));
        var line = new InputLine(_console, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        using var wake = new CancellationTokenSource();
        var read = line.ReadAsync(wake: wake.Token);
        await Task.Delay(20);

        wake.Cancel();
        var result = await read.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("dra", Assert.IsType<InputResult.WakeWord>(result).Draft);
        Assert.Contains("dra", _console.Output);
    }

    [Fact]
    public async Task Wake_AlreadyFired_ReturnsAtOnce()
    {
        using var wake = new CancellationTokenSource();
        wake.Cancel();

        var result = await _line.ReadAsync(wake: wake.Token);

        Assert.IsType<InputResult.WakeWord>(result);
    }

    [Fact]
    public async Task AppToken_StillThrows_EvenWithAWakeToken()
    {
        var scripted = new ScriptedInput();
        var line = new InputLine(_console, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        using var wake = new CancellationTokenSource();
        using var app = new CancellationTokenSource();
        var read = line.ReadAsync(cancellationToken: app.Token, wake: wake.Token);
        await Task.Delay(20);

        app.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ── The alert token ─────────────────────────────────────────────────────

    [Fact]
    public async Task Alert_WithTextOnTheLine_ReturnsAlertWithTheDraft_AndEndsTheRow()
    {
        var scripted = new ScriptedInput();
        scripted.Push(Keys.Char('d'), Keys.Char('r'), Keys.Char('a'));
        var line = new InputLine(_console, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        using var alert = new CancellationTokenSource();
        var read = line.ReadAsync(alert: alert.Token);
        await Task.Delay(20);
        Assert.False(read.IsCompleted);

        alert.Cancel();
        var result = await read.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("dra", Assert.IsType<InputResult.Alert>(result).Draft);
        Assert.EndsWith("\n", _console.Output);
        Assert.Empty(line.History);
    }

    [Fact]
    public async Task Alert_AlreadyFired_ReturnsAtOnce_AndTheWakeWinsWhenBothFired()
    {
        using var alert = new CancellationTokenSource();
        alert.Cancel();
        Assert.Equal("", Assert.IsType<InputResult.Alert>(await _line.ReadAsync(alert: alert.Token)).Draft);

        using var wake = new CancellationTokenSource();
        wake.Cancel();
        Assert.IsType<InputResult.WakeWord>(await _line.ReadAsync(wake: wake.Token, alert: alert.Token));
    }

    [Fact]
    public async Task AppToken_StillThrows_EvenWithAnAlertToken()
    {
        var scripted = new ScriptedInput();
        var line = new InputLine(_console, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        using var alert = new CancellationTokenSource();
        using var app = new CancellationTokenSource();
        var read = line.ReadAsync(cancellationToken: app.Token, alert: alert.Token);
        await Task.Delay(20);

        app.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Remember_AddsToHistory_WithTheSameDeDuplication()
    {
        _line.Remember("spoken");
        _line.Remember("spoken");
        _line.Remember("");
        Type("typed");
        Push(Keys.Enter);
        await SubmitAsync();
        _line.Remember("typed");

        Assert.Equal(new[] { "spoken", "typed" }, _line.History);

        Push(Keys.Up, Keys.Up, Keys.Enter);
        Assert.Equal("spoken", await SubmitAsync());
    }

    [Fact]
    public async Task Remembered_IsToldEachLineTheHistoryGains_NeverARepeatOrAnUnrememberedRead()
    {
        // Keep command history (2026-09-25): the screen stores what this hook hears.
        var heard = new List<string>();
        _line.Remembered = heard.Add;
        _line.Remember("spoken");
        _line.Remember("spoken");
        Type("typed");
        Push(Keys.Enter);
        await SubmitAsync();
        Type("typed");
        Push(Keys.Enter);
        await SubmitAsync();
        Push(Keys.Enter);
        await _line.ReadAsync("masked", remember: false);

        Assert.Equal(new[] { "spoken", "typed" }, heard);
    }

    [Fact]
    public async Task ReplaceHistory_LoadsTheLines_DeDuplicated_AndUpRecallsThem_WithoutTellingRemembered()
    {
        var heard = new List<string>();
        _line.Remembered = heard.Add;
        _line.Remember("gone");
        heard.Clear();

        _line.ReplaceHistory(["older", "newer", "newer", ""]);

        Assert.Equal(new[] { "older", "newer" }, _line.History);
        Assert.Empty(heard);
        Push(Keys.Up, Keys.Up, Keys.Enter);
        Assert.Equal("older", await SubmitAsync());

        _line.ReplaceHistory([]);
        Assert.Empty(_line.History);
    }

    [Fact]
    public void SubmittedMarkup_IsPinned_AndEscaped()
    {
        Assert.Equal("[#33E0FF bold]› hi [[x]][/]", InputLine.SubmittedMarkup("hi [x]"));
        Assert.Equal(37, InputLine.AvailableCells(40));
        Assert.Equal(1, InputLine.AvailableCells(2));
    }

    [Theory]
    [InlineData("hello", 5, 10, "hello", 5)]
    [InlineData("hello", 2, 10, "hello", 2)]
    [InlineData("abcdefghij", 10, 5, "ghij", 4)]
    [InlineData("abcdefghij", 3, 5, "abcde", 3)]
    [InlineData("abcdefghij", 6, 5, "cdefg", 4)]
    [InlineData("日本語", 3, 5, "本語", 4)]
    [InlineData("ab日本", 4, 5, "日本", 4)]
    [InlineData("x😀y", 3, 3, "😀y", 2)]
    [InlineData("", 0, 5, "", 0)]
    [InlineData("abc", 9, 0, "", 0)]
    public void Layout_ScrollsSoTheCursorIsVisible_WithoutSplittingWideChars(string text, int cursor, int available, string visible, int cursorCell)
    {
        var (v, c) = InputLine.Layout(text, cursor, available);
        Assert.Equal(visible, v);
        Assert.Equal(cursorCell, c);
    }

    // ── On the pane ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OnThePane_Enter_PutsTheLineInTheFlow_AndTheRowComesBackEmpty()
    {
        _console.Profile.Height = 8;
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider());
        pane.Show();
        var line = new InputLine(pane, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)));
        Type("hi");
        Push(Keys.Enter);

        var result = await line.ReadAsync();

        Assert.Equal("hi", Assert.IsType<InputResult.Submitted>(result).Text);
        // The committed line, then the pane again (three padding rows: row 1 of 8, four pane rows) with an empty row.
        string rule = new(ScreenPane.RuleGlyph, 40);
        Assert.EndsWith(InputLine.PromptGlyph + "hi\n" + new string('\n', 3) + rule + "\n" + InputLine.PromptGlyph + "\n" + rule + "\n", _console.Output);
        Assert.Equal(1, pane.FlowRow);
    }

    [Fact]
    public async Task OnThePane_ALongLine_WrapsWhileEditing_AndCommitsWhole()
    {
        _console.Profile.Height = 10;
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider());
        pane.Show();
        var line = new InputLine(pane, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)));
        const string text = "the quick brown fox jumps over the lazy dog again";
        Type(text);
        Push(Keys.Enter);

        var result = await line.ReadAsync();

        Assert.Equal(text, Assert.IsType<InputResult.Submitted>(result).Text);
        // While editing: the last space that fits 37 cells ends the first row, the rest is indented under it.
        // (The pane grew at "laz"; the later keys rewrote the rows in place, after the glyph.)
        Assert.Contains(InputLine.PromptGlyph + "the quick brown fox jumps over the\n" + InputLine.ContinuationIndent + "laz\n", _console.Output);
        Assert.Contains("the quick brown fox jumps over the\n" + InputLine.ContinuationIndent + "lazy dog again", _console.Output);
        // On Enter: the whole line in the flow (a markup line, wrapped by Spectre over two rows), then the pane with one empty row.
        string rule = new(ScreenPane.RuleGlyph, 40);
        Assert.EndsWith("lazy dog again\n" + new string('\n', 4) + rule + "\n" + InputLine.PromptGlyph + "\n" + rule + "\n", _console.Output);
        Assert.Equal(2, pane.FlowRow);
        Assert.Equal(1, pane.InputRows);
    }

    [Fact]
    public async Task OnThePane_AClick_PutsTheCursorUnderIt()
    {
        _console.Profile.Height = 10;
        int? cursorTop = 100;
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => cursorTop), new ManualTimeProvider());
        pane.Show();
        var scripted = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.Push(Chars("Now is time for all good men"));
        // Between the "i" and the "m" of "time": column 2 (the glyph) + 9.
        scripted.PushClick(2 + 9, 100);
        scripted.Push(Keys.Char('X'));
        // A click in the transcript changes nothing.
        scripted.PushClick(5, 90);
        scripted.Push(Keys.Char('Y'));
        scripted.Push(Keys.Enter);

        var result = await line.ReadAsync();

        Assert.Equal("Now is tiXYme for all good men", Assert.IsType<InputResult.Submitted>(result).Text);
    }

    /// <summary>Two left clicks on the hint row within DoubleClick.Interval end the read as HintRow with the draft, nothing remembered (2026-09-18); a first click there is nothing.</summary>
    [Fact]
    public async Task OnThePane_ADoubleClickOnTheHintRow_EndsTheRead_WithTheDraft()
    {
        _console.Profile.Height = 10;
        var time = new ManualTimeProvider();
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), time);
        pane.Show();
        var scripted = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.Push(Chars("a draft"));
        // A one-row draft: row 100, the rule 101, the hint row 102.
        scripted.PushClick(3, 102);
        scripted.Push(Keys.Char('!'));            // a key ends the pair
        scripted.PushClick(3, 102);
        scripted.PushClick(30, 102);              // the pair, anywhere on the row

        var result = await line.ReadAsync();

        Assert.Equal("a draft!", Assert.IsType<InputResult.HintRow>(result).Draft);
        Assert.Empty(line.History);
        Assert.False(scripted.IsAvailable);

        // The next read starts over: one click is nothing, the draft back and Enter sends it.
        scripted.PushClick(3, 102);
        scripted.Push(Chars(" more"));
        scripted.Push(Keys.Enter);
        Assert.Equal("a draft! more", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(initialText: "a draft!")).Text);
    }

    /// <summary>The result names the part under the pair (2026-09-18): the strip glyph with its column, the trailer, the row; a click on one part and one on another are two firsts.</summary>
    [Fact]
    public async Task OnThePane_AHintRowPair_CarriesItsZone_AndTwoZonesNeverPair()
    {
        _console.Profile.Height = 10;
        _console.Profile.Width = 40;
        var time = new ManualTimeProvider();
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), time) { Strip = () => "🔊 🎤", Trailer = () => "llama" };
        pane.Show();
        var scripted = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.PushClick(0, 102);               // 🔊
        scripted.PushClick(3, 102);               // 🎤: another part, a first
        scripted.PushClick(36, 102);              // the trailer ("llama" from column 34): a first
        scripted.PushClick(10, 102);              // the row: a first
        scripted.PushClick(4, 102);               // 🎤 again: a first (the row's click ended the pair)
        scripted.PushClick(3, 102);               // the pair

        var hint = Assert.IsType<InputResult.HintRow>(await line.ReadAsync());
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🎤", 3), hint.Hit);

        scripted.PushClick(38, 102);
        scripted.PushClick(34, 102);
        hint = Assert.IsType<InputResult.HintRow>(await line.ReadAsync());
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Trailer, "", 34), hint.Hit);

        scripted.PushClick(20, 102);
        scripted.PushClick(12, 102);
        hint = Assert.IsType<InputResult.HintRow>(await line.ReadAsync());
        Assert.Equal(ScreenPane.HintZone.Row, hint.Hit.Zone);
    }

    /// <summary>The toolbar pair key (2026-09-21): below the outside key and the pairing's own −1, never a hint key — the path its own, the blanks their own (later that day), each glyph column its own.</summary>
    [Fact]
    public void ToolbarPairKey_IsDistinctFromEveryHintKey_AndTheOutsideKey()
    {
        Assert.Equal(-3, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Path, "", 200)));
        Assert.Equal(-4, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1)));
        Assert.Equal(-5, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "⚙️", 0)));
        Assert.Equal(-8, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🛠️", 3)));
        Assert.Equal(-17, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🎭", 12)));
        Assert.Equal(-20, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "💬", 15)));   // the sixth glyph, later on 2026-09-21
        Assert.Equal(-23, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔒", 18)));   // the seventh, the lock, later still that day — the same key open; the key is the column's, not the glyph's
        Assert.Equal(-23, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔓", 18)));
        Assert.Equal(-23, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "💾", 18)));   // the disk in the lock's place while Memory is on (2026-09-22)
        Assert.Equal(-26, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🔒", 21)));   // the lock moved behind it
        Assert.Equal(-29, InputLine.ToolbarPairKey(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "👮", 24)));   // the officer last
        var keys = new[] { -3, -4, -5, -8, -11, -14, -17, -20, -23, -26, -29 };
        Assert.DoesNotContain(-1, keys);
        Assert.DoesNotContain(InputLine.OutsidePairKey, keys);
        Assert.All(keys, key => Assert.True(key < InputLine.HintPairKey(new ScreenPane.HintHit(ScreenPane.HintZone.Row, "", -1))));
    }

    /// <summary>Two left clicks on a toolbar glyph or on the path within DoubleClick.Interval end the read as ToolbarRow with the draft (2026-09-21); the blanks between them, or one click on each of two parts, are nothing.</summary>
    [Fact]
    public async Task OnThePane_ADoubleClickOnTheToolbar_EndsTheRead_WithTheDraft_AndItsPart()
    {
        _console.Profile.Height = 10;
        _console.Profile.Width = 40;
        var time = new ManualTimeProvider();
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), time) { Toolbar = () => new ScreenPane.ToolbarParts("🔧 🎓", @"D:\x") };
        pane.Show();
        var scripted = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.Push(Chars("a draft"));
        // A one-row draft: row 100, the rule 101, the hint row 102, the toolbar 103.
        scripted.PushClick(0, 103);               // 🔧
        scripted.PushClick(3, 103);               // 🎓: another glyph, a first
        scripted.PushClick(20, 103);              // the row: nobody's, and the pair's end
        scripted.PushClick(4, 103);               // 🎓: a first
        scripted.PushClick(3, 103);               // the pair

        var tool = Assert.IsType<InputResult.ToolbarRow>(await line.ReadAsync());
        Assert.Equal("a draft", tool.Draft);
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Glyph, "🎓", 3), tool.Hit);
        Assert.Empty(line.History);

        // The path (D:\x on the last four cells): a pair there is its own.
        scripted.PushClick(38, 103);
        scripted.PushClick(36, 103);
        tool = Assert.IsType<InputResult.ToolbarRow>(await line.ReadAsync(initialText: "a draft"));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Path, "", 35), tool.Hit);

        // Two on the blanks (later on 2026-09-21): the row's own part, the screen's /settings.
        scripted.PushClick(20, 103);
        scripted.PushClick(20, 103);
        tool = Assert.IsType<InputResult.ToolbarRow>(await line.ReadAsync(initialText: "a draft"));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), tool.Hit);
        Assert.Equal("a draft", tool.Draft);

        // A glyph then the blanks: two parts, no pair; Enter sends the draft.
        scripted.PushClick(0, 103);
        scripted.PushClick(20, 103);
        scripted.Push(Keys.Enter);
        Assert.Equal("a draft", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(initialText: "a draft")).Text);
    }

    /// <summary>
    /// The performance bar (2026-09-29, the user's ask): two clicks anywhere on it end the read as the toolbar's blanks —
    /// the screen's /settings — with the toolbar over it or without one; a click on the blanks then one on the bar pairs.
    /// </summary>
    [Fact]
    public async Task OnThePane_ADoubleClickOnThePerfBar_EndsTheRead_AsTheToolbarsBlanks()
    {
        _console.Profile.Height = 10;
        _console.Profile.Width = 40;
        var time = new ManualTimeProvider();
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), time)
        {
            Toolbar = () => new ScreenPane.ToolbarParts("🔧", @"D:\x"),
            Perf = _ => new PerfRow([new PerfSegment("CPU 5%", Theme.DimText)]),
        };
        pane.Show();
        var scripted = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.Push(Chars("a draft"));
        // A one-row draft: row 100, the rule 101, the hint row 102, the toolbar 103, the bar 104.
        scripted.PushClick(2, 104);
        scripted.PushClick(30, 104);

        var tool = Assert.IsType<InputResult.ToolbarRow>(await line.ReadAsync());
        Assert.Equal(ScreenPane.PerfBarHit, tool.Hit);
        Assert.Equal("a draft", tool.Draft);

        // The blanks, then the bar: one part.
        scripted.PushClick(20, 103);
        scripted.PushClick(2, 104);
        tool = Assert.IsType<InputResult.ToolbarRow>(await line.ReadAsync(initialText: "a draft"));
        Assert.Equal(new ScreenPane.ToolbarHit(ScreenPane.ToolbarZone.Row, "", -1), tool.Hit);

        // No toolbar: the bar under the hint row, at 103.
        pane.Toolbar = static () => null;
        scripted.PushClick(5, 103);
        scripted.PushClick(5, 103);
        tool = Assert.IsType<InputResult.ToolbarRow>(await line.ReadAsync(initialText: "a draft"));
        Assert.Equal(ScreenPane.PerfBarHit, tool.Hit);
    }

    /// <summary>Two clicks apart, or a click on the draft or in the transcript between them: the hint row is nothing and Enter sends.</summary>
    [Fact]
    public async Task OnThePane_HintRowClicks_ThatAreNoPair_OrWithTheFlagOff_ChangeNothing()
    {
        _console.Profile.Height = 10;
        var time = new ManualTimeProvider();
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), time);
        pane.Show();
        var scripted = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.Push(Chars("ab"));
        scripted.PushClick(3, 102);
        scripted.OnWait = () =>
        {
            time.Advance(DoubleClick.Interval + TimeSpan.FromMilliseconds(1));
            scripted.PushClick(3, 102);           // too late: a first again
            scripted.PushClick(2, 100);           // the draft's start: the cursor moves, the pair ends
            scripted.PushClick(3, 102);
            scripted.PushClick(5, 50);            // the transcript: the pair ends
            scripted.PushClick(3, 102);
            scripted.PushClick(3, 101);           // the rule: the pair ends
            scripted.PushClick(3, 102);
            scripted.Push(Keys.Char('X'));
            scripted.Push(Keys.Enter);
            scripted.OnWait = null;
        };

        Assert.Equal("Xab", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task OnThePane_ARightClick_PastesTheClipboard_Normalised()
    {
        _console.Profile.Height = 10;
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Show();
        var scripted = new ScriptedInput();
        string? clip = "two\r\nlines";
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)), () => clip);
        scripted.Push(Chars("ab"));
        scripted.Push(Keys.Left);
        scripted.PushClick(0, 0, MouseButton.Right);      // pasted at the cursor, wherever the click was
        scripted.Push(Keys.Enter);

        Assert.Equal("atwo linesb", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);

        // Nothing on the clipboard, or no clipboard at all: nothing happens.
        clip = null;
        scripted.Push(Keys.Char('z')).PushClick(0, 0, MouseButton.Right).Push(Keys.Enter);
        Assert.Equal("z", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);

        var without = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.Push(Keys.Char('w')).PushClick(0, 0, MouseButton.Right).Push(Keys.Enter);
        Assert.Equal("w", Assert.IsType<InputResult.Submitted>(await without.ReadAsync()).Text);
    }

    // ── The transcript scroll ───────────────────────────────────────────────

    /// <summary>PgUp/PgDn page the pane's transcript region and leave the draft alone; a page down at the bottom is nothing.</summary>
    [Fact]
    public async Task OnThePane_PageUpAndDown_ScrollTheTranscript_TheDraftKept()
    {
        _console.Profile.Height = 10;
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Open();
        pane.Show();
        for (int i = 0; i < 12; i++)
        {
            pane.Write(new Markup("line" + Environment.NewLine));
        }

        var line = new InputLine(pane, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)));
        Type("ab");
        Push(Keys.PageDown);   // the bottom already: nothing
        Push(Keys.PageUp);
        Type("c");
        Push(Keys.Enter);

        // Enter's commit is the bottom again (the pane's own rule); the read saw the page keys as scrolls, not text.
        Assert.Equal("abc", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.False(pane.Scrolled);
        Assert.Contains("⇡ 5 rows below", _console.Output);   // the 40-column row cuts the hint short
    }

    /// <summary>PgUp as the push-to-talk key: the empty line talks (pinned above), a line with text scrolls.</summary>
    [Fact]
    public async Task PushToTalkPageUp_WithTextOnTheLine_ScrollsInstead()
    {
        _console.Profile.Height = 10;
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Open();
        pane.Show();
        for (int i = 0; i < 12; i++)
        {
            pane.Write(new Markup("line" + Environment.NewLine));
        }

        var line = new InputLine(pane, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)));
        Type("draft");
        Push(Keys.PageUp);
        Push(Keys.Enter);

        Assert.Equal("draft", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(pushToTalk: ConsoleKey.PageUp)).Text);
        Assert.Contains("⇡ 5 rows below", _console.Output);   // the 40-column row cuts the hint short
    }

    // ── The wheel ───────────────────────────────────────────────────────────
    // The app holds the mouse for the whole screen (2026-09-17): a notch at the line scrolls the
    // pane's transcript region three rows, the draft untouched; under an overlay it is nothing.

    private (InputLine Line, ScriptedInput Keys, ScreenPane Pane) ScrollableLine(int lines = 12)
    {
        _console.Profile.Height = 10;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Open();
        pane.Show();
        for (int i = 0; i < lines; i++)
        {
            pane.Write(new Markup("line" + Environment.NewLine));
        }

        var scripted = new ScriptedInput();
        return (new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1))), scripted, pane);
    }

    [Fact]
    public async Task OnThePane_AWheelNotch_ScrollsTheTranscript_TheDraftKept()
    {
        var (line, keys, pane) = ScrollableLine();
        using var _ = pane;
        keys.Push(Chars("ab")).PushWheel(1).Push(Keys.Char('c')).PushWheel(-1, 3, 100).Push(Keys.Enter);

        // Up one notch (three rows: the anchor 3, three below — the hint says so), down one (the bottom); Enter sends the draft whole.
        Assert.Equal("abc", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.False(pane.Scrolled);
        Assert.Contains("⇡ 3 rows below", _console.Output);   // the 40-column row cuts the hint short
        Assert.Contains(InputLine.PromptGlyph + "abc\n", _console.Output);
    }

    /// <summary>Ctrl+Home (2026-09-18) is the transcript's top at once, the draft and cursor untouched; a plain Home is still the cursor's.</summary>
    [Fact]
    public async Task OnThePane_CtrlHome_IsTheTop_TheDraftAndCursorKept_PlainHomeMovesTheCursor()
    {
        var (line, keys, pane) = ScrollableLine();
        using var _ = pane;
        int waits = 0;
        keys.Push(Chars("ab")).Push(Keys.Left).Push(Keys.Ctrl(ConsoleKey.Home));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.Scrolled);
                    Assert.Equal(0, pane.ScrollTop);
                    keys.Push(Keys.Char('X')).Push(Keys.Home).Push(Keys.Char('Y')).Push(Keys.Enter);
                    break;
            }
        };

        Assert.Equal("YaXb", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.Contains("⇡ 6 rows below", _console.Output);
    }

    [Fact]
    public async Task OnThePane_CtrlEnd_IsTheBottomAgain_TheDraftAndCursorKept()
    {
        // Ctrl+End (2026-09-17) ends the scroll at once — before any Enter — and edits nothing: the
        // cursor stays where Left put it, so the next character lands before the "b".
        var (line, keys, pane) = ScrollableLine();
        using var _ = pane;
        int waits = 0;
        keys.Push(Chars("ab")).Push(Keys.PageUp);
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.Scrolled);
                    keys.Push(Keys.Left).Push(Keys.Ctrl(ConsoleKey.End));
                    break;
                case 1:
                    Assert.False(pane.Scrolled);
                    keys.Push(Keys.Char('X')).Push(Keys.Enter);
                    break;
            }
        };

        Assert.Equal("aXb", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.Contains("⇡ 5 rows below", _console.Output);   // the 40-column row cuts the hint short
    }

    [Fact]
    public async Task OnThePane_ADoubleClickOnTheScrolledHint_IsTheBottomAgain_TheReadGoingOn()
    {
        // Later on 2026-09-18: two clicks on the scroll's hint within the interval are Ctrl+End —
        // the read never ends, the draft and the cursor stay where they were.
        var (line, keys, pane) = ScrollableLine();
        using var _ = pane;
        int waits = 0;
        keys.Push(Chars("ab")).Push(Keys.PageUp);
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.Scrolled);
                    keys.Push(Keys.Left).PushClick(20, 102).PushClick(20, 102);
                    break;
                case 1:
                    Assert.False(pane.Scrolled);
                    keys.Push(Keys.Char('X')).Push(Keys.Enter);
                    break;
            }
        };

        Assert.Equal("aXb", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.Contains("⇡ 5 rows below", _console.Output);
    }

    [Fact]
    public async Task OnThePane_ScrolledHintClicks_ThatAreNoPair_ScrollNothing_AndAGlyphPairStillEndsTheRead()
    {
        // One click leaves the scroll; a pair on a strip glyph while scrolled is the glyph's row result, as ever.
        var (line, keys, pane) = ScrollableLine();
        using var _ = pane;
        pane.Strip = () => "🔊";
        pane.RefreshHint();
        int waits = 0;
        keys.Push(Chars("ab")).Push(Keys.PageUp);
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.Scrolled);
                    keys.PushClick(20, 102);
                    break;
                case 1:
                    Assert.True(pane.Scrolled);
                    keys.PushClick(0, 102).PushClick(0, 102);
                    break;
            }
        };

        var hit = Assert.IsType<InputResult.HintRow>(await line.ReadAsync());
        Assert.Equal("ab", hit.Draft);
        Assert.Equal(new ScreenPane.HintHit(ScreenPane.HintZone.Strip, "🔊", 0), hit.Hit);
        Assert.True(pane.Scrolled);
    }

    [Fact]
    public async Task OnThePane_APlainEnd_WhileScrolled_MovesTheCursor_AndScrollsNothing()
    {
        var (line, keys, pane) = ScrollableLine();
        using var _ = pane;
        int waits = 0;
        keys.Push(Chars("ab")).Push(Keys.PageUp).Push(Keys.Home).Push(Keys.End);
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.True(pane.Scrolled);   // End alone is the draft's
                keys.Push(Keys.Char('c')).Push(Keys.Enter);
            }
        };

        Assert.Equal("abc", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.False(pane.Scrolled);   // Enter's commit
    }

    [Fact]
    public async Task OnThePane_AWheelNotch_UnderAnOverlay_IsNothing()
    {
        var (line, keys, pane) = ScrollableLine();
        using var _ = pane;
        pane.ShowOverlay(new Markup("menu"), "hint", input: true);
        keys.PushWheel(1).Push(Chars("ab")).PushWheel(1, 3, 100).Push(Keys.Enter);

        Assert.Equal("ab", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.False(pane.Scrolled);
        Assert.DoesNotContain("rows below", _console.Output);
    }

    [Fact]
    public async Task OnThePane_AWheelNotch_OverAShortTranscript_IsNothing()
    {
        var (line, keys, pane) = ScrollableLine(lines: 2);
        using var _ = pane;
        keys.PushWheel(1).Push(Chars("ab")).Push(Keys.Enter);

        Assert.Equal("ab", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
        Assert.False(pane.Scrolled);
    }

    [Fact]
    public async Task WithoutThePane_AClick_IsNothing()
    {
        var scripted = new ScriptedInput();
        var line = new InputLine(_console, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.Push(Chars("ab")).PushClick(2, 0).Push(Keys.Char('c')).Push(Keys.Enter);

        Assert.Equal("abc", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task OnThePane_EscapeOnAnEmptyLine_LeavesNothingInTheFlow()
    {
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null), new ManualTimeProvider());
        pane.Show();
        var line = new InputLine(pane, new KeySource(_console.Input, TimeSpan.FromMilliseconds(1)));
        Type("x");
        Push(Keys.Escape, Keys.Escape);

        Assert.IsType<InputResult.Cancelled>(await line.ReadAsync());
        Assert.Equal(0, pane.FlowRow);
        Assert.EndsWith("x ", _console.Output);   // the row blanked in place, no new transcript line
    }

    // ── Selection ───────────────────────────────────────────────────────────
    // A stretch between an anchor and the cursor: Shift+Left/Right/Home/End, Ctrl+A, or a drag from
    // a left click on the pane. Delete and Backspace remove it, typing and a right-click paste
    // replace it, an unshifted Left/Right collapses onto its edge, Home/End/Up/Down/a click drop it.

    private static ConsoleKeyInfo ShiftLeft => Keys.Shift(ConsoleKey.LeftArrow);
    private static ConsoleKeyInfo ShiftRight => Keys.Shift(ConsoleKey.RightArrow);
    private static ConsoleKeyInfo ShiftHome => Keys.Shift(ConsoleKey.Home);
    private static ConsoleKeyInfo ShiftEnd => Keys.Shift(ConsoleKey.End);

    [Theory]
    [InlineData(ConsoleKey.Delete)]
    [InlineData(ConsoleKey.Backspace)]
    public async Task DeleteAndBackspace_RemoveTheSelection_AndLeaveTheCursorAtItsStart(ConsoleKey key)
    {
        Type("abcdefg xyzpdq");
        Push(Keys.Home, Keys.Right, Keys.Right, Keys.Right, Keys.Right, Keys.Right);   // abcde|fg xyzpdq
        Push(ShiftLeft, ShiftLeft, ShiftLeft);                                         // ab[cde]fg xyzpdq
        Push(Keys.Key(key));                                                           // ab|fg xyzpdq
        Type("X");
        Push(Keys.Enter);

        Assert.Equal("abXfg xyzpdq", await SubmitAsync());
    }

    [Fact]
    public async Task WithoutASelection_DeleteAndBackspace_TakeOneCharacter()
    {
        Type("abcd");
        Push(Keys.Left, Keys.Left, Keys.Delete, Keys.Backspace, Keys.Enter);   // ab|cd → ab|d → a|d

        Assert.Equal("ad", await SubmitAsync());
    }

    [Fact]
    public async Task ASelectionCollapsedBackOntoItsAnchor_IsNoSelection()
    {
        Type("abcd");
        Push(ShiftLeft, ShiftRight, Keys.Backspace, Keys.Enter);   // abc|d → abc[d] → abcd| → abc|

        Assert.Equal("abc", await SubmitAsync());
    }

    [Fact]
    public async Task TypingOverASelection_ReplacesIt()
    {
        Type("hello world");
        Push(ShiftLeft, ShiftLeft, ShiftLeft, ShiftLeft, ShiftLeft);   // hello [world]
        Type("there");
        Push(Keys.Enter);

        Assert.Equal("hello there", await SubmitAsync());
    }

    [Fact]
    public async Task ShiftEndAndShiftHome_SelectToTheEnds()
    {
        Type("abcdef");
        Push(Keys.Home, Keys.Right, Keys.Right, ShiftEnd);   // ab[cdef]
        Type("X");                                           // abX|
        Push(Keys.Left, ShiftHome, Keys.Backspace);          // [ab]X → |X
        Push(Keys.Enter);

        Assert.Equal("X", await SubmitAsync());
    }

    [Fact]
    public async Task CtrlA_SelectsEverything_AndNothingOnAnEmptyLine()
    {
        Push(Keys.Ctrl(ConsoleKey.A));   // empty: nothing to select, nothing happens
        Type("all of this");
        Push(Keys.Ctrl(ConsoleKey.A));
        Type("z");
        Push(Keys.Enter);

        Assert.Equal("z", await SubmitAsync());
    }

    [Fact]
    public async Task CtrlA_AsARealConsoleDeliversIt_WithTheControlCharacter()
    {
        Type("gone");
        Push(new ConsoleKeyInfo('', ConsoleKey.A, false, false, true));
        Push(Keys.Delete);
        Type("k");
        Push(Keys.Enter);

        Assert.Equal("k", await SubmitAsync());
    }

    [Fact]
    public async Task UnshiftedLeftAndRight_CollapseOntoTheSelectionsEdges()
    {
        Type("abcdef");
        Push(Keys.Home, Keys.Right, Keys.Right, ShiftRight, ShiftRight);   // ab[cd]ef, cursor after d
        Push(Keys.Left);                                                    // ab|cdef (the start, no step)
        Type("1");                                                          // ab1|cdef
        Push(ShiftLeft, ShiftLeft, Keys.Right);                             // [b1] → b1| (the end)
        Type("2");
        Push(Keys.Enter);

        Assert.Equal("ab12cdef", await SubmitAsync());
    }

    [Fact]
    public async Task HomeAndEnd_DropTheSelection_WithoutDeleting()
    {
        Type("abcd");
        Push(ShiftLeft, ShiftLeft, Keys.Home, Keys.Delete);   // ab[cd] → |abcd → |bcd
        Push(ShiftRight, Keys.End, Keys.Backspace);           // [b]cd → bcd| → bc|
        Push(Keys.Enter);

        Assert.Equal("bc", await SubmitAsync());
    }

    [Fact]
    public async Task HistoryWalk_DropsTheSelection()
    {
        Type("first");
        Push(Keys.Enter);
        Assert.Equal("first", await SubmitAsync());

        Type("abc");
        Push(ShiftLeft, Keys.Up, Keys.Backspace, Keys.Enter);   // ab[c] → first| → firs|

        Assert.Equal("firs", await SubmitAsync());
    }

    [Fact]
    public async Task ASelection_TakesWholeElements()
    {
        Push(Keys.Char('a'), Keys.Char("😀"[0]), Keys.Char("😀"[1]), Keys.Char('漢'), Keys.Char('b'));
        Push(Keys.Left, ShiftLeft, ShiftLeft, Keys.Delete);   // a[😀漢]b → a|b
        Push(Keys.Enter);

        Assert.Equal("ab", await SubmitAsync());
    }

    [Fact]
    public async Task Escape_ClearsTheDraft_SelectionAndAll()
    {
        Type("abc");
        Push(ShiftLeft, Keys.Escape);
        Type("d");
        Push(Keys.Enter);

        Assert.Equal("d", await SubmitAsync());
    }

    // ── Selection by mouse (the pane) ───────────────────────────────────────

    private (InputLine Line, ScriptedInput Keys) PaneLine(Func<string?>? clipboard = null, Func<string, bool>? copyToClipboard = null)
    {
        _console.Profile.Height = 10;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Show();
        var scripted = new ScriptedInput();
        return (new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)), clipboard, copyToClipboard: copyToClipboard), scripted);
    }

    // The glyph takes two cells: column 2 + n is the cell of the draft's character n (the cursor lands before it).
    private static int Col(int index) => 2 + index;

    [Theory]
    [InlineData(ConsoleKey.Delete)]
    [InlineData(ConsoleKey.Backspace)]
    public async Task ADragOverThePane_Selects_AndTheKeyRemovesIt(ConsoleKey key)
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abcdefg xyzpdq"));
        keys.PushClick(Col(2), 100).PushDrag(Col(3), 100).PushDrag(Col(5), 100);   // press on c, drag to after e
        keys.Push(Keys.Key(key)).Push(Keys.Enter);

        Assert.Equal("abfg xyzpdq", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task ADragBackwards_SelectsTheSame()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abcdefg xyzpdq"));
        keys.PushClick(Col(5), 100).PushDrag(Col(2), 100).Push(Keys.Delete).Push(Keys.Enter);

        Assert.Equal("abfg xyzpdq", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task ADrag_OffTheRows_OrAfterAClickThatMissed_IsNothing()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abcdef"));
        // A press in the transcript, then a drag onto the row: no anchor, nothing selected.
        keys.PushClick(Col(1), 90).PushDrag(Col(4), 100).Push(Keys.Delete);            // cursor still at the end: Delete does nothing
        // A press on the row, a drag above it: the selection stays where it was (empty).
        keys.PushClick(Col(2), 100).PushDrag(Col(4), 90).Push(Keys.Delete);            // ab|cdef → ab|def
        keys.Push(Keys.Enter);

        Assert.Equal("abdef", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task AClick_DropsTheSelection()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abcdef"));
        keys.Push(ShiftLeft, ShiftLeft).PushClick(Col(1), 100).Push(Keys.Delete).Push(Keys.Enter);   // abcd[ef] → a|bcdef → a|cdef

        Assert.Equal("acdef", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    // A click's anchor sits on the cursor for the drag that may follow; a key must not find it there
    // (a Backspace moved the cursor off it and left a phantom selection behind — past the text's end
    // when the click was at the end, and the next edit threw out of the read and took the app down).
    [Fact]
    public async Task AClickAtTheEnd_ThenTwoBackspaces_RemovesTwo()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abcdef"));
        keys.PushClick(Col(6), 100).Push(Keys.Backspace, Keys.Backspace).Push(Keys.Enter);

        Assert.Equal("abcd", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task AClick_ThenBackspace_ThenAKey_TypesInPlace()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abcdef"));
        keys.PushClick(Col(3), 100).Push(Keys.Backspace).Push(Keys.Char('X')).Push(Keys.Enter);   // abc|def → ab|def → abX|def

        Assert.Equal("abXdef", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task AClick_ThenAPlainArrow_MovesWithoutSelecting()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abcdef"));
        keys.PushClick(Col(3), 100).Push(Keys.Key(ConsoleKey.LeftArrow)).Push(Keys.Backspace).Push(Keys.Enter);   // abc|def → ab|cdef → a|cdef

        Assert.Equal("acdef", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task ShiftMovesBackOntoTheCursor_ThenBackspace_IsAPlainBackspace()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abc"));
        keys.Push(ShiftLeft, ShiftRight).Push(Keys.Backspace, Keys.Backspace).Push(Keys.Enter);

        Assert.Equal("a", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task ARightClickPaste_ReplacesTheSelection()
    {
        var (line, keys) = PaneLine(() => "Z");
        keys.Push(Chars("abcdef"));
        keys.PushClick(Col(1), 100).PushDrag(Col(5), 100).PushClick(0, 0, MouseButton.Right).Push(Keys.Enter);   // a[bcde]f → aZ|f

        Assert.Equal("aZf", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task ADragAcrossAWrappedDraft_SelectsAcrossTheRows()
    {
        // A geometry that follows the cursor, as the console's does: the area's first row is buffer
        // row 99 for a two-row draft, and the terminal's cursor is on whichever row the pane put it.
        _console.Profile.Height = 10;
        ScreenPane? pane = null;
        pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 99 + pane!.CursorInputRow), new ManualTimeProvider());
        pane.Show();
        var keys = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(keys, TimeSpan.FromMilliseconds(1)));

        // Width 40 → 37 cells per row: the words wrap onto a second row.
        string text = string.Join(' ', Enumerable.Repeat("word", 12));   // 59 chars, two rows
        keys.Push(Chars(text));
        var layout = InputLayout.Wrap(text, text.Length, InputLine.AvailableCells(40));
        Assert.Equal(2, layout.Rows.Count);
        // Press on row 0 ("wo|rd …"), drag two characters into row 1: the selection spans the wrap.
        keys.PushClick(Col(2), 99).PushDrag(Col(2), 100).Push(Keys.Delete).Push(Keys.Enter);

        string expected = text[..2] + text[(layout.Starts[1] + 2)..];
        Assert.Equal(expected, Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    [Fact]
    public async Task WithoutThePane_ADrag_IsNothing()
    {
        var scripted = new ScriptedInput();
        var line = new InputLine(_console, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.Push(Chars("ab")).PushClick(2, 0).PushDrag(3, 0).Push(Keys.Delete).Push(Keys.Char('c')).Push(Keys.Enter);

        Assert.Equal("abc", Assert.IsType<InputResult.Submitted>(await line.ReadAsync()).Text);
    }

    // ── Paste (one block, never a submission) ───────────────────────────────

    private static string Block(int lines) => string.Join("\r\n", Enumerable.Range(1, lines).Select(i => $"line {i}"));

    [Fact]
    public async Task APaste_LandsAsOneBlock_AndNothingSendsUntilEnter()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("say: ")).PushPaste("one\r\ntwo\r\n").Push(Chars("!")).Push(Keys.Enter);

        var result = await line.ReadAsync(multiline: true);

        // Two rows on the pane while it waited (a hard break), one message when sent, the
        // transcript's line with the second row under the glyph.
        Assert.Equal("say: one\ntwo!", Assert.IsType<InputResult.Submitted>(result).Text);
        Assert.Contains("say: one\n" + InputLine.ContinuationIndent + "two!\n", _console.Output);
        Assert.Equal(new[] { "say: one\ntwo!" }, line.History);
    }

    /// <summary>A queued line's events (2026-09-18) are handled ahead of the console, the same arms: the paste's token, the label on the transcript row, the history, the expansion; the keys waiting in the console are untouched, and a cancelled alert token does not stop the replay.</summary>
    [Fact]
    public async Task ALongPaste_Replayed_LandsAsAtIdle_AheadOfTheConsole()
    {
        var (line, keys) = PaneLine();
        string block = Block(9);
        keys.Push(Chars("later")).Push(Keys.Enter);   // the console's own keys: read by the NEXT read
        InputEvent[] replay = [.. Chars("sum ").Select(k => new InputEvent.Key(k)), new InputEvent.Paste(block), new InputEvent.Key(Keys.Enter)];
        using var alert = new CancellationTokenSource();
        alert.Cancel();

        var result = await line.ReadAsync(multiline: true, alert: alert.Token, replay: replay);

        string expected = "sum " + block.Replace("\r\n", "\n");
        Assert.Equal(expected, Assert.IsType<InputResult.Submitted>(result).Text);
        Assert.Contains("sum [Pasted text #1 +9 lines]", _console.Output);
        Assert.Equal(1, line.Pastes.Count);
        Assert.Equal(expected, line.Pastes.Expand(line.History[0]));
        Assert.True(keys.IsAvailable);                  // the console's keys still wait
        Assert.Equal("later", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
    }

    [Fact]
    public async Task ALongPaste_IsAToken_ThatExpandsOnEnter_AndTheTranscriptKeepsTheLabel()
    {
        var (line, keys) = PaneLine();
        string block = Block(5);
        keys.Push(Chars("Summarise: ")).PushPaste(block).Push(Chars(" please")).Push(Keys.Enter);

        var result = await line.ReadAsync(multiline: true);

        string expected = "Summarise: " + block.Replace("\r\n", "\n") + " please";
        Assert.Equal(expected, Assert.IsType<InputResult.Submitted>(result).Text);
        Assert.Contains("Summarise: [Pasted text #1 +5 lines]", _console.Output);   // the 40-column transcript wraps " please" onto the next row
        Assert.DoesNotContain("line 3", _console.Output);
        Assert.Equal(1, line.Pastes.Count);
        // The history keeps the token: Up recalls the same line and Enter sends the block again.
        Assert.Equal(expected, line.Pastes.Expand(line.History[0]));
        keys.Push(Keys.Up).Push(Keys.Enter);
        Assert.Equal(expected, Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
    }

    [Fact]
    public void PreviewText_AndPreviewMarkup_ArePinned()
    {
        // One block: its preview alone, right under its label; two or more: each headed by its label.
        Assert.Equal("", InputLine.PreviewText([]));
        Assert.Equal("a\nb", InputLine.PreviewText([("[Pasted text #1 +2 lines]", "a\nb")]));
        Assert.Equal("[Pasted text #1 +2 lines]\na\nb\n[Pasted text #2 +1 line]\nc", InputLine.PreviewText([("[Pasted text #1 +2 lines]", "a\nb"), ("[Pasted text #2 +1 line]", "c")]));
        // Every line under the glyph, dim, escaped.
        Assert.Equal("[#9A8BB8]  a [[x]]\n  b[/]", InputLine.PreviewMarkup("a [x]\nb"));
        Assert.Throws<ArgumentNullException>(() => InputLine.PreviewText(null!));
        Assert.Throws<ArgumentNullException>(() => InputLine.PreviewMarkup(null!));
    }

    [Fact]
    public async Task WithAPastePreview_TheTranscriptShowsTheStartOfTheBlock_UnderTheLabel()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("q ")).PushPaste(Block(5)).Push(Keys.Enter);

        var result = await line.ReadAsync(multiline: true, pastePreview: 3);

        // The model gets the whole block; the transcript the label, then three dim lines and the closing label.
        Assert.Equal("q " + Block(5).Replace("\r\n", "\n"), Assert.IsType<InputResult.Submitted>(result).Text);
        Assert.Contains("q [Pasted text #1 +5 lines]\n  line 1\n  line 2\n  line 3\n  [… +2 more lines]\n", _console.Output);
        Assert.DoesNotContain("line 4", _console.Output);
    }

    [Fact]
    public async Task WithAPastePreview_TwoBlocks_AreEachHeadedByTheirLabel_AndAShortOneIsWhole()
    {
        var (line, keys) = PaneLine();
        keys.PushPaste(Block(4)).Push(Chars(" vs ")).PushPaste(Block(6)).Push(Keys.Enter);

        await line.ReadAsync(multiline: true, pastePreview: 5);

        // (The 40-column transcript wraps the sent line itself; the preview under it is read alone.)
        Assert.Contains(
            "  [Pasted text #1 +4 lines]\n  line 1\n  line 2\n  line 3\n  line 4\n"
            + "  [Pasted text #2 +6 lines]\n  line 1\n  line 2\n  line 3\n  line 4\n  line 5\n  [… +1 more line]\n",
            _console.Output);
    }

    [Fact]
    public async Task ASecondLongPaste_IsNumberTwo_AndBackspaceRemovesATokenWhole()
    {
        var (line, keys) = PaneLine();
        keys.PushPaste(Block(4)).Push(Chars(" vs ")).PushPaste(Block(6)).Push(Keys.Backspace).Push(Chars("x")).Push(Keys.Enter);

        var result = await line.ReadAsync(multiline: true);

        Assert.Equal(Block(4).Replace("\r\n", "\n") + " vs x", Assert.IsType<InputResult.Submitted>(result).Text);
        // On the pane the labels are unbreakable words (the 40-column row would otherwise split one); the transcript's line has plain spaces.
        Assert.Contains(PasteBlocks.Unbreakable("[Pasted text #2 +6 lines]"), _console.Output);
        Assert.DoesNotContain("[Pasted text #2 +6 lines]", _console.Output);
        Assert.Contains("[Pasted text #1 +4 lines] vs x\n", _console.Output);
    }

    [Fact]
    public async Task AClickInsideALabel_LandsBeforeOrAfterTheToken_NeverInsideIt()
    {
        var (line, keys) = PaneLine();
        string label = PasteBlocks.Label(1, Block(5));
        keys.Push(Chars("ab")).PushPaste(Block(5)).Push(Chars("cd"));
        // The label starts at column Col(2); a click in its second half lands after the token.
        keys.PushClick(Col(2 + label.Length - 2), 100).Push(Keys.Char('X'));
        // And one in its first half lands before it.
        keys.PushClick(Col(2 + 1), 100).Push(Keys.Char('Y'));
        keys.Push(Keys.Enter);

        var result = await line.ReadAsync(multiline: true);
        Assert.Equal("abY" + Block(5).Replace("\r\n", "\n") + "Xcd", Assert.IsType<InputResult.Submitted>(result).Text);
    }

    [Fact]
    public async Task ARightClick_PastesByTheSameRule_OnTheChatLine()
    {
        string clip = Block(5);
        var (line, keys) = PaneLine(() => clip);
        keys.Push(Chars("q ")).PushClick(0, 0, MouseButton.Right).Push(Keys.Enter);

        Assert.Equal("q " + Block(5).Replace("\r\n", "\n"), Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
        Assert.Contains("q [Pasted text #1 +5 lines]\n", _console.Output);
    }

    [Fact]
    public async Task ASingleLineRead_FlattensAPaste()
    {
        var (line, keys) = PaneLine();
        keys.PushPaste("one\r\ntwo\r\n").Push(Keys.Enter);

        Assert.Equal("one two", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(remember: false, allowEmpty: true)).Text);
        Assert.Equal(0, line.Pastes.Count);

        // Long, too: a settings field never holds a token.
        keys.PushPaste(Block(5)).Push(Keys.Enter);
        Assert.Equal(Block(5).Replace("\r\n", " "), Assert.IsType<InputResult.Submitted>(await line.ReadAsync(remember: false, allowEmpty: true)).Text);
        Assert.Equal(0, line.Pastes.Count);
    }

    [Fact]
    public async Task APasteOfOnlyLineBreaks_IsNothing_AndEscapeClearsTokens()
    {
        var (line, keys) = PaneLine();
        keys.PushPaste("\r\n\r\n").Push(Keys.Enter);                 // nothing landed: Enter on a blank line keeps reading
        keys.PushPaste(Block(5)).Push(Keys.Escape).Push(Chars("k")).Push(Keys.Enter);

        Assert.Equal("k", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
        Assert.Equal(1, line.Pastes.Count);   // the block is kept for the session; the draft let go of it
    }

    [Fact]
    public async Task APasteReplacesTheSelection()
    {
        var (line, keys) = PaneLine();
        keys.Push(Chars("abcdef")).Push(Keys.Home).Push(ShiftRight).Push(ShiftRight).PushPaste("XY\r\nZ").Push(Keys.Enter);

        Assert.Equal("XY\nZcdef", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
    }

    [Fact]
    public async Task WithoutThePane_APastedLineBreak_ShowsAsASpace()
    {
        var scripted = new ScriptedInput();
        var line = new InputLine(_console, new KeySource(scripted, TimeSpan.FromMilliseconds(1)));
        scripted.PushPaste("ab\r\ncd").Push(Keys.Enter);

        Assert.Equal("ab\ncd", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
        Assert.Contains("ab cd", _console.Output);
    }

    [Fact]
    public void SubmittedMarkup_IndentsTheLinesAfterTheFirst()
    {
        Assert.EndsWith("]› a\n  b\n  c[/]", InputLine.SubmittedMarkup("a\nb\nc"));
        Assert.Throws<ArgumentNullException>(() => InputLine.SubmittedMarkup(null!));
    }

    // ── Images (a dropped file is a paste of its path) ──────────────────────

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    private string Bmp(string name, int width = 4, int height = 4)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, SmokeChecks.SolidBmp(width, height));
        return path;
    }

    [Fact]
    public async Task ADroppedImage_IsAToken_SentBesideTheText_AndTheTranscriptKeepsTheLabel()
    {
        var (line, keys) = PaneLine();
        string path = Bmp("a shot.bmp");
        keys.Push(Chars("what is ")).PushPaste("\"" + path + "\"").Push(Chars("?")).Push(Keys.Enter);

        var result = await line.ReadAsync(multiline: true);

        var submitted = Assert.IsType<InputResult.Submitted>(result);
        Assert.Equal("what is [Image #1]?", submitted.Text);
        var image = Assert.Single(submitted.Images);
        Assert.Equal(path, image.Path);
        Assert.Equal(ImageFile.Png, image.MediaType);
        Assert.Contains(PasteBlocks.Unbreakable("[Image #1]"), _console.Output);   // the row
        Assert.Contains("what is [Image #1]?\n", _console.Output);                  // the transcript
        Assert.Contains(InputLine.ReadingImage, _console.Output);                   // the hint row while it read
        Assert.DoesNotContain(path, _console.Output);
        Assert.Equal(1, line.Pastes.ImageCount);
        // Up recalls the token and Enter sends the same picture again.
        keys.Push(Keys.Up).Push(Keys.Enter);
        var again = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));
        Assert.Equal("what is [Image #1]?", again.Text);
        Assert.Same(image, Assert.Single(again.Images));
    }

    /// <summary>Several files dropped at once are one paste of their paths, one per line: a token each, a space between.</summary>
    [Fact]
    public async Task SeveralDroppedImages_AreATokenEach()
    {
        var (line, keys) = PaneLine();
        string a = Bmp("a shot.bmp");
        string b = Bmp("b.bmp");
        keys.Push(Chars("compare ")).PushPaste("\"" + a + "\"\r\n" + b + "\r\n").Push(Keys.Enter);

        var result = await line.ReadAsync(multiline: true);

        var submitted = Assert.IsType<InputResult.Submitted>(result);
        Assert.Equal("compare [Image #1] [Image #2]", submitted.Text);
        Assert.Equal([a, b], submitted.Images.Select(i => i.Path));
        Assert.Contains(PasteBlocks.Unbreakable("[Image #1]"), _console.Output);
        Assert.Contains(PasteBlocks.Unbreakable("[Image #2]"), _console.Output);
        Assert.Contains(InputLine.ReadingImages(2), _console.Output);
        Assert.DoesNotContain(a, _console.Output);
        Assert.Equal(2, line.Pastes.ImageCount);
    }

    /// <summary>A file among several that cannot be read stays on the line as its path, with the notice; the others are tokens.</summary>
    [Fact]
    public async Task ADroppedImageThatCannotBeRead_AmongOthers_StaysAsItsPath()
    {
        _console.Profile.Height = 10;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Show();
        var scripted = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)), notices: new TranscriptRenderer(pane));
        string a = Bmp("a.bmp");
        string broken = Path.Combine(_dir, "broken.png");
        File.WriteAllBytes(broken, [1, 2, 3, 4]);
        scripted.PushPaste(a + "\r\n" + broken + "\r\n").Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("[Image #1] " + broken, submitted.Text);
        Assert.Equal(a, Assert.Single(submitted.Images).Path);
        Assert.Contains("(image not attached: ", _console.Output);
        Assert.Equal(1, line.Pastes.ImageCount);
    }
    [Fact]
    public async Task ImagesAndLongPastes_NumberSeparately_AndBackspaceRemovesAnImageWhole()
    {
        var (line, keys) = PaneLine();
        keys.PushPaste(Block(4)).Push(Chars(" ")).PushPaste(Bmp("a.bmp")).Push(Chars(" ")).PushPaste(Bmp("b.bmp")).Push(Keys.Backspace).Push(Chars("x")).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal(Block(4).Replace("\r\n", "\n") + " [Image #1] x", submitted.Text);
        Assert.EndsWith("a.bmp", Assert.Single(submitted.Images).Path);
        Assert.Equal(2, line.Pastes.ImageCount);   // b.bmp was read and let go of
        Assert.Contains("[Pasted text #1 +4 lines] [Image #1] x\n", _console.Output);
    }

    [Fact]
    public async Task AFileThatCannotBeAttached_IsANotice_AndItsPathStaysText()
    {
        _console.Profile.Height = 10;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Show();
        var scripted = new ScriptedInput();
        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)), notices: new TranscriptRenderer(pane));
        Directory.CreateDirectory(_dir);
        string corrupt = Path.Combine(_dir, "corrupt.png");
        File.WriteAllText(corrupt, "not a picture");
        scripted.PushPaste(corrupt).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal(corrupt, submitted.Text);
        Assert.Empty(submitted.Images);
        Assert.Contains("(image not attached: ", _console.Output);
        Assert.Equal(0, line.Pastes.Count);
    }

    [Fact]
    public async Task ATypedPath_APathAmongWords_OrAPathInAField_StaysText()
    {
        var (line, keys) = PaneLine();
        string path = Bmp("typed.bmp");
        keys.Push(Chars(path)).Push(Keys.Enter);
        keys.PushPaste("see " + path).Push(Keys.Enter);
        keys.PushPaste(path).Push(Keys.Enter);

        var typed = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));
        var amongWords = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));
        var field = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: false));

        Assert.Equal(path, typed.Text);
        Assert.Equal("see " + path, amongWords.Text);
        Assert.Equal(path, field.Text);
        Assert.Empty(typed.Images);
        Assert.Empty(amongWords.Images);
        Assert.Empty(field.Images);
        Assert.Equal(0, line.Pastes.Count);
    }

    [Fact]
    public async Task ACopiedPath_PastedByARightClick_IsAnImageToo()
    {
        string path = Bmp("clip.bmp");
        var (line, keys) = PaneLine(() => path);
        keys.Push(Chars("look ")).PushClick(Col(5), 100, MouseButton.Right).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("look [Image #1]", submitted.Text);
        Assert.Equal(path, Assert.Single(submitted.Images).Path);
    }
    // ── A picture on the clipboard (the line's own paste) ──────────────────

    private static readonly ConsoleKeyInfo CtrlV = new('\x16', ConsoleKey.V, false, false, true);
    private static readonly ConsoleKeyInfo AltV = new('v', ConsoleKey.V, false, true, false);

    private (InputLine Line, ScriptedInput Keys) PictureLine(Func<byte[]?>? picture, Func<string?>? clipboard = null)
    {
        _console.Profile.Height = 10;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Show();
        var scripted = new ScriptedInput();
        return (new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)), clipboard, notices: new TranscriptRenderer(pane), clipboardImage: picture), scripted);
    }

    [Fact]
    public async Task CtrlV_KeepsTheClipboardsOwnBytes_ForGenerateImage_WhileTheModelGetsTheDownscale()
    {
        // A paste as generate_image's input (later still on 2026-09-24): the store keeps the picture as it was.
        byte[] wide = SmokeChecks.SolidBmp(3000, 10);
        var (line, keys) = PictureLine(() => wide);
        keys.Push(CtrlV).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.True(Assert.Single(submitted.Images).Width <= ImageFile.MaxSide);
        var original = line.Pastes.Original(1);
        Assert.Same(wide, original!.Bytes);
        Assert.Null(original.FileName);   // a clipboard picture has no name: the saver stamps one
    }

    [Fact]
    public async Task CtrlV_PastesTheClipboardPicture_AsAnImageToken()
    {
        var (line, keys) = PictureLine(() => SmokeChecks.SolidBmp(4, 3));
        keys.Push(Chars("see ")).Push(CtrlV).Push(Chars("!")).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("see [Image #1]!", submitted.Text);
        var image = Assert.Single(submitted.Images);
        Assert.Equal("clipboard-1.png", image.Path);
        Assert.Equal(ImageFile.Png, image.MediaType);
        Assert.Equal((4, 3), (image.Width, image.Height));
        Assert.Contains(InputLine.ReadingImage, _console.Output);
        Assert.Contains("see [Image #1]!\n", _console.Output);
        Assert.Equal(1, line.Pastes.ImageCount);
    }

    [Fact]
    public async Task AltV_PastesThePicture_AndTypesNoV()
    {
        var (line, keys) = PictureLine(() => SmokeChecks.SolidBmp(4, 4));
        keys.Push(AltV).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("[Image #1]", submitted.Text);
        Assert.Single(submitted.Images);
    }

    [Fact]
    public async Task ThePicture_WinsOverTheText_OnTheClipboard()
    {
        var (line, keys) = PictureLine(() => SmokeChecks.SolidBmp(4, 4), () => "some words");
        keys.Push(CtrlV).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("[Image #1]", submitted.Text);
        Assert.Single(submitted.Images);
    }

    [Fact]
    public async Task NoPicture_PastesTheText_AndNeitherPastesNothing()
    {
        var (line, keys) = PictureLine(() => null, () => "some words");
        keys.Push(CtrlV).Push(Keys.Enter);
        var (bare, bareKeys) = PictureLine(null);
        bareKeys.Push(AltV).Push(Chars("x")).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));
        var nothing = Assert.IsType<InputResult.Submitted>(await bare.ReadAsync(multiline: true));

        Assert.Equal("some words", submitted.Text);
        Assert.Empty(submitted.Images);
        Assert.Equal("x", nothing.Text);
    }

    [Fact]
    public async Task ARightClick_PastesThePicture_Too()
    {
        var (line, keys) = PictureLine(() => SmokeChecks.SolidBmp(4, 4), () => "text instead");
        keys.Push(Chars("look ")).PushClick(Col(5), 100, MouseButton.Right).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("look [Image #1]", submitted.Text);
        Assert.Equal("clipboard-1.png", Assert.Single(submitted.Images).Path);
    }

    [Fact]
    public async Task ACorruptPicture_IsANotice_AndNothingOnTheLine()
    {
        var (line, keys) = PictureLine(() => "not a picture"u8.ToArray());
        keys.Push(Chars("a")).Push(CtrlV).Push(Chars("b")).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("ab", submitted.Text);
        Assert.Empty(submitted.Images);
        Assert.Contains("(image not attached: ", _console.Output);   // the sentence wraps at 40 columns
        Assert.Contains("could not be read as an image)", _console.Output);
        Assert.Equal(0, line.Pastes.Count);
    }

    [Fact]
    public async Task AField_TakesNoPicture()
    {
        var (line, keys) = PictureLine(() => SmokeChecks.SolidBmp(4, 4), () => "one\ntwo");
        keys.Push(CtrlV).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: false));

        Assert.Equal("one two", submitted.Text);
        Assert.Empty(submitted.Images);
        Assert.Equal(0, line.Pastes.ImageCount);
    }

    [Fact]
    public async Task ThePicture_ReplacesTheSelection()
    {
        var (line, keys) = PictureLine(() => SmokeChecks.SolidBmp(4, 4));
        keys.Push(Chars("abc")).Push(Keys.Shift(ConsoleKey.LeftArrow)).Push(Keys.Shift(ConsoleKey.LeftArrow)).Push(AltV).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("a[Image #1]", submitted.Text);
    }

    [Fact]
    public async Task ImageNumbers_CountFilesAndTheClipboardTogether()
    {
        string path = Bmp("first.bmp");
        var (line, keys) = PictureLine(() => SmokeChecks.SolidBmp(4, 4));
        keys.PushPaste(path).Push(Chars(" ")).Push(CtrlV).Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("[Image #1] [Image #2]", submitted.Text);
        Assert.Equal([path, "clipboard-2.png"], submitted.Images.Select(i => i.Path));
    }

    [Fact]
    public async Task AnAltLetter_IsAChord_ButAltGrStillTypes()
    {
        var (line, keys) = PictureLine(null);
        keys.Push(new ConsoleKeyInfo('a', ConsoleKey.A, false, true, false))
            .Push(new ConsoleKeyInfo('@', ConsoleKey.Q, false, true, true))
            .Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true));

        Assert.Equal("@", submitted.Text);
    }

    // ── @-mentions (the pane) ───────────────────────────────────────────────

    // A working directory of test/thing.txt, test/bling.txt and top.txt, answered the way
    // WorkingDirectory.Complete does (one level for an empty prefix, the subtree otherwise).
    private static readonly IReadOnlyDictionary<string, string[]> Tree = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [""] = ["test/", "top.txt"],
        ["t"] = ["test/", "test/thing.txt", "top.txt"],
        ["te"] = ["test/", "test/thing.txt"],
        ["test/"] = ["test/bling.txt", "test/thing.txt"],
        ["test/t"] = ["test/thing.txt"],
        ["b"] = ["test/bling.txt"],
    };

    private (InputLine Line, ScriptedInput Keys, ScreenPane Pane, List<string> Asked) MentionLine(bool truncated = false)
    {
        _console.Profile.Height = 12;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Show();
        var scripted = new ScriptedInput();
        var asked = new List<string>();
        MentionResult Complete(string query)
        {
            asked.Add(query);
            return new MentionResult(FileOutcome.Ok, Tree.TryGetValue(query, out var paths) ? paths : [], truncated);
        }

        return (new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)), mentions: Complete), scripted, pane, asked);
    }

    private static string Highlighted(string path) => MenuPane.Pointer + path;

    [Fact]
    public async Task Mentions_AnAtWordOpensTheList_AndEnterAppliesThePick_NotSends()
    {
        var (line, keys, pane, asked) = MentionLine();
        int waits = 0;
        keys.Push(Chars("@t"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    // The list above the row, the input slot kept, the first match highlighted, the hint row.
                    Assert.True(pane.OverlayOpen);
                    Assert.True(pane.OverlayHasInput);
                    Assert.Contains(Highlighted("test/"), _console.Output);
                    Assert.Contains(MenuPane.NoPointer + "test/thing.txt", _console.Output);
                    Assert.Contains(MentionCompleter.Hint, _console.Output);
                    keys.Push(Keys.Down, Keys.Enter);
                    break;
                case 1:
                    // Enter applied the highlight: the list is gone and nothing was sent.
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("x")).Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("@test/thing.txt x", submitted.Text);
        Assert.Equal(["", "t"], asked);   // the bare @ lists the top level, then the prefix
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task Mentions_TypingFilters_TabApplies_AndBackspacePastTheAtCloses()
    {
        var (line, keys, pane, asked) = MentionLine();
        int waits = 0;
        keys.Push(Chars("@"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.Contains(Highlighted("test/"), _console.Output);
                    keys.Push(Chars("b"));
                    break;
                case 1:
                    Assert.Contains(Highlighted("test/bling.txt"), _console.Output);
                    keys.Push(Keys.Backspace, Keys.Backspace);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("@b")).Push(Keys.Tab).Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("@test/bling.txt", submitted.Text);   // the trailing space is trimmed on Enter
        Assert.Equal(["", "b", "", "", "b"], asked);       // "@", "@b"; Backspace to "@" again, then away; "@", "@b" once more
    }

    [Fact]
    public async Task Mentions_EscClosesTheListAndKeepsTheDraft_UntilTheWordChanges()
    {
        var (line, keys, pane, asked) = MentionLine();
        int waits = 0;
        keys.Push(Chars("@t"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Keys.Escape);
                    break;
                case 1:
                    // Closed, the draft kept; a cursor move over the same word does not bring it back.
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Home, Keys.End);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("e"));
                    break;
                case 3:
                    // The word changed: the list is back, filtered.
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("test/"), _console.Output);
                    keys.Push(Keys.Escape, Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("@te", submitted.Text);
        Assert.Equal(["", "t", "te"], asked);
    }

    [Fact]
    public async Task Mentions_AFolderApplied_IsAMentionLikeAFile()
    {
        var (line, keys, pane, _) = MentionLine();
        int waits = 0;
        keys.Push(Chars("@t")).Push(Keys.Enter);   // test/ is the first row
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);
                keys.Push(Keys.Enter);
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("@test/", submitted.Text);
    }

    [Fact]
    public async Task Mentions_AFolderRemained_KeepsTheListOnItsContents()
    {
        var (line, keys, pane, asked) = MentionLine();
        int waits = 0;
        keys.Push(Chars("@t")).Push(Keys.Enter);   // test/ applied, the list stays
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("test/bling.txt"), _console.Output);
                    keys.Push(Keys.Up, Keys.Enter);   // wraps to the last row: test/thing.txt
                    break;
                case 1:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Remain));

        Assert.Equal("@test/thing.txt", submitted.Text);
        Assert.Equal(["", "t", "test/"], asked);
    }

    [Fact]
    public async Task Mentions_APasteOfTheWord_OpensTheListToo()
    {
        // Two keys typed in one go arrive as a paste (PasteBurst): the list is derived from the draft, not the key.
        var (line, keys, pane, asked) = MentionLine();
        int waits = 0;
        keys.PushPaste("@te");
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.True(pane.OverlayOpen);
                keys.Push(Keys.Escape, Keys.Enter);
            }
        };

        Assert.Equal("@te", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
        Assert.Equal(["te"], asked);
    }

    [Fact]
    public async Task Mentions_AnAtInsideAWord_OrAReadWithoutMentions_NeverAsks()
    {
        var (line, keys, pane, asked) = MentionLine();
        keys.Push(Chars("mail a@b")).Push(Keys.Enter);
        Assert.Equal("mail a@b", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        keys.Push(Chars("@t")).Push(Keys.Enter);
        Assert.Equal("@t", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);

        Assert.Empty(asked);
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task Mentions_ATruncatedList_ShowsTheNote_AndNoMatchShowsNothing()
    {
        var (line, keys, pane, _) = MentionLine(truncated: true);
        int waits = 0;
        keys.Push(Chars("@t"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.Contains(MentionCompleter.TruncatedRow, _console.Output);
                    keys.Push(Chars("zz"));
                    break;
                case 1:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        Assert.Equal("@tzz", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    [Fact]
    public async Task Mentions_ACancelledRead_ClosesTheList()
    {
        var (line, keys, pane, _) = MentionLine();
        using var cts = new CancellationTokenSource();
        keys.Push(Chars("@t"));
        keys.OnWait = () =>
        {
            Assert.True(pane.OverlayOpen);
            cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply, cancellationToken: cts.Token));

        Assert.False(pane.OverlayOpen);
    }

    // ── The command and skill lists (2026-09-16) ────────────────────────────

    private static readonly IReadOnlyList<CompletionItem> Commands =
    [
        new("/help", "show this list"),
        new("/server", "pick an LLM server"),
        new("/settings", "edit and save settings"),
        new("/skill", "load a skill: /skill <name> [message]"),
        new("/skills", "list the skills"),
    ];

    private static readonly IReadOnlyList<CompletionItem> Skills =
    [
        new("haiku", "Writes haiku."),
        new("weather-info", "Fetches the weather."),
    ];

    /// <summary>The $-mention list's fixture (2026-09-19): two tool names with their descriptions, the turn's order.</summary>
    private static readonly IReadOnlyList<CompletionItem> Tools =
    [
        new("read_file", "Reads a text file."),
        new("web_search", "Searches the web."),
    ];

    /// <summary>
    /// A stand-in for the app's argument table: the skills after /skill, on | off after /tts, the profile
    /// names and verbs (with a delete level) after /profile, the timers after /timer; narrowed as the app does.
    /// After /speak the path shape (2026-09-17): the mention <see cref="Tree"/>, as <c>ChatScreen.ArgumentPaths</c>
    /// answers it — nothing after a trailing space, <c>Truncated</c> for the <c>trunc</c> query.
    /// </summary>
    private static ArgumentList Arguments(string command, string argText)
    {
        if (command.Equals("/speak", StringComparison.OrdinalIgnoreCase))
        {
            if (argText.Length > 0 && char.IsWhiteSpace(argText[^1]))
            {
                return new ArgumentList([], []);
            }

            return argText == "trunc"
                ? new ArgumentList([], ["trunc.txt"], Truncated: true)
                : new ArgumentList([], Tree.TryGetValue(argText, out var paths) ? paths : []);
        }

        IReadOnlyList<CompletionItem> items = command.ToLowerInvariant() switch
        {
            "/skill" => Skills,
            "/tts" => [new("on", "speech output on"), new("off", "speech output off")],
            "/profile" when argText.StartsWith("delete ", StringComparison.Ordinal) => [new("delete work", "switch to it"), new("delete chef", "switch to it")],
            "/profile" => [new("work", "switch to it"), new("chef", "switch to it"), new("add", "add a profile"), new("delete", "delete a profile")],
            "/timer" when argText.StartsWith("stop ", StringComparison.Ordinal) => [new("stop all", "stop every timer"), new("stop the big pot", "")],
            "/timer" => [new("stop", "stop a timer")],
            _ => [],
        };
        return new ArgumentList(MentionCompleter.Matches(items, argText));
    }

    /// <summary>A pane line with the five sources: the command list, the argument table above, the mention tree of <see cref="MentionLine"/>, the skills as the #-mention list (2026-09-17; <paramref name="hash"/> false = an empty source, the switch off) and the tools as the $-mention list (2026-09-19; <paramref name="dollar"/> the same).</summary>
    private (InputLine Line, ScriptedInput Keys, ScreenPane Pane, List<string> Asked) WordLine(bool commands = true, bool skills = true, bool hash = true, bool dollar = true, bool percent = true, bool caret = true)
    {
        _console.Profile.Height = 12;
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), new ManualTimeProvider());
        pane.Show();
        var scripted = new ScriptedInput();
        var asked = new List<string>();
        MentionResult Complete(string query)
        {
            asked.Add(query);
            return new MentionResult(FileOutcome.Ok, Tree.TryGetValue(query, out var paths) ? paths : [], false);
        }

        var line = new InputLine(pane, new KeySource(scripted, TimeSpan.FromMilliseconds(1)), mentions: Complete, commands: commands ? () => Commands : null, arguments: skills ? Arguments : null, skills: () => hash ? Skills : [], tools: () => dollar ? Tools : [], connections: () => percent ? Connections : [], workflows: () => caret ? Workflows : []);
        return (line, scripted, pane, asked);
    }

    // ── The intercept hook (2026-09-18) ─────────────────────────────────────

    [Fact]
    public async Task Enter_AsksTheInterceptHook_AReplacementSwallowsTheLine_AndBecomesTheDraft()
    {
        var (line, keys, pane, _) = WordLine();
        var seen = new List<string>();
        Task<string?> Intercept(string text, CancellationToken _)
        {
            seen.Add(text);
            return Task.FromResult(text == "clear" ? "/clear " : null);
        }

        keys.Push(Chars("clear"));
        keys.Push(Keys.Enter);   // swallowed: the draft is "/clear " now
        keys.Push(Keys.Enter);   // sent

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply, intercept: Intercept));

        Assert.Equal("/clear", submitted.Text);
        Assert.Equal(["clear", "/clear"], seen);
        Assert.Equal(["/clear"], line.History);   // the swallowed line was never remembered (the sent one trimmed, as ever)
        Assert.DoesNotContain("› clear\n", _console.Output);   // nor committed
        Assert.Contains("› /clear", _console.Output);
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task Enter_TheInterceptHookDeclines_TheLineIsSentAsTyped()
    {
        var (line, keys, _, _) = WordLine();
        keys.Push(Chars("clear"));
        keys.Push(Keys.Enter);

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply, intercept: (_, _) => Task.FromResult<string?>(null)));

        Assert.Equal("clear", submitted.Text);
        Assert.Equal(["clear"], line.History);
        Assert.Contains("› clear", _console.Output);
    }

    // ── The beforeCommit hook (2026-09-18: the welcome splash's dismissal) ──

    [Fact]
    public async Task Enter_RunsTheBeforeCommitHook_OncePerSentLine_AheadOfTheRow_NotOnAnEmptyEnter_NorAnInterceptedOne()
    {
        var (line, keys, _, _) = WordLine();
        var rows = new List<int>();
        void BeforeCommit() => rows.Add(_console.Output.Split('\n').Count(r => r.Contains("› clear") || r.Contains("› hi")));
        Task<string?> Intercept(string text, CancellationToken _) => Task.FromResult(text == "clear" ? "/clear " : null);

        keys.Push(Keys.Enter);           // empty: nothing sent, no hook
        keys.Push(Chars("clear"));
        keys.Push(Keys.Enter);           // intercepted: swallowed, no hook
        keys.Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);
        keys.Push(Chars("hi"));
        keys.Push(Keys.Enter);           // sent: the hook once, before the row is in the flow

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply, intercept: Intercept, beforeCommit: BeforeCommit));

        Assert.Equal("hi", submitted.Text);
        Assert.Equal([0], rows);   // one call, and no committed row on the screen when it ran
        Assert.Contains("› hi", _console.Output);
    }

    // ── The #-mention list (2026-09-17) ─────────────────────────────────────

    [Fact]
    public async Task HashMentions_AHashWordOpensTheSkills_TypingNarrows_AndEnterWritesTheName_NotSends()
    {
        var (line, keys, pane, asked) = WordLine();
        int waits = 0;
        keys.Push(Chars("see #"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    // A bare # lists every skill with its description dim, the first highlighted.
                    Assert.True(pane.OverlayOpen);
                    Assert.True(pane.OverlayHasInput);
                    int width = "weather-info".Length + MentionCompleter.NoteGap;   // the names padded to the longest shown
                    Assert.Contains(MentionCompleter.WordRowText("haiku", "Writes haiku.", width, active: true), _console.Output);
                    Assert.Contains(MentionCompleter.WordRowText("weather-info", "Fetches the weather.", width, active: false), _console.Output);
                    Assert.Contains(MentionCompleter.Hint, _console.Output);
                    keys.Push(Chars("w"));
                    break;
                case 1:
                    // Narrowed to the one; Enter writes #name and a space, nothing is sent.
                    Assert.True(pane.OverlayOpen);
                    Assert.DoesNotContain(Highlighted("haiku"), _console.Output[_console.Output.LastIndexOf(MentionCompleter.Hint, StringComparison.Ordinal)..]);
                    keys.Push(Keys.Enter);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("now")).Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("see #weather-info now", submitted.Text);
        Assert.Empty(asked);   // the file tree was never asked: # is not @
    }

    [Fact]
    public async Task HashMentions_AFullName_AHashInsideAWord_OrAnEmptySource_OpensNothing()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("#haiku"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);   // the name in full closes the list, so Enter sends
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("#haiku", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        waits = 0;
        keys.Push(Chars("issue#h"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);   // the # inside a word
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("issue#h", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        // The switch off: the source answers nothing, so the same keys open nothing.
        var (off, offKeys, offPane, _) = WordLine(hash: false);
        waits = 0;
        offKeys.Push(Chars("#h"));
        offKeys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(offPane.OverlayOpen);
                offKeys.Push(Keys.Enter);
            }
        };
        Assert.Equal("#h", Assert.IsType<InputResult.Submitted>(await off.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    // ── %-mentions (later on 2026-09-23): the $ shape over the SQL connections ─

    private static readonly IReadOnlyList<CompletionItem> Connections =
    [
        new("adventureworks", "127.0.0.1,1433 / AdventureWorks2022 — the sample sales database"),
        new("prod", "sqlhost01,1453 / Reports"),
    ];

    [Fact]
    public async Task PercentMentions_APercentWordOpensTheConnections_TypingNarrows_AndEnterWritesTheName_NotSends()
    {
        var (line, keys, pane, asked) = WordLine();
        int waits = 0;
        keys.Push(Chars("top customers on %"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    // A bare % lists every connection with where it points, the first highlighted.
                    Assert.True(pane.OverlayOpen);
                    // (The fixture's 40 columns cut the notes, so the names and the start of each note are what is checked.)
                    Assert.Contains("adventureworks  127.0.0.1", _console.Output);
                    Assert.Contains("prod            sqlhost01", _console.Output);
                    Assert.Contains(MentionCompleter.Hint, _console.Output);
                    keys.Push(Chars("pr"));
                    break;
                case 1:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("please")).Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("top customers on %prod please", submitted.Text);
        Assert.Empty(asked);   // % is not @
    }

    [Fact]
    public async Task PercentMentions_APercentInsideAWord_OrTheSwitchOff_OpensNothing()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("50%a"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);   // a percentage is not a mention
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("50%a", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        var (off, offKeys, offPane, _) = WordLine(percent: false);
        waits = 0;
        offKeys.Push(Chars("%p"));
        offKeys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(offPane.OverlayOpen);
                offKeys.Push(Keys.Enter);
            }
        };
        Assert.Equal("%p", Assert.IsType<InputResult.Submitted>(await off.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    // ── ^-mentions (2026-09-24): the % shape over the offered ComfyUI workflows ─

    private static readonly IReadOnlyList<CompletionItem> Workflows =
    [
        new("pony-txt2img", "pony · text → image · 1024×1024"),
        new("juggernaut-xl", "sdxl · text → image · 1024×1024"),
    ];

    [Fact]
    public async Task CaretMentions_ACaretWordOpensTheWorkflows_TypingNarrows_AndEnterWritesTheName_NotSends()
    {
        var (line, keys, pane, asked) = WordLine();
        int waits = 0;
        keys.Push(Chars("a fox with ^"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    // A bare ^ lists every workflow with what it is, the first highlighted.
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains("pony-txt2img   pony", _console.Output);
                    Assert.Contains("juggernaut-xl  sdxl", _console.Output);
                    Assert.Contains(MentionCompleter.Hint, _console.Output);
                    keys.Push(Chars("ju"));
                    break;
                case 1:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("please")).Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("a fox with ^juggernaut-xl please", submitted.Text);
        Assert.Empty(asked);   // ^ is not @
    }

    [Fact]
    public async Task CaretMentions_ACaretInsideAWord_OrTheSwitchOff_OpensNothing()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("2^p"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);   // a power is not a mention
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("2^p", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        var (off, offKeys, offPane, _) = WordLine(caret: false);
        waits = 0;
        offKeys.Push(Chars("^p"));
        offKeys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(offPane.OverlayOpen);
                offKeys.Push(Keys.Enter);
            }
        };
        Assert.Equal("^p", Assert.IsType<InputResult.Submitted>(await off.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    // ── $-mentions (2026-09-19): the # shape over the offered tools ─────────

    [Fact]
    public async Task DollarMentions_ADollarWordOpensTheTools_TypingNarrows_AndEnterWritesTheName_NotSends()
    {
        var (line, keys, pane, asked) = WordLine();
        int waits = 0;
        keys.Push(Chars("use $"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    // A bare $ lists every offered tool with its description dim, the first highlighted.
                    Assert.True(pane.OverlayOpen);
                    Assert.True(pane.OverlayHasInput);
                    int width = "web_search".Length + MentionCompleter.NoteGap;
                    Assert.Contains(MentionCompleter.WordRowText("read_file", "Reads a text file.", width, active: true), _console.Output);
                    Assert.Contains(MentionCompleter.WordRowText("web_search", "Searches the web.", width, active: false), _console.Output);
                    Assert.Contains(MentionCompleter.Hint, _console.Output);
                    keys.Push(Chars("w"));
                    break;
                case 1:
                    // Narrowed to the one; Enter writes $name and a space, nothing is sent.
                    Assert.True(pane.OverlayOpen);
                    Assert.DoesNotContain(Highlighted("read_file"), _console.Output[_console.Output.LastIndexOf(MentionCompleter.Hint, StringComparison.Ordinal)..]);
                    keys.Push(Keys.Enter);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("now")).Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("use $web_search now", submitted.Text);
        Assert.Empty(asked);   // the file tree was never asked: $ is not @
    }

    [Fact]
    public async Task DollarMentions_AFullName_ADollarInsideAWord_OrAnEmptySource_OpensNothing()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("$read_file"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);   // the name in full closes the list, so Enter sends
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("$read_file", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        waits = 0;
        keys.Push(Chars("cost$5"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);   // the $ inside a word
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("cost$5", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        // The switch off: the source answers nothing, so the same keys open nothing.
        var (off, offKeys, offPane, _) = WordLine(dollar: false);
        waits = 0;
        offKeys.Push(Chars("$r"));
        offKeys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(offPane.OverlayOpen);
                offKeys.Push(Keys.Enter);
            }
        };
        Assert.Equal("$r", Assert.IsType<InputResult.Submitted>(await off.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    [Fact]
    public async Task Commands_ASlashWordOpensTheList_TypingNarrows_AndEnterAppliesThenSends()
    {
        var (line, keys, pane, asked) = WordLine();
        int waits = 0;
        keys.Push(Chars("/s"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    // The four /s commands with their summaries dim, the first highlighted, the hint row.
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("/server"), _console.Output);
                    Assert.Contains("/settings  edit and save settings", _console.Output);
                    Assert.Contains(MenuPane.NoPointer + "/skills", _console.Output);
                    Assert.Contains(MentionCompleter.Hint, _console.Output);
                    keys.Push(Chars("e"));
                    break;
                case 1:
                    Assert.Contains(Highlighted("/server"), _console.Output);
                    keys.Push(Keys.Down, Keys.Enter);
                    break;
                case 2:
                    // Enter applied /settings and a space: the list is gone and nothing was sent.
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("/settings", submitted.Text);   // the trailing space is trimmed on Enter
        Assert.Empty(asked);                         // never the mention source
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task Commands_AWordTypedInFull_ClosesTheList_SoEnterSends_AndTheNextLetterReopensIt()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("/skil"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Chars("l"));
                    break;
                case 1:
                    // "/skill" is a command even though "/skills" starts with it: no list, Enter would send.
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Backspace);
                    break;
                case 2:
                    // The word is a prefix again: the list is back with both.
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("/skill"), _console.Output);
                    keys.Push(Chars("ls"));
                    break;
                case 3:
                    Assert.False(pane.OverlayOpen);   // "/skills" is a word too
                    keys.Push(Keys.Backspace);
                    break;
                case 4:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        Assert.Equal("/skill", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    [Fact]
    public async Task Commands_EscKeepsTheDraft_TabApplies_AndASlashElsewhereIsNoCommand()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("/h"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Keys.Escape);
                    break;
                case 1:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Home, Keys.End);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Backspace, Keys.Backspace);
                    break;
                case 3:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("see /tmp"));
                    break;
                case 4:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Escape);   // clears the line: no list to close first
                    break;
                case 5:
                    keys.Push(Chars("/he")).Push(Keys.Tab);
                    break;
                case 6:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("me")).Push(Keys.Enter);
                    break;
            }
        };

        Assert.Equal("/help me", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    [Fact]
    public async Task Skills_TheNameAfterSkillOpensTheCatalog_AndAppliedFromTheCommandListItOpensAtOnce()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("/ski"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.Contains(Highlighted("/skill"), _console.Output);
                    keys.Push(Keys.Enter);
                    break;
                case 1:
                    // "/skill " on the line and the skill list open on the same redraw.
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("haiku"), _console.Output);
                    Assert.Contains("weather-info  ", _console.Output);
                    Assert.Contains("Fetches the weather.", _console.Output);
                    keys.Push(Chars("W"));
                    break;
                case 2:
                    Assert.Contains(Highlighted("weather-info"), _console.Output);
                    keys.Push(Keys.Enter);
                    break;
                case 3:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Chars("for Paris")).Push(Keys.Enter);
                    break;
            }
        };

        Assert.Equal("/skill weather-info for Paris", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    [Fact]
    public async Task Skills_AFullName_OrAnotherCommand_OrNoSource_OpensNothing()
    {
        var (line, keys, pane, asked) = WordLine();
        int waits = 0;
        keys.Push(Chars("/skill haiku"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);   // the name in full
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("/skill haiku", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        waits = 0;
        keys.Push(Chars("/skills h"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);   // /skills is not /skill
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("/skills h", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        // A read without the mention switch (a settings field) never lists a command.
        keys.OnWait = null;
        keys.Push(Chars("/se")).Push(Keys.Enter);
        Assert.Equal("/se", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true)).Text);
        Assert.False(pane.OverlayOpen);

        // No sources: the same keys, no list.
        var (bare, bareKeys, barePane, _) = WordLine(commands: false, skills: false);
        bareKeys.Push(Chars("/skill h")).Push(Keys.Enter);
        Assert.Equal("/skill h", Assert.IsType<InputResult.Submitted>(await bare.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
        Assert.False(barePane.OverlayOpen);

        // The @ list still opens on a line that is no command.
        keys.Push(Chars("hi @b")).Push(Keys.Enter).Push(Keys.Enter);
        Assert.Equal("hi @test/bling.txt", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
        Assert.Equal(["", "b"], asked);   // the bare @ lists the top level, then the prefix
    }

    [Fact]
    public async Task Arguments_OnOffAfterASwitch_NarrowsAndApplies_AndTheFullWordCloses()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("/tts "));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("on") + "   speech output on", _console.Output);
                    Assert.Contains(MenuPane.NoPointer + "off  speech output off", _console.Output);
                    keys.Push(Chars("o"));
                    break;
                case 1:
                    Assert.True(pane.OverlayOpen);
                    keys.Push(Chars("f"));
                    break;
                case 2:
                    Assert.Contains(Highlighted("off"), _console.Output);
                    keys.Push(Keys.Backspace, Keys.Enter);
                    break;
                case 3:
                    // "/tts on " on the line, the list gone, nothing sent.
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        Assert.Equal("/tts on", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        // The argument typed in full: no list, Enter sends at once.
        waits = 0;
        keys.Push(Chars("/tts off"));
        keys.OnWait = () =>
        {
            if (waits++ == 0)
            {
                Assert.False(pane.OverlayOpen);
                keys.Push(Keys.Enter);
            }
        };
        Assert.Equal("/tts off", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    [Fact]
    public async Task Arguments_ASecondLevel_OpensAtOnce_AndAMultiWordArgumentIsOneCandidate()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("/profile del"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.Contains(Highlighted("delete"), _console.Output);
                    keys.Push(Keys.Tab);
                    break;
                case 1:
                    // "/profile delete " applied and the names as "delete <name>" open on the same redraw.
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("delete work"), _console.Output);
                    Assert.Contains(MenuPane.NoPointer + "delete chef", _console.Output);
                    keys.Push(Keys.Down, Keys.Enter);
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };
        Assert.Equal("/profile delete chef", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);

        // A timer name with spaces: the query spans the words typed so far and the pick writes the whole name.
        waits = 0;
        keys.Push(Chars("/timer stop the b"));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    Assert.Contains(Highlighted("stop the big pot"), _console.Output);
                    keys.Push(Keys.Enter);
                    break;
                case 1:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };
        Assert.Equal("/timer stop the big pot", Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply)).Text);
    }

    // ── The path argument list (/speak, 2026-09-17) ─────────────────────────

    [Fact]
    public async Task Arguments_APathList_IsTheMentionShape_AFolderRemains_AndAFileApplies()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("/speak "));
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    // The root's level as path rows — no notes — folders first; the input slot kept.
                    Assert.True(pane.OverlayOpen);
                    Assert.True(pane.OverlayHasInput);
                    Assert.Contains(Highlighted("test/"), _console.Output);
                    Assert.Contains(MenuPane.NoPointer + "top.txt", _console.Output);
                    keys.Push(Chars("t"));
                    break;
                case 1:
                    Assert.Contains(Highlighted("test/"), _console.Output);
                    Assert.Contains(MenuPane.NoPointer + "test/thing.txt", _console.Output);
                    keys.Push(Keys.Enter);   // test/ under Remain: applied without a space, the list stays on its contents
                    break;
                case 2:
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("test/bling.txt"), _console.Output);
                    keys.Push(Keys.Down, Keys.Enter);   // a file: applied with a space, the list closes
                    break;
                case 3:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Remain));

        Assert.Equal("/speak test/thing.txt", submitted.Text);
    }

    [Fact]
    public async Task Arguments_APathList_AFolderApplied_ClosesTheList_AndTheTruncatedRowShows()
    {
        var (line, keys, pane, _) = WordLine();
        int waits = 0;
        keys.Push(Chars("/speak t")).Push(Keys.Enter);   // test/ is the first row; under Apply it is written with a space
        keys.OnWait = () =>
        {
            switch (waits++)
            {
                case 0:
                    // "/speak test/ " on the line: the trailing space ends the argument, no list.
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace, Keys.Backspace);   // back to "/speak "
                    keys.Push(Chars("trunc"));
                    break;
                case 1:
                    Assert.True(pane.OverlayOpen);
                    Assert.Contains(Highlighted("trunc.txt"), _console.Output);
                    Assert.Contains(MentionCompleter.TruncatedRow, _console.Output);
                    keys.Push(Keys.Escape);   // the list closes, the draft stays
                    break;
                case 2:
                    Assert.False(pane.OverlayOpen);
                    keys.Push(Keys.Enter);
                    break;
            }
        };

        var submitted = Assert.IsType<InputResult.Submitted>(await line.ReadAsync(multiline: true, mentions: MentionFolderAction.Apply));

        Assert.Equal("/speak trunc", submitted.Text);
    }
}
