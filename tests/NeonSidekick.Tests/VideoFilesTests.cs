using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// Video files (2026-11-12, the video-files plan): <c>play_video</c> and <c>video_control</c> over <see cref="FakeVideoPlayer"/> and a
/// sandbox of their own — what each answers, how each refuses, the wait for the file's own report — the file sentences, <c>/video</c>'s
/// words, the setting's names, the request, the page's messages and the router's refusal.
/// </summary>
public sealed class VideoFilesTests : IDisposable
{
    private const string Bunny = "aqz-KE-bpKQ";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick-video-files-" + Guid.NewGuid().ToString("N"));
    private readonly FakeVideoPlayer _player = new();
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;

    public VideoFilesTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "clip.mp4"), [0]);
        File.WriteAllBytes(Path.Combine(_root, "pause clip.mp4"), [0]);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "notes");
        Directory.CreateDirectory(Path.Combine(_root, "folder.mp4"));
        _files = new WorkingDirectory(() => _root, _time);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] pairs) =>
        (string)(await tool.InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    // The route a test gives, or a play on the fake (ChatScreen.RouteFile's window arm), its refusal thrown as the router's is.
    private PlayVideoTool Play(Func<VideoRequest, VideoRouted>? route = null) =>
        new(_player, _files, _time, route ?? (r => { _player.Play(r); return VideoRouted.Waited; }));

    private VideoControlTool Control(Func<string?>? handoff = null, Func<string?>? youTube = null, IVideoPlayer? player = null) =>
        new(player ?? _player, _time, handoff, youTubePointer: youTube);

    private string Full(string name) => Path.Combine(_root, name);

    // ── play_video ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Play_AFile_PlaysItsFullPath_AndAnswersInAFilesWords()
    {
        string result = await Call(Play(), ("path", "clip.mp4"));

        var request = Assert.Single(_player.Plays);
        Assert.True(request.IsFile);
        Assert.Equal(Full("clip.mp4"), request.FilePath);
        Assert.Equal("clip.mp4", request.FileName);
        Assert.True(request.Autoplay);
        Assert.Equal("Playing \"clip.mp4\" at 0:00 of 10:35, volume 100%", result);
    }

    [Fact]
    public async Task Play_AStartAndNoAutoplay_LoadsPaused_AndAnswersReady()
    {
        string result = await Call(Play(), ("path", "clip.mp4"), ("start", "1:30"), ("autoplay", false));

        var request = Assert.Single(_player.Plays);
        Assert.Equal(90, request.Start);
        Assert.False(request.Autoplay);
        Assert.Equal("Ready (press play to start) \"clip.mp4\" at 1:30 of 10:35, volume 100%", result);
    }

    /// <summary>A name that starts with a verb is a path all the same: <c>pause clip.mp4</c> plays.</summary>
    [Fact]
    public async Task Play_ANameWithABlank_Plays()
    {
        Assert.StartsWith("Playing \"pause clip.mp4\"", await Call(Play(), ("path", "pause clip.mp4")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Play_Refuses_WhatIsNoVideoFile_OrOutsideTheFolder_OrABadArgument()
    {
        Assert.Equal(FileText.PathRequired("path"), await Call(Play()));
        Assert.Equal(VideoText.NotAVideoFile("notes.txt"), await Call(Play(), ("path", "notes.txt")));
        Assert.Equal(VideoText.NotAVideoFile("folder.mp4"), await Call(Play(), ("path", "folder.mp4")));   // a folder so named
        Assert.StartsWith("Error: ", await Call(Play(), ("path", "gone.mp4")), StringComparison.Ordinal);
        Assert.StartsWith("Error: ", await Call(Play(), ("path", Path.Combine("..", "outside.mp4"))), StringComparison.Ordinal);
        Assert.Equal(YouTubeText.NotATime("start", "soon"), await Call(Play(), ("path", "clip.mp4"), ("start", "soon")));
        Assert.Equal(FileText.BadBoolean("autoplay", "maybe"), await Call(Play(), ("path", "clip.mp4"), ("autoplay", "maybe")));
        Assert.Empty(_player.Plays);
    }

    [Fact]
    public async Task Play_AWindowThatCantOpen_IsItsError()
    {
        _player.PlayFailure = VideoText.NoRuntime;
        Assert.Equal("Error: " + VideoText.NoRuntime, await Call(Play(), ("path", "clip.mp4")));
    }

    /// <summary>The default app answers at once: the route's line, its note first, nothing played or waited for.</summary>
    [Fact]
    public async Task Play_ThroughTheRoute_TheDefaultAppsLine_IsTheAnswer()
    {
        VideoRequest? routed = null;
        string result = await Call(Play(r => { routed = r; return new VideoRouted(false, "Opened clip.mp4 in the user's video app.", "A note. "); }), ("path", "clip.mp4"));

        Assert.Equal("A note. Opened clip.mp4 in the user's video app.", result);
        Assert.Equal(Full("clip.mp4"), routed!.FilePath);
        Assert.Empty(_player.Plays);
    }

    /// <summary>A report for another play (its token) is never this play's answer; its own is.</summary>
    [Fact]
    public async Task Play_AReportForAnotherFile_IsNotTheAnswer()
    {
        _player.Behave = false;
        var play = Call(Play(), ("path", "clip.mp4"));
        string token = Assert.Single(_player.Plays).FileToken!;
        _player.Report(new VideoSnapshot(VideoState.Playing, null, "other.mp4") { FileName = "other.mp4", FileToken = VideoFile.NewToken() });
        _player.Report(new VideoSnapshot(VideoState.Playing, Bunny, "Big Buck Bunny"));
        Assert.False(play.IsCompleted);

        _player.Report(new VideoSnapshot(VideoState.Playing, null, "clip.mp4", Position: 2, Duration: 10) { FileName = "clip.mp4", FileToken = token });
        Assert.Equal("Playing \"clip.mp4\" at 0:02 of 0:10, volume 100%", await play);
    }

    [Fact]
    public async Task Play_AFilesError_IsItsSentence()
    {
        _player.Behave = false;
        var play = Call(Play(), ("path", "clip.mp4"));
        string token = Assert.Single(_player.Plays).FileToken!;
        _player.Report(_player.Snapshot! with { Error = 4 });

        Assert.Equal("Error: " + VideoText.FileError(4, "clip.mp4"), await play);
        Assert.Equal(token, _player.Snapshot!.FileToken);
    }

    [Fact]
    public void Play_DescribesWhereItGoes()
    {
        Assert.StartsWith("Plays a video file on this computer (not YouTube", PlayVideoTool.DescriptionFor(VideoFilesPlayInKind.VideoWindow), StringComparison.Ordinal);
        Assert.Contains("video window", PlayVideoTool.DescriptionFor(VideoFilesPlayInKind.VideoWindow), StringComparison.Ordinal);
        Assert.Contains("Video tab", PlayVideoTool.DescriptionFor(VideoFilesPlayInKind.WebUiWindow), StringComparison.Ordinal);
        Assert.Contains("can't see or drive it", PlayVideoTool.DescriptionFor(VideoFilesPlayInKind.DefaultApp), StringComparison.Ordinal);
        Assert.All(Enum.GetValues<VideoFilesPlayInKind>(), kind => Assert.EndsWith("Play only what the user asked for.", PlayVideoTool.DescriptionFor(kind), StringComparison.Ordinal));
    }

    /// <summary>A play given up while its checks ran (a share that doesn't answer) never starts (2026-11-13).</summary>
    [Fact]
    public async Task Play_CancelledBeforeItsRoute_NeverRoutes()
    {
        int routes = 0;
        var tool = Play(r => { routes++; return VideoRouted.Waited; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.PlayAsync("clip.mp4", 0, autoplay: true, new CancellationToken(canceled: true)));

        Assert.Equal(0, routes);
        Assert.Empty(_player.Plays);
    }

    /// <summary>
    /// A video window that fails after it opened (its page never came, 2026-11-13) sends the file to its default app through the fallback,
    /// whose hand-off closes the failed window, so <c>video_control status</c> then says where the file went. A file's own error never does.
    /// </summary>
    [Fact]
    public async Task Play_AWindowThatFailsLater_FallsBackToTheDefaultApp_AndStatusSaysWhere()
    {
        var web = new FakeVideoPlayer { Place = VideoPlace.WebTab };
        var router = new RoutedVideoPlayer(_player, web);
        _player.Behave = false;
        VideoRequest? fell = null;
        string? fellNote = null;
        var tool = new PlayVideoTool(router, _files, _time, r => { router.Play(r, VideoSink.Window); return new VideoRouted(true, null, "A note. "); }, fallback: (r, note) =>
        {
            fell = r;
            fellNote = note;
            router.Handoff(new VideoHandoff(r.FilePath!, YouTubeOpenLinksInKind.DefaultBrowser, null, null) { FileName = r.FileName });
            return new VideoRouted(false, "Opened clip.mp4 in the user's video app.", note);
        });

        var play = tool.PlayAsync("clip.mp4", 0, autoplay: true, CancellationToken.None);
        _player.Report(_player.Snapshot! with { State = VideoState.Failed, Failure = VideoText.PageDidNotLoad, Version = _player.Snapshot.Version + 1 });
        string answer = await play;

        string reason = VideoText.DefaultAppFellBack(VideoText.CouldNotPlayIn(VideoFilesPlayInKind.VideoWindow));
        Assert.Equal(Full("clip.mp4"), fell?.FilePath);
        Assert.Equal(reason, fellNote);
        Assert.Equal("A note. " + reason + "Opened clip.mp4 in the user's video app.", answer);
        Assert.Null(router.Snapshot);
        var control = new VideoControlTool(router, _time, () => router.LastHandoff is { IsFile: true } h ? h.FileName : null);
        Assert.Equal(VideoText.HandedOffStatus("clip.mp4"), await Call(control, ("action", "status")));

        // A file's own error is the answer, never a fallback.
        fell = null;
        var again = tool.PlayAsync("clip.mp4", 0, autoplay: true, CancellationToken.None);
        _player.Report(_player.Snapshot! with { Error = 4, Version = _player.Snapshot.Version + 1 });
        Assert.Equal("Error: A note. " + VideoText.FileError(4, "clip.mp4"), await again);
        Assert.Null(fell);
    }

    /// <summary>The screen's instance words its answers for <c>/video</c> (2026-11-13): no tool names.</summary>
    [Fact]
    public async Task Play_ForTheUser_NamesVideoNotTheTools()
    {
        var tool = new PlayVideoTool(_player, _files, _time, r => { _player.Play(r); return VideoRouted.Waited; }, forUser: true);
        Assert.Equal(VideoText.NotAVideoFile("notes.txt", forUser: true), await tool.PlayAsync("notes.txt", 0, true, CancellationToken.None));
        Assert.Contains("/video plays", VideoText.NotAVideoFile("notes.txt", forUser: true), StringComparison.Ordinal);
        Assert.Equal(VideoText.NoFileCommand, await new VideoControlTool(_player, _time, forUser: true).ControlAsync("pause", "", CancellationToken.None));
    }

    // ── video_control ────────────────────────────────────────────────────────

    /// <summary>A page that can't set its volume (an iPhone, 2026-11-13) answers a volume at once, sending nothing; mute still goes.</summary>
    [Fact]
    public async Task Control_AFixedVolume_AnswersAtOnce()
    {
        await Call(Play(), ("path", "clip.mp4"));
        _player.Report(_player.Snapshot! with { FixedVolume = true });

        Assert.Equal(VideoText.FixedVolume, await Call(Control(), ("action", "volume"), ("value", "30")));
        Assert.Empty(_player.Commands);
        Assert.EndsWith(", muted", await Call(Control(), ("action", "mute")), StringComparison.Ordinal);
    }

    /// <summary>"NaN" and "Infinity" read as numbers; both control tools refuse them as a time or a volume (2026-11-13), sending nothing.</summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Control_NonFiniteValues_AreRefused_ByBothTools(string value)
    {
        await Call(Play(), ("path", "clip.mp4"));
        Assert.StartsWith("Error: ", await Call(Control(), ("action", "volume"), ("value", value)), StringComparison.Ordinal);
        Assert.StartsWith("Error: ", await Call(Control(), ("action", "seek"), ("value", value)), StringComparison.Ordinal);
        Assert.Empty(_player.Commands);

        _player.Play(new VideoRequest(Bunny));
        var youTube = new YouTubeControlTool(_player, _time);
        Assert.Equal(YouTubeText.NotAVolume(value), await Call(youTube, ("action", "volume"), ("value", value)));
        Assert.StartsWith("Error: ", await Call(youTube, ("action", "seek"), ("value", value)), StringComparison.Ordinal);
        Assert.Empty(_player.Commands);
    }

    /// <summary>youtube_save keeps YouTube videos: a file open is refused in the model's words (2026-11-13).</summary>
    [Fact]
    public async Task YouTubeSave_AFileOpen_IsRefused()
    {
        await Call(Play(), ("path", "clip.mp4"));
        var library = new YouTubeLibrary(_root, _time);
        var save = new YouTubeSaveTool(() => library, _player);

        Assert.Equal(VideoText.NoSaveForFiles("clip.mp4", forUser: false), await Call(save, ("action", "add"), ("video", "current")));
    }

    [Fact]
    public async Task Control_WithNothingOpen_SaysSo_OrWhereTheLastFileWent()
    {
        Assert.Equal(VideoText.NoFile, await Call(Control(), ("action", "pause")));
        Assert.Equal(VideoText.NoFile, await Call(Control(), ("action", "status")));
        Assert.Equal(VideoText.NoFile, await Call(new VideoControlTool(null, _time), ("action", "pause")));
        Assert.Equal(VideoText.HandedOffControl("clip.mp4"), await Call(Control(handoff: () => "clip.mp4"), ("action", "pause")));
        Assert.Equal(VideoText.HandedOffStatus("clip.mp4"), await Call(Control(handoff: () => "clip.mp4"), ("action", "status")));
        Assert.Equal(YouTubeText.NotAnAction("next", VideoControlTool.Actions), await Call(Control(), ("action", "next")));
    }

    /// <summary>A YouTube video open: youtube_control named only when it is offered; and the reverse from the YouTube tools.</summary>
    [Fact]
    public async Task Control_PointsAtTheOtherFamily_OnlyWhenItIsOffered()
    {
        _player.Play(new VideoRequest(Bunny));
        Assert.Equal("Error: a YouTube video is open, not a video file; youtube_control drives it.", await Call(Control(youTube: () => YouTubeControlTool.ToolName), ("action", "pause")));
        Assert.Equal("Error: a YouTube video is open, not a video file; this drives video files only.", await Call(Control(), ("action", "pause")));

        await Call(Play(), ("path", "clip.mp4"));
        Assert.Equal("Error: a video file is open (clip.mp4), not a YouTube video; video_control drives it.",
            await Call(new YouTubeControlTool(_player, _time, videoPointer: () => VideoControlTool.ToolName), ("action", "pause")));
        Assert.Equal("Error: a video file is open (clip.mp4), not a YouTube video; this drives YouTube videos only.",
            await Call(new YouTubeStatusTool(_player)));
        Assert.Equal(2, _player.Plays.Count);
        Assert.Empty(_player.Commands);
    }

    [Fact]
    public async Task Control_EachAction_WaitsForItsChange()
    {
        await Call(Play(), ("path", "clip.mp4"));

        Assert.Equal("Paused \"clip.mp4\" at 0:00 of 10:35, volume 100%", await Call(Control(), ("action", "pause")));
        Assert.Equal("Playing \"clip.mp4\" at 0:00 of 10:35, volume 100%", await Call(Control(), ("action", "play")));
        Assert.Equal("Playing \"clip.mp4\" at 0:10 of 10:35, volume 100%", await Call(Control(), ("action", "forward")));
        Assert.Equal("Playing \"clip.mp4\" at 1:10 of 10:35, volume 100%", await Call(Control(), ("action", "forward"), ("value", "60")));
        Assert.Equal("Playing \"clip.mp4\" at 1:00 of 10:35, volume 100%", await Call(Control(), ("action", "back")));
        Assert.Equal("Playing \"clip.mp4\" at 10:35 of 10:35, volume 100%", await Call(Control(), ("action", "seek"), ("value", "20:00")));   // held to the length
        Assert.Equal("Playing \"clip.mp4\" at 10:35 of 10:35, volume 30%", await Call(Control(), ("action", "volume"), ("value", "30")));
        Assert.Equal("Playing \"clip.mp4\" at 10:35 of 10:35, volume 30%, muted", await Call(Control(), ("action", "mute")));
        Assert.Equal("Playing \"clip.mp4\" at 10:35 of 10:35, volume 30%", await Call(Control(), ("action", "unmute")));
        Assert.Equal("Playing \"clip.mp4\" at 10:35 of 10:35, volume 30%", await Call(Control(), ("action", "status")));
        Assert.Equal(YouTubeText.NotATime("value", "soon"), await Call(Control(), ("action", "seek"), ("value", "soon")));
        Assert.Equal(YouTubeText.NotAVolume("150"), await Call(Control(), ("action", "volume"), ("value", "150")));

        Assert.Equal(YouTubeText.Closed, await Call(Control(), ("action", "close")));
        Assert.Equal(1, _player.Closes);
    }

    /// <summary>A file waiting for a Video tab: its held words, naming the file, and nothing sent.</summary>
    [Fact]
    public async Task Control_AHeldFile_AnswersItsWait()
    {
        var request = VideoRequest.File(Full("clip.mp4"));
        _player.Report(VideoSnapshot.Opening(request) with { Held = new VideoHold(Nudged: true, _time.GetUtcNow()), Place = VideoPlace.WebTab });

        string held = await Call(Control(), ("action", "pause"));
        Assert.Equal("Waiting for a Video tab: clip.mp4 plays in the Web UI's Video tab as soon as it opens; the open Web UI page was asked to switch to it.", held);
        Assert.Equal(held, await Call(Control(), ("action", "status")));
        Assert.Empty(_player.Commands);
    }

    // ── the file sentences ───────────────────────────────────────────────────

    [Fact]
    public void FileStatus_TakesStatusOrderOfCases()
    {
        var request = VideoRequest.File(Full("clip.mp4"));
        var opening = VideoSnapshot.Opening(request);
        Assert.Equal(VideoText.NoFile, VideoText.FileStatus(null));
        Assert.Equal("Error: it broke", VideoText.FileStatus(opening with { State = VideoState.Failed, Failure = "it broke" }));
        Assert.Equal("Error: " + VideoText.FileError(3, "clip.mp4"), VideoText.FileStatus(opening with { Error = 3 }));
        Assert.Equal("Opening clip.mp4 in the video window; video_control status says when it plays.", VideoText.FileStatus(opening));
        Assert.Equal("Opening clip.mp4 in the Web UI's Video tab; video_control status says when it plays.", VideoText.FileStatus(opening with { Place = VideoPlace.WebTab }));
        Assert.Equal("Loaded, not started \"clip.mp4\", volume 100% · in the Web UI's Video tab; tap the video on the phone to start",
            VideoText.FileStatus(opening with { State = VideoState.Unstarted, Place = VideoPlace.WebTab }));
        Assert.Equal("Playing \"Holiday\" at 0:05, volume 100% · file clip.mp4", VideoText.FileStatus(opening with { State = VideoState.Playing, Title = "Holiday", Position = 5 }));

        // YouTube's own status words a file's snapshot as a file, so its code is never read as YouTube's.
        Assert.Equal(VideoText.FileStatus(opening with { Error = 2 }), YouTubeText.Status(opening with { Error = 2 }));
    }

    [Fact]
    public void FileErrors_OfferTheDefaultApp_ButAGoneFile()
    {
        foreach (int code in (int[])[1, 2, 3, 4, 99])
        {
            Assert.EndsWith(" Open it in its own app instead (the open tool, or /open).", VideoText.FileError(code, "clip.mp4"), StringComparison.Ordinal);
            Assert.EndsWith(" Open it in its own app instead (/open).", VideoText.FileError(code, "clip.mp4", forUser: true), StringComparison.Ordinal);
            Assert.DoesNotContain("own app", VideoText.FileError(code, "clip.mp4", wayOut: false), StringComparison.Ordinal);   // the Home card on the phone
        }

        // No picture is a note after the state line, not an error (2026-11-13); it names both causes, which the page can't tell apart.
        Assert.Equal("clip.mp4 plays sound but no picture: it has no video, or its video codec (HEVC, say) isn't one the player can show. Open it in its own app instead (the open tool, or /open).",
            VideoText.NoPictureNote("clip.mp4"));

        Assert.Equal("clip.mp4 is no longer there: it was moved or deleted.", VideoText.FileError(VideoFile.GoneCode, "clip.mp4"));
        Assert.StartsWith("The video file could not", VideoText.FileError(99, null), StringComparison.Ordinal);
    }

    [Fact]
    public void HandOffWords_AreCapitalized_AndNameTheFile()
    {
        Assert.Equal("Opened clip.mp4 in the user's video app; it plays there, where video_control can't see or control it.",
            VideoText.HandedOff("opened clip.mp4" + VideoText.InVideoApp));
        Assert.Equal("Opened clip.mp4 in the user's video app; it plays there, where /video can't control it.",
            VideoText.HandedOff("opened clip.mp4" + VideoText.InVideoApp, forUser: true));
        Assert.Equal("There is no video window here, so it opens in its default app instead. ", VideoText.DefaultAppFellBack(VideoText.NoWindowHere));

        // A webui-window → window → default app chain keeps both reasons, each naming its place (2026-11-13).
        Assert.Equal("The Web UI isn't serving; the video window couldn't open, so it opens in its default app instead. ",
            VideoText.DefaultAppFellBack(YouTubeText.WebUiNotServing, VideoText.CouldNotPlayIn(VideoFilesPlayInKind.VideoWindow)));
        Assert.Equal("Error: clip.mp4 is a video file, not a YouTube video; /youtube save keeps YouTube videos.", VideoText.NoSaveForFiles("clip.mp4", forUser: true));
        Assert.Equal("Error: clip.mp4 is a video file, not a YouTube video; youtube_save keeps YouTube videos.", VideoText.NoSaveForFiles("clip.mp4", forUser: false));
    }

    /// <summary>A note never hides an error: <c>Error: </c> stays first, the parts after it (2026-11-13).</summary>
    [Fact]
    public void Lead_KeepsAnErrorFirst()
    {
        Assert.Equal("A note. Playing", VideoText.Lead("Playing", "A note. ", null, ""));
        Assert.Equal("Error: A note. Resumed. it broke", VideoText.Lead("Error: it broke", "A note. ", "Resumed. "));
        Assert.Equal("Error: it broke", VideoText.Lead("Error: it broke"));
    }

    // ── /video's words ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("", VideoFileVerb.Status, null, null)]
    [InlineData("status", VideoFileVerb.Status, null, null)]
    [InlineData("clip.mp4", VideoFileVerb.Play, "clip.mp4", null)]
    [InlineData("pause clip.mp4", VideoFileVerb.Play, "pause clip.mp4", null)]
    [InlineData("./pause.MKV", VideoFileVerb.Play, "./pause.MKV", null)]
    [InlineData("  My Videos/a b.webm  ", VideoFileVerb.Play, "My Videos/a b.webm", null)]
    [InlineData("play", VideoFileVerb.Resume, null, null)]
    [InlineData("resume", VideoFileVerb.Resume, null, null)]
    [InlineData("PAUSE", VideoFileVerb.Pause, null, null)]
    [InlineData("seek 1:30", VideoFileVerb.Seek, null, "1:30")]
    [InlineData("seek soon", VideoFileVerb.Seek, null, "soon")]   // the tool's parser judges it (2026-11-13)
    [InlineData("volume 50%", VideoFileVerb.Volume, null, "50%")]
    [InlineData("volume 150", VideoFileVerb.Volume, null, "150")]
    [InlineData("mute", VideoFileVerb.Mute, null, null)]
    [InlineData("unmute", VideoFileVerb.Unmute, null, null)]
    [InlineData("close", VideoFileVerb.Close, null, null)]
    [InlineData("\"pause\"", VideoFileVerb.Play, "pause", null)]   // quoted: always a path
    [InlineData("'my clip.mp4'", VideoFileVerb.Play, "my clip.mp4", null)]
    [InlineData("clip.avi", VideoFileVerb.Play, "clip.avi", null)]   // looks like a file: the tool says it is no video file
    [InlineData("Videos/holiday", VideoFileVerb.Play, "Videos/holiday", null)]
    public void Command_ReadsAPath_OrAVerb(string args, VideoFileVerb verb, string? path, string? value)
    {
        Assert.Equal(new VideoFileLine(verb, path, value), VideoFileCommand.Parse(args));
    }

    [Theory]
    [InlineData("seek")]
    [InlineData("volume")]
    [InlineData("pause now")]
    [InlineData("stop")]
    [InlineData("paus")]
    [InlineData("\"\"")]
    public void Command_WordsItDoesNotKnow_AreTheUsage(string args)
    {
        Assert.Equal(new VideoFileLine(VideoFileVerb.Unknown, Error: VideoText.CommandUsage), VideoFileCommand.Parse(args));
    }

    [Fact]
    public void Command_CompletesTheVerbs_WhileOneIsTyped()
    {
        Assert.True(VideoFileCommand.TypingVerb(""));
        Assert.True(VideoFileCommand.TypingVerb("pa"));
        Assert.True(VideoFileCommand.TypingVerb("seek "));
        Assert.True(VideoFileCommand.TypingVerb("volume 5"));
        Assert.False(VideoFileCommand.TypingVerb("holiday"));
        Assert.False(VideoFileCommand.TypingVerb("pause clip"));
        Assert.Equal(["pause", "play"], VideoFileCommand.Complete("p").Select(i => i.Text).Order(StringComparer.Ordinal));
        Assert.Equal(["resume"], VideoFileCommand.Complete("re").Select(i => i.Text));
        Assert.Equal(["close"], VideoFileCommand.VerbsStarting("c"));
        Assert.Contains("resume", VideoText.CommandUsage, StringComparison.Ordinal);
        Assert.Empty(VideoFileCommand.Complete("seek "));
        Assert.All(Enum.GetValues<VideoFileVerb>().Where(v => v is not (VideoFileVerb.Unknown or VideoFileVerb.Play)), v => Assert.Contains(VideoFileCommand.ActionOf(v)!, VideoControlTool.Actions));
    }

    // ── the setting, the request, the page's messages, the router ────────────

    [Fact]
    public void PlayIn_ReadsItsWords_AnUnknownOneAsTheWindow()
    {
        Assert.Equal(["video-window", "webui-window", "default-app"], VideoFilesPlayIn.Names);
        Assert.Equal(VideoFilesPlayIn.VideoWindow, new AppSettingsData().VideoFilesPlayIn);
        Assert.True(VideoFilesPlayIn.TryParse(" Default-App ", out var kind));
        Assert.Equal(VideoFilesPlayInKind.DefaultApp, kind);
        Assert.All(VideoFilesPlayIn.Names, name => Assert.Equal(name, VideoFilesPlayIn.Name(VideoFilesPlayIn.Resolve(new AppSettingsData { VideoFilesPlayIn = name }))));
        Assert.Equal(VideoFilesPlayInKind.VideoWindow, VideoFilesPlayIn.Resolve(new AppSettingsData { VideoFilesPlayIn = "phone" }));
        Assert.All(VideoFilesPlayIn.Names, name => Assert.NotEmpty(VideoFilesPlayIn.Describe(name)));
        Assert.False(new AppSettingsData().VideoTools);
        Assert.False(new AppSettingsData().WebUiVideoFilesOverFunnel);
    }

    [Fact]
    public void Request_TakesAFullPath_AndATokenOfItsOwn()
    {
        var one = VideoRequest.File(Full("clip.mp4"), 5, autoplay: false);
        var two = VideoRequest.File(Full("clip.mp4"));
        Assert.Equal("clip.mp4", one.FileName);
        Assert.True(VideoFile.IsToken(one.FileToken));
        Assert.NotEqual(one.FileToken, two.FileToken);
        Assert.Throws<ArgumentException>(() => VideoRequest.File("clip.mp4"));
        Assert.Equal("clip.mp4", VideoSnapshot.Opening(one).Title);
    }

    /// <summary>Every file type the player takes also opens in its default app: the fallback is always there.</summary>
    [Fact]
    public void EveryVideoType_IsOnTheOpenList()
    {
        Assert.All(VideoFile.Extensions, ext => Assert.Null(OpenRules.Refusal(Full("clip" + ext), isDirectory: false)));
    }

    [Fact]
    public void Messages_LoadAFile_AndReadOnlyItsOwnReports()
    {
        var request = VideoRequest.File(Full("clip.mp4"), 3);
        using (var load = JsonDocument.Parse(VideoMessages.LoadFile(request, "file:///x/clip.mp4")))
        {
            Assert.Equal("load", load.RootElement.GetProperty("cmd").GetString());
            Assert.Equal(request.FileToken, load.RootElement.GetProperty("file").GetString());
            Assert.Equal("clip.mp4", load.RootElement.GetProperty("name").GetString());
            Assert.Equal(3, load.RootElement.GetProperty("start").GetDouble());
        }

        var opening = VideoSnapshot.Opening(request);
        var (ev, playing) = VideoMessages.Apply(opening, "{\"ev\":\"state\",\"file\":\"" + request.FileToken + "\",\"state\":1,\"position\":4,\"duration\":60,\"volume\":80}");
        Assert.Equal(VideoPageEvent.State, ev);
        Assert.Equal((VideoState.Playing, 4d, 60d, 80, "clip.mp4", request.FileToken), (playing.State, playing.Position, playing.Duration, playing.Volume, playing.FileName, playing.FileToken));

        // Another file's report, or YouTube's, changes nothing.
        Assert.Same(playing, VideoMessages.Apply(playing, "{\"ev\":\"state\",\"file\":\"" + VideoFile.NewToken() + "\",\"state\":2}").Snapshot);
        Assert.Same(playing, VideoMessages.Apply(playing, "{\"ev\":\"state\",\"id\":\"" + Bunny + "\",\"state\":2}").Snapshot);
        // No picture is a flag, never an error (2026-11-13): waits, status and Resume read the state; a later report keeps it, and once is enough.
        var (noPictureEvent, noPicture) = VideoMessages.Apply(playing, "{\"ev\":\"error\",\"file\":\"" + request.FileToken + "\",\"code\":1000}");
        Assert.Equal(VideoPageEvent.NoPicture, noPictureEvent);
        Assert.True(noPicture.NoPicture);
        Assert.Null(noPicture.Error);
        Assert.Equal(VideoState.Playing, noPicture.State);
        Assert.NotEqual(VideoPageEvent.NoPicture, VideoMessages.Apply(noPicture, "{\"ev\":\"error\",\"file\":\"" + request.FileToken + "\",\"code\":1000}").Event);
        var later = VideoMessages.Apply(noPicture, "{\"ev\":\"state\",\"file\":\"" + request.FileToken + "\",\"state\":2,\"position\":5}").Snapshot;
        Assert.True(later.NoPicture);
        Assert.Contains(VideoText.NoPictureNote("clip.mp4"), VideoText.FileStatus(later), StringComparison.Ordinal);

        // A late code 1000 from the file before never flags the new one; a failed while a file is current is dropped.
        Assert.Same(playing, VideoMessages.Apply(playing, "{\"ev\":\"error\",\"file\":\"" + VideoFile.NewToken() + "\",\"code\":1000}").Snapshot);
        Assert.False(VideoMessages.Apply(playing, "{\"ev\":\"failed\",\"reason\":\"x\"}").Snapshot.State == VideoState.Failed);

        // The page says its kind: the file page's own event (2026-11-13: PageKind went).
        Assert.Equal(VideoPageEvent.FilePage, VideoMessages.Apply(playing, "{\"ev\":\"page\",\"kind\":\"file\"}").Event);
        Assert.Equal(VideoPageEvent.Page, VideoMessages.Apply(playing, "{\"ev\":\"page\"}").Event);

        // A volume carried from the video before (2026-11-13): the load says it.
        using (var carried = JsonDocument.Parse(VideoMessages.LoadFile(request, "file:///x/clip.mp4", new VideoSound(25, true))))
        {
            Assert.Equal(25, carried.RootElement.GetProperty("volume").GetInt32());
            Assert.True(carried.RootElement.GetProperty("muted").GetBoolean());
        }
    }

    /// <summary>A sink that plays no files refuses one before anything switches, so the route can send it to the default app.</summary>
    [Fact]
    public void Router_RefusesAFile_OnASinkThatPlaysNone()
    {
        var web = new FakeVideoPlayer { PlaysFiles = false, Place = VideoPlace.WebTab };
        var router = new RoutedVideoPlayer(_player, web);
        Assert.True(router.PlaysFilesOn(VideoSink.Window));
        Assert.False(router.PlaysFilesOn(VideoSink.Web));

        var error = Assert.Throws<InvalidOperationException>(() => router.Play(VideoRequest.File(Full("clip.mp4")), VideoSink.Web));
        Assert.Equal(VideoText.NoFilePlayer, error.Message);
        Assert.Equal(VideoSink.Window, router.Active);
        Assert.Empty(web.Plays);
    }
}
