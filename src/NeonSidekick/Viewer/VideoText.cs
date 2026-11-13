using System.Globalization;

namespace NeonSidekick.Viewer;

/// <summary>
/// The video window's words (2026-10-05, the YouTube plan): its title, why it cannot open, and the WebView2 failure a status
/// reports. The YouTube tools' and <c>/youtube</c>'s own sentences live in <c>YouTubeText</c>. Pinned where a test says so.
/// </summary>
public static class VideoText
{
    /// <summary>The window's title before the player knows the video's, and what it ends with after.</summary>
    public const string AppTitle = "NeonSidekick video";

    /// <summary>Neither Windows nor a Mac: no window to play in.</summary>
    public const string Unavailable = "The video window needs Windows, or a Mac with macOS 14 or later; open_url can open the video in the browser instead.";

    /// <summary>
    /// On a Mac with no window to play in (2026-10-07): no window server (over SSH), a headless run, or macOS before 14 (the window's
    /// data store needs 14).
    /// </summary>
    public const string UnavailableMac = "The video window needs a desktop session and macOS 14 or later; open_url can open the video in the browser instead.";

    /// <summary>A Mac's video page whose web content process ended (a crash, or macOS took it back): the window closes with this as its failure.</summary>
    public const string ContentEnded = "The video window's web page stopped (its WebKit process ended); play the video again to reopen the window.";

    /// <summary>No WebView2 Runtime installed (it ships with Windows 11; Windows 10 may lack it).</summary>
    public const string NoRuntime = "The video window needs the Microsoft Edge WebView2 Runtime, which is not installed (Windows 11 has it; on Windows 10 it is a free download from Microsoft). open_url can open the video in the browser instead.";

    /// <summary><see cref="WebViewNative.LoaderFileName"/> is missing beside the exe: a broken install, not the machine.</summary>
    public const string NoLoader = "The video window cannot start: WebView2Loader.dll is missing beside NeonSidekick.exe. Reinstall the app; open_url can open the video in the browser meanwhile.";

    /// <summary>The window's title: the video's title and the app's, <c>Big Buck Bunny · NeonSidekick video</c>; the app's alone before the player knows it.</summary>
    public static string Title(string? videoTitle) =>
        string.IsNullOrWhiteSpace(videoTitle) ? AppTitle : videoTitle.Trim() + " · " + AppTitle;

    /// <summary>The not-a-video-id refusal of <see cref="VideoRequest"/>.</summary>
    public static string NotAnId(string? text) => $"\"{text}\" is not a YouTube video id (11 letters, digits, - or _).";

    /// <summary>The not-a-playlist-id refusal of <see cref="VideoRequest.Playlist"/> (2026-10-08).</summary>
    public static string NotAPlaylistId(string? text) => $"\"{text}\" is not a YouTube playlist id (13 to 64 letters, digits, - or _).";

    /// <summary>The not-a-full-path refusal of <see cref="VideoRequest.File"/> (2026-11-12): the caller resolves the path first.</summary>
    public static string NotAFullPath(string? text) => $"\"{text}\" is not a full path to a video file.";

    /// <summary>A video file sent to a player that doesn't play files (2026-11-12: the Mac's window); the file route opens the default app instead.</summary>
    public const string NoFilePlayer = "This video player doesn't play video files.";

    /// <summary>The window's log line for YouTube's refusal (moved here from the window, 2026-11-12).</summary>
    public static string YouTubeRefusedLogLine(string? videoId, int? code) => $"YouTube refused video {videoId} in the player (error {code}).";

    /// <summary>The window's log line for a video file the player couldn't play (2026-11-12): its name and the page's code.</summary>
    public static string FileRefusedLogLine(string? name, int? code) => $"The player could not play the video file {name} (error {code}).";

    // The way out every file error offers: the default app, which the model reaches through open and the user through /open.
    private const string OwnApp = " Open it in its own app instead (the open tool, or /open).";

    /// <summary>
    /// A video file's error as a sentence (2026-11-12): <c>MediaError</c>'s codes (1 aborted, 2 network, 3 decode, 4 not supported),
    /// the page's <see cref="VideoFile.NoPictureCode"/> and the app's <see cref="VideoFile.GoneCode"/>. Each but the gone one offers the
    /// default app. Read by the snapshot's kind: YouTube's codes overlap these.
    /// </summary>
    public static string FileError(int? code, string? name)
    {
        string file = string.IsNullOrWhiteSpace(name) ? "The video file" : name;
        return code switch
        {
            1 => $"Playing {file} stopped before it loaded; play it again.{OwnApp}",
            2 => $"{file} could not be read (the disk, the share or the network failed); play it again.{OwnApp}",
            3 => $"{file} could not be decoded: the file may be damaged, or its codec is one the player can't show.{OwnApp}",
            4 => $"{file} is in a format the player can't play (its codec or its container: an HEVC video or an MKV, say).{OwnApp}",
            VideoFile.NoPictureCode => $"{file} plays sound but no picture: its video codec isn't one the player can show (HEVC needs Microsoft's HEVC extension).{OwnApp}",
            VideoFile.GoneCode => $"{file} is no longer there: it was moved or deleted.",
            VideoFile.FunnelCode => $"{file} can't play on the Web UI page: it is reached over Tailscale Funnel, and Web UI video files over Funnel is off.",
            _ => $"{file} could not play (error {code?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}).{OwnApp}",
        };
    }

