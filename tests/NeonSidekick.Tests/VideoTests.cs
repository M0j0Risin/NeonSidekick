using System.Text.Json;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The video window's portable half (2026-10-05, the YouTube plan): the request and command records, the page's protocol both
/// ways (<see cref="VideoMessages"/>, checked against the page itself), the keys and the wording. The window is the smoke's and
/// the hand's (<c>video:webview2</c>, a run on the published exe).
/// </summary>
public class VideoTests
{
    private const string Bunny = "aqz-KE-bpKQ";

    // ── VideoRequest, VideoCommand ───────────────────────────────────────────

    [Theory]
    [InlineData("aqz-KE-bpKQ", true)]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("___________", true)]
    [InlineData("aqz-KE-bpK", false)]
    [InlineData("aqz-KE-bpKQQ", false)]
    [InlineData("aqz KE-bpKQ", false)]
    [InlineData("aqz\"KE-bpKQ", false)]
    [InlineData("ąqz-KE-bpKQ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVideoId_IsElevenOfYouTubesCharacters(string? text, bool id)
    {
        Assert.Equal(id, VideoRequest.IsVideoId(text));
    }

    [Fact]
    public void Request_RefusesWhatIsNotAnId_AndHoldsTheStartAtZeroOrMore()
    {
        var refused = Assert.Throws<ArgumentException>(() => new VideoRequest("https://youtu.be/aqz-KE-bpKQ"));
        Assert.StartsWith(VideoText.NotAnId("https://youtu.be/aqz-KE-bpKQ"), refused.Message, StringComparison.Ordinal);

        Assert.Equal(0, new VideoRequest(Bunny, -5).Start);
        Assert.Equal(0, new VideoRequest(Bunny, double.NaN).Start);
        Assert.Equal(90.5, new VideoRequest(Bunny, 90.5).Start);
        Assert.True(new VideoRequest(Bunny).Autoplay);
    }

    [Fact]
    public void Commands_HoldTheirValues_InRange()
    {
        Assert.Equal(0, VideoCommand.Seek(-3).Value);
        Assert.Equal(0, VideoCommand.Seek(double.PositiveInfinity).Value);
        Assert.Equal(75.25, VideoCommand.Seek(75.25).Value);
        Assert.Equal(100, VideoCommand.Volume(140).Value);
        Assert.Equal(0, VideoCommand.Volume(-1).Value);
        Assert.Equal(33, VideoCommand.Volume(32.6).Value);
        Assert.Equal(VideoCommandKind.Pause, VideoCommand.Pause.Kind);
    }

    // ── VideoMessages: the app's side ────────────────────────────────────────

    [Fact]
    public void Load_IsThePagesLoadMessage_WithInvariantNumbers()
    {
        Assert.Equal("{\"cmd\":\"load\",\"id\":\"aqz-KE-bpKQ\",\"start\":90.5,\"autoplay\":false}", VideoMessages.Load(new VideoRequest(Bunny, 90.5, autoplay: false)));
        Assert.Equal("{\"cmd\":\"load\",\"id\":\"aqz-KE-bpKQ\",\"start\":0,\"autoplay\":true}", VideoMessages.Load(new VideoRequest(Bunny)));
    }

    [Fact]
    public void Command_IsEachCommandsMessage()
    {
        Assert.Equal("{\"cmd\":\"play\"}", VideoMessages.Command(VideoCommand.Play));
        Assert.Equal("{\"cmd\":\"pause\"}", VideoMessages.Command(VideoCommand.Pause));
        Assert.Equal("{\"cmd\":\"stop\"}", VideoMessages.Command(VideoCommand.Stop));
        Assert.Equal("{\"cmd\":\"mute\"}", VideoMessages.Command(VideoCommand.Mute));
        Assert.Equal("{\"cmd\":\"unmute\"}", VideoMessages.Command(VideoCommand.Unmute));
        Assert.Equal("{\"cmd\":\"seek\",\"t\":75.25}", VideoMessages.Command(VideoCommand.Seek(75.25)));
        Assert.Equal("{\"cmd\":\"volume\",\"v\":40}", VideoMessages.Command(VideoCommand.Volume(40)));
        Assert.Equal("{\"cmd\":\"next\"}", VideoMessages.Command(VideoCommand.Next));                    // a playlist's (2026-10-08)
        Assert.Equal("{\"cmd\":\"previous\"}", VideoMessages.Command(VideoCommand.Previous));
        Assert.Equal("{\"cmd\":\"at\",\"i\":4}", VideoMessages.Command(VideoCommand.PlayAt(4)));
        Assert.Equal(0, VideoCommand.PlayAt(-3).Value);
    }

    /// <summary>A playlist's load (2026-10-08): the list and its entry, the video to start at only when one is named.</summary>
    [Fact]
    public void Load_APlaylist_CarriesTheListAndTheEntry()
    {
        Assert.Equal("{\"cmd\":\"load\",\"start\":0,\"autoplay\":true,\"list\":\"PLabcdefghijklmnopq\",\"index\":2}", VideoMessages.Load(VideoRequest.Playlist("PLabcdefghijklmnopq", 2)));
        Assert.Equal("{\"cmd\":\"load\",\"id\":\"aqz-KE-bpKQ\",\"start\":30,\"autoplay\":false,\"list\":\"PLabcdefghijklmnopq\",\"index\":0}", VideoMessages.Load(VideoRequest.Playlist("PLabcdefghijklmnopq", 0, Bunny, 30, autoplay: false)));
        Assert.Throws<ArgumentException>(() => VideoRequest.Playlist("PL1"));
        Assert.Throws<ArgumentException>(() => VideoRequest.Playlist("PLabcdefghijklmnopq", 0, "short"));
        Assert.True(VideoRequest.IsPlaylistId("UUSMOQeBJ2RAnuFungnQOxLg"));
        Assert.False(VideoRequest.IsPlaylistId(Bunny));
        Assert.Equal(new VideoSnapshot(VideoState.Opening, null, PlaylistId: "PLabcdefghijklmnopq"), VideoSnapshot.Opening(VideoRequest.Playlist("PLabcdefghijklmnopq")));
    }

    /// <summary>Every command the app can send is one the page acts on: its <c>case</c> is in the page's switch.</summary>
    [Fact]
    public void EveryCommand_IsOneThePageHandles()
    {
        string page = VideoPage.Text();
        Assert.Contains("if (m.cmd === 'load') { load(m); return; }", page);
        foreach (var kind in Enum.GetValues<VideoCommandKind>())
        {
            using var doc = JsonDocument.Parse(VideoMessages.Command(new VideoCommand(kind, 1)));
            string cmd = doc.RootElement.GetProperty("cmd").GetString()!;
            Assert.Contains($"case '{cmd}':", page);
        }

        Assert.Contains("player.seekTo(m.t, true)", page);
        Assert.Contains("player.setVolume(m.v)", page);
        Assert.Contains("player.playVideoAt(m.i)", page);
        Assert.Contains("player.loadPlaylist(args)", page);
        Assert.Contains("player.cuePlaylist(args)", page);
        // A playlist link's named video starts at its own time, cued when the load was (2026-10-09, the review): never playVideoAt there.
        Assert.Contains("startSeconds: seek ? 0 : (m.start || 0)", page);
        Assert.Contains("const args = { list, listType: 'playlist', index: i, startSeconds: at };", page);
        Assert.DoesNotContain("if (i >= 0 && i !== entry()) player.playVideoAt(i);", page);
        // A playlist's player is made with no videoId key: the API throws on one there but undefined (2026-10-09, the silent window).
        Assert.Contains("if (!m.list) options.videoId = m.id;", page);
        Assert.DoesNotContain("videoId: m.list", page);
    }

    // ── VideoMessages: the page's side ───────────────────────────────────────

    [Fact]
    public void Apply_AStateReport_IsTheNewSnapshot()
    {
        var opening = VideoSnapshot.Opening(Bunny);

        var (kind, snapshot) = VideoMessages.Apply(opening, "{\"ev\":\"state\",\"why\":\"change\",\"id\":\"aqz-KE-bpKQ\",\"title\":\"Big Buck Bunny\",\"author\":\"Blender\",\"state\":1,\"position\":4.39,\"duration\":634.601,\"volume\":25,\"muted\":false}");

        Assert.Equal(VideoPageEvent.State, kind);
        Assert.Equal(new VideoSnapshot(VideoState.Playing, Bunny, "Big Buck Bunny", "Blender", 4.39, 634.601, 25, false, null, null, 1), snapshot);
    }

    [Fact]
    public void Apply_AnError_IsKeptForThatVideo_AndDroppedForAnother()
    {
        var (kind, failed) = VideoMessages.Apply(VideoSnapshot.Opening("aaaaaaaaaaa"), "{\"ev\":\"error\",\"code\":150,\"id\":\"aaaaaaaaaaa\"}");
        Assert.Equal(VideoPageEvent.Error, kind);
        Assert.Equal(150, failed.Error);
        Assert.Equal(1, failed.Version);

        var (_, same) = VideoMessages.Apply(failed, "{\"ev\":\"state\",\"id\":\"aaaaaaaaaaa\",\"state\":-1}");
        Assert.Equal(150, same.Error);
        var (_, other) = VideoMessages.Apply(same, "{\"ev\":\"state\",\"id\":\"aqz-KE-bpKQ\",\"state\":3}");
        Assert.Null(other.Error);
        Assert.Equal(VideoState.Buffering, other.State);
        Assert.Equal(3, other.Version);
    }

    /// <summary>A playlist's report (2026-10-08): its list, entry and count; an error the page moves past marked skip, cleared by the next entry.</summary>
    [Fact]
    public void Apply_APlaylistsReport_CarriesItsPlace_AndASkippedError()
    {
        var opening = VideoSnapshot.Opening(VideoRequest.Playlist("PLabcdefghijklmnopq"));

        var (_, playing) = VideoMessages.Apply(opening, "{\"ev\":\"state\",\"id\":\"aqz-KE-bpKQ\",\"state\":1,\"list\":\"PLabcdefghijklmnopq\",\"index\":2,\"count\":25}");
        Assert.Equal(("PLabcdefghijklmnopq", 2, 25), (playing.PlaylistId, playing.PlaylistEntry, playing.PlaylistCount));

        var (_, refused) = VideoMessages.Apply(playing, "{\"ev\":\"error\",\"code\":150,\"id\":\"aaaaaaaaaaa\",\"skip\":true}");
        Assert.True(refused.Skipping);
        Assert.Equal(("PLabcdefghijklmnopq", 150), (refused.PlaylistId, refused.Error));
        var (_, still) = VideoMessages.Apply(refused, "{\"ev\":\"state\",\"id\":\"aaaaaaaaaaa\",\"state\":-1,\"list\":\"PLabcdefghijklmnopq\",\"index\":3,\"count\":25}");
        Assert.True(still.Skipping);
        var (_, next) = VideoMessages.Apply(still, "{\"ev\":\"state\",\"id\":\"jNQXAC9IVRw\",\"state\":1,\"list\":\"PLabcdefghijklmnopq\",\"index\":4,\"count\":25}");
        Assert.False(next.Skipping);
        Assert.Null(next.Error);

        var (_, last) = VideoMessages.Apply(next, "{\"ev\":\"error\",\"code\":150,\"id\":\"jNQXAC9IVRw\"}");
        Assert.False(last.Skipping);   // the last entry: nothing to move on to
        var (_, single) = VideoMessages.Apply(opening, "{\"ev\":\"state\",\"id\":\"aqz-KE-bpKQ\",\"state\":1,\"index\":-1,\"count\":0}");
        Assert.Equal((null, null, null), (single.PlaylistId, single.PlaylistEntry, single.PlaylistCount));
    }

    [Theory]
    [InlineData("{\"ev\":\"page\"}", VideoPageEvent.Page)]
    [InlineData("{\"ev\":\"ready\"}", VideoPageEvent.Ready)]
    [InlineData("{\"ev\":\"echo\",\"value\":\"neon\"}", VideoPageEvent.Echo)]
    [InlineData("{\"ev\":\"jserror\",\"m\":\"boom\"}", VideoPageEvent.Other)]
    [InlineData("{\"cmd\":\"play\"}", VideoPageEvent.Other)]
    [InlineData("[1,2]", VideoPageEvent.Other)]
    [InlineData("not json", VideoPageEvent.Other)]
    [InlineData("", VideoPageEvent.Other)]
    [InlineData(null, VideoPageEvent.Other)]
    public void Apply_AnythingElse_LeavesTheSnapshotAsItIs(string? message, VideoPageEvent expected)
    {
        var opening = VideoSnapshot.Opening(Bunny);

        var (kind, snapshot) = VideoMessages.Apply(opening, message);

        Assert.Equal(expected, kind);
        Assert.Same(opening, snapshot);
    }

    /// <summary>What the page cannot be trusted to send right: a missing or wrong-typed field is a default, never a throw.</summary>
    [Fact]
    public void Apply_AStateWithOddFields_TakesTheDefaults()
    {
        var current = new VideoSnapshot(VideoState.Playing, Bunny, Volume: 60);

        var (_, snapshot) = VideoMessages.Apply(current, "{\"ev\":\"state\",\"state\":\"1\",\"position\":-2,\"duration\":null,\"volume\":\"loud\",\"muted\":1}");

        Assert.Equal(new VideoSnapshot(VideoState.Unstarted, Bunny, Volume: 60, Version: 1), snapshot);
    }

    [Theory]
    [InlineData(-1.0, VideoState.Unstarted)]
    [InlineData(0.0, VideoState.Ended)]
    [InlineData(1.0, VideoState.Playing)]
    [InlineData(2.0, VideoState.Paused)]
    [InlineData(3.0, VideoState.Buffering)]
    [InlineData(5.0, VideoState.Cued)]
    [InlineData(4.0, VideoState.Unstarted)]
    [InlineData(null, VideoState.Unstarted)]
    public void StateOf_IsYouTubesPlayerState(double? state, VideoState expected)
    {
        Assert.Equal(expected, VideoMessages.StateOf(state));
    }

    // ── VideoKeys ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ViewerState.VkF11, false, false, false, VideoKeyAction.ToggleFullScreen)]
    [InlineData(ViewerState.VkF11, true, false, false, VideoKeyAction.ToggleFullScreen)]
    [InlineData(ViewerState.VkEscape, true, false, false, VideoKeyAction.LeaveFullScreen)]
    [InlineData(ViewerState.VkEscape, false, false, false, VideoKeyAction.Close)]
    [InlineData(ViewerState.VkEscape, false, true, false, VideoKeyAction.None)]   // a chord: the chat's
    [InlineData(ViewerState.VkF11, false, false, true, VideoKeyAction.None)]
    [InlineData(0x20, false, false, false, VideoKeyAction.None)]   // Space: the player's
    [InlineData(0x25, false, false, false, VideoKeyAction.None)]   // ←: the player's
    [InlineData(0x09, false, false, false, VideoKeyAction.None)]   // TAB types a character: the page's
    public void ActionFor_IsTheViewersRules_TheRestThePlayers(int key, bool fullScreen, bool control, bool alt, VideoKeyAction expected)
    {
        Assert.Equal(expected, VideoKeys.ActionFor(key, fullScreen, control, alt));
    }

    // ── VideoText ────────────────────────────────────────────────────────────

    [Fact]
    public void Title_IsTheVideosThenTheApps()
    {
        Assert.Equal("NeonSidekick video", VideoText.Title(null));
        Assert.Equal("NeonSidekick video", VideoText.Title("  "));
        Assert.Equal("Big Buck Bunny · NeonSidekick video", VideoText.Title(" Big Buck Bunny "));
    }

    [Fact]
    public void Failed_NamesTheStep_AndTheProfileInUse()
    {
        Assert.Equal("The video window's browser could not start (WebView2 Controller, 0x80004005).", VideoText.Failed("Controller", unchecked((int)0x80004005)));
        Assert.Contains("profile folder is in use by another copy of the app", VideoText.Failed("Environment", unchecked((int)0x8007139F)));
        Assert.Contains("open_url", VideoText.NoRuntime);
        Assert.Contains("WebView2Loader.dll", VideoText.NoLoader);
    }
}
