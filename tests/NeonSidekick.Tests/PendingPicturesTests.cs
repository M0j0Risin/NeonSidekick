using NeonSidekick.App;
using NeonSidekick.Comfy;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The ComfyUI pictures under way (2026-09-27, <c>/botchat</c>'s; every one since 2026-11-27): the lower rule's timer.</summary>
public class PendingPicturesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void RuleText_IsEmpty_WithNothingPending()
    {
        Assert.Equal("", new PendingPictures().RuleText);
    }

    [Fact]
    public void RuleText_IsTheOldestOnesKindAndTime_WithTheCountPastOne_TwoSpacesAfterTheGlyph()
    {
        var time = new ManualTimeProvider();
        var pending = new PendingPictures(time);
        using var first = pending.Begin(0);
        Assert.Equal(ComfyText.TextToImageLabel + "  00:00", pending.RuleText);
        time.Advance(TimeSpan.FromSeconds(38));
        using var second = pending.Begin(1);
        Assert.Equal(ComfyText.TextToImageLabel + PendingPictures.Gap + "2 · 00:38", pending.RuleText);
        time.Advance(TimeSpan.FromSeconds(2));
        first.Dispose();
        Assert.Equal(ComfyText.GeneratingLabel + PendingPictures.Gap + "00:02", pending.RuleText);   // the image-to-image one is the oldest now
        time.Advance(TimeSpan.FromSeconds(3723));
        Assert.Equal(ComfyText.GeneratingLabel + PendingPictures.Gap + "01:02:05", pending.RuleText);   // the busy row's clock past an hour
    }

    [Fact]
    public void Dispose_Twice_DropsItOnce()
    {
        var pending = new PendingPictures();
        var first = pending.Begin(0);
        using var second = pending.Begin(0);
        first.Dispose();
        first.Dispose();
        Assert.Equal(1, pending.Count);
    }

    [Fact]
    public async Task TrackAsync_CountsFromTheCall_UntilTheJobEnds_EvenOnAFailure()
    {
        var pending = new PendingPictures();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = pending.TrackAsync(0, () => gate.Task);
        Assert.Equal(1, pending.Count);
        gate.SetResult(7);
        Assert.Equal(7, await job.WaitAsync(Wait));
        Assert.Equal(0, pending.Count);

        var failing = pending.TrackAsync<int>(0, () => Task.FromException<int>(new InvalidOperationException("boom")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(Wait));
        Assert.Equal(0, pending.Count);
    }
}
