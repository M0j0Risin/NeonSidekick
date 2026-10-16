using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// YouTube open links in on the screen (2026-10-16): <c>/youtube play</c>, a search's pick and a saved video under each setting, the status
/// after a hand-off, the Web UI's tab holding a video for a page and handing it over, its fallback to the window, the window's links, a profile
/// switch and the app's exit, Home's remote on the tab, no tab without a player, and the PC's flashes for a Play here and a Resume.
/// </summary>
public partial class ChatScreenTests
{
    private const string BunnyId = "aqz-KE-bpKQ";

    private static string PageOf(char c) => new(c, 22);

    private void YouTubeRouteFixture(string target, bool webUi = false)
    {
        YouTubeFixture(key: false);
        _settings.Update(d =>
        {
            d.YouTubeOpenLinksIn = target;
            d.WebUi = webUi;
            d.WebUiReach = "local";
        });
        if (webUi)
        {
            _webUiHost = _ => new RecordingHost();
        }
    }

    private void UntilServing() => SpinWait.SpinUntil(() => _running!.WebUiServingForTests, TimeSpan.FromSeconds(10));

    [Fact]
    public async Task YouTube_UnderWebUiWindow_APlayWaitsForATab_AndThePageThatAttachesGetsIt()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        string? watch = null;
        bool tab = false;
        List<string> events = [];
        var input = WhenIdle(
            i => { UntilServing(); PushLine(i, "/youtube play " + BunnyId); },
            i =>
            {
                watch = Web.Pulse().Watch;
                tab = Web.Tabs().YouTube;
                using var page = _running!.WebVideoForTests!.Attach(PageOf('a'), PageOf('1'), false, null, () => { });
                while (page.Events.TryRead(out var e))
                {
                    events.Add(e.Name);
                }

                PushLine(i, "/exit");
            });

        string output = await RunAsync(input);

