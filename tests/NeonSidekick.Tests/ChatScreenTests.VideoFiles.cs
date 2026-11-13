using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// Video files on the screen (2026-11-12, the video-files plan): <c>/view</c> and <c>/video</c> playing a file and driving it, the YouTube
/// commands pointing back, <c>/view</c>'s flags refused with a video, and every fallback ending at the default app: no window, a window
/// that can't open, <c>default-app</c>, and <c>webui-window</c> with the Web UI off.
/// </summary>
public partial class ChatScreenTests
{
    // A clip in the profile's default working folder; its full path.
    private string VideoFixture(string target = VideoFilesPlayIn.VideoWindow, bool window = true)
    {
        YouTubeFixture(key: false, window: window);
        _settings.Update(d => d.VideoFilesPlayIn = target);
        string files = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(files);
        string clip = Path.Combine(files, "clip.mp4");
        File.WriteAllBytes(clip, [0]);
        return clip;
    }

    [Fact]
    public async Task Video_ViewAndVideo_PlayAFile_TheVerbsDriveIt_AndYouTubePointsBack()
    {
        string clip = VideoFixture();
        PushLine("/view clip.mp4");
        PushLine("/video pause");
        PushLine("/video volume 30");
        PushLine("/video status");
        PushLine("/youtube pause");
        PushLine("/youtube status");
        PushLine("/view clip.mp4 --thumbs");
        PushLine("/video stop");
        PushLine("/video close");
        PushLine("/video status");
        PushLine("/exit");

        string output = await RunAsync();

        var play = Assert.Single(_videoPlayer!.Plays);
        Assert.Equal(clip, play.FilePath);
        Assert.Contains("Playing \"clip.mp4\" at 0:00 of 10:35, volume 100%", output);
        Assert.Contains("Paused \"clip.mp4\" at 0:00 of 10:35, volume 100%", output);
        Assert.Contains("Paused \"clip.mp4\" at 0:00 of 10:35, volume 30%", output);
        Assert.Equal(2, Count(output, "  ✗ a video file is open (clip.mp4), not a YouTube video; /video drives it."));
        Assert.Contains("  ✗ " + VideoText.ViewVideoFlagsError(VideoFilesPlayInKind.VideoWindow), output);
        Assert.Contains("  ✗ " + VideoText.CommandUsage, output);
        Assert.Contains(YouTubeText.Closed, output);
        Assert.Contains(VideoText.NoFileCommand, output);
    }

    /// <summary>A YouTube video open: <c>/video</c>'s verbs point at <c>/youtube</c>, and nothing is sent.</summary>
    [Fact]
    public async Task Video_WithAYouTubeVideoOpen_PointsAtYouTube()
    {
        VideoFixture();
        PushLine("/youtube play " + BunnyId);
        PushLine("/video pause");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ a YouTube video is open, not a video file; /youtube drives it.", output);
        Assert.Empty(_videoPlayer!.Commands);
    }

    /// <summary>No video window at all: the default app opens the file, the reason leading, and control says where it went.</summary>
    [Fact]
    public async Task Video_WithNoWindow_OpensInTheDefaultApp_AndControlSaysWhere()
    {
        string clip = VideoFixture(window: false);
        PushLine("/video clip.mp4");
        PushLine("/video status");
        PushLine("/video pause");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([clip], _openedFiles);
        Assert.Contains(VideoText.DefaultAppFellBack(VideoText.NoWindowHere) + "Opened clip.mp4 in the user's video app; it plays there, where /video can't control it.", output);
        Assert.Contains(VideoText.HandedOffStatus("clip.mp4"), output);
        Assert.Contains("  ✗ clip.mp4 is in its default app, which the app can't control; the user can pause or close it there.", output);
    }

    /// <summary><c>default-app</c> beside a window: the file opens there, recorded as the router's hand-off, which YouTube never reads.</summary>
    [Fact]
    public async Task Video_UnderDefaultApp_OpensThere_AndTheHandOffIsTheFilesOwn()
    {
        string clip = VideoFixture(VideoFilesPlayIn.DefaultApp);
        PushLine("/video clip.mp4");
        PushLine("/video status");
        PushLine("/youtube status");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([clip], _openedFiles);
        Assert.Empty(_videoPlayer!.Plays);
        Assert.Contains("Opened clip.mp4 in the user's video app; it plays there", output);
        Assert.Contains(VideoText.HandedOffStatus("clip.mp4"), output);
        Assert.Contains(YouTubeText.NoVideo, output);
    }

