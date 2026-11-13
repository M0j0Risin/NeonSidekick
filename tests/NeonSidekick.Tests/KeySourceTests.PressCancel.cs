using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// ESC pressed from another thread (2026-11-12, plan what-would-it-take-tingly-ullman: the Web UI's ⏭️ during a botchat): the running watch
/// takes it as its cancel key — the soft cancel first, then the turn's — and nothing else ever does: no watch, a watch whose cancel keys leave
/// ESC out, or a pane holding the keys refuse it; a double tap is one press.
/// </summary>
public partial class KeySourceTests
{
    [Fact]
    public async Task PressCancel_UnderAWatch_IsTheSoftCancel_ThenTheTurnsCancel()
    {
        var keys = new KeySource(new TestConsoleInput(), FastPoll);
        using var turn = new CancellationTokenSource();
        int asked = 0;
        var watch = keys.WatchAsync(turn, CancellationToken.None, null, null, null, () => ++asked == 1);
        await WaitUntilAsync(() => keys.Watching);

        Assert.Equal(CancelPress.Taken, keys.PressCancel());
        await WaitUntilAsync(() => asked == 1);
        Assert.False(turn.IsCancellationRequested);

        Assert.Equal(CancelPress.Taken, keys.PressCancel());
        Assert.Equal(Interrupt.Cancel, await watch);
        Assert.True(turn.IsCancellationRequested);
        Assert.Equal(2, asked);
        Assert.Equal(0, keys.Buffered);   // never type-ahead
    }

    [Fact]
    public async Task PressCancel_WithNoWatch_IsRefused_AndTheNextWatchNeverSeesIt()
    {
        var keys = new KeySource(new TestConsoleInput(), FastPoll);
        Assert.Equal(CancelPress.NoWatch, keys.PressCancel());

        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        await WaitUntilAsync(() => keys.Watching);
        await Task.Delay(50);
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.False(turn.IsCancellationRequested);
        Assert.Equal(CancelPress.NoWatch, keys.PressCancel());   // the watch has ended
    }

    [Fact]
    public async Task PressCancel_UnderAWatchWhoseCancelLeavesEscOut_IsRefused()
    {
        // A connect's spinner: ESC is type-ahead there, so the page's press is too little to cancel it.
        var keys = new KeySource(new TestConsoleInput(), FastPoll);
        using var connect = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(connect, stop.Token, null, null, cancel: Keys.IsInterrupt);
        await WaitUntilAsync(() => keys.Watching);

        Assert.Equal(CancelPress.NoWatch, keys.PressCancel());
        await Task.Delay(50);
        stop.Cancel();
        Assert.Equal(Interrupt.None, await watch);
        Assert.False(connect.IsCancellationRequested);
    }

    [Fact]
    public async Task PressCancel_WhileAPaneHoldsTheKeys_IsRefused_AndAfterItIsTaken()
    {
        var keys = new KeySource(new TestConsoleInput(), FastPoll);
        using var turn = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, CancellationToken.None);
        await WaitUntilAsync(() => keys.Watching);
        CancelPress inPane = CancelPress.Taken;
        await keys.RequestPaneAsync(() =>
        {
            inPane = keys.PressCancel();
            return Task.CompletedTask;
        });

        Assert.Equal(CancelPress.InPhase, inPane);
        await Task.Delay(50);
        Assert.False(turn.IsCancellationRequested);

        Assert.Equal(CancelPress.Taken, keys.PressCancel());
        Assert.Equal(Interrupt.Cancel, await watch);
    }

    [Fact]
    public async Task PressCancel_WithNoKeyboard_StillCancelsTheTurn()
    {
        var keys = new KeySource(new ScriptedInput { NoKeyboard = true }, FastPoll);
        using var turn = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, CancellationToken.None);
        await WaitUntilAsync(() => keys.Watching);
        await Task.Delay(20);   // into the no-keyboard wait

        Assert.Equal(CancelPress.Taken, keys.PressCancel());
        Assert.Equal(Interrupt.Cancel, await watch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(turn.IsCancellationRequested);
    }

    [Fact]
    public async Task PressCancel_TwiceBeforeTheWatchTakesIt_IsOnePress()
    {
        // A double tap on a phone must not jump from "skip the voice" to "end the chat". A slow poll keeps both presses ahead of the take.
        var keys = new KeySource(new TestConsoleInput(), TimeSpan.FromMilliseconds(300));
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        int asked = 0;
        var watch = keys.WatchAsync(turn, stop.Token, null, null, null, () => { Interlocked.Increment(ref asked); return true; });
        await WaitUntilAsync(() => keys.Watching);

        Assert.Equal((CancelPress.Taken, CancelPress.Taken), (keys.PressCancel(), keys.PressCancel()));
        await WaitUntilAsync(() => Volatile.Read(ref asked) == 1);
        await Task.Delay(700);
        stop.Cancel();

        Assert.Equal(Interrupt.None, await watch);
        Assert.Equal(1, asked);
        Assert.False(turn.IsCancellationRequested);
    }
}