    // ---- video files: the tools', /video's and /view's sentences (2026-11-12, the video-files plan) ----

    /// <summary>No video file playing: <c>YouTubeText.NoVideo</c>'s file form. Pinned.</summary>
    public const string NoFile = "No video file is playing; play_video plays one.";

    /// <summary><see cref="NoFile"/> for <c>/video</c>, whose user plays with <c>/video &lt;path&gt;</c>. Pinned.</summary>
    public const string NoFileCommand = "No video file is playing; /video <path> plays one.";

    /// <summary>A path that is no video file (<see cref="VideoFile.Extensions"/>), or a folder so named.</summary>
    public static string NotAVideoFile(string? path) =>
        $"Error: {path} is not a video file; play_video plays {string.Join(", ", VideoFile.Extensions)} files.";

    /// <summary>The place a video file plays, in a sentence: the video window, the Web UI's tab or the default app.</summary>
    public static string PlaceName(VideoFilesPlayInKind kind) => kind switch
    {
        VideoFilesPlayInKind.WebUiWindow => YouTube.YouTubeText.WebTabName,
        VideoFilesPlayInKind.DefaultApp => "its default app",
        _ => "the video window",
    };

    /// <summary>The note before a file play's answer when <c>webui-window</c> fell back to the window: the Web UI isn't serving. Pinned.</summary>
    public const string WebUiOffFellBack = "The Web UI isn't serving, so the video file plays in the video window instead. ";

    /// <summary>The note when <c>webui-window</c> fell back to the window because <i>Web UI YouTube controls</i> is off (no tab). Pinned.</summary>
    public const string WebUiControlsOffFellBack = "Web UI YouTube controls is off, so the video file plays in the video window instead. ";

    /// <summary>The note when the video window couldn't take a file and the default app did: <paramref name="reason"/> leads.</summary>
    public static string DefaultAppFellBack(string reason) =>
        $"The video window can't play it ({reason.TrimEnd('.', ' ')}), so it opens in its default app instead. ";

    /// <summary>A screen with no video window at all (<see cref="DefaultAppFellBack"/>'s reason).</summary>
    public const string NoWindowHere = "there is no video window here";

    /// <summary>A video window that doesn't play files (a Mac's, for now; <see cref="DefaultAppFellBack"/>'s reason).</summary>
    public const string WindowPlaysNoFiles = "the video window here doesn't play files";

    /// <summary>Where <c>play_video</c>'s answer says the default app showed a file (<c>FileText.Opened</c>'s <c>where</c>). Pinned.</summary>
    public const string InVideoApp = " in the user's video app";

    /// <summary>
    /// <c>play_video</c>'s answer for a file sent to its default app: <paramref name="opened"/> (<c>FileText.Opened</c>'s "opened X in
    /// the user's video app"), capitalized, and that the app can't see or drive it there.
    /// </summary>
    public static string HandedOff(string opened)
    {
        ArgumentException.ThrowIfNullOrEmpty(opened);
        return char.ToUpperInvariant(opened[0]) + opened[1..] + "; it plays there, where video_control can't see or control it.";
    }

    /// <summary><c>video_control status</c> after a file went to its default app: where it went, and that none plays in the app's own players.</summary>
    public static string HandedOffStatus(string? name) =>
        $"The last video file, {name}, went to its default app, which the app can't see into; no video file is playing in its own players.";

    /// <summary><c>video_control</c> after a file went to its default app: nothing to drive.</summary>
    public static string HandedOffControl(string? name) =>
        $"Error: {name} is in its default app, which the app can't control; the user can pause or close it there.";

    /// <summary><c>video_control</c> (or <c>/video</c>) with a YouTube video open: the other family's tool, named when it is offered. Pinned.</summary>
    public static string YouTubeIsOpen(string? pointer) =>
        "Error: a YouTube video is open, not a video file" + (pointer is null ? "; this drives video files only." : "; " + pointer + " drives it.");

    /// <summary><c>youtube_control</c>/<c>youtube_status</c> (or <c>/youtube</c>) with a video file open: the other family's, named when offered. Pinned.</summary>
    public static string FileIsOpen(string? name, string? pointer) =>
        $"Error: a video file is open ({name}), not a YouTube video" + (pointer is null ? "; this drives YouTube videos only." : "; " + pointer + " drives it.");

