using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A page's microphone as the pipeline sees it (2026-11-13, the webui-voice plan): chunks raised in order as the PC microphone's 50 ms
/// buffers, early ones held until the pipeline starts, a retry answered without a second delivery, a gap refused, and 3 s without a chunk
/// raised once as a stall.
/// </summary>
public sealed class WebAudioCaptureTests
{
    private static byte[] Pcm(int bytes, byte fill = 1) => Enumerable.Repeat(fill, bytes).ToArray();

    [Fact]
    public void AChunk_IsRaisedAsFiftyMillisecondBuffers_InOrder()
    {
        using var capture = new WebAudioCapture(new ManualTimeProvider());
        var raised = new List<int>();
        capture.DataAvailable += (_, count) => raised.Add(count);
        capture.Start();

        Assert.Equal(WebChunkResult.Delivered, capture.Deliver(0, Pcm(4000)));

        Assert.Equal([WebAudioCapture.SliceBytes, WebAudioCapture.SliceBytes, 800], raised);
        Assert.Equal(1, capture.Delivered);
    }

    [Fact]
    public void ChunksBeforeTheStart_AreHeld_ThenRaisedAtTheStart()
    {
        using var capture = new WebAudioCapture(new ManualTimeProvider());
        var raised = new List<byte>();
        capture.DataAvailable += (buffer, count) => raised.Add(buffer[0]);

        Assert.Equal(WebChunkResult.Delivered, capture.Deliver(0, Pcm(1600, 1)));
        Assert.Equal(WebChunkResult.Delivered, capture.Deliver(1, Pcm(1600, 2)));
        Assert.Empty(raised);

        capture.Start();

        Assert.Equal([1, 2], raised);
    }

    [Fact]
    public void ARetry_IsARepeat_AndAGap_IsRefused_NeitherRaised()
    {
        using var capture = new WebAudioCapture(new ManualTimeProvider());
        int raised = 0;
        capture.DataAvailable += (_, _) => raised++;
        capture.Start();
        capture.Deliver(0, Pcm(1600));

        Assert.Equal(WebChunkResult.Repeat, capture.Deliver(0, Pcm(1600)));
        Assert.Equal(WebChunkResult.OutOfOrder, capture.Deliver(2, Pcm(1600)));
        Assert.Equal(1, raised);
        Assert.Equal(WebChunkResult.Delivered, capture.Deliver(1, Pcm(1600)));
        Assert.Equal(2, raised);
    }

    [Fact]
    public void AfterTheStop_EveryChunkIsEnded_AndStartedIsRaisedOnce_NeverAfterAStop()
    {
        var capture = new WebAudioCapture(new ManualTimeProvider());
        int started = 0;
        capture.Started += () => started++;
        capture.Start();
        capture.Start();

        capture.Stop();
        capture.Dispose();

        Assert.Equal(WebChunkResult.Ended, capture.Deliver(0, Pcm(1600)));
        Assert.Equal(1, started);

        var stoppedFirst = new WebAudioCapture(new ManualTimeProvider());
        stoppedFirst.Started += () => started++;
        stoppedFirst.Stop();
        stoppedFirst.Start();
        Assert.Equal(1, started);   // a listen refused before it began never starts (2026-11-14, the review's #13/#17)
    }

    [Fact]
    public void AStartHandlerThatThrows_IsLogged_AndTheCaptureStillRuns()
    {
        using var capture = new WebAudioCapture(new ManualTimeProvider());
        int raised = 0;
        capture.DataAvailable += (_, _) => raised++;
        capture.Started += () => throw new InvalidOperationException("boom");

        capture.Start();

        Assert.True(capture.IsCapturing);
        Assert.Equal(WebChunkResult.Delivered, capture.Deliver(0, Pcm(1600)));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ThreeSecondsWithoutAChunk_RaiseTheStallOnce()
    {
        var time = new ManualTimeProvider();
        using var capture = new WebAudioCapture(time);
        int stalls = 0;
        capture.Stalled += () => stalls++;
        capture.Start();
        capture.Deliver(0, Pcm(1600));

        time.Advance(TimeSpan.FromSeconds(2));
        capture.Deliver(1, Pcm(1600));   // a chunk in time pushes the stall back
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, stalls);

        time.Advance(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, stalls);
    }

    [Fact]
    public void TooManyEarlyChunks_AreRefused()
    {
        using var capture = new WebAudioCapture(new ManualTimeProvider());
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(WebChunkResult.Delivered, capture.Deliver(i, Pcm(2)));
        }

        Assert.Equal(WebChunkResult.OutOfOrder, capture.Deliver(40, Pcm(2)));
    }
}
