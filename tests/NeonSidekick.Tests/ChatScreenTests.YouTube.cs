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
        _http.Map("https://www.googleapis.com/youtube/v3/search?part=snippet&type=playlist", HttpStatusCode.OK, YouTubeFixtureFile("search-playlists.json"));   // 2026-10-08
        _http.Map("https://www.googleapis.com/youtube/v3/playlists?", HttpStatusCode.OK, YouTubeFixtureFile("playlist.json"));
        _http.Map("https://www.googleapis.com/youtube/v3/channels?", HttpStatusCode.OK, YouTubeFixtureFile("channels.json"));
        _http.Map("https://www.googleapis.com/youtube/v3/playlistItems?", (request, _) => Task.FromResult(Fakes.StubHttpMessageHandler.Json(HttpStatusCode.OK,
            YouTubeFixtureFile(request.RequestUri!.Query.Contains("pageToken=", StringComparison.Ordinal) ? "playlistItems-2.json" : "playlistItems-1.json"))));
        _http.Map("https://www.googleapis.com/youtube/v3/search", HttpStatusCode.OK, YouTubeFixtureFile("search.json"));
        _http.Map("https://www.googleapis.com/youtube/v3/videos", HttpStatusCode.OK, YouTubeFixtureFile("videos.json"));
        _http.Map("https://www.youtube.com/oembed", HttpStatusCode.OK, YouTubeFixtureFile("oembed.json"));   // a saved video's title (2026-10-07): the zoo's, whatever is asked
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
        Assert.Matches(@"Big Buck Bunny 60fps 4K - Official Blender Foundation S…  Blender +10:35", output);   // in columns, the title cut (2026-10-09)
        Assert.Matches(@"Me at the zoo & the elephants' ""trunks"" +jawed +0:19", output);
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
        Assert.Contains("  ✗ /youtube [<words> | play <id|link> [<time>] |", output);   // the usage, which wraps since 2026-10-08's verbs
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == "www.googleapis.com");
    }

    /// <summary>
    /// The saved videos (2026-10-07): <c>/youtube save</c> keeps the one playing, <c>save &lt;link&gt;</c> another; the <c>list</c> pane
    /// lists them in columns, <c>d</c> removes the highlighted one and <c>c</c> clears them all, each after a yes (2026-10-08);
    /// <c>unsave &lt;n&gt;</c> takes one off by its number.
    /// </summary>
    [Fact]
    public async Task YouTube_Save_TheSavedPane_RemovesOne_ClearsAll_AndUnsaveTakesOneOff()
    {
        YouTubeFixture(key: false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/youtube play aqz-KE-bpKQ");
        PushLine("/youtube save");
        PushLine("/youtube save https://youtu.be/jNQXAC9IVRw");
        PushLine("/youtube list");
        _console.Input.PushKey(Keys.Char('c'));
        _console.Input.PushKey(Keys.Enter);                // No: kept, the list again
        _console.Input.PushKey(Keys.Down);                 // the zoo
        _console.Input.PushKey(Keys.Char('d'));
        _console.Input.PushKey(Keys.Down);                 // Yes: the zoo gone, the pane kept on the bunny
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/youtube save https://youtu.be/jNQXAC9IVRw");
        PushLine("/youtube unsave 1");
        PushLine("/youtube list");
        _console.Input.PushKey(Keys.Char('c'));
        _console.Input.PushKey(Keys.Down);                 // Yes: the list emptied, the pane closed
        _console.Input.PushKey(Keys.Enter);
        PushLine("/youtube list");
        _console.Input.PushKey(Keys.Escape);               // the empty list opens its pane too (2026-10-07)
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · Saved \"Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film\"; it resumes where it is left.", output);
        Assert.Contains("  · Saved \"Me at the zoo\"; it resumes where it is left.", output);   // looked up by its link (oEmbed)
        Assert.Contains(YouTubeText.SavedCaption(2), output);
        Assert.Contains("Big Buck Bunny 60fps 4K - Official Blender Foundation S…  Blender  ", output);   // uniform columns (2026-10-08)
        Assert.Contains("Me at the zoo" + new string(' ', 43) + "  jawed    not played yet", output);
        Assert.Contains(YouTubeText.ClearPrompt(2), output);
        Assert.Contains(ChatScreen.KeptNotice, output);
        Assert.Contains("📺 Remove \"Me at the zoo\" from the saved videos?", output);
        Assert.Contains("Removed \"Me at the zoo\" from the saved videos.", output);
        Assert.Contains("  · Removed \"Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film\" from the saved videos.", output);
        Assert.Contains(YouTubeText.SavedCaption(1), output);
        Assert.Contains(YouTubeText.ClearPrompt(1), output);
        Assert.Contains("  · " + YouTubeText.Cleared(1), output);
        Assert.Contains("▸ " + YouTubeText.NoneSaved + "\n", output);
        Assert.Empty(new YouTubeLibrary(_settings.ProfileDirectory).List());
    }

    /// <summary><c>/youtube list --clear</c> (2026-10-08): a yes/no first; No keeps the list, Yes empties it, and an empty list asks nothing.</summary>
    [Fact]
    public async Task YouTube_SavedClear_AsksFirst_ThenEmptiesTheList()
    {
        YouTubeFixture(key: false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var library = new YouTubeLibrary(_settings.ProfileDirectory);
        library.Add("aqz-KE-bpKQ", "Big Buck Bunny", "Blender");
        library.Add("jNQXAC9IVRw", "Me at the zoo", "jawed");
        PushLine("/youtube list --clear");
        _console.Input.PushKey(Keys.Enter);                // No
        PushLine("/youtube list --clear");
        _console.Input.PushKey(Keys.Down);                 // Yes
        _console.Input.PushKey(Keys.Enter);
        PushLine("/youtube list --clear");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(YouTubeText.ClearPrompt(2), output);
        Assert.Contains("  · " + ChatScreen.KeptNotice, output);
        Assert.Contains("  · " + YouTubeText.Cleared(2), output);
        Assert.Contains("  · " + YouTubeText.NoneSaved, output);
        Assert.Empty(new YouTubeLibrary(_settings.ProfileDirectory).List());
    }

    /// <summary>The toolbar's 📺 (2026-10-08, the user's ask; the Camera live viewer's until then): a double-click opens the saved videos, as <c>/youtube list</c>.</summary>
    [Fact]
    public async Task YouTube_TheToolbarsItem_OpensTheSavedVideos()
    {
        YouTubeFixture(key: false);
        new YouTubeLibrary(_settings.ProfileDirectory).Add("jNQXAC9IVRw", "Me at the zoo", "jawed");
        _settings.Update(d => d.ToolbarItems = [ToolbarItems.YouTube]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // an empty line: row 100, the rule 101, the hint row 102, the toolbar 103
        StepsWhenIdle(
            input => { input.PushClick(0, 103); input.PushClick(0, 103); },   // 📺
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(YouTubeText.SavedCaption(1), output);
        Assert.Contains("Me at the zoo  jawed  not played yet", output);   // one video: the columns its own widths
        Assert.Empty(_videoPlayer!.Plays);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>In a search's picker, s saves the highlighted video without playing it (2026-10-07), the picker kept open.</summary>
    [Fact]
    public async Task YouTube_ThePickersSaveKey_SavesTheHit_WithoutPlayingIt()
    {
        YouTubeFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/youtube big buck bunny");
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Down);                 // the zoo, third
        _console.Input.PushKey(Keys.Char('s'));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(YouTubeText.PickKeys, output);
        var saved = Assert.Single(new YouTubeLibrary(_settings.ProfileDirectory).List());
        Assert.Equal(("jNQXAC9IVRw", "jawed", 19.0), (saved.Id, saved.Author, saved.Duration));
        Assert.Contains("it resumes where it is left.", output);
        Assert.Empty(_videoPlayer!.Plays);
    }

    /// <summary>A saved video played with no time picks up where it was left (2026-10-07), and the place follows the window.</summary>
    [Fact]
    public async Task YouTube_PlayingASavedVideo_ResumesIt_AndAPauseKeepsTheNewPlace()
    {
        YouTubeFixture(key: false);
        new YouTubeLibrary(_settings.ProfileDirectory).Add("aqz-KE-bpKQ", position: 100);
        PushLine("/youtube play aqz-KE-bpKQ");
        PushLine("/youtube seek 3:00");
        PushLine("/youtube pause");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(97, Assert.Single(_videoPlayer!.Plays).Start);
        Assert.Contains("  · Resumed the saved video at 1:37, a moment before where it was left. Playing ", output);
        Assert.Equal(180, new YouTubeLibrary(_settings.ProfileDirectory).Find("aqz-KE-bpKQ")!.Position);
    }

    /// <summary>
    /// A playlist search (2026-10-08): its rows on the picker, s on one says only a video saves, Enter plays the list from its start;
    /// then next moves through it, and a channel search's Enter plays the channel's uploads.
    /// </summary>
    [Fact]
    public async Task YouTube_APlaylistSearch_EnterPlaysTheList_NextMoves_AndAChannelPlaysItsUploads()
    {
        YouTubeFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/youtube search --playlists --order date lofi");
        _console.Input.PushKey(Keys.Char('s'));
        _console.Input.PushKey(Keys.Enter);
        PushLine("/youtube next");
        PushLine("/youtube --channels blender");
        _console.Input.PushKey(Keys.Enter);
        PushLine("/youtube search --since someday lofi");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Matches(@"Open Movies & Shorts +Blender +3 videos", output);
        Assert.Contains(YouTubeText.SaveVideosOnly(YouTube.YouTubeKind.Playlist), output);
        Assert.Contains(_http.Requests, r => r.Uri.AbsoluteUri.Contains("type=playlist", StringComparison.Ordinal) && r.Uri.AbsoluteUri.Contains("&order=date", StringComparison.Ordinal));
        Assert.Equal([VideoRequest.Playlist("PLabcdefghijklmnopq"), VideoRequest.Playlist("UUSMOQeBJ2RAnuFungnQOxLg")], _videoPlayer!.Plays);
        Assert.Contains(" · entry 2 of 3 in playlist PLabcdefghijklmnopq", output);
        Assert.Matches(@"Blender +1\.8M subscribers +689 videos", output);
        Assert.Contains("  ✗ \"since\" must be today, week, month, year or a date like 2026-01-31, not \"someday\".", output);
        Assert.Empty(new YouTubeLibrary(_settings.ProfileDirectory).List());
    }

    /// <summary>
    /// A play the page never answers (2026-10-09, the user's report: a channel picked from a search opened a silent window and the
    /// screen sat on the picker, keys dead, for the wait's whole cap): the wait runs under a spinner, ESC stops it, the transcript
    /// says so, and the window is left open.
    /// </summary>
    [Fact]
    public async Task YouTube_ASilentPlay_EscStopsTheWait_AndLeavesTheWindowOpen()
    {
        YouTubeFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _videoPlayer!.Behave = false;   // the page never reports past the window's own opening
        var input = Scripted();
        PushLine("/youtube --channels blender");
        input.Push(Keys.Enter);
        _videoPlayer.Changed += s =>
        {
            if (s is { State: VideoState.Opening })
            {
                input.Push(Keys.Escape);   // under the wait's spinner (the test clock never reaches its cap)
            }
        };
        input.OnWait = () =>
        {
            if (_videoPlayer.Plays.Count == 1)
            {
                input.OnWait = null;
                PushLine(input, "/exit");   // at the idle line after
            }
        };

        string output = await RunAsync();

        Assert.Equal([VideoRequest.Playlist("UUSMOQeBJ2RAnuFungnQOxLg")], _videoPlayer.Plays);
        Assert.Contains("  · " + YouTubeText.StoppedWaiting[..40], output);   // the rest wraps
        Assert.Equal(0, _videoPlayer.Closes);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// A play the page answers late, from the window's thread (2026-10-09, the user's second report: the video played, but the picker
    /// stayed drawn and no key reached the screen after): the screen is the user's again once the answer comes — the next command
    /// runs.
    /// </summary>
    [Fact]
    public async Task YouTube_APlayAnsweredLate_GivesTheKeysBack()
    {
        YouTubeFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _videoPlayer!.Behave = false;
        var input = Scripted();
        PushLine("/youtube big buck bunny");
        input.Push(Keys.Enter);
        _videoPlayer.Changed += s =>
        {
            if (s is { State: VideoState.Opening, VideoId: { } id })
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    _videoPlayer.Report(new VideoSnapshot(VideoState.Playing, id, "Big Buck Bunny", "Blender", 0, 635));
                });
            }
        };
        input.OnWait = () =>
        {
            if (_videoPlayer.Snapshot is { State: VideoState.Playing })
            {
                input.OnWait = null;
                PushLine(input, "/youtube pause");
                PushLine(input, "/exit");
            }
        };

        string output = await RunAsync();

        Assert.Contains("  · Playing \"Big Buck Bunny\"", output);
        Assert.Equal([VideoCommand.Pause], _videoPlayer.Commands);
    }

    /// <summary>
    /// The pickers close (2026-10-09, the user's report: since 2026-10-07 the search picker stayed drawn over the input row after
    /// Enter or ESC, every key going to a line no one could see): Enter closes it before the play's wait, ESC after; the playlist's
    /// picker the same.
    /// </summary>
    [Fact]
    public async Task YouTube_ThePickers_CloseOnEnterBeforeThePlay_AndOnEsc()
    {
        YouTubeFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var input = Scripted();
        var openAtPlay = new List<bool>();
        _videoPlayer!.Changed += s =>
        {
            if (s is { State: VideoState.Opening })
            {
                openAtPlay.Add(_running!.MenuPaneOpen);
            }
        };
        PushLine("/youtube big buck bunny");
        input.Push(Keys.Enter);
        PushLine("/youtube playlist https://www.youtube.com/playlist?list=PLabcdefghijklmnopq");
        input.Push(Keys.Enter);
        PushLine("/youtube big buck bunny");
        input.Push(Keys.Escape);
        bool? openAtIdle = null;
        input.OnWait = () =>
        {
            input.OnWait = null;
            openAtIdle = _running!.MenuPaneOpen;
            PushLine(input, "/exit");
        };

        await RunAsync();

        Assert.Equal([false, false], openAtPlay);
        Assert.False(openAtIdle);
    }

    /// <summary><c>/youtube playlist &lt;link&gt;</c> (2026-10-08): its entries on the picker, Enter plays the list from the one picked.</summary>
    [Fact]
    public async Task YouTube_APlaylistsEntries_OnThePicker_EnterPlaysFromThatOne()
    {
        YouTubeFixture();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/youtube playlist https://www.youtube.com/playlist?list=PLabcdefghijklmnopq");
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Enter);
        PushLine("/youtube https://www.youtube.com/playlist?list=PLzyxwvutsrqponmlk");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(YouTubeText.PlaylistPickTitle(new YouTubePlaylist("PLabcdefghijklmnopq", "Open Movies & Shorts", "Blender", 3)), output);
        Assert.Matches(@"2\. +Me at the zoo & more +jawed", output);
        Assert.Matches(@"3\. +\(deleted video\)", output);
        Assert.Equal([VideoRequest.Playlist("PLabcdefghijklmnopq", 1), VideoRequest.Playlist("PLzyxwvutsrqponmlk")], _videoPlayer!.Plays);
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
