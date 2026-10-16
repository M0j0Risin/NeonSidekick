namespace NeonSidekick.YouTube;

/// <summary>What happens to a link the video window opens (<see cref="YouTubeLinkRoute.Decide"/>).</summary>
public enum YouTubeLinkAction
{
    /// <summary>The default browser, at once and on the window's own thread, as before the setting.</summary>
    DefaultBrowser,

    /// <summary>Played through the route, on a pool thread: the window's video replaced in place, or moved to the Web UI's tab.</summary>
    Play,

    /// <summary>The app's Browser window, on a pool thread; the video window stays open.</summary>
    BrowserWindow,
}

/// <summary>
/// The links the video window opens (2026-10-16, <c>YouTube open links in</c>; the YouTube logo, "Watch on YouTube", an end screen): a video or
/// <c>list=</c> link plays where the setting says, and anything else — a channel, a search, another site, the video already playing — opens in
/// the default browser as before. <c>default-browser</c> keeps every link there. A link from the window never hands off to a browser as a
/// play would, so the window stays open: <c>browser-window</c> opens the page beside it. Pure.
/// </summary>
public static class YouTubeLinkRoute
{
    /// <param name="target">Where a play goes now (the setting with its fallbacks, <c>ChatScreen.PlayTarget</c>).</param>
    /// <param name="url">The link the page asked to open.</param>
    /// <param name="currentVideoId">The video the window plays now, if any.</param>
    public static YouTubeLinkAction Decide(YouTubeOpenLinksInKind target, string? url, string? currentVideoId)
    {
        if (target == YouTubeOpenLinksInKind.DefaultBrowser || !YouTubeIds.IsPlayable(url))
        {
            return YouTubeLinkAction.DefaultBrowser;
        }

        // "Watch on YouTube" names the video playing: that one is for the browser, never a replay in place.
        bool list = YouTubeIds.TryParsePlaylist(url, out _, out _, out _, out _);
        if (!list && YouTubeIds.TryParse(url, out string id, out _) && id == currentVideoId)
        {
            return YouTubeLinkAction.DefaultBrowser;
        }

        return target == YouTubeOpenLinksInKind.BrowserWindow ? YouTubeLinkAction.BrowserWindow : YouTubeLinkAction.Play;
    }
}
