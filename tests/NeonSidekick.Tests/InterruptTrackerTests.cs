using NeonSidekick.App;

namespace NeonSidekick.Tests;

public class InterruptTrackerTests
{
    [Fact]
    public void OneSilentInterruption_DoesNothing()
    {
        var tracker = new InterruptTracker();

        Assert.False(tracker.Note(hadRequest: false));

        Assert.Equal(1, tracker.SilentInARow);
        Assert.False(tracker.Disabled);
    }

    [Fact]
    public void TwoSilentInARow_Disable_ReportedOnce()
    {
        var tracker = new InterruptTracker();

        Assert.False(tracker.Note(false));
        Assert.True(tracker.Note(false));
        Assert.False(tracker.Note(false));   // still disabled, not reported again

        Assert.True(tracker.Disabled);
        Assert.Equal(3, tracker.SilentInARow);
    }

    [Fact]
    public void ARequest_ClearsTheRun()
    {
        var tracker = new InterruptTracker();

        tracker.Note(false);
        Assert.False(tracker.Note(true));
        Assert.Equal(0, tracker.SilentInARow);
        Assert.False(tracker.Note(false));
        Assert.False(tracker.Disabled);
    }

    [Fact]
    public void Reset_StartsAgain()
    {
        var tracker = new InterruptTracker();
        tracker.Note(false);
        tracker.Note(false);

        tracker.Reset();

        Assert.False(tracker.Disabled);
        Assert.Equal(0, tracker.SilentInARow);
        Assert.False(tracker.Note(false));
        Assert.True(tracker.Note(false));
    }

    [Fact]
    public void Wording_IsPinned()
    {
        Assert.Equal(2, InterruptTracker.Threshold);
        Assert.Equal("(🎤 heard nothing — 1 of 2)", InterruptTracker.SilentHint(1));
    }
}
