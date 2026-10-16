namespace NeonSidekick.Viewer;

/// <summary>
/// The video window as the chat sees it (2026-10-05, the YouTube plan): play a video, send it a command, read what it last
/// reported, close it. <see cref="VideoWindow.Player"/> is the real one (Windows, WebView2), <c>FakeVideoPlayer</c> the tests';
/// <c>Program</c> passes null where there is no window to have. Portable: nothing here touches Windows.
/// </summary>
public interface IVideoPlayer
{
    /// <summary>
    /// The window on <paramref name="request"/>'s video: opened (shown without taking the keyboard, where it last closed) or
    /// the open one switched to it. Throws <see cref="InvalidOperationException"/> with the reason (<see cref="VideoText"/>)
    /// when no window can be had: no WebView2 Runtime, a window that would not open.
    /// </summary>
    void Play(VideoRequest request);

    /// <summary>A command for the open video; false when no window is open. It takes effect as the player gets to it.</summary>
    bool Send(VideoCommand command);

    /// <summary>What the open window last reported, or null when none is open. Read from any thread; never blocks.</summary>
    VideoSnapshot? Snapshot { get; }

    /// <summary>
    /// Each new snapshot as the window reports it, and null as the window closes. Raised on the window's thread: a handler
    /// must not block.
    /// </summary>
    event Action<VideoSnapshot?>? Changed;

    /// <summary>The window closed; true when one was open.</summary>
    bool Close();

    /// <summary>
    /// Where this player plays (2026-10-16, <c>YouTube open links in</c>): the video window, or the Web UI's YouTube tab. A router says its
    /// active one's, so a sentence with no snapshot to read can still name the place.
    /// </summary>
    VideoPlace Place => VideoPlace.Window;
}

/// <summary>Where the app's own players play (2026-10-16): the video window, or the Web UI's YouTube tab.</summary>
public enum VideoPlace
{
    /// <summary>The app's video window: the zero value, so the PC's snapshots need no change.</summary>
    Window,

    /// <summary>The Web UI's YouTube tab, on the user's phone or browser.</summary>
    WebTab,
}

/// <summary>
/// A video waiting for a YouTube tab to open (2026-10-16): whether an open Web UI page was asked to switch to it and will see the ask soon
/// (<see cref="Nudged"/>), since when it waits, and (later that day) whether YouTube tabs are open but none is in view
/// (<see cref="TabsHidden"/>: a locked phone, a tab in the background), so it plays as soon as one comes into view.
/// </summary>
public sealed record VideoHold(bool Nudged, DateTimeOffset Since, bool TabsHidden = false);

/// <summary>A player that another side can take playback from by itself (2026-10-16: the Web UI's YouTube tab, its Resume tap).</summary>
public interface IVideoTakeover
{
    /// <summary>The player started a video on its own; raised outside its lock.</summary>
    event Action? TookOver;
}

/// <summary>
/// A video to play: YouTube's 11-character id, where to start (seconds), and whether to start at once or wait cued. Or a public
/// playlist (2026-10-08, the user's ask: <see cref="Playlist"/>), played by YouTube's own player, which moves through it itself:
/// its id, the entry to start at (0-based), and the video to start at instead when one is named (a <c>watch?v=…&amp;list=…</c> link
/// without its <c>index</c>; the page finds it in the list).
/// </summary>
public sealed record VideoRequest
{
    public VideoRequest(string videoId, double start = 0, bool autoplay = true)
    {
        if (!IsVideoId(videoId))
        {
            throw new ArgumentException(VideoText.NotAnId(videoId), nameof(videoId));
        }

        VideoId = videoId;
        Start = double.IsFinite(start) && start > 0 ? start : 0;
        Autoplay = autoplay;
    }

    private VideoRequest(string playlistId, int entry, string? videoId, double start, bool autoplay)
    {
        if (!IsPlaylistId(playlistId))
        {
            throw new ArgumentException(VideoText.NotAPlaylistId(playlistId), nameof(playlistId));
        }

        if (videoId is not null && !IsVideoId(videoId))
        {
            throw new ArgumentException(VideoText.NotAnId(videoId), nameof(videoId));
        }

        PlaylistId = playlistId;
        Entry = Math.Max(0, entry);
        VideoId = videoId;
        Start = double.IsFinite(start) && start > 0 ? start : 0;
        Autoplay = autoplay;
    }

    /// <summary>
    /// The playlist <paramref name="playlistId"/> from its entry <paramref name="entry"/> (0-based), or from <paramref name="videoId"/>
    /// when that is named and in the list. Throws <see cref="ArgumentException"/> on an id of the wrong shape.
    /// </summary>
    public static VideoRequest Playlist(string playlistId, int entry = 0, string? videoId = null, double start = 0, bool autoplay = true) =>
        new(playlistId, entry, videoId, start, autoplay);

    /// <summary>The video's id; null for a playlist started at an entry (the player says which video that is).</summary>
    public string? VideoId { get; }

    /// <summary>The playlist's id, or null for one video.</summary>
    public string? PlaylistId { get; }

    /// <summary>The playlist entry to start at, 0-based; 0 for one video.</summary>
    public int Entry { get; }

    public double Start { get; }

    public bool Autoplay { get; }

