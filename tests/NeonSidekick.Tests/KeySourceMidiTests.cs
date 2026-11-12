using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// A MIDI button's line through the keys (2026-11-12, the MIDI plan, <see cref="KeySource.PostCommand"/>): the idle read wakes and returns it
/// as a command chord's event marked MIDI; the watcher under a reply hands it to the line hook as a chord's line and says it took it;
/// with no hook it is dropped, never left to fire at the next idle line.
/// </summary>
public sealed class KeySourceMidiTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(1);

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task PostCommand_WakesTheIdleRead_AsAMidiCommand()
    {
        var keys = new KeySource(new ScriptedInput(), FastPoll);
        var reading = keys.ReadInputAsync(CancellationToken.None);
        await Task.Delay(20);
        Assert.False(reading.IsCompleted);

        keys.PostCommand("/tts toggle");
        var e = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new InputEvent.Command("/tts toggle", Midi: true), e);
        Assert.Equal(0, keys.PendingCommands);
    }

    [Fact]
    public async Task PostCommand_UnderAReply_IsTheLineHooksChordLine_AndItIsFlashed()
    {
        var keys = new KeySource(new ScriptedInput(), FastPoll);
        var taken = new List<string>();
        keys.MidiTaken = taken.Add;
        var offered = new List<KeySource.WatchedLine>();
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token, null, null, onLine: line => { offered.Add(line); return Task.FromResult(true); });

        keys.PostCommand("/midi list");
        await WaitUntilAsync(() => offered.Count == 1);
        await stop.CancelAsync();
        await watch;

        var line = Assert.Single(offered);
        Assert.True(line.Chord);
        Assert.Equal("/midi list", line.Text);
        Assert.NotNull(line.Line);
        Assert.Equal(["/midi list"], taken);
    }

    [Fact]
    public async Task PostCommand_UnderAWatchWithNoHook_IsDropped()
    {
        var keys = new KeySource(new ScriptedInput(), FastPoll);
        using var turn = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watch = keys.WatchAsync(turn, stop.Token);
        keys.PostCommand("/clear");
        await WaitUntilAsync(() => keys.PendingCommands == 0);
        await stop.CancelAsync();
        await watch;

        using var none = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.Null(await keys.ReadInputAsync(none.Token));
    }
}
