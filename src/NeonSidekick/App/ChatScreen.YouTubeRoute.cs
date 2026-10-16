using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.App;

// ── YouTube open links in (2026-10-16, the user's ask) ──────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The router over the video window and the Web UI's player (<see cref="RoutedVideoPlayer"/>); null with no video window.</summary>
    private readonly RoutedVideoPlayer? _routedVideo;

    /// <summary>The Web UI's YouTube tab as a player (2026-10-16); null with no video window to route beside.</summary>
    private readonly WebUi.WebVideoPlayer? _webVideo;

    /// <summary>The router (tests).</summary>
    internal RoutedVideoPlayer? RoutedVideoForTests => _routedVideo;

    /// <summary>The Web UI's player (tests).</summary>
    internal WebUi.WebVideoPlayer? WebVideoForTests => _webVideo;

    /// <summary>Whether the Web UI serves now (tests).</summary>
    internal bool WebUiServingForTests => _webUiController.Serving;

    private const string YouTubeCategory = "YouTube";

    /// <summary>
    /// Where a play goes now, worked out once for the play, the spinner's words and the tools' descriptions, so they never disagree: the
    /// setting, with <c>webui-window</c> falling back to the video window while the Web UI isn't serving (or there is no web player) or, since
    /// 2026-10-16, while <i>Web UI YouTube controls</i> is off (there is no tab), and
    /// <c>browser-window</c> to the default browser where the Browser window can't take it (none here, as on a Mac; no runtime; a bot check
    /// waiting in it). The note leads a play's answer when a fallback was taken.
    /// </summary>
    private (YouTubeOpenLinksInKind Kind, string? Note) PlayTarget()
    {
        var effective = _effective();
        var setting = YouTubeOpenLinksIn.Resolve(effective);
        switch (setting)
        {
            case YouTubeOpenLinksInKind.WebUiWindow:
                if (_routedVideo is not { HasWeb: true } || !effective.WebUi || !_webUiController.Serving)
                {
                    return (YouTubeOpenLinksInKind.YouTubeWindow, YouTubeText.WebUiOffFellBack);
                }

                return effective.WebUiYouTubeControls ? (setting, null) : (YouTubeOpenLinksInKind.YouTubeWindow, YouTubeText.WebUiControlsOffFellBack);
            case YouTubeOpenLinksInKind.BrowserWindow:
                // No window at all is tested first: on a Mac _browserWindow?.Unavailable() reads null, which would look like "available".
                if (_browserWindow is null)
                {
                    return (YouTubeOpenLinksInKind.DefaultBrowser, YouTubeText.BrowserFellBack(YouTubeText.NoBrowserWindowHere));
                }

                return _browserWindow.Unavailable() is { } reason
                    ? (YouTubeOpenLinksInKind.DefaultBrowser, YouTubeText.BrowserFellBack(reason))
                    : (setting, null);
            default:
                return (setting, null);
        }
    }

    /// <summary>
    /// A play routed where <see cref="PlayTarget"/> says: the video window or the Web UI's tab (played on the router, then waited for by the
    /// tool), or a browser (opened, recorded as a hand-off, answered at once). A window that can't open throws, as a play does.
    /// </summary>
    private VideoRouted RouteVideo(VideoRequest request)
    {
        var router = _routedVideo ?? throw new InvalidOperationException(YouTubeText.NoWindowHere);
        var (target, note) = PlayTarget();
        string setting = YouTubeOpenLinksIn.Name(YouTubeOpenLinksIn.Resolve(_effective()));
        string url = YouTubeIds.UrlFor(request);
        DiagnosticLog.Info(YouTubeCategory, $"YouTube open links in {setting}: {url} to {YouTubeOpenLinksIn.Name(target)}" + (note is null ? "." : " (" + note.Trim() + ")"));
        switch (target)
        {
            case YouTubeOpenLinksInKind.WebUiWindow:
                router.Play(request, VideoSink.Web);
                return new VideoRouted(true, null, note);
            case YouTubeOpenLinksInKind.BrowserWindow:
            case YouTubeOpenLinksInKind.DefaultBrowser:
                return HandOff(router, request, url, target, note);
            default:
                router.Play(request, VideoSink.Window);
                return new VideoRouted(true, null, note);
        }
    }

    // A video sent to a browser: the Browser window through the one open every caller uses, or the default browser (the counted site).
    private VideoRouted HandOff(RoutedVideoPlayer router, VideoRequest request, string url, YouTubeOpenLinksInKind to, string? note)
    {
        if (to == YouTubeOpenLinksInKind.BrowserWindow)
        {
            OpenInBrowser(new Uri(url), sink: null);
        }
        else
        {
            try
            {
                _web.OpenUrl(url);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
            {
                return new VideoRouted(false, Web.WebText.OpenFailed(url, e.Message));   // its Error: leads the answer
            }
        }

        var handoff = new VideoHandoff(url, to, request.VideoId, request.PlaylistId);
        router.Handoff(handoff);
        return new VideoRouted(false, YouTubeText.HandedOff(handoff), note);
    }

    /// <summary>A play tool for <c>/youtube</c>'s own use, with the routing the model's has.</summary>
    private YouTubePlayTool PlayToolFor(IVideoPlayer player) =>
        new(player, _effective, _time, YouTubeLibraryNow, _routedVideo is null ? null : RouteVideo, () => PlayTarget().Kind);

    /// <summary>
    /// A link the video window opens (2026-10-16, <see cref="YouTubeLinkRoute"/>), called on the window's own thread (WebView2's, or the
    /// Mac's AppKit thread): anything but a video or <c>list=</c> link, and every link under <c>default-browser</c>, goes to
    /// <paramref name="defaultBrowser"/> at once, as before. A video plays through the route on a pool thread (never on the window's: a play
    /// can wait 10 s on the window's own gate), the answer flashed on the hint row; <c>browser-window</c> opens the page beside the window.
    /// Whatever fails goes to <paramref name="defaultBrowser"/>.
    /// </summary>
    public void RouteVideoLink(string url, Action<string> defaultBrowser)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(defaultBrowser);
        var action = _videoPlayer is null ? YouTubeLinkAction.DefaultBrowser : YouTubeLinkRoute.Decide(PlayTarget().Kind, url, _videoPlayer.Snapshot?.VideoId);
        DiagnosticLog.Info(YouTubeCategory, $"The video window opened {url}: {action}.");
        if (action == YouTubeLinkAction.DefaultBrowser)
        {
            defaultBrowser(url);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (action == YouTubeLinkAction.BrowserWindow)
                {
                    _pane.Flash(OpenInBrowser(new Uri(url), sink: null));
                    return;
                }

                var arguments = new Microsoft.Extensions.AI.AIFunctionArguments { [YouTubePlayTool.VideoArgument] = url };
                string answer = (string)(await PlayToolFor(_videoPlayer!).InvokeAsync(arguments, CancellationToken.None).ConfigureAwait(false))!;
                if (answer.StartsWith("Error: ", StringComparison.Ordinal))
                {
                    DiagnosticLog.Info(YouTubeCategory, $"The video window's link {url} did not play ({answer}); the default browser has it.");
                    defaultBrowser(url);
                    return;
                }

                _pane.Flash(answer);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DiagnosticLog.Warn(YouTubeCategory, $"The video window's link {url} could not be routed: {e.Message}; the default browser has it.");
                try
                {
                    defaultBrowser(url);
                }
                catch (Exception again) when (again is not OutOfMemoryException)
                {
                    DiagnosticLog.Warn(YouTubeCategory, $"Could not open {url} in the browser: {again.Message}");
                }
            }
        });
    }

    /// <summary>
    /// <c>open_url</c>'s YouTube hook (2026-10-16): a video or <c>list=</c> link played through youtube_play's own tool (so a saved video resumes,
    /// a <c>list=</c> link plays as a playlist, a mix's link as its video, and the wait and words are youtube_play's), its answer the link's
    /// line, by <see cref="YouTubeOpenUrl"/> (its rules: what passes on, the loop guard, the notes).
    /// </summary>
    private Task<YouTubeLinkOutcome> OpenYouTubeForToolAsync(Uri url, CancellationToken cancellationToken) =>
        YouTubeOpenUrl.OpenAsync(url, _effective().YouTubeTools, _videoPlayer, () => PlayToolFor(_videoPlayer!), PlayTarget().Kind, cancellationToken);

    /// <summary>A control tool for <c>/youtube</c>'s own use, with the hand-off the model's reads.</summary>
    private YouTubeControlTool ControlToolFor(IVideoPlayer player) =>
        new(player, _time, () => _routedVideo?.LastHandoff, () => PlayTarget().Kind);
}