    /// <summary>A window that can't open sends the file to its default app, its reason leading the answer.</summary>
    [Fact]
    public async Task Video_AWindowThatCantOpen_FallsBackToTheDefaultApp()
    {
        string clip = VideoFixture();
        _videoPlayer!.PlayFailure = VideoText.NoRuntime;
        PushLine("/video clip.mp4");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([clip], _openedFiles);
        Assert.Contains(VideoText.DefaultAppFellBack(VideoText.CouldNotPlayIn(VideoFilesPlayInKind.VideoWindow)) + "Opened clip.mp4", output);
        Assert.DoesNotContain("WebView2 Runtime", output);   // the exception's text is the log's, not the answer's (2026-11-13)
    }

    /// <summary><c>webui-window</c> with the Web UI off and no video window: both reasons in one note, never "plays in the video window instead".</summary>
    [Fact]
    public async Task Video_UnderWebUiWindow_WithNoWebUiAndNoWindow_KeepsBothReasons()
    {
        string clip = VideoFixture(VideoFilesPlayIn.WebUiWindow, window: false);
        PushLine("/video clip.mp4");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([clip], _openedFiles);
        Assert.Contains(VideoText.DefaultAppFellBack(YouTubeText.WebUiNotServing, VideoText.NoWindowHere) + "Opened clip.mp4", output);
        Assert.DoesNotContain(VideoText.WebUiOffFellBack, output);
    }