    /// <summary>Whether <paramref name="text"/> has the shape of a YouTube video id: 11 of A–Z, a–z, 0–9, - and _.</summary>
    public static bool IsVideoId(string? text) =>
        text is { Length: 11 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Whether <paramref name="text"/> has the shape of a YouTube playlist id (2026-10-08): 13 to 64 of A–Z, a–z, 0–9, - and _
    /// (<c>PL…</c> is 34, a channel's uploads <c>UU…</c> 24, a mix <c>RD…</c> longer); never an 11-character video id.
    /// </summary>
    public static bool IsPlaylistId(string? text) =>
        text is { Length: >= 13 and <= 64 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>What a command does to the open video.</summary>
public enum VideoCommandKind
{
    Play,
    Pause,
    Stop,
    Seek,
    Volume,
    Mute,
    Unmute,

    /// <summary>The playlist's next entry (2026-10-08).</summary>
    Next,

    /// <summary>The playlist's entry before.</summary>
    Previous,

    /// <summary>The playlist's entry <see cref="VideoCommand.Value"/> (0-based).</summary>
    PlayAt,
}

/// <summary>A command for the open video; <see cref="Value"/> is the seconds of a seek and the 0–100 of a volume.</summary>
public sealed record VideoCommand(VideoCommandKind Kind, double Value = 0)
{
    public static VideoCommand Play { get; } = new(VideoCommandKind.Play);

    public static VideoCommand Pause { get; } = new(VideoCommandKind.Pause);

    public static VideoCommand Stop { get; } = new(VideoCommandKind.Stop);

    public static VideoCommand Mute { get; } = new(VideoCommandKind.Mute);

    public static VideoCommand Unmute { get; } = new(VideoCommandKind.Unmute);

    public static VideoCommand Next { get; } = new(VideoCommandKind.Next);

    public static VideoCommand Previous { get; } = new(VideoCommandKind.Previous);

    /// <summary>The playlist's entry <paramref name="entry"/>, 0-based (never before the first).</summary>
    public static VideoCommand PlayAt(int entry) => new(VideoCommandKind.PlayAt, Math.Max(0, entry));

    /// <summary>To <paramref name="seconds"/> from the start (never before it).</summary>
    public static VideoCommand Seek(double seconds) => new(VideoCommandKind.Seek, double.IsFinite(seconds) ? Math.Max(0, seconds) : 0);

    /// <summary>The volume, held to 0–100 (YouTube's scale).</summary>
    public static VideoCommand Volume(double level) => new(VideoCommandKind.Volume, double.IsFinite(level) ? Math.Clamp(Math.Round(level), 0, 100) : 0);
}

/// <summary>
/// The player's state: YouTube's own (<c>YT.PlayerState</c>: -1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued)
/// and the window's two before and beside it — opening (the window is up, the player not yet) and failed (WebView2 refused).
/// </summary>
public enum VideoState
{
    Opening,
    Unstarted,
    Ended,
    Playing,
    Paused,
    Buffering,
    Cued,
    Failed,
}

/// <summary>
/// What the window last reported (<see cref="IVideoPlayer.Snapshot"/>): the state, the video (its id, and its title and channel
/// once the player knows them), where it is and how long it runs (seconds), the volume (0–100) and mute, YouTube's error for
/// this video (<see cref="Error"/>, its <c>onError</c> code: 2 a bad id, 5 HTML5, 100 not found, 101 or 150 not playable
/// embedded — 150 for a missing video too, the spike found), the window's own failure as a sentence, and a count that grows
/// with every report (<see cref="Version"/>), so a caller can wait for a newer one. While a playlist plays (2026-10-08), its id,
/// the entry playing (0-based) and how many the player has of it; <see cref="Skipping"/> marks an error the page is moving past
/// to the next entry, which ends no wait.
/// </summary>
public sealed record VideoSnapshot(
    VideoState State,
    string? VideoId,
    string? Title = null,
    string? Author = null,
    double Position = 0,
    double Duration = 0,
    int Volume = 100,
    bool Muted = false,
    int? Error = null,
    string? Failure = null,
    long Version = 0,
    string? PlaylistId = null,
    int? PlaylistEntry = null,
    int? PlaylistCount = null,
    bool Skipping = false)
{
    /// <summary>Where it plays (2026-10-16): the video window, or the Web UI's YouTube tab, stamped by the web player after each report.</summary>
    public VideoPlace Place { get; init; }

    /// <summary>A video waiting for a YouTube tab (2026-10-16); its state then reads <see cref="VideoState.Opening"/>.</summary>
    public VideoHold? Held { get; init; }

    /// <summary>
    /// A video the Web UI's page could start only muted (2026-10-16, a phone's autoplay rules): a tap on it brings the sound. Read by
    /// <see cref="VideoMessages.Apply"/> from a state report's <c>gesture</c>, true only when that field is <c>true</c>.
    /// </summary>
    public bool NeedsTap { get; init; }

    /// <summary>The window up, nothing heard from the player yet, <paramref name="videoId"/> asked for.</summary>
    public static VideoSnapshot Opening(string? videoId) => new(VideoState.Opening, videoId);

    /// <summary>The window up, nothing heard from the player yet, <paramref name="request"/> asked for: its video, or its playlist.</summary>
    public static VideoSnapshot Opening(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(VideoState.Opening, request.VideoId, PlaylistId: request.PlaylistId);
    }
}
