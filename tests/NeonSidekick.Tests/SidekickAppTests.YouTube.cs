using System.Net;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>The saved videos outside the screen (2026-10-08): headless <c>/youtube list --clear</c> asks nothing.</summary>
public partial class SidekickAppTests
{
    [Fact]
    public async Task Headless_YouTubeSavedClear_EmptiesTheList_WithoutAsking()
    {
        var library = new YouTubeLibrary(_settings.ProfileDirectory);
        library.Add("aqz-KE-bpKQ", "Big Buck Bunny", "Blender");
        library.Add("jNQXAC9IVRw", "Me at the zoo", "jawed");

        string output = await Headless("/youtube list --clear\n/youtube list --clear\n");

        Assert.Contains(YouTubeText.Cleared(2) + Environment.NewLine, output);
        Assert.Contains(YouTubeText.NoneSaved + Environment.NewLine, output);   // the second finds nothing
        Assert.DoesNotContain("[error]", output);
        Assert.Empty(new YouTubeLibrary(_settings.ProfileDirectory).List());
    }

    /// <summary>The bare <c>/youtube</c> (2026-10-18, the user's ask): the saved videos listed, as <c>/youtube list</c>; the window's error until then.</summary>
    [Fact]
    public async Task Headless_BareYouTube_ListsTheSavedVideos()
    {
        new YouTubeLibrary(_settings.ProfileDirectory).Add("jNQXAC9IVRw", "Me at the zoo", "jawed");

        string output = await Headless("/youtube\n");

        Assert.Contains("Me at the zoo", output);
        Assert.DoesNotContain("[error]", output);
        Assert.DoesNotContain(YouTubeText.NeedsScreen, output);
    }

    /// <summary>
    /// Headless saved playlists (2026-11-08): <c>save &lt;list link&gt;</c> saves the playlist, its title from oEmbed with no key;
    /// <c>save playlist</c> has no window to read one from; <c>unsave &lt;list link&gt;</c> takes it off.
    /// </summary>
    [Fact]
    public async Task Headless_YouTubeSave_APlaylistsLink_SavesThePlaylist()
    {
        _http.Map("https://www.youtube.com/oembed", HttpStatusCode.OK, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "youtube", "oembed-playlist.json")));
        const string Link = "https://www.youtube.com/playlist?list=PLabcdefghijklmnopq";

        string output = await Headless("/youtube save " + Link + "\n/youtube\n/youtube save playlist\n");

        Assert.Contains("Saved the playlist \"Open Movies & Shorts\"; it resumes at the entry and place it is left at." + Environment.NewLine, output);
        Assert.Contains("1. ▤ Open Movies & Shorts — Blender · not played yet · playlist id PLabcdefghijklmnopq", output);
        Assert.Contains("[error] " + YouTubeText.NoPlaylistToSave, output);   // headless leads a refusal so
        var saved = Assert.Single(new YouTubeLibrary(_settings.ProfileDirectory).List());
        Assert.Equal((true, (string?)null, (string?)"aqz-KE-bpKQ"), (saved.Playlist, saved.EntryVideo, saved.Thumbnail));   // the list's thumbnail, never played from

        output = await Headless("/youtube unsave " + Link + "\n");
        Assert.Contains("Removed the playlist \"Open Movies & Shorts\" from the saved videos.", output);
        Assert.Empty(new YouTubeLibrary(_settings.ProfileDirectory).List());
    }
}
