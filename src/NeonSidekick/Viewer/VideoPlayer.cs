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
}

/// <summary>A video to play: YouTube's 11-character id, where to start (seconds), and whether to start at once or wait cued.</summary>
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

    public string VideoId { get; }

    public double Start { get; }

    public bool Autoplay { get; }

    /// <summary>Whether <paramref name="text"/> has the shape of a YouTube video id: 11 of A–Z, a–z, 0–9, - and _.</summary>
    public static bool IsVideoId(string? text) =>
        text is { Length: 11 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
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
}

/// <summary>A command for the open video; <see cref="Value"/> is the seconds of a seek and the 0–100 of a volume.</summary>
public sealed record VideoCommand(VideoCommandKind Kind, double Value = 0)
{
    public static VideoCommand Play { get; } = new(VideoCommandKind.Play);

    public static VideoCommand Pause { get; } = new(VideoCommandKind.Pause);

    public static VideoCommand Stop { get; } = new(VideoCommandKind.Stop);

    public static VideoCommand Mute { get; } = new(VideoCommandKind.Mute);

    public static VideoCommand Unmute { get; } = new(VideoCommandKind.Unmute);

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
/// with every report (<see cref="Version"/>), so a caller can wait for a newer one.
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
    long Version = 0)
{
    /// <summary>The window up, nothing heard from the player yet, <paramref name="videoId"/> asked for.</summary>
    public static VideoSnapshot Opening(string? videoId) => new(VideoState.Opening, videoId);
}
