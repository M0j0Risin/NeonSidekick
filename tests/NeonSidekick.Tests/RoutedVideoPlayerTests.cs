using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The router over the video window and the Web UI's player (2026-10-16, <c>YouTube open links in</c>): a switch's one null, raised by the
/// router and never the closing player's; a hand-off's late null dropped and cleared by the next play; a closing player read as closed; a play
/// waiting for its own player's close, never the other's; the close off the caller's thread; versions that only climb; the web player's takeover.
/// </summary>
public class RoutedVideoPlayerTests
{
    private const string Bunny = "aqz-KE-bpKQ";
    private const string Other = "jNQXAC9IVRw";
    private readonly FakeVideoPlayer _window = new();
    private readonly FakeVideoPlayer _web = new() { Place = VideoPlace.WebTab };
    private readonly List<VideoSnapshot?> _seen = [];

    private RoutedVideoPlayer Router()
    {
        var router = new RoutedVideoPlayer(_window, _web);
        router.Changed += s => { lock (_seen) { _seen.Add(s); } };
        return router;
    }

    private int Nulls() { lock (_seen) { return _seen.Count(s => s is null); } }

    [Fact]
    public async Task ASwitch_ClosesTheOldPlayer_AndRaisesExactlyOneNull_OfItsOwn()
    {
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        _seen.Clear();

        router.Play(new VideoRequest(Other), VideoSink.Web);
        await router.PendingClose;

        Assert.Equal(1, Nulls());
        Assert.Null(_seen[0]);   // the router's own null comes first, before the new player's reports
        Assert.Equal(1, _window.Closes);
        Assert.Equal(VideoSink.Web, router.Active);
        Assert.Equal(VideoPlace.WebTab, router.Place);
        Assert.Equal(Other, router.Snapshot?.VideoId);
    }

    [Fact]
    public async Task ASwitch_WithALateClose_StillRaisesOneNull()
    {
        _window.LateClose = true;
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        _seen.Clear();

        router.Play(new VideoRequest(Other), VideoSink.Web);
        await router.PendingClose;
        _window.RaiseLateClose();

        Assert.Equal(1, Nulls());
    }

    [Fact]
    public void ASwitch_AwayFromNothingOpen_RaisesNothing_AndClosesNothingOpen()
    {
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Web);