        Assert.Contains("Waiting for a YouTube tab: no Web UI page is open now, so video " + BunnyId, output);
        Assert.Empty(_videoPlayer!.Plays);
        Assert.Equal(WebUiText.WatchBanner, watch);
        Assert.True(tab);
        Assert.Contains("load", events);
    }

    [Fact]
    public async Task YouTube_UnderWebUiWindow_WithTheWebUiOff_PlaysInTheWindow_WithANote()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: false);
        PushLine("/youtube play " + BunnyId);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(BunnyId, Assert.Single(_videoPlayer!.Plays).VideoId);
        Assert.Contains("  · " + YouTubeText.WebUiOffFellBack + "Playing ", output);
    }

    [Fact]
    public async Task YouTube_UnderDefaultBrowser_HandsTheVideoOff_StatusSaysWhere_AndControlRefuses()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.DefaultBrowser);
        PushLine("/youtube play " + BunnyId);
        PushLine("/youtube status");
        PushLine("/youtube pause");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([YouTubeIds.WatchUrl(BunnyId)], _openedUrls);
        Assert.Empty(_videoPlayer!.Plays);
        Assert.Contains("  · Opened video " + BunnyId + " in the user's default browser", output);
        Assert.Contains("  · The last video, video " + BunnyId + ", went to the user's default browser", output);
        Assert.Contains("  ✗ video " + BunnyId + " is in the user's default browser, which the app can't control", output);
    }

    [Fact]
    public async Task YouTube_UnderBrowserWindow_OpensTheBrowserWindow_AndWithoutOneTheDefaultBrowser()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.BrowserWindow);
        _browserWindow = new FakeBrowserWindow();
        PushLine("/youtube play " + BunnyId);
        PushLine("/exit");

        string output = await RunAsync();

        SpinWait.SpinUntil(() => _browserWindow.Browses.Count > 0, TimeSpan.FromSeconds(5));
        Assert.Equal(new Uri(YouTubeIds.WatchUrl(BunnyId)), Assert.Single(_browserWindow.Browses));
        Assert.Empty(_openedUrls);
        Assert.Contains("  · Opened video " + BunnyId + " in the app's Browser window", output);
    }

    [Fact]
    public async Task YouTube_UnderBrowserWindow_WithNoBrowserWindow_GoesToTheDefaultBrowser_WithTheReason()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.BrowserWindow);
        _browserWindow = null;
        PushLine("/youtube play " + BunnyId);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([YouTubeIds.WatchUrl(BunnyId)], _openedUrls);
        Assert.Contains(YouTubeText.NoBrowserWindowHere, output);
    }

    [Fact]
    public async Task YouTube_ASearchsPick_AndASavedVideo_FollowTheSetting()
    {
        YouTubeFixture();
        _settings.Update(d => d.YouTubeOpenLinksIn = YouTubeOpenLinksIn.DefaultBrowser);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        new YouTubeLibrary(_settings.ProfileDirectory).Add("jNQXAC9IVRw", "Me at the zoo", "jawed", position: 30);
        PushLine("/youtube big buck bunny");
        _console.Input.PushKey(Keys.Enter);        // the first hit
        PushLine("/youtube list");
        _console.Input.PushKey(Keys.Enter);        // the saved one, where it was left
        PushLine("/exit");

        await RunAsync();

        Assert.Empty(_videoPlayer!.Plays);
        Assert.Equal([YouTubeIds.WatchUrl(BunnyId), YouTubeIds.WatchUrl("jNQXAC9IVRw", 27)], _openedUrls);
    }

    [Fact]
    public async Task YouTube_UnderWebUiWindow_ThePlaysSpinner_NamesTheTab_AndAStoppedWaitLeavesTheVideoWaiting()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            UntilServing();
            Web.Pulse();   // a page reading the pulse: the nudge is seen, so the play waits on
            _running!.RoutedVideoForTests!.Changed += s => { if (s is { Held: not null }) input.Push(Keys.Escape); };
            PushLine(input, "/youtube play " + BunnyId);
            input.OnWait = () =>
            {
                if (_running!.WebVideoForTests!.Waiting)
                {
                    input.OnWait = null;
                    PushLine(input, "/exit");
                }
            };
        };

        string output = await RunAsync(input);

        Assert.Contains(YouTubeText.WaitingFor(YouTubeOpenLinksInKind.WebUiWindow), output);
        Assert.Contains("(stopped waiting; the video still waits for a YouTube tab", output);
    }

    [Fact]
    public async Task TheVideoWindowsLinks_PlayWhereTheSettingSays_AnythingElseInTheDefaultBrowser()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.YouTubeWindow);
        var inline = new List<string>();
        var input = WhenIdle(
            i =>
            {
                _running!.RouteVideoLink("https://www.youtube.com/channel/UCSMOQeBJ2RAnuFungnQOxLg", inline.Add);
                _running.RouteVideoLink("https://youtu.be/jNQXAC9IVRw", inline.Add);
                SpinWait.SpinUntil(() => _videoPlayer!.Plays.Count == 1, TimeSpan.FromSeconds(10));
                _settings.Update(d => d.YouTubeOpenLinksIn = YouTubeOpenLinksIn.DefaultBrowser);
                _running.RouteVideoLink("https://youtu.be/jNQXAC9IVRw", inline.Add);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal("jNQXAC9IVRw", Assert.Single(_videoPlayer!.Plays).VideoId);
        Assert.Equal(["https://www.youtube.com/channel/UCSMOQeBJ2RAnuFungnQOxLg", "https://youtu.be/jNQXAC9IVRw"], inline);
    }

    [Fact]
    public async Task AProfileSwitch_DropsAHeldVideo_WhileAPlayingOnePlaysOn()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        Profiles.Create(_dir, "work", AppSettings.Copy(_settings.Current));
        Profiles.Create(_dir, "play", AppSettings.Copy(_settings.Current));
        VideoSnapshot? afterHeld = null;
        VideoSnapshot? afterPlaying = null;
        var input = WhenIdle(
            i => { UntilServing(); PushLine(i, "/youtube play " + BunnyId); },
            i => PushLine(i, "/profile work"),
            i =>
            {
                afterHeld = _running!.WebVideoForTests!.Snapshot;
                PushLine(i, "/profile play");
            },
            i =>
            {
                var web = _running!.WebVideoForTests!;
                var page = web.Attach(PageOf('a'), PageOf('1'), false, null, () => { });
                web.Play(new VideoRequest(BunnyId));
                web.Report(PageOf('a'), "{\"ev\":\"state\",\"id\":\"" + BunnyId + "\",\"state\":1,\"position\":3,\"duration\":635}", null);
                PushLine(i, "/profile work");
            },
            i =>
            {
                afterPlaying = _running!.WebVideoForTests!.Snapshot;
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Null(afterHeld);
        Assert.Equal(VideoState.Playing, afterPlaying?.State);
    }

    [Fact]
    public async Task TheAppsExit_StopsTheTabsVideo_AndSavesItsPlace()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        new YouTubeLibrary(_settings.ProfileDirectory).Add(BunnyId);
        List<string> events = [];
        WebVideoAttachment? page = null;
        var input = WhenIdle(
            i =>
            {
                UntilServing();
                var web = _running!.WebVideoForTests!;
                page = web.Attach(PageOf('a'), PageOf('1'), false, null, () => { });
                // The page answers the load, as its player would: the play's wait ends on it.
                _running.RoutedVideoForTests!.Changed += s =>
                {
                    if (s is { State: VideoState.Opening, Place: VideoPlace.WebTab })
                    {
                        _ = Task.Run(() => web.Report(PageOf('a'), "{\"ev\":\"state\",\"id\":\"" + BunnyId + "\",\"state\":1,\"position\":10,\"duration\":635}", null));
                    }
                };
                PushLine(i, "/youtube play " + BunnyId);
            },
            i =>
            {
                _running!.WebVideoForTests!.Report(PageOf('a'), "{\"ev\":\"state\",\"id\":\"" + BunnyId + "\",\"state\":1,\"position\":120,\"duration\":635}", null);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        while (page!.Events.TryRead(out var e))
        {
            events.Add(e.Name);
        }

        Assert.Contains("close", events);
        Assert.Equal(120, new YouTubeLibrary(_settings.ProfileDirectory).Find(BunnyId)!.Position);
    }

    [Fact]
    public async Task HomesRemote_UnderWebUiWindow_ReachesTheTab_AHeldVideoIsWaitingWithNoRemote_AndNoVideoNamesTheTab()
    {
        HomeTab();
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        WebHomeVideo? held = null;
        WebReply heldTap = default;
        WebReply none = default;
        List<string> events = [];
        var input = WhenIdle(
            i => { UntilServing(); PushLine(i, "/youtube play " + BunnyId); },
            i =>
            {
                held = HomeSide.Read().Video;
                heldTap = HomeSide.Video(new WebHomeVideoRequest { Action = "pause" }, null);
                var web = _running!.WebVideoForTests!;
                using var page = web.Attach(PageOf('a'), PageOf('1'), false, null, () => { });
                web.Report(PageOf('a'), "{\"ev\":\"state\",\"id\":\"" + BunnyId + "\",\"state\":1,\"position\":3,\"duration\":635}", null);
                HomeSide.Video(new WebHomeVideoRequest { Action = "pause" }, null);
                while (page.Events.TryRead(out var e))
                {
                    events.Add(e.Name);
                }

                web.Close();
                none = HomeSide.Video(new WebHomeVideoRequest { Action = "play" }, null);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal(("waiting", WebUiText.VideoWhere(VideoPlace.WebTab)), (held!.State, held.Where));
        Assert.Equal(WebOutcome.Conflict, heldTap.Outcome);
        Assert.StartsWith("Waiting for a YouTube tab", heldTap.Message, StringComparison.Ordinal);
        Assert.Contains("cmd", events);
        Assert.Equal(WebUiText.NoVideoIn(VideoPlace.WebTab), none.Message);
    }

    [Fact]
    public async Task UnderWebUiWindow_WithNoPlayer_ThereIsNoYouTubeTab()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        _videoPlayer = null;
        bool? tab = null;
        var input = WhenIdle(
            i =>
            {
                tab = Web.Tabs().YouTube;
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.False(tab);
    }

    [Fact]
    public async Task APlayHere_AndAResume_FlashOnThePcsHintRow()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        var flashed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        VideoSink? active = null;
        var input = WhenIdle(
            i =>
            {
                Web.FlashOnPc = flashed.Enqueue;
                var web = _running!.WebVideoForTests!;
                using var a = web.Attach(PageOf('a'), PageOf('1'), false, null, () => { });
                using var b = web.Attach(PageOf('b'), PageOf('2'), true, null, () => { });
                SpinWait.SpinUntil(() => flashed.Count >= 1, TimeSpan.FromSeconds(10));   // the flashes coalesce a burst: one at a time
                web.Report(PageOf('b'), "{\"ev\":\"resumed\"}", null);
                SpinWait.SpinUntil(() => flashed.Count >= 2, TimeSpan.FromSeconds(10));
                active = _running.RoutedVideoForTests!.Active;
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Contains(WebUiText.YouTubeClaimFlash, flashed);
        Assert.Contains(WebUiText.YouTubeResumeFlash, flashed);
        Assert.Equal(VideoSink.Web, active);   // the Resume took playback over
    }
}
