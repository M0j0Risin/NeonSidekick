using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The fake clock itself (2026-09-26): <c>ChatScreenTests.RunLoopAsync</c> advances it from a pool task while the screen
/// makes and disposes timers on its own thread (every <c>Task.Delay</c> on it), and an unguarded timer list threw
/// "Collection was modified" inside <see cref="ManualTimeProvider.Advance"/> — the clock task died unobserved, the next
/// loop gap never came due, and <c>Loop_Imagine_OnlyTheLastPassRidesWithTheNextMessage</c> hung a full run to the blame limit.
/// </summary>
public class ManualTimeProviderTests
{
    [Fact]
    public async Task Advance_WhileAnotherThreadMakesAndDisposesTimers_NeverThrows_AndFiresWhatCameDue()
    {
        var clock = new ManualTimeProvider();
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        int fired = 0;
        var advancer = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }
        });
        var churner = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var timer = clock.CreateTimer(_ => Interlocked.Increment(ref fired), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                timer.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
                timer.Dispose();
            }
        });

        await Task.WhenAll(advancer, churner);   // either one faulting is the bug
        Assert.Equal(0, clock.TimerCount);
    }

    [Fact]
    public async Task ADelayOnTheClock_CompletesWhenAnotherThreadAdvancesPastIt()
    {
        var clock = new ManualTimeProvider();
        var delays = Enumerable.Range(0, 200).Select(i => Task.Delay(TimeSpan.FromMilliseconds(i % 7 + 1), clock)).ToList();
        var advancer = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }
        });

        await advancer;
        await Task.WhenAll(delays).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ACallback_MayChangeOrDisposeItsTimer_WithinTheSameAdvance()
    {
        var clock = new ManualTimeProvider();
        int fired = 0;
        ITimer? timer = null;
        timer = clock.CreateTimer(_ =>
        {
            if (++fired == 3)
            {
                timer!.Dispose();
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(3, fired);
        Assert.Equal(0, clock.TimerCount);
    }
}