    /// <summary><c>/youtube save</c> with a video file open: saves keep YouTube videos. Pinned.</summary>
    public static string NoSaveForFiles(string? name) => $"Error: {name} is a video file, not a YouTube video; /youtube save keeps YouTube videos.";

    /// <summary><c>/view</c> of a video with <c>--chat</c> or <c>--thumbs</c>, which are for pictures (<c>ViewPdfFlagsError</c>'s shape). Pinned.</summary>
    public const string ViewVideoFlagsError = "--chat and --thumbs are for pictures; /view <video> plays it in the video window.";

    /// <summary>
    /// <c>/video</c> in a headless run (<c>PastedCommand</c>'s and <c>HaText</c>'s <c>…NeedsScreen</c> shape): there is no window to play in. Pinned.
    /// </summary>
    public const string NeedsScreen = "/video plays in the video window, which needs the interactive screen; this headless run has none.";

    /// <summary>The spinner's words under <c>/video</c> and <c>/view</c> while a file opens where <paramref name="target"/> says.</summary>
    public static string WaitingFor(VideoFilesPlayInKind target) => target switch
    {
        VideoFilesPlayInKind.WebUiWindow => "Waiting for the Web UI's YouTube tab…",
        VideoFilesPlayInKind.DefaultApp => "Opening the video file in its default app…",
        _ => YouTube.YouTubeText.WaitingForPlayer,
    };

    /// <summary>The line after a stopped wait for a file: a held file still waits; the tab plays on; the window keeps loading.</summary>
    public static string StoppedWaitingFor(VideoSnapshot? snapshot) => snapshot switch
    {
        { Held: not null } => "(stopped waiting; the video file still waits for a YouTube tab: /video status says so)",
        { Place: VideoPlace.WebTab } => "(stopped waiting; the video file is still in " + YouTube.YouTubeText.WebTabName + ": /video status says where it is)",
        _ => "(stopped waiting; the video window is still open: /video status says where it is)",
    };

    /// <summary>
    /// What the player reports of a video file, as one line, in <c>YouTubeText.Status</c>' order of cases: the window's failure, the
    /// file's error (<see cref="FileError"/>), a wait for a YouTube tab, the opening, then the state line —
    /// <c>Playing "clip.mp4" at 0:31 of 10:35, volume 25%, muted</c> and the tab's tap hint. <see cref="NoFile"/> with none.
    /// </summary>
    public static string FileStatus(VideoSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return NoFile;
        }

        if (snapshot.State == VideoState.Failed)
        {
            return "Error: " + (snapshot.Failure ?? "the video window's browser could not start.");
        }

        if (snapshot.Error is { } code)
        {
            return "Error: " + FileError(code, snapshot.FileName);
        }

        if (snapshot.Held is { } held)
        {
            return YouTube.YouTubeText.Held(snapshot, held);
        }

        string name = snapshot.FileName ?? "the video file";
        if (snapshot.State == VideoState.Opening)
        {
            return $"Opening {name} in {YouTube.YouTubeText.PlaceName(snapshot.Place)}; video_control status says when it plays.";
        }

        string state = snapshot.State switch
        {
            VideoState.Playing => "Playing",
            VideoState.Paused => "Paused",
            VideoState.Buffering => "Buffering",
            VideoState.Ended => "Ended",
            VideoState.Cued => "Ready (press play to start)",
            _ => "Loaded, not started",
        };
        string title = string.IsNullOrWhiteSpace(snapshot.Title) ? name : snapshot.Title.Trim();
        string where = snapshot.Duration > 0
            ? $" at {YouTube.YouTubeIds.FormatTime(snapshot.Position)} of {YouTube.YouTubeIds.FormatTime(snapshot.Duration)}"
            : snapshot.Position > 0 ? $" at {YouTube.YouTubeIds.FormatTime(snapshot.Position)}" : "";
        string sound = $", volume {snapshot.Volume.ToString(CultureInfo.InvariantCulture)}%{(snapshot.Muted ? ", muted" : "")}";
        string file = string.Equals(title, name, StringComparison.Ordinal) ? "" : " · file " + name;
        return state + " \"" + title + "\"" + where + sound + file + YouTube.YouTubeText.OnTheTab(snapshot);
    }

    /// <summary>A WebView2 step that failed, as the snapshot's <see cref="VideoSnapshot.Failure"/>: what it was and its HRESULT.</summary>
    public static string Failed(string step, int hr) => hr == unchecked((int)0x8007139F)
        ? $"The video window's browser could not start: its profile folder is in use by another copy of the app started with other options (WebView2 {step}, 0x{hr.ToString("X8", CultureInfo.InvariantCulture)})."
        : $"The video window's browser could not start (WebView2 {step}, 0x{hr.ToString("X8", CultureInfo.InvariantCulture)}).";
}
