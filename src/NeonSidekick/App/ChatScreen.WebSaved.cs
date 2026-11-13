using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;
using NeonSidekick.YouTube;

namespace NeonSidekick.App;

// ── The Web UI's Video tab: its saved list (later on 2026-10-16, the user's ask: /youtube list under the player) ──
// Play or resume, remove, clear all, move, filter (on the page) and each video's details, plus the two extras the user chose: save the playing
// video, and thumbnails. Every hook runs on a Kestrel thread against state that is safe there — the library's one static lock over the profile's
// youtube.json, the router's own lock — and never waits on the screen or writes the transcript: the PC hears of a change on its hint row
// (FlashPc) and in the log (no titles). A tap from a page that only waits claims the player first (WebVideoPlayer.Claim), then plays there.

internal sealed partial class ChatScreen
{
    /// <summary>Whether a lookup of the saved videos' missing titles runs now (1) or not (0): one at a time, started by a read.</summary>
    private int _webSavedLooking;

    /// <summary>How long a lookup of missing titles may run.</summary>
    private static readonly TimeSpan WebSavedLookupCap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The saved list as the tab shows it: the videos in the user's order, each with the PC pane's footer as two lines in the PC's time zone; the
    /// video the player has open (and whether it is saved) for the ☆, and its playlist only when that can be saved (2026-11-08, the review:
    /// <see cref="YouTubeLibrary.CanSavePlaylist"/>, so the page never offers a save that is refused); a missing title's lookup started on the
    /// pool when none runs. A saved playlist's thumbnail is the video it was left on, else the list's own.
    /// </summary>
    private WebSavedVideos WebSavedNow()
    {
        var library = YouTubeLibraryNow();
        var videos = library.List();
        var zone = _time.LocalTimeZone;
        var items = videos.Select(v =>
        {
            var footer = YouTubeText.SavedFooter(v, zone);
            return new WebSavedVideo(v.Id, v.Title, v.Author, YouTubeText.Place(v), v.Watched, footer.Text, footer.Last ?? "", v.Playlist, v.Playlist ? v.EntryVideo ?? v.Thumbnail : null);
        }).ToList();

        // The ☆s' states from the list read above, not two more reads of the file (2026-11-08, the review).
        WebSavedPlaying? playing = _videoPlayer?.Snapshot is { VideoId: { } id, State: not VideoState.Failed, Held: null } open
            ? new WebSavedPlaying(id, open.Title, videos.Any(v => v.Id == id),
                open.PlaylistId is { } list && YouTubeLibrary.CanSavePlaylist(list) ? list : null,
                open.PlaylistId is { } saved && videos.Any(v => v.Id == saved))
            : null;
        bool looking = Volatile.Read(ref _webSavedLooking) == 1;
        if (!looking && YouTubeTitles.AnyMissing(library) && Interlocked.CompareExchange(ref _webSavedLooking, 1, 0) == 0)
        {
            looking = true;
            var lookup = YouTubeTitles.Lookup(_youTubeSearch, _effective);
            var playlistLookup = YouTubeTitles.PlaylistLookup(_youTubeSearch, _effective);
            _ = Task.Run(async () =>
            {
                using var cap = new CancellationTokenSource(WebSavedLookupCap);
                try
                {
                    await YouTubeTitles.FillMissingAsync(library, lookup, playlistLookup, cap.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException)
                {
                    DiagnosticLog.Debug(WebUiCategory, "Web UI: the saved videos' titles could not be looked up: " + e.Message);
                }
                finally
                {
                    Volatile.Write(ref _webSavedLooking, 0);
                }
            });
        }

        return new WebSavedVideos(items, YouTubeText.SavedCaption(videos), YouTubeText.NoneSaved, looking, playing);
    }

    /// <summary>
    /// A saved video played on the tapping page: the page claims the player (a waiting page's tap is its Play here), then the video plays on the
    /// router's web player directly — not through <see cref="RouteVideo"/>: a play started on the tab plays on the tab whatever <i>YouTube open
    /// links in</i> says (the user's call, twice on 2026-10-16), a playing PC window closed by the router's switch with its place saved. Nothing
    /// waits: the page's own reports drive the snapshot. A saved playlist (2026-11-08) plays from its entry and place, as youtube_play's resume
    /// gives them (<see cref="YouTubePlayTool.ResumeOf"/>), from the one read of the list. Autoplay stays on whatever the setting says: the tap
    /// is the user's own gesture to play.
    /// </summary>
    private WebReply WebPlaySaved(WebSavedPlay request, IPAddress? remote)
    {
        if (request.Id is not { } id || request.Page is not { } page || !WebVideoPlayer.IsId(page))
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        var library = YouTubeLibraryNow();
        if (library.Find(id) is not { } saved)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.SavedVideoGone);
        }