    /// <summary>
    /// Quotes make a path (2026-11-13): <c>/video "pause"</c> plays a file named pause, not the verb; <c>/view "my clip.mp4"</c> plays too, its
    /// flag peeled first; a file-looking name that is no video says so; a seek's bad text is the tool's refusal, worded for <c>/video</c>.
    /// </summary>
    [Fact]
    public async Task Video_QuotedPaths_AndFileLookingText_AndABadSeek()
    {
        string clip = VideoFixture();
        string files = Path.GetDirectoryName(clip)!;
        File.WriteAllBytes(Path.Combine(files, "my clip.mp4"), [0]);
        PushLine("/video \"pause\"");
        PushLine("/view \"my clip.mp4\"");
        PushLine("/video movie.avi");
        PushLine("/video seek soon");
        PushLine("/video volume NaN");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + VideoText.NotAVideoFile("pause", forUser: true)["Error: ".Length..], output);
        Assert.Equal(Path.Combine(files, "my clip.mp4"), Assert.Single(_videoPlayer!.Plays).FilePath);
        Assert.Contains("  ✗ " + VideoText.NotAVideoFile("movie.avi", forUser: true)["Error: ".Length..], output);
        Assert.Contains("  ✗ " + YouTubeText.NotATimeForUser("/video seek", "soon")["Error: ".Length..], output);
        Assert.Contains("  ✗ " + YouTubeText.NotAVolumeForUser("/video volume", "NaN")["Error: ".Length..], output);
        Assert.Empty(_videoPlayer.Commands);
    }

    /// <summary>A folder named like a video (2026-11-13) is the picture viewer's: its thumbnails with --thumbs, never a play or the flags error.</summary>
    [Fact]
    public async Task Video_AFolderNamedLikeAVideo_IsThePictureViewers()
    {
        string clip = VideoFixture();
        string trip = Path.Combine(Path.GetDirectoryName(clip)!, "Trip.mov");
        Directory.CreateDirectory(trip);
        var opened = new List<(string Folder, string? Select)>();
        _openThumbs = (folder, select) => opened.Add((folder, select));
        PushLine("/view Trip.mov --thumbs");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([(trip, null)], opened);
        Assert.Empty(_videoPlayer!.Plays);
        Assert.DoesNotContain(VideoText.ViewVideoFlagsError(VideoFilesPlayInKind.VideoWindow), output);
    }

    /// <summary><c>webui-window</c> with the Web UI off: the window plays it, the note saying why.</summary>
    [Fact]
    public async Task Video_UnderWebUiWindow_WithTheWebUiOff_PlaysInTheWindow()
    {
        VideoFixture(VideoFilesPlayIn.WebUiWindow);
        PushLine("/video clip.mp4");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Single(_videoPlayer!.Plays);
        Assert.Contains(VideoText.WebUiOffFellBack + "Playing \"clip.mp4\"", output);
    }

    /// <summary><c>webui-window</c> with the Web UI serving: the file waits for a Video tab, and the page that attaches gets its load, served by its token.</summary>
    [Fact]
    public async Task Video_UnderWebUiWindow_WaitsForATab_AndThePageGetsTheFilesLoad()
    {
        string clip = VideoFixture(VideoFilesPlayIn.WebUiWindow);
        _settings.Update(d =>
        {
            d.WebUi = true;
            d.WebUiReach = "local";
            d.WebUiYouTubeControls = true;
        });
        _webUiHost = _ => new RecordingHost();
        string? load = null;
        WebUi.WebVideoFile? served = null;
        var input = WhenIdle(
            i => { UntilServing(); PushLine(i, "/video clip.mp4"); },
            i =>
            {
                var web = _running!.WebVideoForTests!;
                using var page = web.Attach(PageOf('a'), PageOf('1'), false, null, () => { });
                while (page.Events.TryRead(out var e))
                {
                    if (e.Name == "load")
                    {
                        load = e.Json.Value;
                    }
                }

                if (load is not null)
                {
                    served = web.FileOf(System.Text.Json.JsonDocument.Parse(load).RootElement.GetProperty("file").GetString()!);
                }

                PushLine(i, "/exit");
            });

        string output = await RunAsync(input);

        Assert.Empty(_videoPlayer!.Plays);
        Assert.Contains("clip.mp4", output);
        Assert.NotNull(load);
        Assert.Contains("\"src\":\"/api/youtube/file/", load, StringComparison.Ordinal);
        Assert.Equal(clip, served?.Path);
    }

    [Fact]
    public async Task Video_RefusesWhatIsNoVideoFile()
    {
        VideoFixture();
        PushLine("/video gone.mp4");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + FileText.Missing("gone.mp4")["Error: ".Length..], output);
        Assert.Empty(_videoPlayer!.Plays);
        Assert.Empty(_openedFiles);
    }

    /// <summary>The tools are offered by their switch alone, with no window too (the default app takes the play), and named in the help.</summary>
    [Fact]
    public void Video_TheToolsAreOffered_ByTheirSwitch()
    {
        var tools = ChatScreen.VideoTools(null, new WorkingDirectory(() => _dir, _time), _time, _ => new Llm.Tools.VideoRouted(false, "opened"), null, null, null);
        Assert.Equal(VideoToolNames.Order(StringComparer.Ordinal), tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.False(ChatScreen.VideoOffered(new Settings.AppSettingsData()));
        Assert.True(ChatScreen.VideoOffered(new Settings.AppSettingsData { VideoTools = true }));
        Assert.All(VideoToolNames, name => Assert.Contains(name, Plans.PlanTools.Mutating));
        Assert.All(VideoToolNames, name => Assert.Contains(name, CheckpointTools.NoFiles));
        Assert.Contains(SlashCommand.Video, WebCommands.Never);
    }

    // The video file tools' names (moved here from ChatScreen, 2026-11-13: only this test read them).
    private static readonly string[] VideoToolNames = [Llm.Tools.PlayVideoTool.ToolName, Llm.Tools.VideoControlTool.ToolName];

    /// <summary>
    /// <c>/video</c>'s completion (2026-11-13): one word that starts a verb and a file lists both, the verbs first; with no such file, the
    /// verbs' own list; a path argument keeps a mention character the list's. <c>/view</c> offers no flag after a video or a PDF, and after a
    /// flag lists neither.
    /// </summary>
    [Fact]
    public void Video_Completion_ListsVerbsThenFiles_AndViewsFlagsSkipVideos()
    {
        static Func<string, MentionResult> Listing(params string[] names) =>
            prefix => new MentionResult(FileOutcome.Ok, [.. names.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))], false);
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], Listing(), Listing("clip.mp4", "cat.png", "doc.pdf"),
            VideoFiles: Listing("clip.mp4", "Clips/", "holiday.webm"));
        Assert.Equal(["close", "clip.mp4", "Clips/"], ChatScreen.ArgumentPaths("/video", "c", sources)!.Paths);
        Assert.Null(ChatScreen.ArgumentPaths("/video", "pa", sources));   // no file starts "pa": the verbs' list with notes
        Assert.Equal(["holiday.webm"], ChatScreen.ArgumentPaths("/video", "ho", sources)!.Paths);
        Assert.True(ChatScreen.TakesPathArgument("/video"));
        Assert.Null(ChatScreen.ViewFlagPaths("clip.mp4 ", sources.ImageFiles));
        Assert.Null(ChatScreen.ViewFlagPaths("doc.pdf ", sources.ImageFiles));
        Assert.Equal(["--thumbs cat.png"], ChatScreen.ArgumentPaths("/view", "--thumbs c", sources)!.Paths);
        Assert.Equal(("my clip.mp4", true, false), ChatScreen.ParseViewArgs("\"my clip.mp4\" --chat"));
    }
}
