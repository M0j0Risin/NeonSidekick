using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tools (2026-10-05, the YouTube plan): the four over a stub search and <see cref="FakeVideoPlayer"/> — what each
/// answers, how each refuses, the wait for the player to show a change — and how the group is offered and composed.
/// </summary>
public class YouTubeToolsTests
{
    private const string Bunny = "aqz-KE-bpKQ";
    private readonly FakeVideoPlayer _player = new();
    private readonly StubSearch _search = new();
    private readonly ManualTimeProvider _time = new();
    private AppSettingsData _settings = new() { YouTubeTools = true, YouTubeApiKey = "AIza-k" };

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] pairs) =>
        (string)(await tool.InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private YouTubeSearchTool Search() => new(_search, () => _settings, _time);

    private YouTubePlaylistTool Playlist(IVideoPlayer? player = null) => new(_search, () => _settings, player);

    private YouTubePlayTool Play() => new(_player, () => _settings, _time);

    private YouTubeControlTool Control() => new(_player, _time);

    // ── youtube_search ───────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ListsTheHits_WithTheirIds_AtTheSettingsCount()
    {
        _settings.YouTubeSearchMaxResults = 2;
        _search.Hits = [new YouTubeHit(Bunny, "Big Buck Bunny", "Blender", new DateTimeOffset(2014, 11, 10, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(635), 21_543_678), new YouTubeHit("jfKfPfyJRdk", "lofi hip hop radio", "Lofi Girl", null, null, 947, YouTubeLive.Live)];

        string result = await Call(Search(), ("query", " big buck bunny "));

        Assert.Equal("YouTube: 2 videos for \"big buck bunny\":\n1. Big Buck Bunny — Blender · 10:35 · 21M views · 2014 · id aqz-KE-bpKQ\n2. lofi hip hop radio — Lofi Girl · live · 947 views · id jfKfPfyJRdk", result);
        Assert.Equal(("big buck bunny", 2, "AIza-k"), _search.Last);
    }

    [Fact]
    public async Task Search_Refuses_ABadCount_AnEmptyQuery_AndSaysWhyTheApiRefused()
    {
        Assert.Equal("Error: \"max\" must be 1 to 20.", await Call(Search(), ("query", "x"), ("max", 50)));
        Assert.Equal("Error: \"query\" is empty; say what to look for.", await Call(Search(), ("query", "  ")));
        _search.Outcome = YouTubeSearchOutcome.Failed(YouTubeFailure.Quota, "HTTP 403: quota");
        Assert.Equal(YouTubeText.Failure(YouTubeFailure.Quota, "HTTP 403: quota"), await Call(Search(), ("query", "x"), ("max", 3)));
        Assert.Equal(3, _search.Last.Max);
        Assert.Equal("YouTube: no videos for \"zzz\".", YouTubeText.Results("zzz", []));
    }

    /// <summary>
    /// The filters (2026-10-08): each given one goes to the search, laid over the user's saved choices; one left out keeps the saved
    /// one; a playlist search lists playlists with their ids.
    /// </summary>
    [Fact]
    public async Task Search_TakesTheFilters_OverTheSavedChoices()
    {
        _settings.YouTubeSearchOrder = "views";
        _settings.YouTubeRegion = "gb";
        _settings.YouTubeSafeSearch = "strict";

        await Call(Search(), ("query", "lofi"));
        var saved = _search.LastFilters!;
        Assert.Equal((YouTubeKind.Video, "views", "GB", "", "strict"), (saved.Kind, saved.Order, saved.Region, saved.Language, saved.SafeSearch));

        await Call(Search(), ("query", "lofi"), ("order", "date"), ("since", "week"), ("language", "EN"), ("length", "long"), ("live", true), ("captions", "true"), ("safe_search", "none"));
        var given = _search.LastFilters!;
        Assert.Equal(("date", "en", "long", true, true, "none", "GB"), (given.Order, given.Language, given.Length, given.Live, given.Captions, given.SafeSearch, given.Region));
        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromDays(7), given.Since);

        _search.Hits = [new YouTubeHit("PLabcdefghijklmnop", "Chill beats", "Lofi Girl", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, Kind: YouTubeKind.Playlist, Count: 42)];
        Assert.Equal("YouTube: 1 playlist for \"lofi\":\n1. Chill beats — Lofi Girl · 42 videos · 2020 · playlist PLabcdefghijklmnop", await Call(Search(), ("query", "lofi"), ("kind", "playlist")));
        Assert.Equal(YouTubeKind.Playlist, _search.LastFilters!.Kind);
    }

    [Fact]
    public async Task Search_RefusesABadFilter_BeforeAsking()
    {
        _search.Calls = 0;
        Assert.Equal(YouTubeText.VideoOnlyFilter, await Call(Search(), ("query", "x"), ("kind", "channel"), ("live", true)));
        Assert.Equal(YouTubeText.NotASince("someday"), await Call(Search(), ("query", "x"), ("since", "someday")));
        Assert.Equal(YouTubeText.NotARegion("USA"), await Call(Search(), ("query", "x"), ("region", "USA")));
        Assert.Equal(YouTubeText.NotALanguage("english"), await Call(Search(), ("query", "x"), ("language", "english")));
        Assert.Equal(YouTubeText.NotOneOf("order", "newest", YouTubeSearchFilters.Orders), await Call(Search(), ("query", "x"), ("order", "newest")));
        Assert.Equal(YouTubeText.NotOneOf("kind", "song", YouTubeSearchChoices.Kinds), await Call(Search(), ("query", "x"), ("kind", "song")));
        Assert.Equal(Files.FileText.BadBoolean("live", "maybe"), await Call(Search(), ("query", "x"), ("live", "maybe")));
        Assert.Equal(0, _search.Calls);
    }

    [Fact]
    public void Results_WordEachKind()
    {
        var channel = new YouTubeHit("UCSMOQeBJ2RAnuFungnQOxLg", "Blender", "Blender", null, null, null, Kind: YouTubeKind.Channel, Count: 340, Subscribers: 1_234_567);
        Assert.Equal("YouTube: 1 channel for \"blender\":\n1. Blender · 1.2M subscribers · 340 videos · channel UCSMOQeBJ2RAnuFungnQOxLg", YouTubeText.Results("blender", [channel], YouTubeKind.Channel));
        Assert.Equal(["Blender  1.2M subscribers  340 videos"], YouTubeText.PickRows([channel]));
        Assert.Equal("YouTube: no playlists for \"zzz\".", YouTubeText.Results("zzz", [], YouTubeKind.Playlist));
        var video = new YouTubeHit(Bunny, "Big Buck Bunny", "Blender", new DateTimeOffset(2014, 11, 10, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(635), 21_543_678);
        Assert.Equal(["Big Buck Bunny  Blender  10:35  21M views"], YouTubeText.PickRows([video]));
        Assert.Equal("Blender · 10:35 · 21M views · 2014 · id aqz-KE-bpKQ", YouTubeText.PickFooter(video).Last);
    }

    /// <summary>
    /// The pickers' rows in columns (2026-10-09, the user's ask: the saved list's way): each column as wide as its widest cell, the
    /// title cut at the saved list's width with an ellipsis (the footer has it whole), the numbers right-aligned; a playlist's entries
    /// numbered on the right of their column, a deleted one in brackets with no channel.
    /// </summary>
    [Fact]
    public void PickRows_LineUpInColumns()
    {
        string longTitle = new string('x', YouTubeText.SavedTitleWidth + 10);
        var rows = YouTubeText.PickRows(
        [
            new YouTubeHit("aaaaaaaaaaa", "Short", "Blender", null, TimeSpan.FromSeconds(635), 21_543_678),
            new YouTubeHit("bbbbbbbbbbb", longTitle, "MorgueOfficial", null, TimeSpan.FromSeconds(3736), 698_000),
        ]);

        Assert.Equal("Short" + new string(' ', YouTubeText.SavedTitleWidth - 5) + "  Blender           10:35   21M views", rows[0]);
        Assert.Equal(new string('x', YouTubeText.SavedTitleWidth - 1) + "…  MorgueOfficial  1:02:16  698K views", rows[1]);

        var entries = YouTubeText.PlaylistPickRows(
        [
            new YouTubePlaylistEntry(0, "aaaaaaaaaaa", "Big Buck Bunny", "Blender"),
            new YouTubePlaylistEntry(9, "bbbbbbbbbbb", "Deleted video", "", Available: false),
        ]);

        Assert.Equal([" 1.  Big Buck Bunny   Blender", "10.  (deleted video)"], entries);
    }

    // ── youtube_play ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Play_ByALink_StartsAtItsTime_AndAnswersWhatThePlayerReports()
    {
        string result = await Call(Play(), ("video", "https://youtu.be/aqz-KE-bpKQ?t=30"));

        Assert.Equal(new VideoRequest(Bunny, 30), Assert.Single(_player.Plays));
        Assert.Equal("Playing \"Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film\" (Blender) at 0:30 of 10:35, volume 100% · video aqz-KE-bpKQ", result);
    }

    [Fact]
    public async Task Play_StartOverridesTheLink_AndAutoplayOffCues()
    {
        _settings.YouTubeAutoplay = false;

        string result = await Call(Play(), ("video", "https://www.youtube.com/watch?v=aqz-KE-bpKQ&t=30"), ("start", "1:30"));

        Assert.Equal(new VideoRequest(Bunny, 90, autoplay: false), Assert.Single(_player.Plays));
        Assert.StartsWith("Cued (press play to start) \"Big Buck Bunny", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Play_RefusesWhatIsNotAVideo_OrATime_AndSaysWhyNoWindowOpened()
    {
        Assert.Equal(YouTubeText.NotAVideo("lofi"), await Call(Play(), ("video", "lofi")));
        Assert.Equal(YouTubeText.NotATime("start", "soon"), await Call(Play(), ("video", Bunny), ("start", "soon")));
        Assert.Empty(_player.Plays);

        _player.PlayFailure = VideoText.NoRuntime;
        Assert.Equal("Error: " + VideoText.NoRuntime, await Call(Play(), ("video", Bunny)));
    }

    /// <summary>YouTube's refusal (150: embedding off, private, age-restricted or missing) is its sentence, pointing at open_url.</summary>
    [Fact]
    public async Task Play_ARefusedVideo_SaysSo_WithTheWatchPage()
    {
        _player.Behave = false;
        _player.Changed += s =>
        {
            if (s is { State: VideoState.Opening, Error: null })
            {
                _player.Report(s with { State = VideoState.Unstarted, Error = 150 });
            }
        };

        string result = await Call(Play(), ("video", "aaaaaaaaaaa"));

        Assert.Equal(YouTubeText.Refused("aaaaaaaaaaa", 150), result);
        Assert.Contains("open_url can open https://www.youtube.com/watch?v=aaaaaaaaaaa", result);
        Assert.Contains("embedding off, or it is private, age-restricted or unavailable", result);
    }

    /// <summary>
    /// A late error for the video before (2026-10-06, the code review's catch) does not end the new play's wait as its refusal:
    /// the play waits on for the video it asked for.
    /// </summary>
    [Fact]
    public async Task Play_ALateErrorForTheVideoBefore_IsNotThisVideosRefusal()
    {
        const string Before = "bbbbbbbbbbb";
        _player.Behave = false;
        bool once = false;
        _player.Changed += s =>
        {
            if (!once && s is { State: VideoState.Opening, VideoId: Bunny })
            {
                once = true;
                _player.Report(s with { VideoId = Before, Error = 150 });                      // the old video's error, late
            }
        };

        var call = Call(Play(), ("video", Bunny));
        Assert.False(call.IsCompleted);                                                         // still waiting for its own video
        _player.Report(new VideoSnapshot(VideoState.Playing, Bunny, "Bunny", "Blender", 0, 635));
        string result = await call;

        Assert.StartsWith("Playing", result, StringComparison.Ordinal);
        Assert.DoesNotContain(Before, result);
    }

    /// <summary>
    /// A new window's first report is the player's ready one, cued, just before its play shows (2026-10-07, found in the Mac live run
    /// and confirmed on Windows): with autoplay on it is not the answer; the playing report after it is.
    /// </summary>
    [Fact]
    public async Task Play_WithAutoplay_TheReadyReportsCued_IsNotTheAnswer()
    {
        _player.Behave = false;

        var call = Call(Play(), ("video", Bunny));
        var opening = _player.Snapshot!;
        _player.Report(new VideoSnapshot(VideoState.Cued, Bunny, "Big Buck Bunny", "Blender", 0, 635, Version: opening.Version + 1));
        Assert.False(call.IsCompleted);                                                         // the ready report: not yet playing
        _player.Report(new VideoSnapshot(VideoState.Playing, Bunny, "Big Buck Bunny", "Blender", 0.01, 635, Version: opening.Version + 2));

        Assert.StartsWith("Playing \"Big Buck Bunny\"", await call, StringComparison.Ordinal);
    }

    /// <summary>A play the browser keeps from starting stays cued: at the cap that is the answer, and the truth.</summary>
    [Fact]
    public async Task Play_WithAutoplay_ThatStaysCued_AnswersCuedAtTheCap()
    {
        _player.Behave = false;

        var call = Call(Play(), ("video", Bunny));
        _player.Report(new VideoSnapshot(VideoState.Cued, Bunny, "Big Buck Bunny", "Blender", 0, 635, Version: _player.Snapshot!.Version + 1));
        Assert.False(call.IsCompleted);
        _time.Advance(YouTubeWait.Play);

        Assert.StartsWith("Cued (press play to start) \"Big Buck Bunny\"", await call, StringComparison.Ordinal);
    }

    /// <summary>A player that never starts: at the cap the newest report answers (still opening, so status says to look later).</summary>
    [Fact]
    public async Task Play_AWindowThatNeverStarts_AnswersAtTheCap()
    {
        _player.Behave = false;

        var call = Call(Play(), ("video", Bunny));
        _time.Advance(YouTubeWait.Play);

        Assert.Equal(YouTubeText.Status(VideoSnapshot.Opening(Bunny)), await call);
        Assert.StartsWith("Opening video aqz-KE-bpKQ in the video window", await call, StringComparison.Ordinal);
    }

    // ── Playlists (2026-10-08) ───────────────────────────────────────────────

    /// <summary>A playlist link as the video, or named as the playlist, plays the list from its place; no saved place is resumed in one.</summary>
    [Fact]
    public async Task Play_APlaylist_ByItsLink_OrNamed_FromItsPlace()
    {
        const string List = "PLabcdefghijklmnopq";

        string result = await Call(Play(), ("video", "https://www.youtube.com/playlist?list=" + List));
        Assert.Equal(VideoRequest.Playlist(List), _player.Plays[^1]);
        Assert.EndsWith(" · video aqz-KE-bpKQ · entry 1 of 3 in playlist " + List, result);

        result = await Call(Play(), ("playlist", List), ("entry", 2));
        Assert.Equal(VideoRequest.Playlist(List, 1), _player.Plays[^1]);
        Assert.EndsWith(" · entry 2 of 3 in playlist " + List, result);

        await Call(Play(), ("video", "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=" + List + "&t=20"));
        Assert.Equal(VideoRequest.Playlist(List, 0, "dQw4w9WgXcQ", 20), _player.Plays[^1]);

        await Call(Play(), ("playlist", "UCSMOQeBJ2RAnuFungnQOxLg"));                         // a channel: its uploads
        Assert.Equal("UUSMOQeBJ2RAnuFungnQOxLg", _player.Plays[^1].PlaylistId);

        Assert.Equal(YouTubeText.NotAPlaylist("lofi"), await Call(Play(), ("playlist", "lofi")));
        Assert.Equal(YouTubeText.NotAnEntry("0", null), await Call(Play(), ("playlist", List), ("entry", 0)));
        Assert.Equal(4, _player.Plays.Count);
    }

    /// <summary>An entry the page is moving past (skip) ends no wait: the play answers with the entry that plays.</summary>
    [Fact]
    public async Task Play_APlaylist_WaitsPastASkippedEntry()
    {
        const string List = "PLabcdefghijklmnopq";
        _player.Behave = false;

        var call = Call(Play(), ("playlist", List));
        var opening = _player.Snapshot!;
        _player.Report(opening with { VideoId = "aaaaaaaaaaa", Error = 150, Skipping = true, Version = opening.Version + 1 });
        Assert.False(call.IsCompleted);
        _player.Report(new VideoSnapshot(VideoState.Playing, "jNQXAC9IVRw", "Me at the zoo", "jawed", 0, 19, PlaylistId: List, PlaylistEntry: 1, PlaylistCount: 3));

        Assert.StartsWith("Playing \"Me at the zoo\"", await call, StringComparison.Ordinal);
        Assert.EndsWith(" The playlist moves on to the next entry.", YouTubeText.Status(opening with { VideoId = "aaaaaaaaaaa", Error = 150, Skipping = true }));
    }

    /// <summary>
    /// The real player's first report on a playlist's move is Paused (2026-10-09, traced in the video window): switching an open
    /// window to a playlist, and next, report Paused — next's with the new entry's number beside the old video's id — before they
    /// buffer and play. With autoplay on neither wait ends on it; each answers with the entry playing.
    /// </summary>
    [Fact]
    public async Task APlaylistsMove_IsNotAnsweredWithTheTransientPause()
    {
        const string List = "UUBR8-60-B28hp2BmDPdntcQ";
        _player.Behave = false;

        var play = Call(Play(), ("playlist", List));
        var opening = _player.Snapshot!;
        _player.Report(new VideoSnapshot(VideoState.Paused, "SLUoz187oSA", "Entry one", "YouTube", 0, 45, PlaylistId: List, PlaylistEntry: 0, PlaylistCount: 200, Version: opening.Version + 1));
        Assert.False(play.IsCompleted);
        _player.Report(new VideoSnapshot(VideoState.Playing, "SLUoz187oSA", "Entry one", "YouTube", 0.1, 45, PlaylistId: List, PlaylistEntry: 0, PlaylistCount: 200));
        Assert.StartsWith("Playing \"Entry one\"", await play, StringComparison.Ordinal);

        var next = Call(Control(), ("action", "next"));
        _player.Report(_player.Snapshot! with { State = VideoState.Paused, PlaylistEntry = 1 });   // the old video, the new number
        Assert.False(next.IsCompleted);
        _player.Report(new VideoSnapshot(VideoState.Playing, "1yfCBYdjLHU", "Entry two", "YouTube", 0.1, 30, PlaylistId: List, PlaylistEntry: 1, PlaylistCount: 200));
        string moved = await next;
        Assert.StartsWith("Playing \"Entry two\"", moved, StringComparison.Ordinal);
        Assert.Contains("entry 2 of 200", moved);
    }

    [Fact]
    public async Task Control_NextPreviousAndEntry_MoveThroughThePlaylist()
    {
        await Call(Play(), ("video", Bunny));
        Assert.Equal(YouTubeText.NoPlaylist, await Call(Control(), ("action", "next")));

        await Call(Play(), ("playlist", "PLabcdefghijklmnopq"));
        Assert.Contains("entry 2 of 3", await Call(Control(), ("action", "next")));
        Assert.Contains("entry 3 of 3", await Call(Control(), ("action", "next")));
        Assert.Contains("entry 2 of 3", await Call(Control(), ("action", "previous")));
        Assert.Contains("entry 1 of 3", await Call(Control(), ("action", "entry"), ("value", "1")));
        Assert.Equal(YouTubeText.NotAnEntry("4", 3), await Call(Control(), ("action", "entry"), ("value", "4")));
        Assert.Equal(YouTubeText.NotAnEntry("first", 3), await Call(Control(), ("action", "entry"), ("value", "first")));
        Assert.Equal([VideoCommand.Next, VideoCommand.Next, VideoCommand.Previous, VideoCommand.PlayAt(0)], _player.Commands);
    }

    [Fact]
    public async Task PlaylistTool_ListsTheEntries_TheOneNamed_OrTheOnePlaying()
    {
        const string List = "PLabcdefghijklmnopq";
        _search.Playlist = new YouTubePlaylistOutcome(new YouTubePlaylist(List, "Shorts", "Blender", 3),
            [new YouTubePlaylistEntry(0, Bunny, "Big Buck Bunny", "Blender"), new YouTubePlaylistEntry(1, "jNQXAC9IVRw", "Deleted video", "", Available: false)]);

        Assert.Equal(YouTubeText.NoPlaylistNamed, await Call(Playlist(_player)));
        Assert.Equal(YouTubeText.NotAPlaylist("lofi"), await Call(Playlist(), ("playlist", "lofi")));
        Assert.Equal(YouTubeText.BadPlaylistMax, await Call(Playlist(), ("playlist", List), ("max", 500)));

        string listed = await Call(Playlist(), ("playlist", "https://www.youtube.com/playlist?list=" + List), ("max", 2));
        Assert.Equal(
            "Playlist \"Shorts\" by Blender: 3 videos, the first 2 listed; youtube_play with playlist " + List + " and an entry number plays from it:\n" +
            "1. Big Buck Bunny — Blender · id aqz-KE-bpKQ\n2. (deleted video, which the player skips)",
            listed);
        Assert.Equal((List, 2), _search.LastPlaylist);

        await Call(Play(), ("playlist", "PLzyxwvutsrqponmlk"));
        await Call(Playlist(_player));
        Assert.Equal(("PLzyxwvutsrqponmlk", YouTubeDataApi.DefaultPlaylistEntries), _search.LastPlaylist);

        _search.Playlist = YouTubePlaylistOutcome.Failed(YouTubeFailure.NotFound);
        Assert.Equal(YouTubeText.Failure(YouTubeFailure.NotFound, ""), await Call(Playlist(), ("playlist", List)));
    }

    // ── youtube_control ──────────────────────────────────────────────────────

    [Fact]
    public async Task Control_WithoutAWindow_SaysToPlayFirst()
    {
        Assert.Equal(YouTubeText.NoVideo, await Call(Control(), ("action", "pause")));
        Assert.Empty(_player.Commands);
    }

    [Fact]
    public async Task Control_EachAction_SendsItsCommand_AndAnswersTheNewState()
    {
        await Call(Play(), ("video", Bunny), ("start", "60"));

        Assert.StartsWith("Paused", await Call(Control(), ("action", "pause")), StringComparison.Ordinal);
        Assert.Contains(" at 1:30 of ", await Call(Control(), ("action", "seek"), ("value", "1:30")));
        Assert.Contains(" at 1:40 of ", await Call(Control(), ("action", "forward")));               // 10 s by default
        Assert.Contains(" at 1:10 of ", await Call(Control(), ("action", "back"), ("value", 30)));
        Assert.Contains(" at 0:00 of ", await Call(Control(), ("action", "back"), ("value", "5m")));  // never before the start
        Assert.Contains(" at 10:35 of ", await Call(Control(), ("action", "forward"), ("value", "1h"))); // never past the end
        Assert.Contains("volume 40%", await Call(Control(), ("action", "volume"), ("value", "40%")));
        Assert.EndsWith("volume 40%, muted · video aqz-KE-bpKQ", await Call(Control(), ("action", "mute")));
        Assert.DoesNotContain("muted", await Call(Control(), ("action", "unmute")));
        Assert.StartsWith("Playing", await Call(Control(), ("action", "PLAY")), StringComparison.Ordinal);
        Assert.Equal(
            [VideoCommand.Pause, VideoCommand.Seek(90), VideoCommand.Seek(100), VideoCommand.Seek(70), VideoCommand.Seek(0), VideoCommand.Seek(635), VideoCommand.Volume(40), VideoCommand.Mute, VideoCommand.Unmute, VideoCommand.Play],
            _player.Commands);

        Assert.Equal(YouTubeText.Closed, await Call(Control(), ("action", "close")));
        Assert.Equal(1, _player.Closes);
        Assert.Null(_player.Snapshot);
    }

    [Fact]
    public async Task Control_RefusesABadAction_Time_OrVolume()
    {
        await Call(Play(), ("video", Bunny));

        Assert.Equal(YouTubeText.NotAnAction("rewind", YouTubeControlTool.Actions), await Call(Control(), ("action", "rewind")));
        Assert.Equal(YouTubeText.NotATime("value", ""), await Call(Control(), ("action", "seek")));
        Assert.Equal(YouTubeText.NotATime("value", "later"), await Call(Control(), ("action", "forward"), ("value", "later")));
        Assert.Equal(YouTubeText.NotAVolume("140"), await Call(Control(), ("action", "volume"), ("value", "140")));
        Assert.Equal(YouTubeText.NotAVolume("loud"), await Call(Control(), ("action", "volume"), ("value", "loud")));
        Assert.Empty(_player.Commands);
        Assert.StartsWith("Error: \"action\" must be one of play, pause, seek, forward, back, volume, mute, unmute, next, previous, entry, close, not \"rewind\".", YouTubeText.NotAnAction("rewind", YouTubeControlTool.Actions), StringComparison.Ordinal);
    }

    /// <summary>The page's first report after a command still shows the old state: the tool waits for one that shows the change.</summary>
    [Fact]
    public async Task Control_WaitsPastAStaleReport_ForTheChange()
    {
        await Call(Play(), ("video", Bunny));
        _player.Behave = false;
        var playing = _player.Snapshot!;

        var call = Call(Control(), ("action", "pause"));
        _player.Report(playing);                                   // the stale report, as the harness saw
        Assert.False(call.IsCompleted);
        _player.Report(playing with { State = VideoState.Paused });

        Assert.StartsWith("Paused", await call, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Control_TheWindowClosedMeanwhile_SaysSo()
    {
        await Call(Play(), ("video", Bunny));
        _player.Behave = false;

        var call = Call(Control(), ("action", "pause"));
        _player.CloseByUser();

        Assert.Equal(YouTubeText.ClosedMeanwhile, await call);
    }

    // ── youtube_status, the words ────────────────────────────────────────────

    [Fact]
    public async Task Status_IsTheLastReport_OrNone()
    {
        var status = new YouTubeStatusTool(_player);
        Assert.Equal(YouTubeText.NoVideo, await Call(status));

        _player.Report(new VideoSnapshot(VideoState.Playing, "jfKfPfyJRdk", "lofi hip hop radio", "Lofi Girl", 3725, 0, 25, true));
        Assert.Equal("Playing \"lofi hip hop radio\" (Lofi Girl) at 1:02:05, volume 25%, muted · video jfKfPfyJRdk", await Call(status));

        _player.Report(new VideoSnapshot(VideoState.Failed, Bunny, Failure: VideoText.Failed("Controller", unchecked((int)0x80004005))));
        Assert.Equal("Error: " + VideoText.Failed("Controller", unchecked((int)0x80004005)), await Call(status));
    }

    [Theory]
    [InlineData(2, "is not a valid video id")]
    [InlineData(5, "an HTML5 player error")]
    [InlineData(100, "found no video")]
    [InlineData(101, "will not play video")]
    [InlineData(150, "will not play video")]
    public void Refused_NamesWhatYouTubeSaid(int code, string words)
    {
        string text = YouTubeText.Refused("aaaaaaaaaaa", code);

        Assert.StartsWith("Error: ", text, StringComparison.Ordinal);
        Assert.Contains(words, text);
        Assert.EndsWith("open_url can open https://www.youtube.com/watch?v=aaaaaaaaaaa in the browser instead.", text);
    }

    [Fact]
    public void Status_WordsEachState()
    {
        Assert.Equal("Paused video aqz-KE-bpKQ, volume 100%", YouTubeText.Status(new VideoSnapshot(VideoState.Paused, Bunny)));
        Assert.StartsWith("Ended ", YouTubeText.Status(new VideoSnapshot(VideoState.Ended, Bunny)), StringComparison.Ordinal);
        Assert.StartsWith("Buffering ", YouTubeText.Status(new VideoSnapshot(VideoState.Buffering, Bunny)), StringComparison.Ordinal);
        Assert.StartsWith("Loaded, not started ", YouTubeText.Status(new VideoSnapshot(VideoState.Unstarted, Bunny)), StringComparison.Ordinal);
    }

    // ── The group ────────────────────────────────────────────────────────────

    [Fact]
    public void TheGroup_IsTheSearchAlone_Headless_AndTheFourWithAWindow_TheSearchCutWithoutAKey()
    {
        var headless = ChatScreen.YouTubeTools(_search, player: null, () => _settings);
        var screen = ChatScreen.YouTubeTools(_search, _player, () => _settings);

        Assert.Equal(["youtube_search", "youtube_playlist"], headless.Select(t => t.Name));
        Assert.Equal(["youtube_search", "youtube_playlist", "youtube_play", "youtube_control", "youtube_status"], screen.Select(t => t.Name));
        Assert.Subset(ChatScreen.YouTubeToolNames.ToHashSet(), screen.Select(t => t.Name).ToHashSet());   // the saved videos' two come with a library (YouTubeSavedToolsTests)

        var noKey = new AppSettingsData { YouTubeTools = true };
        Assert.Equal(["youtube_play", "youtube_control", "youtube_status"], ChatScreen.YouTubeToolsFor(screen, noKey).Select(t => t.Name));
        Assert.True(ChatScreen.YouTubeOffered(noKey, screen));          // playing needs no key
        Assert.False(ChatScreen.YouTubeOffered(noKey, headless));       // nothing left headless without one
        Assert.True(ChatScreen.YouTubeOffered(_settings, headless));
        Assert.False(ChatScreen.YouTubeOffered(new AppSettingsData { YouTubeApiKey = "AIza-k" }, screen));   // the switch off
    }

    [Fact]
    public void ComposeTurnTools_OffersTheGroupAfterTheScreen_AndPlanModeKeepsTheReads()
    {
        var tools = ChatScreen.YouTubeTools(_search, _player, () => _settings);
        var inputs = new TurnToolInputs { Standing = [], YouTube = tools, YouTubeEnabled = true };

        Assert.Equal(["youtube_search", "youtube_playlist", "youtube_play", "youtube_control", "youtube_status"], ChatScreen.ComposeTurnTools(inputs).Tools.Select(t => t.Name));
        Assert.Empty(ChatScreen.ComposeTurnTools(inputs with { YouTubeEnabled = false }).Tools);
        Assert.Equal(["youtube_search", "youtube_playlist", "youtube_status"], ChatScreen.ComposeTurnTools(inputs with { PlanReadOnly = true }).Tools.Select(t => t.Name));
        Assert.Equal(["youtube_search", "youtube_playlist", "youtube_status"], ChatScreen.ComposeTurnTools(inputs with { Disabled = new HashSet<string> { "youtube_play", "youtube_control" } }).Tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.True(PlanTools.ReadOnly.Contains(t.Name) ^ PlanTools.Mutating.Contains(t.Name), t.Name));
    }

    private sealed class StubSearch : IYouTubeSearch
    {
        public IReadOnlyList<YouTubeHit> Hits { get; set; } = [];

        public YouTubeSearchOutcome? Outcome { get; set; }

        public (string Query, int Max, string Key) Last { get; private set; }

        public YouTubeSearchFilters? LastFilters { get; private set; }

        public int Calls { get; set; }

        public YouTubePlaylistOutcome Playlist { get; set; } = YouTubePlaylistOutcome.Failed(YouTubeFailure.NotFound);

        public (string Id, int Max) LastPlaylist { get; private set; }

        public Task<YouTubeSearchOutcome> SearchAsync(string query, int max, YouTubeSearchFilters filters, string apiKey, CancellationToken cancellationToken)
        {
            Calls++;
            Last = (query, max, apiKey);
            LastFilters = filters;
            return Task.FromResult(Outcome ?? new YouTubeSearchOutcome(Hits));
        }

        public Task<YouTubePlaylistOutcome> PlaylistAsync(string playlistId, int max, string apiKey, CancellationToken cancellationToken)
        {
            LastPlaylist = (playlistId, max);
            return Task.FromResult(Playlist);
        }

        public Task<YouTubeVideoInfo?> LookupAsync(string videoId, string? apiKey, CancellationToken cancellationToken) => Task.FromResult<YouTubeVideoInfo?>(null);
    }
}