        if (_webVideo is not { } web || _routedVideo is not { } router)
        {
            return new WebReply(WebOutcome.NotFound);
        }

        if (web.Claim(page, remote) == WebOutcome.Conflict)
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.YouTubePageNotConnected);
        }

        if (saved.Playlist)
        {
            var (entry, video, from, lead) = YouTubePlayTool.ResumeOf(saved);
            router.Play(VideoRequest.Playlist(id, entry, video, from), VideoSink.Web);
            _webUi.FlashPc(WebUiText.SavedPlayFlash);
            DiagnosticLog.Info(WebUiCategory, WebUiText.SavedLogLine("played", remote, playlist: true));
            return new WebReply(WebOutcome.Ok, lead.Length > 0 ? lead.Trim() : null);
        }

        double start = YouTubeLibrary.ResumeStart(saved.Position);
        router.Play(new VideoRequest(id, start), VideoSink.Web);
        _webUi.FlashPc(WebUiText.SavedPlayFlash);
        DiagnosticLog.Info(WebUiCategory, WebUiText.SavedLogLine("played", remote));
        return new WebReply(WebOutcome.Ok, start > 0 ? YouTubeText.Resumed(start).Trim() : null);
    }

    /// <summary>A saved video removed, the page's two taps having been the yes: youtube_save's remove.</summary>
    private WebReply WebRemoveSaved(WebSavedId request, IPAddress? remote)
    {
        if (request.Id is not { Length: > 0 } id)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        var library = YouTubeLibraryNow();
        if (library.Find(id) is null)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.SavedVideoGone);
        }

        return SavedChange(YouTubeSaveTool.Remove(library, _videoPlayer, id), WebUiText.SavedRemoveFlash, "removed", remote);
    }

    /// <summary>Every saved video removed, the page's two taps having been the yes: <c>/youtube list --clear</c>'s answer.</summary>
    private WebReply WebClearSaved(IPAddress? remote) =>
        SavedChange(YouTubeText.ClearAnswer(YouTubeLibraryNow().Clear()), WebUiText.SavedClearFlash, "cleared", remote);

    /// <summary>A saved video moved: up, down, top or bottom (the pane's Shift/Alt+↑↓ and Home/End), silently as there.</summary>
    private WebReply WebMoveSaved(WebSavedMove request, IPAddress? remote)
    {
        int? delta = request.To switch
        {
            "up" => -1,
            "down" => 1,
            "top" => int.MinValue,
            "bottom" => int.MaxValue,
            _ => null,
        };
        if (request.Id is not { Length: > 0 } id || delta is not { } by)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.SavedMoveUnknown);
        }

        var library = YouTubeLibraryNow();
        if (library.Find(id) is null)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.SavedVideoGone);
        }

        if (!library.Move(id, by))
        {
            return new WebReply(WebOutcome.Refused, YouTubeText.SaveFailed);   // found a moment ago: the write failed (logged)
        }

        _webUi.FlashPc(WebUiText.SavedMoveFlash);
        DiagnosticLog.Info(WebUiCategory, WebUiText.SavedLogLine("moved", remote));
        return WebReply.Ok;
    }

    /// <summary>The video playing saved (the ☆): youtube_save's <c>current</c>.</summary>
    private WebReply WebSaveCurrent(IPAddress? remote) =>
        SavedChange(YouTubeSaveTool.Add(YouTubeLibraryNow(), _videoPlayer, ""), WebUiText.SavedSaveFlash, "saved", remote);

    /// <summary>
    /// The playlist playing saved (2026-11-08, the ☆ list): youtube_save's playlist <c>current</c>, at its entry and place. No lookup here (a
    /// Kestrel thread): the next read of the list looks its title up, as for any untitled row.
    /// </summary>
    private WebReply WebSaveCurrentList(IPAddress? remote) =>
        SavedChange(YouTubeSaveTool.AddPlaylist(YouTubeLibraryNow(), _videoPlayer, YouTubeSaveTool.Current), WebUiText.SavedSaveListFlash, "saved", remote, playlist: true);

    // An answer from the shared path: a sentence starting "Error: " refused (422) without it; else the PC told, the log written, the sentence back.
    private WebReply SavedChange(string answer, string flash, string what, IPAddress? remote, bool playlist = false)
    {
        if (answer.StartsWith("Error: ", StringComparison.Ordinal))
        {
            return new WebReply(WebOutcome.Refused, answer["Error: ".Length..]);
        }

        _webUi.FlashPc(flash);
        DiagnosticLog.Info(WebUiCategory, WebUiText.SavedLogLine(what, remote, playlist));
        return new WebReply(WebOutcome.Ok, answer);
    }
}
