using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// YouTube open links in at the tools (2026-10-16): the route hook's three outcomes (a player of the app's own waited for, a browser answered at
/// once, a fallback's note first), a held video's answers, the hand-off's status and control, the tap hints and the place in each sentence, and
/// the descriptions under each setting (the window's byte for byte as before).
/// </summary>
public partial class YouTubeToolsTests
{
    private const string Today =
        "Plays a YouTube video, with sound, in the app's video window on the user's screen (one window: a new video replaces the one playing). " +
        "Give the id from youtube_search or a link the user pasted, or a playlist to play through. Play only what the user asked for. " +
        "Then youtube_control pauses, seeks, sets the volume or moves through a playlist, and youtube_status says where it is. " +
        "A saved video (youtube_saved) resumes where it was left unless start is given.";

    private const string StatusToday =
        "Says what the video window is playing: the video and channel, playing or paused, the position and length, the volume. " +
        "Use it before answering a question about the video, or to see whether a video started.";

    private const string ControlToday =
        "Controls the video playing in the video window: play, pause, seek to a time, forward or back some seconds, volume 0–100, mute, unmute, " +
        "next, previous or entry in a playlist, or close the window. " +
        "Use it when the user asks; it answers with where the video then is.";

    private const string SaveVideoToday = "The video's 11-character id or a YouTube link; \"current\" for the one in the video window; for remove, also its number in youtube_saved's list.";

    [Fact]
    public void TheDescriptions_FollowTheSetting_TheWindowsByteForByte()
    {
        Assert.Equal(Today, Play().Description);
        Assert.Equal(Today, YouTubePlayTool.DescriptionFor(YouTubeOpenLinksInKind.YouTubeWindow));
        Assert.Equal(StatusToday, new YouTubeStatusTool(_player).Description);
        Assert.Equal(ControlToday, Control().Description);
        Assert.Equal(SaveVideoToday, SaveVideoText(YouTubeOpenLinksInKind.YouTubeWindow));

        Assert.Contains("Web UI's YouTube tab", YouTubePlayTool.DescriptionFor(YouTubeOpenLinksInKind.WebUiWindow));
        Assert.Contains("waits for one", YouTubePlayTool.DescriptionFor(YouTubeOpenLinksInKind.WebUiWindow));
        Assert.Contains("with no tab in view it waits for one", YouTubePlayTool.DescriptionFor(YouTubeOpenLinksInKind.WebUiWindow));   // later on 2026-10-16: in view, not open
        Assert.Contains("Browser window", YouTubePlayTool.DescriptionFor(YouTubeOpenLinksInKind.BrowserWindow));
        Assert.Contains("default browser", YouTubePlayTool.DescriptionFor(YouTubeOpenLinksInKind.DefaultBrowser));
        Assert.All([YouTubeOpenLinksInKind.BrowserWindow, YouTubeOpenLinksInKind.DefaultBrowser], k =>
        {
            Assert.Contains("out of your control", YouTubePlayTool.DescriptionFor(k));
            Assert.DoesNotContain("Then youtube_control", YouTubePlayTool.DescriptionFor(k));
            Assert.Contains("can't be seen", YouTubeStatusTool.DescriptionFor(k));
            Assert.Contains("can't be controlled", YouTubeControlTool.DescriptionFor(k));
            Assert.Contains("never one opened in a browser", SaveVideoText(k));
        });
        Assert.Contains("Web UI's YouTube tab", YouTubeStatusTool.DescriptionFor(YouTubeOpenLinksInKind.WebUiWindow));
        Assert.Contains("close the player", YouTubeControlTool.DescriptionFor(YouTubeOpenLinksInKind.WebUiWindow));
        Assert.Contains("Web UI's YouTube tab", SaveVideoText(YouTubeOpenLinksInKind.WebUiWindow));

        // Read at each turn through the target hook.
        var target = YouTubeOpenLinksInKind.YouTubeWindow;
        var play = new YouTubePlayTool(_player, () => _settings, _time, target: () => target);
        target = YouTubeOpenLinksInKind.WebUiWindow;
        Assert.Equal(YouTubePlayTool.DescriptionFor(YouTubeOpenLinksInKind.WebUiWindow), play.Description);
    }

    private static string SaveVideoText(YouTubeOpenLinksInKind target) =>
        YouTubeSaveTool.SchemaFor(target).GetProperty("properties").GetProperty("video").GetProperty("description").GetString()!;

