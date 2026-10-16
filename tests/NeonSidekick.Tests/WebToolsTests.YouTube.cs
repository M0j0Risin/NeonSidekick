using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.Web;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// open_url and YouTube's links (2026-10-16, YouTube open links in): a video or list= link goes through youtube_play's tool, its answer the line;
/// the id the player refused, a channel, YouTube tools off and no player pass on to Web open links in; a refusal now passes on with a note; one
/// player at a time names the rest, the default browser opens each; a saved video resumes and a mix's link plays its video.
/// </summary>
public sealed partial class WebToolsTests
{
    private const string Bunny = "aqz-KE-bpKQ";
    private readonly FakeVideoPlayer _video = new();

    private OpenUrlTool YouTubeOpen(YouTubeOpenLinksInKind target = YouTubeOpenLinksInKind.YouTubeWindow, bool tools = true, Func<YouTubeLibrary>? library = null, IVideoPlayer? player = null) =>
        new(_web, () => _settings, openYouTube: (url, ct) => YouTubeOpenUrl.OpenAsync(url, tools, player ?? _video, () => new YouTubePlayTool(player ?? _video, () => _settings, _time, library), target, ct));

    private async Task<string> OpenAsync(OpenUrlTool tool, params string[] urls) =>
        (string)(await tool.InvokeAsync(urls.Length == 1 ? Args(("url", urls[0])) : Args(("urls", Json(System.Text.Json.JsonSerializer.Serialize(urls)))), CancellationToken.None))!;

    [Fact]
    public async Task AYouTubeLink_PlaysThroughYouTubePlay_ItsAnswerTheLine()
    {
        string answer = await OpenAsync(YouTubeOpen(), "https://youtu.be/" + Bunny);

        Assert.StartsWith("Playing ", answer, StringComparison.Ordinal);
        Assert.Equal(Bunny, Assert.Single(_video.Plays).VideoId);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task WithYouTubeToolsOff_NoPlayer_OrAChannel_TheLinkPassesOn()
    {
        Assert.Equal(WebText.OpenedOne("https://www.youtube.com/watch?v=" + Bunny), await OpenAsync(YouTubeOpen(tools: false), "https://www.youtube.com/watch?v=" + Bunny));
        Assert.Equal(WebText.OpenedOne("https://www.youtube.com/channel/UCSMOQeBJ2RAnuFungnQOxLg"), await OpenAsync(YouTubeOpen(), "https://www.youtube.com/channel/UCSMOQeBJ2RAnuFungnQOxLg"));
        var noPlayer = new OpenUrlTool(_web, () => _settings, openYouTube: (url, ct) => YouTubeOpenUrl.OpenAsync(url, true, null, () => throw new InvalidOperationException(), YouTubeOpenLinksInKind.YouTubeWindow, ct));
        Assert.Equal(WebText.OpenedOne("https://www.youtube.com/watch?v=" + Bunny), await OpenAsync(noPlayer, "https://www.youtube.com/watch?v=" + Bunny));
        Assert.Empty(_video.Plays);
        Assert.Equal(3, _opened.Count);
    }

    [Fact]
    public async Task TheIdThePlayerRefused_PassesOn_TheLoopGuard()
    {
        _video.Report(new VideoSnapshot(VideoState.Unstarted, Bunny, Error: 150));

        string answer = await OpenAsync(YouTubeOpen(), YouTubeIds.WatchUrl(Bunny));

        Assert.Equal(WebText.OpenedOne(YouTubeIds.WatchUrl(Bunny)), answer);
        Assert.Empty(_video.Plays);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(153)]
    public async Task ARefusalNow_PassesOn_WithANote(int code)
    {
        _video.Behave = false;
        _video.Changed += s => { if (s is { State: VideoState.Opening, Error: null }) _video.Report(s with { Error = code }); };

        string answer = await OpenAsync(YouTubeOpen(), YouTubeIds.WatchUrl(Bunny));

        Assert.Equal(YouTubeText.WontEmbedHere + WebText.OpenedOne(YouTubeIds.WatchUrl(Bunny)), answer);
        Assert.Single(_opened);
    }

    [Fact]
    public async Task OnePlayer_PlaysTheFirst_NamesTheRestOfYouTubes_AndOpensTheOtherSites()
    {
        string answer = await OpenAsync(YouTubeOpen(), "https://www.youtube.com/watch?v=" + Bunny, "https://youtu.be/jNQXAC9IVRw", "https://example.com/a");

        string[] lines = answer.Split('\n');
        Assert.StartsWith("Playing ", lines[0], StringComparison.Ordinal);
        Assert.Equal(WebText.OneAtATime(["https://youtu.be/jNQXAC9IVRw"]), lines[1]);
        Assert.Equal(WebText.OpenedOne("https://example.com/a"), lines[2]);
        Assert.Single(_video.Plays);
        Assert.Equal(["https://example.com/a"], _opened);

        // The Browser window shows one page at a time too.
        _video.Plays.Clear();
        string window = await OpenAsync(YouTubeOpen(YouTubeOpenLinksInKind.BrowserWindow), "https://www.youtube.com/watch?v=" + Bunny, "https://youtu.be/jNQXAC9IVRw");
        Assert.Contains(WebText.OneAtATime(["https://youtu.be/jNQXAC9IVRw"]), window);
    }

    [Fact]
    public async Task UnderTheDefaultBrowser_EachYouTubeLinkGoesThroughTheHook()
    {
        string answer = await OpenAsync(YouTubeOpen(YouTubeOpenLinksInKind.DefaultBrowser), "https://www.youtube.com/watch?v=" + Bunny, "https://youtu.be/jNQXAC9IVRw");

        Assert.Equal(2, answer.Split('\n').Length);
        Assert.Equal(2, _video.Plays.Count);   // a hand-off each, in the app: here the fake plays both
    }

    [Fact]
    public async Task ASavedVideo_ResumesThroughOpenUrl_AndAMixLinkPlaysItsVideo()
    {
        var library = new YouTubeLibrary(_dir, _time);
        library.Add(Bunny);
        library.Update(Bunny, s => s with { Position = 120, Duration = 635 });

        string answer = await OpenAsync(YouTubeOpen(library: () => library), "https://www.youtube.com/watch?v=" + Bunny);
        Assert.StartsWith(YouTubeText.Resumed(library.ResumeAt(Bunny)), answer, StringComparison.Ordinal);

        _video.Plays.Clear();
        await OpenAsync(YouTubeOpen(), "https://www.youtube.com/watch?v=jNQXAC9IVRw&list=RDjNQXAC9IVRw&start_radio=1");
        var mix = Assert.Single(_video.Plays);
        Assert.Equal(("jNQXAC9IVRw", null), (mix.VideoId, mix.PlaylistId));

        _video.Plays.Clear();
        await OpenAsync(YouTubeOpen(), "https://www.youtube.com/playlist?list=PLabcdefghijklmnopqrstu");
        Assert.Equal("PLabcdefghijklmnopqrstu", Assert.Single(_video.Plays).PlaylistId);
    }

    [Fact]
    public void OpenUrlsDescription_IsUnchanged_WithTheYouTubeHook()
    {
        Assert.Equal(_open.Description, YouTubeOpen().Description);
    }
}
