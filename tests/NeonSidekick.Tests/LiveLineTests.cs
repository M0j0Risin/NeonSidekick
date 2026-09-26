using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The chat line's editor under a reply (2026-09-25, the user's ask: under <c>/botchat</c> the arrows, the history, the lists
/// and the mouse did nothing until the turn ended): the key watcher feeds <see cref="InputLine.Chat"/>, whose draft,
/// cursor and tokens outlive the watch; Enter hands the line to the watcher's hook, ESC keeps the draft.
/// </summary>
public class LiveLineTests : IDisposable
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(1);

    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly KeySource _keys;
    private readonly InputLine _line;
    private readonly List<string> _copied = [];

    public LiveLineTests()
    {
        _console.Profile.Width = 40;
        _keys = new KeySource(_console.Input, FastPoll);
        _line = new InputLine(_console, _keys, copyToClipboard: text =>
        {
            _copied.Add(text);
            return true;
        });
    }

    public void Dispose() => _console.Dispose();

    private void Push(params ConsoleKeyInfo[] keys)
    {
        foreach (var key in keys)
        {
            _console.Input.PushKey(key);
        }
    }

    private static ConsoleKeyInfo[] Chars(string text) => text.Select(Keys.Char).ToArray();

    /// <summary>A watch over the chat editor until the keys are all read (or <paramref name="until"/> holds), then stopped.</summary>
    private async Task<Interrupt> WatchAsync(CancellationTokenSource turn, Func<KeySource.WatchedLine, Task<bool>>? onLine = null, Func<ConsoleKeyInfo, bool>? cancel = null, Func<bool>? until = null)
    {
        using var stop = new CancellationTokenSource();
        var watch = _keys.WatchAsync(turn, stop.Token, null, null, onLine, cancel: cancel, editor: _line.Chat);
        await WaitUntilAsync(() => watch.IsCompleted || (until?.Invoke() ?? !_console.Input.IsKeyAvailable()));
        await Task.Delay(20);
        stop.Cancel();
        return await watch;
    }

    [Fact]
    public async Task TheArrowsEditTheRow_AndEnterOffersTheEditedLine_TakenItLeavesTheRowAndJoinsTheHistory()
    {
        Push([.. Chars("helo"), Keys.Left, Keys.Char('l'), Keys.End, Keys.Char('!'), Keys.Enter]);
        var offered = new List<KeySource.WatchedLine>();
        using var turn = new CancellationTokenSource();

        await WatchAsync(turn, line =>
        {
            offered.Add(line);
            return Task.FromResult(true);
        }, until: () => offered.Count == 1);

        var only = Assert.Single(offered);
        Assert.Equal("hello!", only.Text);
        Assert.Equal("hello!", only.Line!.Text);
        Assert.Equal("hello!", only.Label);
        Assert.Equal("", _line.Chat.Text);
        Assert.Equal(["hello!"], _line.History);
        Assert.False(turn.IsCancellationRequested);
        Assert.Equal(0, _keys.Buffered);
    }

    [Fact]
    public async Task AKeptLine_StaysOnTheRow()
    {
        Push([.. Chars("wait"), Keys.Enter]);
        int offered = 0;
        using var turn = new CancellationTokenSource();

        await WatchAsync(turn, _ =>
        {
            offered++;
            return Task.FromResult(false);
        }, until: () => offered == 1);

        Assert.Equal("wait", _line.Chat.Text);
        Assert.Empty(_line.History);
    }

    [Fact]
    public async Task TheDraftAndItsCursor_OutliveTheWatch_TheIdleReadGoesOnFromThem()
    {
        Push([.. Chars("abc"), Keys.Left]);
        using var turn = new CancellationTokenSource();
        await WatchAsync(turn);

        Push(Keys.Char('X'), Keys.Enter);
        var result = await _line.ReadAsync(multiline: true, editor: _line.Chat);

        Assert.Equal("abXc", Assert.IsType<InputResult.Submitted>(result).Text);
        Assert.Equal("", _line.Chat.Text);
    }

    [Fact]
    public async Task UpRecallsTheHistory_UnderAReply()
    {
        _line.Remember("earlier");
        Push(Keys.Up, Keys.Enter);
        var offered = new List<string?>();
        using var turn = new CancellationTokenSource();

        await WatchAsync(turn, line =>
        {
            offered.Add(line.Text);
            return Task.FromResult(true);
        }, until: () => offered.Count == 1);

        Assert.Equal(["earlier"], offered);
    }

    [Fact]
    public async Task Esc_CancelsTheTurn_TheDraftStays()
    {
        Push([.. Chars("keep me"), Keys.Escape]);
        using var turn = new CancellationTokenSource();

        Assert.Equal(Interrupt.Cancel, await WatchAsync(turn));

        Assert.True(turn.IsCancellationRequested);
        Assert.Equal("keep me", _line.Chat.Text);
    }

    [Fact]
    public async Task CtrlC_OverASelection_Copies_AndTheTurnRunsOn_TheNextCancels()
    {
        Push([.. Chars("copy me"), Keys.Ctrl(ConsoleKey.A), Keys.CtrlC]);
        using var turn = new CancellationTokenSource();

        Assert.Equal(Interrupt.None, await WatchAsync(turn));

        Assert.Equal(["copy me"], _copied);
        Assert.False(turn.IsCancellationRequested);

        Push(Keys.Right, Keys.CtrlC);
        Assert.Equal(Interrupt.Cancel, await WatchAsync(turn));
        Assert.Equal("copy me", _line.Chat.Text);
    }

    [Fact]
    public async Task CtrlX_OverASelection_Cuts_AndTheTurnRunsOn()
    {
        Push([.. Chars("cut me"), Keys.Home, ShiftRight, ShiftRight, ShiftRight, ShiftRight, Keys.Ctrl(ConsoleKey.X)]);
        using var turn = new CancellationTokenSource();

        Assert.Equal(Interrupt.None, await WatchAsync(turn));

        Assert.Equal(["cut "], _copied);
        Assert.Equal("me", _line.Chat.Text);
        Assert.False(turn.IsCancellationRequested);
    }

    private static ConsoleKeyInfo ShiftRight => Keys.Shift(ConsoleKey.RightArrow);

    [Fact]
    public async Task UnderACtrlCOnlyWatch_ABareEsc_IsTypeAhead_TheDraftUntouched()
    {
        // A connect's short wait: ESC is not its cancel key and stays for the picker after it.
        Push([.. Chars("ab"), Keys.Escape]);
        using var turn = new CancellationTokenSource();

        Assert.Equal(Interrupt.None, await WatchAsync(turn, cancel: Keys.IsInterrupt));

        Assert.Equal("ab", _line.Chat.Text);
        Assert.Equal(1, _keys.Buffered);
    }

    [Fact]
    public async Task APaneRequest_ReadsTheKeys_ThenTheRowIsTheDraftAgain()
    {
        Push([.. Chars("draft")]);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = _keys.WatchAsync(turn, stop.Token, null, null, editor: _line.Chat);
        await WaitUntilAsync(() => _line.Chat.Text == "draft");

        Push(Keys.Char('y'), Keys.Enter);
        string? answer = null;
        await _keys.RequestPaneAsync(async () =>
        {
            var read = await _line.ReadAsync(remember: false);
            answer = (read as InputResult.Submitted)?.Text;
        });
        stop.Cancel();
        await watch;

        Assert.Equal("y", answer);
        Assert.Equal("draft", _line.Chat.Text);
        Assert.Empty(_line.History);
    }

    [Fact]
    public void Send_WritesTheRow_AndHandsBackTheText()
    {
        var sent = _line.Send(new SubmittedLine("later", "later", [], "later"));

        Assert.Equal("later", sent.Text);
        Assert.Empty(sent.Images);
        Assert.Contains(InputLine.PromptGlyph + "later", _console.Output);
    }

    [Fact]
    public void ALine_WithAPasteToken_OrALineBreak_IsNoCommand()
    {
        Assert.Equal("/help", new SubmittedLine(" /help ", "/help", [], "/help").CommandText);
        Assert.Null(new SubmittedLine("a\nb", "a\nb", [], "a b").CommandText);
        Assert.Null(new SubmittedLine("/help \uE000", "/help block", [], "/help [Pasted text #1 +40 lines]").CommandText);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
