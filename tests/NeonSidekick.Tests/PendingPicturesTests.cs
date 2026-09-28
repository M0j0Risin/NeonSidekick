using NeonSidekick.App;
using NeonSidekick.Comfy;

namespace NeonSidekick.Tests;

/// <summary><c>/botchat</c>'s pictures still rendering with <c>Botchat image async</c> on (2026-09-27): the strip's glyph.</summary>
public class PendingPicturesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Glyph_IsEmpty_WithNothingPending()
    {
        Assert.Equal("", new PendingPictures().Glyph);
    }

    [Fact]
    public void Glyph_IsTheOldestOnesKind_WithTheCountPastOne()
    {
        var pending = new PendingPictures();
        using var first = pending.Begin(0);
        Assert.Equal(ComfyText.TextToImageLabel, pending.Glyph);
        using var second = pending.Begin(1);
        Assert.Equal(ComfyText.TextToImageLabel + " 2", pending.Glyph);
        first.Dispose();
        Assert.Equal(ComfyText.GeneratingLabel, pending.Glyph);   // the image-to-image one is the oldest now
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