        Assert.Equal(0, Nulls());
    }

    [Fact]
    public async Task AHandoff_ClosesTheActivePlayer_WithOneNull_ItsLateNullDropped()
    {
        _window.LateClose = true;
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        _seen.Clear();

        router.Handoff(new VideoHandoff(YouTubeIds.WatchUrl(Bunny), YouTubeOpenLinksInKind.DefaultBrowser, Bunny, null));
        await router.PendingClose;
        _window.RaiseLateClose();

        Assert.Equal(1, Nulls());
        Assert.Equal(VideoSink.Window, router.Active);
        Assert.NotNull(router.LastHandoff);
        Assert.Null(router.Snapshot);
    }

    [Fact]
    public async Task AfterAHandoff_ANewPlay_ClearsTheDrop_SoTheWindowsOwnCloseIsForwarded()
    {
        _window.LateClose = true;
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        router.Handoff(new VideoHandoff(YouTubeIds.WatchUrl(Bunny), YouTubeOpenLinksInKind.BrowserWindow, Bunny, null));
        await router.PendingClose;

        router.Play(new VideoRequest(Other), VideoSink.Window);   // the fake drops the held null, as a new window does
        Assert.Null(router.LastHandoff);
        _seen.Clear();
        _window.CloseByUser();

        Assert.Equal(1, Nulls());
    }

    [Fact]
    public async Task WhileAHandoffsCloseRuns_TheRouterReadsTheVideoAsClosed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.CloseGate = gate;
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);

        router.Handoff(new VideoHandoff(YouTubeIds.WatchUrl(Bunny), YouTubeOpenLinksInKind.DefaultBrowser, Bunny, null));   // returns while the gate is shut

        Assert.NotNull(_window.Snapshot);
        Assert.Null(router.Snapshot);
        Assert.False(router.Send(VideoCommand.Pause));
        Assert.Empty(_window.Commands);
        gate.SetResult();
        await router.PendingClose;
    }

    [Fact]
    public async Task APlay_OnAPlayerWhoseCloseRuns_WaitsForIt_AndTheOldCloseNeverClosesTheNewVideo()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.CloseGate = gate;
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        router.Handoff(new VideoHandoff(YouTubeIds.WatchUrl(Bunny), YouTubeOpenLinksInKind.DefaultBrowser, Bunny, null));

        var play = Task.Run(() => router.Play(new VideoRequest(Other), VideoSink.Window));
        await Task.Delay(200);
        Assert.False(play.IsCompleted);   // waiting on the close still running
        gate.SetResult();
        await play.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Other, router.Snapshot?.VideoId);
        Assert.Equal(1, _window.Closes);
    }

    [Fact]
    public async Task APlay_OnTheOtherPlayer_NeverWaits_ForTheWindowsClose()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.CloseGate = gate;
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);

        router.Play(new VideoRequest(Other), VideoSink.Web);   // returns while the window's close is held

        Assert.Equal(Other, router.Snapshot?.VideoId);
        gate.SetResult();
        await router.PendingClose;
    }

    [Fact]
    public async Task ASwitchBack_WhileTheWindowsCloseRuns_LandsItsNullUnheard_AndTheNewPlaysWaitEndsOnItsReport()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.CloseGate = gate;
        _window.LateClose = true;
        var time = new ManualTimeProvider();
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        router.Play(new VideoRequest(Other), VideoSink.Web);   // the window's close starts, held
        long before = router.Snapshot!.Version;

        var waited = Task.Run(async () =>
        {
            router.Play(new VideoRequest(Bunny), VideoSink.Window);
            return await YouTubeWait.UntilAsync(router, before, s => s.VideoId == Bunny && s.State == VideoState.Playing, YouTubeWait.Play, time, CancellationToken.None);
        });
        await Task.Delay(200);
        _seen.Clear();
        gate.SetResult();
        var snapshot = await waited.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(snapshot);   // never ClosedMeanwhile
        Assert.Equal(Bunny, snapshot.VideoId);
        Assert.Equal(1, Nulls());   // the router's own, as the web player's video went
    }

    [Fact]
    public async Task TheOldPlayersClose_RunsOffTheCallersThread()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window.CloseGate = gate;
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);

        router.Play(new VideoRequest(Other), VideoSink.Web);
        router.Handoff(new VideoHandoff(YouTubeIds.WatchUrl(Other), YouTubeOpenLinksInKind.DefaultBrowser, Other, null));
        router.Play(new VideoRequest(Bunny), VideoSink.Web);
        _web.RaiseTookOver();

        gate.SetResult();
        await router.PendingClose;
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, _window.CloseThreads);
    }

    [Fact]
    public async Task Versions_OnlyClimb_AcrossASwitch_EvenWhenAPlayerCountsFromOneAgain()
    {
        var router = Router();
        for (int i = 0; i < 5; i++)
        {
            router.Play(new VideoRequest(Bunny), VideoSink.Window);
        }

        router.Play(new VideoRequest(Other), VideoSink.Web);
        await router.PendingClose;
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        await router.PendingClose;
        _web.Report(null);
        router.Play(new VideoRequest(Other), VideoSink.Web);   // the fake starts over at 1
        await router.PendingClose;

        var versions = _seen.Where(s => s is not null).Select(s => s!.Version).ToList();
        Assert.Equal(versions.Order().ToList(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    [Fact]
    public async Task APlay_OnTheNewPlayer_StillWaitsForItsReport_AndNeverSeesTheSwitchsNull()
    {
        _web.Behave = false;
        var time = new ManualTimeProvider();
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        long before = router.Snapshot!.Version;

        router.Play(new VideoRequest(Other), VideoSink.Web);
        var wait = YouTubeWait.UntilAsync(router, before, s => s.VideoId == Other && s.State == VideoState.Playing, YouTubeWait.Play, time, CancellationToken.None);
        Assert.False(wait.IsCompleted);
        _web.Report(new VideoSnapshot(VideoState.Playing, Other));

        var snapshot = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Other, snapshot?.VideoId);
        Assert.Equal(VideoPlace.WebTab, snapshot?.Place);
        await router.PendingClose;
    }

    [Fact]
    public async Task TheWebPlayersTakeover_SwitchesToWeb_ALateTickFromAClosedPlayer_SwitchesNothing()
    {
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        _web.Report(new VideoSnapshot(VideoState.Playing, Other));   // the page's own video, not yet active: unheard

        _web.RaiseTookOver();
        await router.PendingClose;
        Assert.Equal(VideoSink.Web, router.Active);
        Assert.Equal(1, _window.Closes);

        _window.Report(new VideoSnapshot(VideoState.Playing, Bunny));   // the PC window's last tick
        Assert.Equal(VideoSink.Web, router.Active);
        Assert.Equal(Other, router.Snapshot?.VideoId);
    }

    [Fact]
    public async Task AHandoff_IsSet_AndClearedByTheNextPlay()
    {
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        router.Handoff(new VideoHandoff("https://www.youtube.com/watch?v=" + Other, YouTubeOpenLinksInKind.BrowserWindow, Other, null));
        await router.PendingClose;
        Assert.Equal(Other, router.LastHandoff?.VideoId);

        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        Assert.Null(router.LastHandoff);
    }

    [Fact]
    public void TheRoutersClose_IsTheActivePlayers_Synchronously_ItsNullForwarded()
    {
        var router = Router();
        router.Play(new VideoRequest(Bunny), VideoSink.Window);
        _seen.Clear();

        Assert.True(router.Close());
        Assert.Equal([null], _seen);
        Assert.False(router.Close());
    }

    [Fact]
    public async Task TheResume_SavesTheOldVideosPlace_OnASwitch_AndOnAHandoff()
    {
        string home = Path.Combine(Path.GetTempPath(), "ns-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var time = new ManualTimeProvider();
            var library = new YouTubeLibrary(home, time);
            library.Add(Bunny);
            library.Add(Other);
            var router = Router();
            _ = new YouTubeResume(router, () => library, time);

            router.Play(new VideoRequest(Bunny), VideoSink.Window);
            _window.Report(_window.Snapshot! with { Position = 120 });
            router.Play(new VideoRequest(Other), VideoSink.Web);
            await router.PendingClose;
            Assert.Equal(120, library.Find(Bunny)!.Position);

            _web.Report(_web.Snapshot! with { Position = 60 });
            router.Handoff(new VideoHandoff(YouTubeIds.WatchUrl(Bunny), YouTubeOpenLinksInKind.DefaultBrowser, Bunny, null));
            await router.PendingClose;
            Assert.Equal(60, library.Find(Other)!.Position);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }
}
