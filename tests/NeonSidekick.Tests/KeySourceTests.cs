using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class KeySourceTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task ReadKey_DrainsTheBufferBeforeTheConsole()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        input.PushKey(Keys.Char('b'));                 // buffered by the watcher below
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);

        input.PushKey(Keys.Char('c'));                 // arrives after the turn

        Assert.Equal('b', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal('c', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal(0, keys.Buffered);
    }

    [Fact]
    public async Task TakeQueued_TakesWaitingMatches_AndLeavesTheFirstOtherEventNextInOrder()
    {
        // 2026-10-03: a pane folds a held arrow's queued presses into one move.
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        input.PushKey(Keys.Down);
        input.PushKey(Keys.Down);
        input.PushKey(Keys.Char('q'));
        input.PushKey(Keys.Down);
        static bool IsDown(InputEvent e) => e is InputEvent.Key { Info.Key: ConsoleKey.DownArrow };

        Assert.NotNull(keys.TakeQueued(IsDown));
        Assert.NotNull(keys.TakeQueued(IsDown));
        Assert.Null(keys.TakeQueued(IsDown));   // the 'q' is not taken…
        Assert.Null(keys.TakeQueued(IsDown));   // …and stays at the head
        Assert.Equal('q', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.NotNull(keys.TakeQueued(IsDown));
        Assert.Null(keys.TakeQueued(IsDown));   // nothing waiting: null, no wait
    }

    [Fact]
    public void TakeQueued_WithoutAKeyboard_IsNull()
    {
        var keys = new KeySource(new ScriptedInput { NoKeyboard = true });
        Assert.Null(keys.TakeQueued(_ => true));
    }

    [Fact]
    public void ChordLine_IsMarkedAsAChord_ATypedLineIsNot()
    {
        // Later on 2026-10-02: a window chord pressed again closes its window under a reply, the typed command never does.
        var chord = KeySource.ChordLine("/log");
        Assert.True(chord.Chord);
        Assert.Equal("/log", chord.Text);
        Assert.False(new KeySource.WatchedLine("/log", []).Chord);
    }

    [Fact]
    public async Task ReadKey_NoInput_Throws()
    {
        var keys = new KeySource(new TestConsoleInput());
        await Assert.ThrowsAsync<InvalidOperationException>(() => keys.ReadKeyAsync(CancellationToken.None));
    }

    /// <summary>
    /// Both shapes of the underlying console: Spectre's real input throws TaskCanceledException
    /// when the token fires (the wake word's first live run crashed the exe through it); a fake
    /// that returns null must work too. Either way the caller sees null.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadKey_Cancelled_ReturnsNull(bool inputReturnsNull)
    {
        var keys = new KeySource(new ScriptedInput { ReturnNullOnCancel = inputReturnsNull });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Assert.Null(await keys.ReadKeyAsync(cts.Token));
    }

    [Fact]
    public async Task ReadKey_TokenAlreadyCancelled_ReturnsNullWithoutTouchingTheInput()
    {
        var keys = new KeySource(new ScriptedInput { NoKeyboard = true });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await keys.ReadKeyAsync(cts.Token));
    }

    [Fact]
    public async Task Watch_Escape_CancelsTheTurn_AndReturnsCancel()
    {
        var input = new TestConsoleInput();
        input.PushKey(Keys.Escape);
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();

        Assert.Equal(Interrupt.Cancel, await keys.WatchAsync(turn, CancellationToken.None));
        Assert.True(turn.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_CtrlC_CancelsTheTurn_AndReturnsCancel()
    {
        // Ctrl+C is ESC's twin under a reply (2026-09-17): one cancel path.
        var input = new TestConsoleInput();
        input.PushKey(Keys.CtrlC);
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();

        Assert.Equal(Interrupt.Cancel, await keys.WatchAsync(turn, CancellationToken.None));
        Assert.True(turn.IsCancellationRequested);
        Assert.True(KeySource.IsTurnCancel(Keys.CtrlC));
        Assert.True(KeySource.IsTurnCancel(Keys.Escape));
        Assert.False(KeySource.IsTurnCancel(Keys.Ctrl(ConsoleKey.Q)));
    }

    [Fact]
    public async Task Watch_CtrlC_ASoftCancelThatTakesIt_KeepsWatching_TheTurnUncancelled()
    {
        // The speech stopped by the first Ctrl+C; the key is spent, not buffered.
        var input = new TestConsoleInput();
        input.PushKey(Keys.CtrlC);
        input.PushKey(Keys.Char('a'));
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        int asked = 0;
        var watch = keys.WatchAsync(turn, stop.Token, null, null, null, () => { asked++; return true; });
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.False(turn.IsCancellationRequested);
        Assert.Equal(1, asked);
        Assert.Equal('a', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public async Task Watch_CancelPredicate_TakesCtrlCAlone_AndLeavesEscAsTypeAhead()
    {
        // A connect's watcher: ESC typed under the spinner is for the picker after it, as ever;
        // Ctrl+C is the cancel.
        var input = new TestConsoleInput();
        input.PushKey(Keys.Escape);
        input.PushKey(Keys.CtrlC);
        var keys = new KeySource(input, FastPoll);
        using var connect = new CancellationTokenSource();

        Assert.Equal(Interrupt.Cancel, await keys.WatchAsync(connect, CancellationToken.None, null, null, cancel: Keys.IsInterrupt));
        Assert.True(connect.IsCancellationRequested);
        Assert.Equal(1, keys.Buffered);
        Assert.Equal(ConsoleKey.Escape, (await keys.ReadKeyAsync(CancellationToken.None))!.Value.Key);
    }

    [Fact]
    public async Task Watch_Escape_ASoftCancelThatTakesIt_KeepsWatching_TheTurnUncancelled()
    {
        // The screen's stop of a reply being read aloud: the key is spent, the ESC is not buffered
        // either, and the turn runs on to its stop.
        var input = new TestConsoleInput();
        input.PushKey(Keys.Escape);
        input.PushKey(Keys.Char('a'));
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        int asked = 0;
        var watch = keys.WatchAsync(turn, stop.Token, null, null, null, () => { asked++; return true; });
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.False(turn.IsCancellationRequested);
        Assert.Equal(1, asked);
        Assert.Equal('a', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public async Task Watch_SecondEscape_AfterASoftCancel_CancelsTheTurn()
    {
        var input = new TestConsoleInput();
        input.PushKey(Keys.Escape);
        input.PushKey(Keys.Escape);
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        int asked = 0;

        Assert.Equal(Interrupt.Cancel, await keys.WatchAsync(turn, CancellationToken.None, null, null, null, () => ++asked == 1));
        Assert.True(turn.IsCancellationRequested);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task Watch_Escape_ASoftCancelThatDeclines_CancelsTheTurn()
    {
        var input = new TestConsoleInput();
        input.PushKey(Keys.Escape);
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();

        Assert.Equal(Interrupt.Cancel, await keys.WatchAsync(turn, CancellationToken.None, null, null, null, () => false));
        Assert.True(turn.IsCancellationRequested);
    }

    /// <summary>Ctrl+Q used to quit; it is an ordinary key now, kept as type-ahead like any other.</summary>
    /// <summary>The kill switch (2026-10-01): a read spends Ctrl+Alt+X on the hook and returns the next event; without a hook the key passes.</summary>
    [Fact]
    public async Task ReadInputAsync_KillSwitch_IsSpentOnTheHook_OrPassesWithoutOne()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        int kills = 0;
        keys.KillSwitch = () => kills++;
        input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
        input.PushKey(Keys.Char('a'));

        Assert.Equal(new InputEvent.Key(Keys.Char('a')), await keys.ReadInputAsync(CancellationToken.None));
        Assert.Equal(1, kills);

        input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
        input.PushKey(Keys.Char('b'));
        Assert.Equal('b', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
        input.PushKey(Keys.Char('c'));
        Assert.Equal('c', ((IAnsiConsoleInput)keys).ReadKey(intercept: true)!.Value.KeyChar);
        Assert.Equal(3, kills);

        // A hook that throws still spends the key.
        keys.KillSwitch = () => throw new InvalidOperationException("boom");
        input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
        input.PushKey(Keys.Char('d'));
        Assert.Equal(new InputEvent.Key(Keys.Char('d')), await keys.ReadInputAsync(CancellationToken.None));

        keys.KillSwitch = null;
        input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
        Assert.Equal(new InputEvent.Key(Keys.CtrlAlt(ConsoleKey.X)), await keys.ReadInputAsync(CancellationToken.None));
    }

    /// <summary>The kill switch under a reply (2026-10-01): run on the watcher, never a cancel, never type-ahead, never a line.</summary>
    [Fact]
    public async Task Watch_KillSwitch_RunsTheHook_NeverBufferedNorALine()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        int kills = 0;
        keys.KillSwitch = () => kills++;
        input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
        input.PushKey(Keys.Char('h'));
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var lines = new List<KeySource.WatchedLine>();

        var watch = keys.WatchAsync(turn, stop.Token, null, null, line => { lines.Add(line); return Task.FromResult(true); });
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.Equal(1, kills);
        Assert.False(turn.IsCancellationRequested);
        Assert.Empty(lines);
        Assert.Equal('h', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    /// <summary>The learning's cancel (2026-10-04): a read spends Ctrl+L on its hook, the kill switch's on its own; without a hook the key passes.</summary>
    [Fact]
    public async Task ReadInputAsync_LearnCancel_IsSpentOnItsHook_OrPassesWithoutOne()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        int cancels = 0;
        int kills = 0;
        keys.LearnCancel = () => cancels++;
        keys.KillSwitch = () => kills++;
        input.PushKey(Keys.CtrlL);
        input.PushKey(Keys.Char('a'));

        Assert.Equal(new InputEvent.Key(Keys.Char('a')), await keys.ReadInputAsync(CancellationToken.None));
        Assert.Equal((1, 0), (cancels, kills));

        input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
        input.PushKey(Keys.Ctrl(ConsoleKey.L));
        input.PushKey(Keys.Char('b'));
        Assert.Equal('b', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        input.PushKey(Keys.CtrlL);
        input.PushKey(Keys.Char('c'));
        Assert.Equal('c', ((IAnsiConsoleInput)keys).ReadKey(intercept: true)!.Value.KeyChar);
        Assert.Equal((3, 1), (cancels, kills));

        // Ctrl+Alt+L is /cmdlist's chord, never the cancel.
        input.PushKey(Keys.CtrlAlt(ConsoleKey.L));
        Assert.Equal(new InputEvent.Key(Keys.CtrlAlt(ConsoleKey.L)), await keys.ReadInputAsync(CancellationToken.None));

        // A hook that throws still spends the key.
        keys.LearnCancel = () => throw new InvalidOperationException("boom");
        input.PushKey(Keys.CtrlL);
        input.PushKey(Keys.Char('d'));
        Assert.Equal(new InputEvent.Key(Keys.Char('d')), await keys.ReadInputAsync(CancellationToken.None));

        keys.LearnCancel = null;
        input.PushKey(Keys.CtrlL);
        Assert.Equal(new InputEvent.Key(Keys.CtrlL), await keys.ReadInputAsync(CancellationToken.None));
        Assert.Equal(3, cancels);
    }

    /// <summary>The learning's cancel under a reply (2026-10-04): run on the watcher, never a cancel, never type-ahead, never a line.</summary>
    [Fact]
    public async Task Watch_LearnCancel_RunsTheHook_NeverBufferedNorALine()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        int cancels = 0;
        keys.LearnCancel = () => cancels++;
        input.PushKey(Keys.CtrlL);
        input.PushKey(Keys.Char('h'));
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var lines = new List<KeySource.WatchedLine>();

        var watch = keys.WatchAsync(turn, stop.Token, null, null, line => { lines.Add(line); return Task.FromResult(true); });
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.Equal(1, cancels);
        Assert.False(turn.IsCancellationRequested);
        Assert.Empty(lines);
        Assert.Equal('h', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public async Task Watch_CtrlQ_IsJustAnotherKey()
    {
        var input = new TestConsoleInput();
        input.PushKey(Keys.Ctrl(ConsoleKey.Q));
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource(50);

        Assert.Equal(Interrupt.None, await keys.WatchAsync(turn, stop.Token));
        Assert.False(turn.IsCancellationRequested);
        Assert.Equal(1, keys.Buffered);
    }

    [Fact]
    public async Task Watch_OtherKeys_AreBufferedInOrder_UntilStopped()
    {
        var input = new TestConsoleInput();
        input.PushText("hi");
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();

        var watch = keys.WatchAsync(turn, stop.Token);
        await WaitUntilAsync(() => keys.Buffered == 2);
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.False(turn.IsCancellationRequested);
        Assert.Equal('h', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal('i', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public async Task Watch_KeyArrivingLater_IsStillSeen()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();

        var watch = keys.WatchAsync(turn, CancellationToken.None);
        await Task.Delay(20);
        input.PushKey(Keys.Escape);

        Assert.Equal(Interrupt.Cancel, await watch.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Watch_NoKeyboard_ReturnsNoneWhenStopped()
    {
        var keys = new KeySource(new ScriptedInput { NoKeyboard = true }, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        Assert.Equal(Interrupt.None, await keys.WatchAsync(turn, stop.Token));
        Assert.False(turn.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_DisposedTurnSource_DoesNotThrow()
    {
        var input = new TestConsoleInput();
        input.PushKey(Keys.Escape);
        var keys = new KeySource(input, FastPoll);
        var turn = new CancellationTokenSource();
        turn.Dispose();

        Assert.Equal(Interrupt.Cancel, await keys.WatchAsync(turn, CancellationToken.None));
    }

    private static bool IsF4OrEnter(ConsoleKeyInfo k) => k.Key is ConsoleKey.F4 or ConsoleKey.Enter;

    [Fact]
    public async Task Watch_AcceptKey_CancelsTheAcceptSource_KeepsWatching_AndReturnsAccept()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var accept = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        input.PushKey(Keys.F4);
        input.PushText("ab");

        var watch = keys.WatchAsync(turn, stop.Token, IsF4OrEnter, accept);
        await WaitUntilAsync(() => keys.Buffered == 2);

        Assert.True(accept.IsCancellationRequested);
        Assert.False(turn.IsCancellationRequested);
        Assert.False(watch.IsCompleted);   // still watching after the accept

        stop.Cancel();
        Assert.Equal(Interrupt.Accept, await watch);
        Assert.Equal('a', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public async Task Watch_ASpentKey_IsNeverTypeAhead_AndNeverPartOfALine()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var spent = new List<ConsoleKey>();
        input.PushKey(Keys.PageUp);
        input.PushText("ab");
        input.PushKey(Keys.PageDown);
        input.PushKey(Keys.Enter);
        string? line = null;

        var watch = keys.WatchAsync(turn, stop.Token, null, null, l => { line = l.Text; return Task.FromResult(false); }, null, e =>
        {
            if (e is InputEvent.Key { Info.Key: ConsoleKey.PageUp or ConsoleKey.PageDown } k)
            {
                spent.Add(k.Info.Key);
                return true;
            }

            return false;
        });
        await WaitUntilAsync(() => keys.Buffered == 3);

        Assert.Equal(new[] { ConsoleKey.PageUp, ConsoleKey.PageDown }, spent);
        Assert.Equal("ab", line);                       // the line the hook saw has no page key in it
        Assert.False(turn.IsCancellationRequested);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);
        Assert.Equal('a', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal('b', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal(ConsoleKey.Enter, (await keys.ReadKeyAsync(CancellationToken.None))!.Value.Key);
    }

    [Fact]
    public async Task Watch_AWheelNotch_IsOfferedToTheSpendHook_AndNeverBuffered()
    {
        var input = new ScriptedInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var notches = new List<int>();
        input.PushWheel(2).Push(Keys.Char('a')).PushWheel(-1, 3, 5).PushClick(1, 1);

        var watch = keys.WatchAsync(turn, stop.Token, null, null, spend: e => { if (e is InputEvent.Wheel w) { notches.Add(w.Notches); return true; } return false; });
        await WaitUntilAsync(() => keys.Buffered == 1 && notches.Count == 2);

        Assert.Equal(new[] { 2, -1 }, notches);   // the click was dropped without asking
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);
        Assert.Equal('a', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public async Task Watch_TheSpendHook_IsAskedAfterCancelAndAccept()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var accept = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var asked = new List<ConsoleKey>();
        input.PushKey(Keys.F4);
        input.PushKey(Keys.Escape);

        var watch = keys.WatchAsync(turn, stop.Token, IsF4OrEnter, accept, spend: e => { asked.Add(((InputEvent.Key)e).Info.Key); return true; });
        Assert.Equal(Interrupt.Cancel, await watch);

        Assert.True(accept.IsCancellationRequested);
        Assert.Empty(asked);   // the accept key and ESC were theirs first
    }

    [Fact]
    public async Task Watch_EscapeAfterAccept_IsStillCancel()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var accept = new CancellationTokenSource();
        input.PushKey(Keys.Enter);
        input.PushKey(Keys.Escape);

        Assert.Equal(Interrupt.Cancel, await keys.WatchAsync(turn, CancellationToken.None, IsF4OrEnter, accept));
        Assert.True(accept.IsCancellationRequested);
        Assert.True(turn.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_SecondAcceptKey_IsDropped_NotBuffered()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var accept = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        input.PushKey(Keys.F4);
        input.PushKey(Keys.Enter);
        input.PushKey(Keys.Char('z'));

        var watch = keys.WatchAsync(turn, stop.Token, IsF4OrEnter, accept);
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();

        Assert.Equal(Interrupt.Accept, await watch);
        Assert.Equal('z', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal(0, keys.Buffered);
    }

    [Fact]
    public async Task Watch_NoAcceptKey_ReturnsNone()
    {
        var input = new TestConsoleInput();
        input.PushText("x");
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var accept = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();

        var watch = keys.WatchAsync(turn, stop.Token, IsF4OrEnter, accept);
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.False(accept.IsCancellationRequested);
    }

    [Fact]
    public void Constructor_RejectsNonPositivePoll()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeySource(new TestConsoleInput(), TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new KeySource(null!));
    }

    [Fact]
    public void PreviewText_IsPinned()
    {
        Assert.Equal("", Preview());
        Assert.Equal("ab", Preview(Keys.Char('a'), Keys.Char('b')));
        Assert.Equal("a", Preview(Keys.Char('a'), Keys.Char('b'), Keys.Backspace));
        Assert.Equal("", Preview(Keys.Backspace));
        Assert.Equal("x", Preview(Keys.Char('日'), Keys.Backspace, Keys.Char('x')));
        // A surrogate pair is one element: one Backspace takes both halves.
        Assert.Equal("a", Preview(Keys.Char('a'), Keys.Char('\ud83d'), Keys.Char('\ude00'), Keys.Backspace));
        // Only what follows the last Enter: the line before it is sent first.
        Assert.Equal("second", Preview(Keys.Char('f'), Keys.Enter, Keys.Char('s'), Keys.Char('e'), Keys.Char('c'), Keys.Char('o'), Keys.Char('n'), Keys.Char('d')));
        // A function key types nothing.
        Assert.Equal("a", Preview(Keys.Char('a'), Keys.F4));
    }

    [Fact]
    public void PreviewText_ShowsAPaste_AsTheLineWill()
    {
        // A short block is its text, line breaks kept; nothing in it sends.
        Assert.Equal("a\nb", KeySource.PreviewText(new InputEvent[] { new InputEvent.Paste("a\r\nb\r\n") }, out var labels));
        Assert.Empty(labels);

        // A long one is the label without its number, and Backspace takes it back whole.
        string block = string.Join('\n', Enumerable.Range(1, 5).Select(i => $"line {i}"));
        Assert.Equal("[Pasted text +5 lines]", KeySource.PastePreview(block));
        var events = new InputEvent[] { new InputEvent.Key(Keys.Char('x')), new InputEvent.Paste(block), new InputEvent.Key(Keys.Char('y')) };
        Assert.Equal("x" + PasteBlocks.Unbreakable("[Pasted text +5 lines]") + "y", KeySource.PreviewText(events, out labels));
        Assert.Equal(new[] { (1, "[Pasted text +5 lines]".Length) }, labels);
        Assert.Equal("x", KeySource.PreviewText(events.Take(2).Append(new InputEvent.Key(Keys.Backspace)), out labels));
        Assert.Empty(labels);

        // An empty paste (only control characters) is nothing.
        Assert.Equal("", KeySource.PreviewText(new InputEvent[] { new InputEvent.Paste("\r\n") }, out _));
        Assert.Equal("[Pasted text +1 line]", KeySource.PastePreview(new string('x', 500)));
    }

    [Fact]
    public async Task Watch_BuffersAPaste_AndTheNextReadServesIt()
    {
        var input = new ScriptedInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        input.PushPaste("one\r\ntwo\r\n");
        input.Push(Keys.Char('!'));
        await WaitUntilAsync(() => keys.Buffered == 2);
        Assert.False(turn.IsCancellationRequested);   // the paste's line breaks are not Enter, and nothing cancels
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);

        Assert.Equal(new InputEvent.Paste("one\r\ntwo\r\n"), await keys.ReadInputAsync(CancellationToken.None));
        Assert.Equal('!', ((InputEvent.Key)(await keys.ReadInputAsync(CancellationToken.None))!).Info.KeyChar);
    }

    [Fact]
    public async Task ABufferedPaste_IsNothingToASpectrePrompt()
    {
        var input = new ScriptedInput();
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        input.PushPaste("one\r\ntwo");
        input.Push(Keys.Char('k'));
        await WaitUntilAsync(() => keys.Buffered == 2);
        stop.Cancel();
        await watch;

        IAnsiConsoleInput prompt = keys;
        Assert.True(prompt.IsKeyAvailable());
        Assert.Equal('k', prompt.ReadKey(true)!.Value.KeyChar);
        Assert.Equal(0, keys.Buffered);
    }

    private static string Preview(params ConsoleKeyInfo[] keys) => KeySource.PreviewText(keys.Select(k => new InputEvent.Key(k)), out _);

    [Fact]
    public async Task Watch_MirrorsTypedAheadText_OntoThePane()
    {
        using var console = new TestConsole().Interactive();
        console.Profile.Width = 40;
        console.Profile.Height = 10;
        using var pane = new ScreenPane(console, new ScreenGeometry(() => null), new ManualTimeProvider());
        pane.Show();
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll) { Mirror = pane };
        input.PushKey(Keys.Char('q'));
        input.PushKey(Keys.Char('d'));
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();

        var watch = keys.WatchAsync(turn, stop.Token);
        await WaitUntilAsync(() => keys.Buffered == 2);
        stop.Cancel();
        await watch;

        Assert.EndsWith("qd", console.Output);
        Assert.Equal(2, keys.Buffered);   // still there for the next read
    }

    // ── Clicks ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadInput_ReturnsKeysAndClicksInOrder_TheBufferFirst()
    {
        var input = new ScriptedInput().Push(Keys.Char('a')).PushClick(3, 4).Push(Keys.Char('b'));
        var keys = new KeySource(input, FastPoll);

        Assert.Equal('a', Assert.IsType<InputEvent.Key>(await keys.ReadInputAsync(CancellationToken.None)).Info.KeyChar);
        Assert.Equal(new InputEvent.Click(3, 4, MouseButton.Left), await keys.ReadInputAsync(CancellationToken.None));
        Assert.Equal('b', Assert.IsType<InputEvent.Key>(await keys.ReadInputAsync(CancellationToken.None)).Info.KeyChar);
    }

    [Fact]
    public async Task ReadKey_DropsClicks()
    {
        var input = new ScriptedInput().PushClick(3, 4).PushClick(5, 6, MouseButton.Right).PushDrag(6, 6).PushWheel(-1).Push(Keys.Char('k'));
        var keys = new KeySource(input, FastPoll);

        Assert.Equal('k', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public void TheInterfaceMembers_DropClicksAndDrags()
    {
        var input = new ScriptedInput().PushClick(3, 4).PushDrag(4, 4).PushWheel(1).Push(Keys.Char('k'));
        IAnsiConsoleInput keys = new KeySource(input, FastPoll);

        Assert.True(keys.IsKeyAvailable());
        Assert.Equal('k', keys.ReadKey(true)!.Value.KeyChar);
        input.PushClick(1, 1).PushDrag(2, 1).PushWheel(-2);
        Assert.False(keys.IsKeyAvailable());
        Assert.Null(keys.ReadKey(true));
    }

    [Fact]
    public async Task TheWatcher_DropsClicksAndDrags_AndBuffersKeys()
    {
        var input = new ScriptedInput().PushClick(3, 4).PushDrag(4, 4).PushWheel(1).Push(Keys.Char('q'));
        var keys = new KeySource(input, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);
        Assert.False(turn.IsCancellationRequested);
        Assert.Equal('q', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public async Task ACompletedSource_IsNoKeyboard()
    {
        var input = new ScriptedInput { Completed = true };
        var keys = new KeySource(input, FastPoll);

        await Assert.ThrowsAsync<InvalidOperationException>(() => keys.ReadKeyAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => keys.ReadInputAsync(CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => keys.IsKeyAvailable());
    }

    [Fact]
    public async Task AScriptThatRanDry_FailsTheRead_WithItsMessage()
    {
        // The read of a screen whose script expected something that never came fails, never waits
        // for ever (2026-09-21): a TimeoutException, which nothing on the idle read's path takes
        // for the end of input, so the test fails with this message rather than exiting quietly.
        var input = new ScriptedInput { DryTimeout = TimeSpan.FromMilliseconds(50) };
        var keys = new KeySource(input, FastPoll);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => keys.ReadInputAsync(CancellationToken.None));

        Assert.Equal(ScriptedInput.DryMessage(TimeSpan.FromMilliseconds(50)), ex.Message);
        Assert.Equal("The script ran dry: the read waited 0 s for a key nothing pushed. What the script expected next (a turn, a wake hit, a pane) did not happen.", ex.Message);
    }

    // ── The mid-turn line hook ──────────────────────────────────────────────

    [Fact]
    public void LineText_ReadsTheKeysAsOneLine_BackspaceTakesOneBack_APasteIsNull()
    {
        var line = new List<InputEvent> { new InputEvent.Key(Keys.Char('/')), new InputEvent.Key(Keys.Char('t')), new InputEvent.Key(Keys.Char('x')), new InputEvent.Key(Keys.Backspace), new InputEvent.Key(Keys.Char('t')), new InputEvent.Key(Keys.Char('s')), new InputEvent.Key(Keys.Enter) };
        Assert.Equal("/tts", KeySource.LineText(line));
        Assert.Equal("", KeySource.LineText([new InputEvent.Key(Keys.Backspace), new InputEvent.Key(Keys.Enter)]));
        Assert.Null(KeySource.LineText([new InputEvent.Key(Keys.Char('a')), new InputEvent.Paste("b"), new InputEvent.Key(Keys.Enter)]));
        // A surrogate pair goes as one.
        Assert.Equal("a", KeySource.LineText([new InputEvent.Key(Keys.Char('a')), new InputEvent.Key(Keys.Char('\ud83d')), new InputEvent.Key(Keys.Char('\ude00')), new InputEvent.Key(Keys.Backspace)]));
    }

    /// <summary>Ctrl+Enter (2026-09-22) is a line break inside a typed-ahead line, never its end: the line reads as two rows, labels as one, and is no command.</summary>
    [Fact]
    public void ATypedLineBreak_IsPartOfTheLine()
    {
        InputEvent[] line = [new InputEvent.Key(Keys.Char('a')), new InputEvent.Key(Keys.CtrlEnter), new InputEvent.Key(Keys.Char('b')), new InputEvent.Key(Keys.Enter)];
        Assert.Equal("a\nb", KeySource.PreviewText(line.Take(3), out _));
        Assert.Equal("a", KeySource.PreviewText(line.Take(2).Append(new InputEvent.Key(Keys.Backspace)), out _));   // Backspace takes the break back
        Assert.Equal("a b", KeySource.LineLabel(line));
        Assert.Null(KeySource.LineText(line));
    }

    [Fact]
    public async Task Watch_ACtrlEnter_DoesNotEndTheLine()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        var offered = new List<IReadOnlyList<InputEvent>>();
        input.PushText("a");
        input.PushKey(Keys.CtrlEnter);
        PushLine(input, "b");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, l => { offered.Add(l.Events); return Task.FromResult(false); });
        await WaitUntilAsync(() => offered.Count == 1);
        stop.Cancel();
        await watch;

        var only = Assert.Single(offered);
        Assert.Equal(4, only.Count);
        Assert.Equal("a b", KeySource.LineLabel(only));
        Assert.Equal(4, keys.Buffered);
    }

    private static void PushLine(TestConsoleInput input, string text)
    {
        input.PushText(text);
        input.PushKey(Keys.Enter);
    }

    private static void PushLine(ScriptedInput input, string text)
    {
        foreach (char c in text)
        {
            input.Push(Keys.Char(c));
        }

        input.Push(Keys.Enter);
    }

    [Fact]
    public async Task Watch_ACompletedLine_IsOfferedToTheHook_AConsumedOneLeavesTheBuffer()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        var offered = new List<string>();
        input.PushKey(Keys.Char('h'));                 // part of the line: goes with it
        PushLine(input, "/tts");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, l => { offered.Add(l.Text!); return Task.FromResult(true); });
        await WaitUntilAsync(() => offered.Count == 1);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);

        Assert.Equal(new[] { "h/tts" }, offered);
        Assert.Equal(0, keys.Buffered);
        Assert.False(turn.IsCancellationRequested);
    }

    [Fact]
    public async Task Watch_ADeclinedLine_StaysAsTypeAhead_AndEarlierLinesAreUntouched()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        var offered = new List<string>();
        PushLine(input, "hello");
        PushLine(input, "/tts");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, l => { offered.Add(l.Text!); return Task.FromResult(l.Text == "/tts"); });
        await WaitUntilAsync(() => offered.Count == 2);
        stop.Cancel();
        await watch;

        Assert.Equal(new[] { "hello", "/tts" }, offered);
        // "hello" + Enter stay, in order; "/tts" + Enter are gone.
        Assert.Equal(6, keys.Buffered);
        string text = "";
        for (int i = 0; i < 5; i++)
        {
            text += (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar;
        }

        Assert.Equal("hello", text);
        Assert.Equal(ConsoleKey.Enter, (await keys.ReadKeyAsync(CancellationToken.None))!.Value.Key);
    }

    /// <summary>The click hook (2026-09-18): a click the hook makes a line of runs the line hook with that text and nothing is buffered; a click it declines, or with no hook, is dropped as before; a drag is never asked.</summary>
    [Fact]
    public async Task Watch_AClickTheHookAnswers_RunsTheLineHook_WithThatText_AndTheRestAreDropped()
    {
        var input = new ScriptedInput();
        var keys = new KeySource(input, FastPoll);
        var clicks = new List<InputEvent.Click>();
        var offered = new List<string>();
        input.PushClick(3, 4).PushClick(9, 9, MouseButton.Right).PushDrag(5, 5).PushClick(3, 4).Push(Keys.Char('k'));
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null,
            l => { offered.Add(l.Text!); return Task.FromResult(true); },
            onClick: click => { clicks.Add(click); return clicks.Count == 3 ? "/queue" : null; });
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();
        await watch;

        Assert.Equal(new[] { new InputEvent.Click(3, 4, MouseButton.Left), new InputEvent.Click(9, 9, MouseButton.Right), new InputEvent.Click(3, 4, MouseButton.Left) }, clicks);
        Assert.Equal(new[] { "/queue" }, offered);
        Assert.Equal('k', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);   // the one type-ahead key
    }

    [Fact]
    public async Task Watch_AClick_WithNoLineHook_IsAskedOfTheClickHook_AndItsLineDropped()
    {
        // Later on 2026-09-18: the hook is asked whatever the line hook (a pair on the scroll's hint is spent inside it); a line it answers with none to run it goes nowhere.
        var input = new ScriptedInput();
        var keys = new KeySource(input, FastPoll);
        int asked = 0;
        input.PushClick(3, 4).Push(Keys.Char('k'));
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, onLine: null, onClick: _ => { asked++; return "/queue"; });
        await WaitUntilAsync(() => keys.Buffered == 1);
        stop.Cancel();
        await watch;

        Assert.Equal(1, asked);
        Assert.Equal(1, keys.Buffered);
    }

    /// <summary>A pasted line is offered like any other since 2026-09-18 (it was never offered before), with no Text — never a command — and its label; declined, its events stay type-ahead; consumed, they are gone.</summary>
    [Fact]
    public async Task Watch_APastedLine_IsOffered_WithNoText_AndItsLabel()
    {
        var input = new ScriptedInput();
        var keys = new KeySource(input, FastPoll);
        var offered = new List<KeySource.WatchedLine>();
        input.Push(Keys.Char('s'));
        input.PushPaste("one\ntwo\nthree\nfour\nfive");
        input.Push(Keys.Enter);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, l => { offered.Add(l); return Task.FromResult(offered.Count == 2); });
        await WaitUntilAsync(() => offered.Count == 1);
        await Task.Delay(20);
        Assert.Equal(3, keys.Buffered);   // declined: s, the paste, Enter stay type-ahead

        input.PushPaste("a\nb");
        input.Push(Keys.Enter);
        await WaitUntilAsync(() => offered.Count == 2);
        stop.Cancel();
        await watch;

        Assert.Null(offered[0].Text);
        Assert.Equal("s[Pasted text +5 lines]", offered[0].Label);
        Assert.Equal(3, offered[0].Events.Count);
        Assert.Null(offered[1].Text);
        Assert.Equal("a b", offered[1].Label);          // an inline paste is its text, the break folded
        Assert.Equal(3, keys.Buffered);                 // consumed: the first line still waits, the second is gone
    }

    [Fact]
    public void LineLabel_IsPinned()
    {
        static InputEvent K(char c) => new InputEvent.Key(Keys.Char(c));
        var enter = new InputEvent.Key(Keys.Enter);
        var backspace = new InputEvent.Key(Keys.Backspace);
        Assert.Equal("", KeySource.LineLabel([]));
        Assert.Equal("", KeySource.LineLabel([enter]));
        Assert.Equal("hi", KeySource.LineLabel([K('h'), K('i'), enter]));
        Assert.Equal("h", KeySource.LineLabel([K('h'), K('i'), backspace, enter]));
        Assert.Equal("sum [Pasted text +8 lines]", KeySource.LineLabel([K('s'), K('u'), K('m'), K(' '), new InputEvent.Paste("1\n2\n3\n4\n5\n6\n7\n8"), enter]).Replace('\u00A0', ' '));
        Assert.Equal("sum ", KeySource.LineLabel([K('s'), K('u'), K('m'), K(' '), new InputEvent.Paste("1\n2\n3\n4\n5\n6\n7\n8"), backspace, enter]));   // Backspace takes the whole paste
        Assert.Equal("a b c", KeySource.LineLabel([new InputEvent.Paste("a\r\nb\nc"), enter]));
        Assert.Equal("x", KeySource.LineLabel([K('x'), new InputEvent.Paste(""), enter]));   // an empty paste is nothing
    }

    [Fact]
    public async Task Watch_WithoutAHook_EveryLineIsTypeAhead()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        PushLine(input, "/tts");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        await WaitUntilAsync(() => keys.Buffered == 5);
        stop.Cancel();
        await watch;
        Assert.Equal(5, keys.Buffered);
    }

    [Fact]
    public async Task Watch_TheHookReadsTheKeysItself_WithoutEatingEarlierTypeAhead()
    {
        // A pane opened by the hook reads through ReadInputAsync until ESC: the earlier "hi" + Enter
        // stay set aside for the input line, and the key typed after the pane closed follows them.
        var input = new ScriptedInput();
        var keys = new KeySource(input, FastPoll);
        var seen = new List<ConsoleKey>();
        PushLine(input, "hi");
        PushLine(input, "/help");
        input.Push(Keys.Key(ConsoleKey.DownArrow), Keys.Escape, Keys.Char('z'));
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, async l =>
        {
            Assert.Equal("/help", l.Text);
            while (await keys.ReadInputAsync(CancellationToken.None) is InputEvent.Key { Info: var k })
            {
                seen.Add(k.Key);
                if (Keys.IsCancel(k))
                {
                    return true;
                }
            }

            return true;
        });
        await WaitUntilAsync(() => keys.Buffered == 4);
        stop.Cancel();
        await watch;

        Assert.Equal(new[] { ConsoleKey.DownArrow, ConsoleKey.Escape }, seen);
        Assert.Equal('h', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal('i', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal(ConsoleKey.Enter, (await keys.ReadKeyAsync(CancellationToken.None))!.Value.Key);
        Assert.Equal('z', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.True(keys.PendingLine.IsCompleted);
    }

    [Fact]
    public async Task Watch_StopDuringTheHook_ReturnsAtOnce_AndTheHookRunsOnAsPendingLine()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PushLine(input, "/help");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, async _ =>
        {
            started.SetResult();
            await release.Task;
            return true;
        });
        await started.Task;
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.False(keys.PendingLine.IsCompleted);
        release.SetResult();
        await keys.PendingLine;
        Assert.Equal(0, keys.Buffered);
    }

    [Fact]
    public async Task Watch_AHookThatCancelsTheTurn_AndDeclines_LeavesTheLineForTheIdleRead()
    {
        // /clear and /exit: the hook cancels the turn and hands the line back.
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        PushLine(input, "/exit");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, _ => { turn.Cancel(); return Task.FromResult(false); });
        await WaitUntilAsync(() => turn.IsCancellationRequested);
        stop.Cancel();
        await watch;
        Assert.Equal(6, keys.Buffered);
    }

    [Fact]
    public async Task Watch_AThrowingHook_LeavesTheLineAsTypeAhead()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        PushLine(input, "/tts");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        int offers = 0;
        var watch = keys.WatchAsync(turn, stop.Token, null, null, _ => { offers++; throw new InvalidOperationException("boom"); });
        await WaitUntilAsync(() => offers == 1);
        await Task.Delay(20);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);
        Assert.Equal(5, keys.Buffered);
    }

    // ── The pane request (ask_user, 2026-09-15) ─────────────────────────────

    [Fact]
    public async Task RequestPane_RunsThePhaseOnTheWatcher_WithTheBufferSetAside_AndRestoresItInOrder()
    {
        // The tool's pane reads Down and ESC itself; the earlier "hi" + Enter stay set aside for the
        // input line, and the key typed after the pane closed follows them.
        var input = new ScriptedInput();
        var keys = new KeySource(input, FastPoll);
        var seen = new List<ConsoleKey>();
        PushLine(input, "hi");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, _ => Task.FromResult(false));
        await WaitUntilAsync(() => keys.Buffered == 3);

        bool pendingSeen = false;
        var request = keys.RequestPaneAsync(async () =>
        {
            pendingSeen = !keys.PendingLine.IsCompleted;
            input.Push(Keys.Key(ConsoleKey.DownArrow), Keys.Escape, Keys.Char('z'));
            while (await keys.ReadInputAsync(CancellationToken.None) is InputEvent.Key { Info: var k })
            {
                seen.Add(k.Key);
                if (Keys.IsCancel(k))
                {
                    return;
                }
            }
        });
        await request;
        Assert.True(pendingSeen);
        Assert.True(keys.PendingLine.IsCompleted);
        Assert.Equal(new[] { ConsoleKey.DownArrow, ConsoleKey.Escape }, seen);
        await WaitUntilAsync(() => keys.Buffered == 4);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);

        Assert.Equal('h', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal('i', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
        Assert.Equal(ConsoleKey.Enter, (await keys.ReadKeyAsync(CancellationToken.None))!.Value.Key);
        Assert.Equal('z', (await keys.ReadKeyAsync(CancellationToken.None))!.Value.KeyChar);
    }

    [Fact]
    public async Task RequestPane_StopDuringThePhase_TheWatcherReturns_ThePhaseRunsOnAsPendingLine()
    {
        var keys = new KeySource(new TestConsoleInput(), FastPoll);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        var request = keys.RequestPaneAsync(async () =>
        {
            started.SetResult();
            await release.Task;
        });
        await started.Task;
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.False(keys.PendingLine.IsCompleted);
        Assert.False(request.IsCompleted);
        release.SetResult();
        await keys.PendingLine;
        await request;
    }

    [Fact]
    public async Task RequestPane_WithoutAWatcher_OrLeftWhenItEnds_IsCancelled_NeverRun()
    {
        var keys = new KeySource(new TestConsoleInput(), FastPoll);
        int ran = 0;
        var idle = keys.RequestPaneAsync(() => { ran++; return Task.CompletedTask; });
        Assert.True(idle.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle);

        // Posted while a hook holds the keys and the watcher is stopped under it: the request never ran.
        var input = new TestConsoleInput();
        keys = new KeySource(input, FastPoll);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PushLine(input, "/help");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, async _ =>
        {
            started.SetResult();
            await release.Task;
            return true;
        });
        await started.Task;
        var late = keys.RequestPaneAsync(() => { ran++; return Task.CompletedTask; });
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
        release.SetResult();
        await keys.PendingLine;
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task RequestPane_WhileALineHookHoldsTheKeys_RunsAfterIt()
    {
        var input = new TestConsoleInput();
        var keys = new KeySource(input, FastPoll);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        PushLine(input, "/help");
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, async _ =>
        {
            started.SetResult();
            await release.Task;
            order.Add("hook");
            return true;
        });
        await started.Task;
        var request = keys.RequestPaneAsync(() => { order.Add("request"); return Task.CompletedTask; });
        await Task.Delay(20);
        Assert.False(request.IsCompleted);
        release.SetResult();
        await request;
        Assert.Equal(["hook", "request"], order);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);
        Assert.Equal(0, keys.Buffered);
    }

    [Fact]
    public async Task RequestPane_NoKeyboard_StillRunsThePhase_AndAThrowingPhaseCompletes()
    {
        var keys = new KeySource(new ScriptedInput { NoKeyboard = true }, FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        await Task.Delay(20);   // the watcher is in its no-keyboard wait
        int ran = 0;
        await keys.RequestPaneAsync(() => { ran++; return Task.CompletedTask; });
        await keys.RequestPaneAsync(() => { ran++; throw new InvalidOperationException("boom"); });
        Assert.Equal(2, ran);
        Assert.True(keys.PendingLine.IsCompleted);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(5);
        }
    }
}
