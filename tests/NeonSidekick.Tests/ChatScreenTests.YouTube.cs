using System.Net;
using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/youtube</c> on the screen (2026-10-05, the YouTube plan): a search's picker playing the pick in the (fake) video window,
/// the verbs driving it, and the refusals — no key, no window.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The fake video window the chat screen gets; null (the default) is a screen with no window to play in.</summary>
    private FakeVideoPlayer? _videoPlayer;

    private static string YouTubeFixtureFile(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "youtube", name));

    private void YouTubeFixture(bool key = true, bool window = true)
    {
        _videoPlayer = window ? new FakeVideoPlayer() : null;
        _settings.Update(d => { d.TtsOutput = false; d.YouTubeApiKey = key ? "AIza-test" : ""; d.YouTubeSearchMaxResults = 3; });
        _http.Map("https://www.googleapis.com/youtube/v3/search", HttpStatusCode.OK, YouTubeFixtureFile("search.json"));
        _http.Map("https://www.googleapis.com/youtube/v3/videos", HttpStatusCode.OK, YouTubeFixtureFile("videos.json"));
    }

    [Fact]
    public async Task YouTube_Words_OpenThePicker_EnterPlays_AndTheVerbsDriveTheWindow()
    {
        YouTubeFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/youtube big buck bunny");
        _console.Input.PushKey(Keys.Enter);        // the first hit
        PushLine("/youtube pause");
        PushLine("/youtube volume 30");
        PushLine("/youtube");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(YouTubeText.PickTitle("big buck bunny"), output);
        Assert.Contains("Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film  ·  Blender · 10:35 · 21M views", output);
        Assert.Contains("Me at the zoo & the elephants' \"trunks\"  ·  jawed · 0:19", output);
        Assert.Equal(new VideoRequest("aqz-KE-bpKQ"), Assert.Single(_videoPlayer!.Plays));
        Assert.Contains("  · Playing \"Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film\" (Blender) at 0:00 of 10:35, volume 100% · video aqz-KE-bpKQ", output);
        Assert.Contains("  · Paused \"Big Buck Bunny", output);
        Assert.Contains("  · Paused \"Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film\" (Blender) at 0:00 of 10:35, volume 30% · video aqz-KE-bpKQ", output);
        Assert.Equal([VideoCommand.Pause, VideoCommand.Volume(30)], _videoPlayer.Commands);
        Assert.Equal(2, _http.Requests.Count(r => r.Uri.Host == "www.googleapis.com"));   // one search, its details
        Assert.Empty(_chat.Requests);                                                       // the user's own hand: no turn
    }

    [Fact]
    public async Task YouTube_PlayALink_AtATime_ThenClose()
    {
        YouTubeFixture(key: false);
        PushLine("/youtube play https://youtu.be/jNQXAC9IVRw 0:05");
        PushLine("/youtube close");
        PushLine("/youtube pause");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(new VideoRequest("jNQXAC9IVRw", 5), Assert.Single(_videoPlayer!.Plays));   // no key needed to play
        Assert.Contains("  · Playing ", output);
        Assert.Contains("  · " + YouTubeText.Closed, output);
        Assert.Contains("  · " + YouTubeText.NoVideo, output);
    }

    [Fact]
    public async Task YouTube_Refusals_NoKey_NoWindow_AndAVerbWithoutItsArgument()
    {
        YouTubeFixture(key: false, window: false);
        PushLine("/youtube lofi beats");
        PushLine("/youtube pause");
        PushLine("/youtube seek");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ Searching YouTube needs a YouTube Data API key: add one as YouTube API key on the YouTube tab of /tools.", output);   // the rest wraps
        Assert.Contains("  ✗ " + (OperatingSystem.IsMacOS() ? YouTubeText.NoWindowMac : YouTubeText.NoWindow), output);   // a Mac's own since 2026-10-07
        Assert.Contains("  ✗ " + YouTubeText.CommandUsage, output);
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == "www.googleapis.com");
    }

    /// <summary>Without the pane (or a window), a search's hits are listed instead of picked.</summary>
    [Fact]
    public async Task YouTube_WithoutThePane_ListsTheHits()
    {
        YouTubeFixture();
        PushLine("/youtube big buck bunny");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · YouTube: 3 videos for \"big buck bunny\":\n  · 1. Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film — Blender · 10:35 · 21M views · 2014 · id aqz-KE-bpKQ\n", output);
        Assert.Empty(_videoPlayer!.Plays);
    }
}
