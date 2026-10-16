using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_play(video, start?)</c> (2026-10-05, the YouTube plan): a YouTube video in the app's video window — by its id or any
/// YouTube link (<see cref="YouTubeIds"/>), from <c>start</c> or the link's own time — waiting for the player to start it, cue it
/// (<c>YouTube autoplay</c> off) or refuse it (<see cref="YouTubeWait.Play"/>), and answering with what it reports
/// (<see cref="YouTubeText.Status"/>). One window: a second play switches it. Needs no key. Never headless: there is no window.
/// A saved video played with no time named resumes where it was left (2026-10-07, <see cref="YouTubeLibrary.ResumeAt"/>), and the
/// answer says so (<see cref="YouTubeText.Resumed"/>); a <c>start</c> or a link's own time wins.
///
/// <para>A public playlist (2026-10-08, the user's ask): <c>playlist</c> (an id, a link with <c>list=</c>, or a channel for its uploads),
/// or such a link as <c>video</c>, from <c>entry</c> (1-based) or the link's own place. YouTube's player plays it and moves through it
/// itself (<see cref="VideoRequest.Playlist"/>); no saved place is resumed in one. The wait ends at any entry of that list playing,
/// passing over an entry the page is skipping (<see cref="VideoSnapshot.Skipping"/>).</para>
///
/// <para>Where a play goes (2026-10-16, <c>YouTube open links in</c>): through the route hook, to the video window or the Web UI's YouTube
/// tab (waited for) or a browser (answered at once, out of the app's control). Stopping the wait stops only the wait: a held video stays
/// held and plays when a tab opens, as a window plays on today.</para>
/// </summary>
public sealed class YouTubePlayTool : AIFunction
{
    public const string ToolName = "youtube_play";
    public const string VideoArgument = "video";
    public const string StartArgument = "start";
    public const string PlaylistArgument = "playlist";
    public const string EntryArgument = "entry";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "video": { "type": "string", "description": "The video's 11-character id (from youtube_search) or a YouTube link. Leave it out to play a playlist from its first entry." },
            "start": { "type": "string", "description": "Where to start: seconds (90) or a time (1:30). Leave it out to start at the beginning, or at a link's own t=." },
            "playlist": { "type": "string", "description": "A public playlist to play through: its id (PL…), a link with list=, or a channel's id (UC…) for its uploads. With video, the playlist starts at that video." },
            "entry": { "type": "integer", "description": "With playlist: the entry to start at, 1 for the first." }
          }
        }
        """);

    private readonly IVideoPlayer _player;
    private readonly Func<AppSettingsData> _effective;
    private readonly TimeProvider _time;
    private readonly Func<YouTubeLibrary>? _library;
    private readonly Func<VideoRequest, VideoRouted>? _route;
    private readonly Func<YouTubeOpenLinksInKind>? _target;

    /// <param name="library">The profile's saved videos, whose places a play with no time resumes; null for none.</param>
    /// <param name="route">
    /// Where a play goes (2026-10-16, <c>YouTube open links in</c>; <c>ChatScreen.RouteVideo</c>): played and waited for, or sent to a
    /// browser with its answer; null plays on <paramref name="player"/>, as before. A window that can't open throws out of it as a play does.
    /// </param>
    /// <param name="target">Where a play would go now, for the description (<c>ChatScreen.PlayTarget</c>); null is the video window.</param>
    public YouTubePlayTool(IVideoPlayer player, Func<AppSettingsData> effective, TimeProvider? time = null, Func<YouTubeLibrary>? library = null,
        Func<VideoRequest, VideoRouted>? route = null, Func<YouTubeOpenLinksInKind>? target = null)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _time = time ?? TimeProvider.System;
        _library = library;
        _route = route;
        _target = target;
    }

    public override string Name => ToolName;

    /// <summary>Follows <c>YouTube open links in</c> (2026-10-16), read at each turn; the window's is the text before it, byte for byte. Pinned.</summary>
    public override string Description => DescriptionFor(_target?.Invoke() ?? YouTubeOpenLinksInKind.YouTubeWindow);

    /// <summary>The description where a play goes to <paramref name="target"/>.</summary>
    public static string DescriptionFor(YouTubeOpenLinksInKind target) => target switch
    {
        YouTubeOpenLinksInKind.WebUiWindow =>
            "Plays a YouTube video, with sound, in the Web UI's YouTube tab on the user's phone or browser (one player: a new video replaces the one playing; with no tab open it waits for one). " +
            Give + Then + Saved,
        YouTubeOpenLinksInKind.BrowserWindow =>
            "Opens a YouTube video in the app's Browser window on the user's screen. Once it opens it is out of your control: " + YouTubeControlTool.ToolName + " and " + YouTubeStatusTool.ToolName + " can't see or drive it. " +
            Give + Saved,
        YouTubeOpenLinksInKind.DefaultBrowser =>
            "Opens a YouTube video in the user's default browser. Once it opens it is out of your control: " + YouTubeControlTool.ToolName + " and " + YouTubeStatusTool.ToolName + " can't see or drive it. " +
            Give + Saved,
        _ =>
            "Plays a YouTube video, with sound, in the app's video window on the user's screen (one window: a new video replaces the one playing). " +
            Give + Then + Saved,
    };

    private const string Give = "Give the id from " + YouTubeSearchTool.ToolName + " or a link the user pasted, or a playlist to play through. Play only what the user asked for. ";

    private const string Then = "Then " + YouTubeControlTool.ToolName + " pauses, seeks, sets the volume or moves through a playlist, and " + YouTubeStatusTool.ToolName + " says where it is. ";

    private const string Saved = "A saved video (" + YouTubeSavedTool.ToolName + ") resumes where it was left unless start is given.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string video = ToolArguments.ReadString(arguments, VideoArgument).Trim();
        string playlist = ToolArguments.ReadString(arguments, PlaylistArgument).Trim();
        if (!ToolArguments.TryReadInt32(arguments, EntryArgument, out int? entry, out string raw))
        {
            return ClockText.BadInteger(EntryArgument, raw);
        }

        string startText = ToolArguments.ReadString(arguments, StartArgument).Trim();
        double? startNamed = null;
        if (startText.Length > 0)
        {
            if (YouTubeIds.ParseTime(startText) is not { } seconds)
            {
                return YouTubeText.NotATime(StartArgument, startText);
            }

            startNamed = seconds;
        }

        // A playlist: named as one, or a link with list= given as the video (2026-10-08).
        if (playlist.Length > 0 || (video.Length > 0 && !VideoRequest.IsVideoId(video) && YouTubeIds.TryParsePlaylist(video, out _, out _, out _, out _)))
        {
            return await PlayPlaylistAsync(playlist.Length > 0 ? playlist : video, playlist.Length > 0 ? video : "", entry, startNamed, cancellationToken).ConfigureAwait(false);
        }

        if (!YouTubeIds.TryParse(video, out string id, out double linkStart))
        {
            return YouTubeText.NotAVideo(video);
        }

        double start = startNamed ?? linkStart;

        // A saved video with no time named picks up where it was left (2026-10-07).
        double resumed = startNamed is null && linkStart <= 0 && _library?.Invoke() is { } library ? library.ResumeAt(id) : 0;
        if (resumed > 0)
        {
            start = resumed;
        }

        long before = _player.Snapshot?.Version ?? -1;
        bool autoplay = _effective().YouTubeAutoplay;
        VideoRouted routed;
        try
        {
            routed = Route(new VideoRequest(id, start, autoplay));
        }
        catch (InvalidOperationException e)
        {
            return "Error: " + e.Message;
        }

        if (!routed.Wait)
        {
            // Sent to a browser (2026-10-16): nothing to wait for; the saved place went with the link's t=.
            return (routed.Note ?? "") + (resumed > 0 ? YouTubeText.Resumed(resumed) : "") + routed.Answer;
        }

        // Started, cued, ended at once or refused: anything but the window's own opening, and on the video asked for (its error
        // too, not a late one for the video before). The page holds its every-second report from a load until the player's next
        // change (player.html), so a replay of the same video at another time is not answered with the old position. Cued is an
        // answer only when the play asked for none (2026-10-07, found in the Mac live run and confirmed on Windows): a new window's
        // first report comes from the player's ready event, just before its playVideo shows, and read "Cued (press play to start)"
        // to the model and the saved session while the video started a second later. A play the browser keeps from starting stays
        // cued and is answered so at the wait's cap.
        // A video held for a YouTube tab with no page to see the ask (2026-10-16) is an answer at once: none will open within the wait.
        var snapshot = await YouTubeWait.UntilAsync(_player, before, s => s.Held is { Nudged: false } || (s.VideoId == id && (s.State is VideoState.Playing or VideoState.Paused or VideoState.Ended || (s.State == VideoState.Cued && !autoplay))), YouTubeWait.Play, _time, cancellationToken, video: id).ConfigureAwait(false);
        if (snapshot is null)
        {
            return (routed.Note ?? "") + YouTubeText.ClosedMeanwhileIn(_player.Place);
        }

        string status = YouTubeText.Status(snapshot);
        return (routed.Note ?? "") + (resumed > 0 && !status.StartsWith("Error: ", StringComparison.Ordinal) ? YouTubeText.Resumed(resumed) + status : status);
    }

    // The play through the route, or on the player as before the setting.
    private VideoRouted Route(VideoRequest request)
    {
        if (_route is not null)
        {
            return _route(request);
        }

        _player.Play(request);
        return VideoRouted.Waited;
    }

    // The playlist <paramref name="named"/> from entry (1-based), the video named in it, or the link's own place; waited for until an
    // entry of it plays (the named video, when there is one), or the player refuses an entry it is not moving past.
    private async Task<string> PlayPlaylistAsync(string named, string video, int? entry, double? startNamed, CancellationToken cancellationToken)
    {
        if (!YouTubeIds.TryParsePlaylist(named, out string list, out int linkEntry, out string? linkVideo, out double linkStart))
        {
            return YouTubeIds.NamesMix(named) ? YouTubeText.Mix(named) : YouTubeText.NotAPlaylist(named);
        }

        if (entry is < 1)
        {
            return YouTubeText.NotAnEntry(entry.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        }

        string? startVideo = linkVideo;
        if (video.Length > 0)
        {
            if (!YouTubeIds.TryParse(video, out string id, out _))
            {
                return YouTubeText.NotAVideo(video);
            }

            startVideo = id;
        }

        int startEntry = entry is { } one ? one - 1 : linkEntry;
        if (entry is not null)
        {
            startVideo = null;   // an entry named wins over a video in the link
        }

        bool autoplay = _effective().YouTubeAutoplay;
        long before = _player.Snapshot?.Version ?? -1;
        VideoRouted routed;
        try
        {
            routed = Route(VideoRequest.Playlist(list, startEntry, startVideo, startNamed ?? (startVideo is not null ? linkStart : 0), autoplay));
        }
        catch (InvalidOperationException e)
        {
            return "Error: " + e.Message;
        }

        if (!routed.Wait)
        {
            return (routed.Note ?? "") + routed.Answer;
        }

        // Paused is an answer only when the play asked for none (2026-10-09, seen on the real player): an open window switched to a
        // playlist first reports Paused on the list's entry, then buffers and plays it. A playlist the browser keeps from starting
        // is answered at the wait's cap with what the player then says.
        // A held plain list has no video id yet (2026-10-16): the held test comes first, or the wait would run to its cap.
        var snapshot = await YouTubeWait.UntilAsync(_player, before, s => s.Held is { Nudged: false } || (s.PlaylistId == list && s.VideoId is not null && (startVideo is null || s.VideoId == startVideo)
            && (s.State is VideoState.Playing or VideoState.Ended || (s.State is VideoState.Cued or VideoState.Paused && !autoplay))), YouTubeWait.Play, _time, cancellationToken).ConfigureAwait(false);
        return (routed.Note ?? "") + (snapshot is null ? YouTubeText.ClosedMeanwhileIn(_player.Place) : YouTubeText.Status(snapshot));
    }
}

/// <summary>
/// Where a play went (2026-10-16, <c>YouTube open links in</c>): <see cref="Wait"/> for a player of the app's own, whose report answers;
/// else <see cref="Answer"/>, a browser's line. <see cref="Note"/> leads the answer (a fallback's reason).
/// </summary>
public sealed record VideoRouted(bool Wait, string? Answer = null, string? Note = null)
{
    /// <summary>Played on a player of the app's own, no note.</summary>
    public static VideoRouted Waited { get; } = new(true);
}
