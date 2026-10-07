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

    private YouTubeSearchTool Search() => new(_search, () => _settings);

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
        Assert.StartsWith("Error: \"action\" must be one of play, pause, seek, forward, back, volume, mute, unmute, close, not \"rewind\".", YouTubeText.NotAnAction("rewind", YouTubeControlTool.Actions), StringComparison.Ordinal);
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

        Assert.Equal(["youtube_search"], headless.Select(t => t.Name));
        Assert.Equal(["youtube_search", "youtube_play", "youtube_control", "youtube_status"], screen.Select(t => t.Name));
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

        Assert.Equal(["youtube_search", "youtube_play", "youtube_control", "youtube_status"], ChatScreen.ComposeTurnTools(inputs).Tools.Select(t => t.Name));
        Assert.Empty(ChatScreen.ComposeTurnTools(inputs with { YouTubeEnabled = false }).Tools);
        Assert.Equal(["youtube_search", "youtube_status"], ChatScreen.ComposeTurnTools(inputs with { PlanReadOnly = true }).Tools.Select(t => t.Name));
        Assert.Equal(["youtube_search", "youtube_status"], ChatScreen.ComposeTurnTools(inputs with { Disabled = new HashSet<string> { "youtube_play", "youtube_control" } }).Tools.Select(t => t.Name));
        Assert.All(tools, t => Assert.True(PlanTools.ReadOnly.Contains(t.Name) ^ PlanTools.Mutating.Contains(t.Name), t.Name));
    }

    private sealed class StubSearch : IYouTubeSearch
    {
        public IReadOnlyList<YouTubeHit> Hits { get; set; } = [];

        public YouTubeSearchOutcome? Outcome { get; set; }

        public (string Query, int Max, string Key) Last { get; private set; }

        public Task<YouTubeSearchOutcome> SearchAsync(string query, int max, string apiKey, CancellationToken cancellationToken)
        {
            Last = (query, max, apiKey);
            return Task.FromResult(Outcome ?? new YouTubeSearchOutcome(Hits));
        }

        public Task<YouTubeVideoInfo?> LookupAsync(string videoId, string? apiKey, CancellationToken cancellationToken) => Task.FromResult<YouTubeVideoInfo?>(null);
    }
}