    [Fact]
    public async Task ARoutedPlay_ToAPlayerOfTheAppsOwn_IsWaitedFor_TheNoteFirst()
    {
        var played = new List<VideoRequest>();
        var play = new YouTubePlayTool(_player, () => _settings, _time, route: r => { played.Add(r); _player.Play(r); return new VideoRouted(true, null, YouTubeText.WebUiOffFellBack); });

        string answer = await Call(play, ("video", Bunny));

        Assert.Single(played);
        Assert.StartsWith(YouTubeText.WebUiOffFellBack + "Playing ", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoutedPlay_ToABrowser_AnswersAtOnce_WithTheSavedPlaceInItsLink()
    {
        string home = Path.Combine(Path.GetTempPath(), "ns-yt-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var library = new YouTubeLibrary(home, _time);
            library.Add(Bunny);
            library.Update(Bunny, s => s with { Position = 200, Duration = 635 });
            VideoRequest? sent = null;
            var play = new YouTubePlayTool(_player, () => _settings, _time, () => library, route: r =>
            {
                sent = r;
                var handoff = new VideoHandoff(YouTubeIds.UrlFor(r), YouTubeOpenLinksInKind.DefaultBrowser, r.VideoId, r.PlaylistId);
                return new VideoRouted(false, YouTubeText.HandedOff(handoff));
            });

            string answer = await Call(play, ("video", Bunny));

            Assert.Empty(_player.Plays);
            double resumed = library.ResumeAt(Bunny);
            Assert.Equal(resumed, sent!.Start);
            Assert.StartsWith(YouTubeText.Resumed(resumed), answer, StringComparison.Ordinal);
            Assert.Contains("&t=" + ((int)resumed).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s", answer);
            Assert.Contains("default browser", answer);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public async Task AWindowThatWontOpen_ThroughTheRoute_IsStillAnError()
    {
        var play = new YouTubePlayTool(_player, () => _settings, _time, route: _ => throw new InvalidOperationException("no WebView2 Runtime"));

        Assert.Equal("Error: no WebView2 Runtime", await Call(play, ("video", Bunny)));
    }

    [Fact]
    public async Task AHeldVideo_WithNoPageToSeeTheAsk_AnswersAtOnce_AndAHeldPlaylistToo()
    {
        _player.Behave = false;
        var play = new YouTubePlayTool(_player, () => _settings, _time, route: r =>
        {
            _player.Report(VideoSnapshot.Opening(r) with { Held = new VideoHold(false, _time.GetUtcNow()), Place = VideoPlace.WebTab });
            return VideoRouted.Waited;
        });

        string video = await Call(play, ("video", Bunny)).WaitAsync(TimeSpan.FromSeconds(5));
        string list = await Call(play, ("playlist", "PLabcdefghijklmnopqrstu")).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith("Waiting for a YouTube tab: no Web UI page is open now, so video " + Bunny, video, StringComparison.Ordinal);
        Assert.StartsWith("Waiting for a YouTube tab: no Web UI page is open now, so playlist PLabcdefghijklmnopqrstu", list, StringComparison.Ordinal);
        Assert.DoesNotContain("in the video window", video);
    }

    /// <summary>A video held while YouTube tabs are open but none is in view (later on 2026-10-16): its own sentence, whatever the nudge.</summary>
    [Fact]
    public void Held_WithTabsHidden_SaysItPlaysWhenOneComesIntoView()
    {
        var snapshot = VideoSnapshot.Opening(new VideoRequest(Bunny)) with { Place = VideoPlace.WebTab };
        string hidden = YouTubeText.Held(snapshot, new VideoHold(false, _time.GetUtcNow(), TabsHidden: true));
        Assert.StartsWith("Waiting for a YouTube tab in view: video " + Bunny + " plays in the Web UI's YouTube tab as soon as one comes into view", hidden, StringComparison.Ordinal);
        Assert.Equal(hidden, YouTubeText.Held(snapshot, new VideoHold(true, _time.GetUtcNow(), TabsHidden: true)));
        Assert.StartsWith("Waiting for a YouTube tab: no Web UI page", YouTubeText.Held(snapshot, new VideoHold(false, _time.GetUtcNow())), StringComparison.Ordinal);
    }
    [Fact]
    public async Task AHeldVideo_WithAPageNudged_KeepsWaiting_AndAStoppedWaitLeavesItHeld()
    {
        _player.Behave = false;
        var play = new YouTubePlayTool(_player, () => _settings, _time, route: r =>
        {
            _player.Report(VideoSnapshot.Opening(r) with { Held = new VideoHold(true, _time.GetUtcNow()), Place = VideoPlace.WebTab });
            return VideoRouted.Waited;
        });
        using var stop = new CancellationTokenSource();

        var call = play.InvokeAsync(new AIFunctionArguments { ["video"] = Bunny }, stop.Token).AsTask();
        await Task.Delay(50);
        Assert.False(call.IsCompleted);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.NotNull(_player.Snapshot?.Held);
        Assert.Contains("the open Web UI page was asked to switch to it", YouTubeText.Status(_player.Snapshot));
        Assert.Contains("still waits for a YouTube tab", YouTubeText.StoppedWaitingFor(_player.Snapshot));
    }

    [Fact]
    public async Task Control_OnAHeldVideo_CloseDropsIt_TheRestAnswerTheHeldWordsAndSendNothing()
    {
        _player.Report(VideoSnapshot.Opening(new VideoRequest(Bunny)) with { Held = new VideoHold(false, _time.GetUtcNow()), Place = VideoPlace.WebTab });

        string paused = await Call(Control(), ("action", "pause"));
        Assert.StartsWith("Waiting for a YouTube tab", paused, StringComparison.Ordinal);
        Assert.Empty(_player.Commands);

        Assert.Equal(YouTubeText.ClosedIn(VideoPlace.WebTab), await Call(Control(), ("action", "close")));
        Assert.Equal(1, _player.Closes);
        Assert.Contains("Web UI's YouTube tab", YouTubeText.ClosedIn(VideoPlace.WebTab));
    }

    [Fact]
    public async Task AfterAHandoff_StatusNamesThePlace_AndControlRefuses()
    {
        var handoff = new VideoHandoff(YouTubeIds.WatchUrl(Bunny), YouTubeOpenLinksInKind.BrowserWindow, Bunny, null);
        var status = new YouTubeStatusTool(_player, () => handoff);
        var control = new YouTubeControlTool(_player, _time, () => handoff);

        string said = await Call(status);
        Assert.Contains("Browser window", said);
        Assert.Contains(YouTubeIds.WatchUrl(Bunny), said);
        Assert.StartsWith("Error: ", await Call(control, ("action", "pause")), StringComparison.Ordinal);
        Assert.Contains("can't control", await Call(control, ("action", "pause")));

        // A video playing again in a player of the app's own reads as ever.
        _player.Play(new VideoRequest(Bunny));
        Assert.StartsWith("Playing ", await Call(status), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTapHints_SitOnTheTabsStateLineOnly()
    {
        var unstarted = new VideoSnapshot(VideoState.Unstarted, Bunny) { Place = VideoPlace.WebTab };
        var muted = new VideoSnapshot(VideoState.Playing, Bunny, Muted: true) { Place = VideoPlace.WebTab, NeedsTap = true };

        Assert.EndsWith("tap the video on the phone to start", YouTubeText.Status(unstarted), StringComparison.Ordinal);
        Assert.EndsWith("playing muted until the video is tapped", YouTubeText.Status(muted), StringComparison.Ordinal);
        Assert.Contains(", muted", YouTubeText.Status(muted));
        Assert.DoesNotContain("tap", YouTubeText.Status(unstarted with { Place = VideoPlace.Window }));
        Assert.DoesNotContain("tap", YouTubeText.Status(muted with { Place = VideoPlace.Window }));
        Assert.Equal("Paused video aqz-KE-bpKQ, volume 100%", YouTubeText.Status(new VideoSnapshot(VideoState.Paused, Bunny)));
    }

    [Fact]
    public void ThePlacedSentences_NameTheTab_TheWindowsUnchanged()
    {
        var opening = VideoSnapshot.Opening(Bunny);
        Assert.Equal("Opening video aqz-KE-bpKQ in the video window; youtube_status says when it plays.", YouTubeText.Status(opening));
        Assert.Contains("in the Web UI's YouTube tab;", YouTubeText.Status(opening with { Place = VideoPlace.WebTab }));
        Assert.Equal(YouTubeText.NoPlaylist, YouTubeText.NoPlaylistIn(VideoPlace.Window));
        Assert.Contains("Web UI's YouTube tab", YouTubeText.NoPlaylistIn(VideoPlace.WebTab));
        Assert.Equal(YouTubeText.Closed, YouTubeText.ClosedIn(VideoPlace.Window));
        Assert.Equal(YouTubeText.ClosedMeanwhile, YouTubeText.ClosedMeanwhileIn(VideoPlace.Window));
        Assert.Contains("Web UI's YouTube tab", YouTubeText.ClosedMeanwhileIn(VideoPlace.WebTab));
        Assert.Equal(YouTubeText.WaitingForPlayer, YouTubeText.WaitingFor(YouTubeOpenLinksInKind.YouTubeWindow));
        Assert.Contains("Web UI", YouTubeText.WaitingFor(YouTubeOpenLinksInKind.WebUiWindow));
        Assert.Equal(YouTubeText.StoppedWaiting, YouTubeText.StoppedWaitingFor(null));
        Assert.Equal(YouTubeText.StoppedWaiting, YouTubeText.StoppedWaitingFor(new VideoSnapshot(VideoState.Playing, Bunny)));
        Assert.Contains("Web UI's YouTube tab", YouTubeText.StoppedWaitingFor(new VideoSnapshot(VideoState.Playing, Bunny) { Place = VideoPlace.WebTab }));
    }

    [Fact]
    public void Refused_SaysWhatErrors152And153Mean_TheOtherCodesUnchanged()
    {
        Assert.Contains("no usable referrer", YouTubeText.Refused(Bunny, 153));
        Assert.Contains("no usable referrer", YouTubeText.Refused(Bunny, 152));
        Assert.Contains("open_url can open", YouTubeText.Refused(Bunny, 153));
        Assert.Equal("Error: YouTube will not play video aqz-KE-bpKQ in an embedded player: its uploader may have turned embedding off, or it is private, age-restricted or unavailable. open_url can open https://www.youtube.com/watch?v=aqz-KE-bpKQ in the browser instead.", YouTubeText.Refused(Bunny, 150));
        Assert.Contains("found no video", YouTubeText.Refused(Bunny, 100));
    }
}
