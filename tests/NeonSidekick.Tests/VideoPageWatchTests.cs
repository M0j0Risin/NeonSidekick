using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The video window's page switch (2026-11-13, the review's fix): which page is wanted, whether it came, and what a pending play does next —
/// wait for a page still loading, send to one that came, navigate again after one that never came or for the other kind of page.
/// </summary>
public sealed class VideoPageWatchTests
{
    private static readonly VideoRequest File = VideoRequest.File(Path.Combine(Path.GetTempPath(), "clip.mp4"));
    private static readonly VideoRequest YouTube = new("aqz-KE-bpKQ");

    [Fact]
    public void APageLoading_Waits_AndOnceItCame_Sends()
    {
        var watch = new VideoPageWatch(showingFile: false);
        Assert.Equal(VideoPageStep.Wait, watch.Next(YouTube));

        Assert.False(watch.Arrived(filePage: true));   // the other kind's page: not the one wanted
        Assert.Equal(VideoPageStep.Wait, watch.Next(YouTube));

        Assert.True(watch.Arrived(filePage: false));
        Assert.Equal(VideoPageStep.Send, watch.Next(YouTube));
        Assert.Equal(VideoPageStep.Send, watch.Next(null));
        Assert.False(watch.TimedOut());   // a page that came never times out
    }

    [Fact]
    public void APlayOfTheOtherKind_NavigatesAgain()
    {
        var watch = new VideoPageWatch(showingFile: false);
        watch.Arrived(filePage: false);
        Assert.Equal(VideoPageStep.Navigate, watch.Next(File));

        watch.Navigated(file: true);
        Assert.True(watch.ShowingFile);
        Assert.False(watch.Ready);
        Assert.Equal(VideoPageStep.Wait, watch.Next(File));
    }

    /// <summary>A page that never came: the next play navigates again, never queues behind it; a late arrival then flushes nothing.</summary>
    [Fact]
    public void APageThatNeverCame_TheNextPlayNavigates_AndALateArrivalSendsNothingOld()
    {
        var watch = new VideoPageWatch(showingFile: true);
        Assert.True(watch.TimedOut());
        Assert.True(watch.Expired);
        Assert.Equal(VideoPageStep.Navigate, watch.Next(File));
        Assert.Equal(VideoPageStep.Wait, watch.Next(null));   // nothing pending: nothing to do

        Assert.True(watch.Arrived(filePage: true));   // late: ready now, and the window dropped what waited
        Assert.False(watch.Expired);
        Assert.Equal(VideoPageStep.Send, watch.Next(null));
    }
}
