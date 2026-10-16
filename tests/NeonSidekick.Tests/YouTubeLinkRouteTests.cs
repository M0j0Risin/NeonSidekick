using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The video window's links (2026-10-16, <see cref="YouTubeLinkRoute"/>): a video, a <c>list=</c> or a mix's link plays where the setting says,
/// a channel, a search, another site and the video already playing go to the default browser, and <c>default-browser</c> keeps every link there.
/// </summary>
public class YouTubeLinkRouteTests
{
    private const string Bunny = "aqz-KE-bpKQ";

    [Theory]
    [InlineData(YouTubeOpenLinksInKind.YouTubeWindow, YouTubeLinkAction.Play)]
    [InlineData(YouTubeOpenLinksInKind.WebUiWindow, YouTubeLinkAction.Play)]
    [InlineData(YouTubeOpenLinksInKind.BrowserWindow, YouTubeLinkAction.BrowserWindow)]
    [InlineData(YouTubeOpenLinksInKind.DefaultBrowser, YouTubeLinkAction.DefaultBrowser)]
    public void AVideoAListAndAMix_GoWhereTheSettingSays(YouTubeOpenLinksInKind kind, YouTubeLinkAction expected)
    {
        Assert.Equal(expected, YouTubeLinkRoute.Decide(kind, "https://www.youtube.com/watch?v=jNQXAC9IVRw", Bunny));
        Assert.Equal(expected, YouTubeLinkRoute.Decide(kind, "https://www.youtube.com/playlist?list=PLabcdefghijklmnopqrstu", Bunny));
        Assert.Equal(expected, YouTubeLinkRoute.Decide(kind, "https://www.youtube.com/watch?v=jNQXAC9IVRw&list=RDjNQXAC9IVRw&start_radio=1", Bunny));
        Assert.Equal(expected, YouTubeLinkRoute.Decide(kind, "https://www.youtube.com/watch?v=" + Bunny + "&list=PLabcdefghijklmnopqrstu", Bunny));   // a list, even from the video playing
    }

    [Theory]
    [InlineData(YouTubeOpenLinksInKind.YouTubeWindow)]
    [InlineData(YouTubeOpenLinksInKind.WebUiWindow)]
    [InlineData(YouTubeOpenLinksInKind.BrowserWindow)]
    [InlineData(YouTubeOpenLinksInKind.DefaultBrowser)]
    public void AChannelASearchAnotherSite_AndTheVideoPlaying_GoToTheDefaultBrowser(YouTubeOpenLinksInKind kind)
    {
        Assert.Equal(YouTubeLinkAction.DefaultBrowser, YouTubeLinkRoute.Decide(kind, "https://www.youtube.com/channel/UCSMOQeBJ2RAnuFungnQOxLg", Bunny));
        Assert.Equal(YouTubeLinkAction.DefaultBrowser, YouTubeLinkRoute.Decide(kind, "https://www.youtube.com/@blender", Bunny));
        Assert.Equal(YouTubeLinkAction.DefaultBrowser, YouTubeLinkRoute.Decide(kind, "https://www.youtube.com/results?search_query=bunny", Bunny));
        Assert.Equal(YouTubeLinkAction.DefaultBrowser, YouTubeLinkRoute.Decide(kind, "https://www.blender.org/", Bunny));
        Assert.Equal(YouTubeLinkAction.DefaultBrowser, YouTubeLinkRoute.Decide(kind, "https://www.youtube.com/watch?v=" + Bunny + "&t=12s", Bunny));   // "Watch on YouTube"
        Assert.Equal(YouTubeLinkAction.DefaultBrowser, YouTubeLinkRoute.Decide(kind, null, Bunny));
    }

    [Fact]
    public void ThePlayableTest_IsTheOneOpenUrlsHookReads()
    {
        foreach (string link in new[] { "https://youtu.be/jNQXAC9IVRw", "https://www.youtube.com/playlist?list=PLabcdefghijklmnopqrstu", "https://www.youtube.com/channel/UCSMOQeBJ2RAnuFungnQOxLg" })
        {
            bool plays = YouTubeLinkRoute.Decide(YouTubeOpenLinksInKind.YouTubeWindow, link, null) == YouTubeLinkAction.Play;
            Assert.Equal(YouTubeIds.IsPlayable(link), plays);
        }
    }
}
