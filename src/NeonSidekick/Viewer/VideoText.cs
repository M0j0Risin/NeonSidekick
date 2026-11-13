using System.Globalization;
using NeonSidekick.YouTube;

namespace NeonSidekick.Viewer;

/// <summary>
/// The video window's words (2026-10-05, the YouTube plan): its title, why it cannot open, and the WebView2 failure a status
/// reports. The YouTube tools' and <c>/youtube</c>'s own sentences live in <c>YouTubeText</c>. Pinned where a test says so.
///
/// <para>The video file sentences (2026-11-12) come in two forms where they name a way on (2026-11-13, the review's fix): the model's
/// (<c>video_control status</c>, <c>play_video</c>, the <c>open</c> tool) and, with <c>forUser</c>, <c>/video</c>'s and <c>/view</c>'s
/// (<c>/video status</c>, <c>/video &lt;path&gt;</c>, <c>/open</c>). The screen's own tool instances pass <c>forUser: true</c>.</para>
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

    /// <summary>
    /// The window's page that never said it was ready (2026-11-13, the review's fix): a switch between the YouTube player and the file page,
    /// or the first navigation, whose page never posted its <c>page</c> message in time. The snapshot fails with it and the waiting play goes.
    /// </summary>
    public const string PageDidNotLoad = "The video window's page didn't load; play the video again to try once more.";

    /// <summary>The window's log line for <see cref="PageDidNotLoad"/>: the page it waited for.</summary>
    public static string PageDidNotLoadLogLine(bool filePage) =>
        $"The video window's {(filePage ? "file" : "YouTube")} page didn't say it was ready in time; its waiting play and commands were dropped.";

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
    public static string YouTubeRefusedLogLine(string? videoId, int? code) =>
        $"YouTube refused video {videoId} in the player (error {code?.ToString(CultureInfo.InvariantCulture)}).";

    /// <summary>The window's log line for a video file the player couldn't play (2026-11-12): its name and the page's code.</summary>
    public static string FileRefusedLogLine(string? name, int? code) =>
        $"The player could not play the video file {name} (error {code?.ToString(CultureInfo.InvariantCulture)}).";

    /// <summary>A player's log line for a video file with sound and no picture (2026-11-13), once per load.</summary>
    public static string NoPictureLogLine(string? name) =>
        $"The video file {name} plays sound with no picture (no video track, or a video codec the player can't show).";

    /// <summary><c>/video</c>'s and the model's log line for where a file play goes (2026-11-13: born here, not inline).</summary>
    public static string FileRouteLogLine(string setting, string? name, string target, string? note) =>
        $"Video files play in {setting}: {name} to {target}" + (note is null ? "." : " (" + note.Trim() + ")");

    /// <summary>The log line for a file the sink refused (the exception's text, which the answer never shows): its default app has it.</summary>
    public static string FileRouteFailedLogLine(string? name, string target, string reason) =>
        $"{name} could not play in {target} ({reason}); its default app has it.";

    /// <summary>The log line for a video window that failed after it opened on a file (2026-11-13): its default app has it.</summary>
    public static string FileWindowFailedLogLine(string? name, string? failure) =>
        $"The video window failed on {name} ({failure}); its default app has it.";

    // The way out every file error offers: the default app, which the model reaches through open and the user through /open.
    private static string OwnApp(bool forUser) => forUser
        ? " Open it in its own app instead (/open)."
        : " Open it in its own app instead (the open tool, or /open).";

    // The status command each reader names.
    private static string StatusCommand(bool forUser) => forUser ? "/video status" : "video_control status";

    private const string ErrorLead = "Error: ";

    /// <summary>
    /// A video file's error as a sentence (2026-11-12): <c>MediaError</c>'s codes (1 aborted, 2 network, 3 decode, 4 not supported) and
    /// the app's <see cref="VideoFile.GoneCode"/> and <see cref="VideoFile.FunnelCode"/>. Each but those two offers the default app,
    /// unless <paramref name="wayOut"/> is false (2026-11-13: the Home card on the phone, where the PC's default app is no way out).
    /// Read by the snapshot's kind: YouTube's codes overlap these.
    /// </summary>
    public static string FileError(int? code, string? name, bool wayOut = true, bool forUser = false)
    {
        string file = string.IsNullOrWhiteSpace(name) ? "The video file" : name;
        string own = wayOut ? OwnApp(forUser) : "";
        return code switch
        {
            1 => $"Playing {file} stopped before it loaded; play it again.{own}",
            2 => $"{file} could not be read (the disk, the share or the network failed); play it again.{own}",
            3 => $"{file} could not be decoded: the file may be damaged, or its codec is one the player can't show.{own}",
            4 => $"{file} is in a format the player can't play (its codec or its container: an HEVC video or an MKV, say).{own}",
            VideoFile.GoneCode => $"{file} is no longer there: it was moved or deleted.",
            VideoFile.FunnelCode => $"{file} can't play on the Web UI page: it is reached over Tailscale Funnel, and Web UI video files over Funnel is off.",
            _ => $"{file} could not play (error {code?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}).{own}",
        };
    }

    /// <summary>
    /// The note after a file's state line when it plays sound with no picture (2026-11-13, <see cref="VideoSnapshot.NoPicture"/>). The page
    /// can't tell a file with no video from one whose video it can't decode (no picture either way), so the sentence says both.
    /// </summary>
    public static string NoPictureNote(string? name, bool forUser = false)
    {
        string file = string.IsNullOrWhiteSpace(name) ? "The video file" : name;
        return $"{file} plays sound but no picture: it has no video, or its video codec (HEVC, say) isn't one the player can show.{OwnApp(forUser)}";
    }

    // ---- video files: the tools', /video's and /view's sentences (2026-11-12, the video-files plan) ----

    /// <summary>No video file playing: <c>YouTubeText.NoVideo</c>'s file form. Pinned.</summary>
    public const string NoFile = "No video file is playing; play_video plays one.";

    /// <summary><see cref="NoFile"/> for <c>/video</c>, whose user plays with <c>/video &lt;path&gt;</c>. Pinned.</summary>
    public const string NoFileCommand = "No video file is playing; /video <path> plays one.";

    /// <summary><see cref="NoFile"/> or, with <paramref name="forUser"/>, <see cref="NoFileCommand"/>.</summary>
    public static string NoFileFor(bool forUser) => forUser ? NoFileCommand : NoFile;

    /// <summary>A path that is no video file (<see cref="VideoFile.Extensions"/>), or a folder so named.</summary>
    public static string NotAVideoFile(string? path, bool forUser = false) =>
        $"Error: {path} is not a video file; {(forUser ? "/video" : "play_video")} plays {string.Join(", ", VideoFile.Extensions)} files.";

    /// <summary>The place a video file plays, in a sentence: the video window, the Web UI's tab or the default app.</summary>
    public static string PlaceName(VideoFilesPlayInKind kind) => kind switch
    {
        VideoFilesPlayInKind.WebUiWindow => YouTubeText.WebTabName,
        VideoFilesPlayInKind.DefaultApp => "its default app",
        _ => "the video window",
    };

    /// <summary>The note before a file play's answer when <c>webui-window</c> fell back to the window: the Web UI isn't serving. Pinned.</summary>
    public static readonly string WebUiOffFellBack = YouTubeText.FellBackToWindow(YouTubeText.WebUiNotServing, "video file");

    /// <summary>The note when <c>webui-window</c> fell back to the window because <i>Web UI video controls</i> is off (no tab). Pinned.</summary>
    public static readonly string WebUiControlsOffFellBack = YouTubeText.FellBackToWindow(YouTubeText.WebUiControlsOff, "video file");

    /// <summary>
    /// The note when a file went to its default app (2026-11-13, the review's fix): each reason a whole clause naming its place (<see cref="NoWindowHere"/>,
    /// <see cref="WindowPlaysNoFiles"/>, <see cref="CouldNotPlayIn"/>, the Web UI's clauses), joined in the order the fallbacks ran, so a
    /// <c>webui-window</c> → window → default app chain keeps both.
    /// </summary>
    public static string DefaultAppFellBack(params string[] reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        string joined = string.Join("; ", reasons.Select(r => r.Trim().TrimEnd('.', ' ')).Where(r => r.Length > 0));
        return YouTubeText.Capitalized(joined) + ", so it opens in its default app instead. ";
    }

    /// <summary>A screen with no video window at all (<see cref="DefaultAppFellBack"/>'s reason).</summary>
    public const string NoWindowHere = "there is no video window here";

    /// <summary>A video window that doesn't play files (a Mac's, for now; <see cref="DefaultAppFellBack"/>'s reason).</summary>
    public const string WindowPlaysNoFiles = "the video window here doesn't play files";

    /// <summary>A sink that refused a file or a window that couldn't open (<see cref="DefaultAppFellBack"/>'s reason; the exception's text goes to the log).</summary>
    public static string CouldNotPlayIn(VideoFilesPlayInKind target) => target == VideoFilesPlayInKind.WebUiWindow
        ? "the Web UI's Video tab couldn't take it"
        : "the video window couldn't open";

    /// <summary>
    /// <paramref name="answer"/> with <paramref name="before"/> (a fallback note, a Resumed line) in front, keeping <c>Error: </c> first
    /// (2026-11-13, the review's fix): an error answer stays one, so the screen shows it as an error and the model reads it so.
    /// </summary>
    public static string Lead(string answer, params string?[] before)
    {
        ArgumentNullException.ThrowIfNull(answer);
        string parts = before is null ? "" : string.Concat(before.Where(p => !string.IsNullOrEmpty(p)));
        return answer.StartsWith(ErrorLead, StringComparison.Ordinal)
            ? ErrorLead + parts + answer[ErrorLead.Length..]
            : parts + answer;
    }

    /// <summary>Where <c>play_video</c>'s answer says the default app showed a file (<c>FileText.Opened</c>'s <c>where</c>). Pinned.</summary>
    public const string InVideoApp = " in the user's video app";

    /// <summary>
    /// <c>play_video</c>'s answer for a file sent to its default app: <paramref name="opened"/> (<c>FileText.Opened</c>'s "opened X in
    /// the user's video app"), capitalized, and that the app can't see or drive it there.
    /// </summary>
    public static string HandedOff(string opened, bool forUser = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(opened);
        return YouTubeText.Capitalized(opened) + (forUser
            ? "; it plays there, where /video can't control it."
            : "; it plays there, where video_control can't see or control it.");
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

    /// <summary><c>/youtube save</c> (or, without <paramref name="forUser"/>, <c>youtube_save</c>) with a video file open: saves keep YouTube videos. Pinned.</summary>
    public static string NoSaveForFiles(string? name, bool forUser) =>
        $"Error: {name} is a video file, not a YouTube video; {(forUser ? "/youtube save" : "youtube_save")} keeps YouTube videos.";

    /// <summary>
    /// A file whose volume the page can't set (2026-11-13, <see cref="VideoSnapshot.FixedVolume"/>: an iPhone): answered at once, never waited for.
    /// </summary>
    public const string FixedVolume = "Error: the phone's own buttons set this video file's volume (the page can't); mute and unmute work.";

    /// <summary><c>/view</c> of a video with <c>--chat</c> or <c>--thumbs</c>, which are for pictures (<c>ViewPdfFlagsError</c>'s shape): where it plays. Pinned.</summary>
    public static string ViewVideoFlagsError(VideoFilesPlayInKind target) =>
        $"--chat and --thumbs are for pictures; /view <video> plays it in {PlaceName(target)}.";

    /// <summary>
    /// <c>/video</c> in a headless run (<c>PastedCommand</c>'s and <c>HaText</c>'s <c>…NeedsScreen</c> shape): there is no window to play in. Pinned.
    /// </summary>
    public const string NeedsScreen = "/video plays in the video window, which needs the interactive screen; this headless run has none.";

    /// <summary><c>/video</c>'s usage, the answer to words it doesn't know (moved here from <c>VideoFileCommand</c>, 2026-11-13). Pinned.</summary>
    public const string CommandUsage = "Usage: /video <path> plays a video file; /video play|resume|pause|seek <time>|volume <0-100>|mute|unmute|status|close controls it.";

    /// <summary><c>/video</c>'s verbs in the order the completion offers them, with their notes (<c>VideoFileCommand.Words</c>).</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> CommandWords =
    [
        ("status", "what the video file plays, and where"),
        ("pause", "pause the video file"),
        ("play", "carry on playing"),
        ("resume", "carry on playing, as play does"),
        ("seek", "go to a time: /video seek <time>"),
        ("volume", "set the volume: /video volume <0-100>"),
        ("mute", "mute the video file"),
        ("unmute", "unmute the video file"),
        ("close", "close the video file's player"),
    ];

    /// <summary>The spinner's words under <c>/video</c> and <c>/view</c> while a file opens where <paramref name="target"/> says.</summary>
    public static string WaitingFor(VideoFilesPlayInKind target) => target switch
    {
        VideoFilesPlayInKind.WebUiWindow => YouTubeText.WaitingFor(YouTubeOpenLinksInKind.WebUiWindow),
        VideoFilesPlayInKind.DefaultApp => "Opening the video file in its default app…",
        _ => YouTubeText.WaitingForPlayer,
    };

    /// <summary>The line after a stopped wait for a file: a held file still waits; the tab plays on; the window keeps loading.</summary>
    public static string StoppedWaitingFor(VideoSnapshot? snapshot) => YouTubeText.StoppedWaitingFor(snapshot, "video file", "/video");

    /// <summary>
    /// What the player reports of a video file, as one line, in <c>YouTubeText.Status</c>' order of cases: the window's failure, the
    /// file's error (<see cref="FileError"/>), a wait for a Video tab, the opening, then the state line —
    /// <c>Playing "clip.mp4" at 0:31 of 10:35, volume 25%, muted</c> and the tab's tap hint, and <see cref="NoPictureNote"/> after it
    /// for a file with no picture. <see cref="NoFile"/> with none.
    /// </summary>
    public static string FileStatus(VideoSnapshot? snapshot, bool forUser = false)
    {
        if (snapshot is null)
        {
            return NoFileFor(forUser);
        }

        if (snapshot.State == VideoState.Failed)
        {
            return ErrorLead + (snapshot.Failure ?? "the video window's browser could not start.");
        }

        if (snapshot.Error is { } code)
        {
            return ErrorLead + FileError(code, snapshot.FileName, forUser: forUser);
        }

        if (snapshot.Held is { } held)
        {
            return YouTubeText.Held(snapshot, held);
        }

        string name = snapshot.FileName ?? "the video file";
        if (snapshot.State == VideoState.Opening)
        {
            return $"Opening {name} in {YouTubeText.PlaceName(snapshot.Place)}; {StatusCommand(forUser)} says when it plays.";
        }

        string title = string.IsNullOrWhiteSpace(snapshot.Title) ? name : snapshot.Title.Trim();
        string file = string.Equals(title, name, StringComparison.Ordinal) ? "" : " · file " + name;
        string line = YouTubeText.StateWord(snapshot.State, "Ready (press play to start)") + " \"" + title + "\"" + YouTubeText.WhereAndSound(snapshot)
            + file + YouTubeText.OnTheTab(snapshot);
        return snapshot.NoPicture ? line + ". " + NoPictureNote(snapshot.FileName, forUser) : line;
    }

    /// <summary>A WebView2 step that failed, as the snapshot's <see cref="VideoSnapshot.Failure"/>: what it was and its HRESULT.</summary>
    public static string Failed(string step, int hr) => hr == unchecked((int)0x8007139F)
        ? $"The video window's browser could not start: its profile folder is in use by another copy of the app started with other options (WebView2 {step}, 0x{hr.ToString("X8", CultureInfo.InvariantCulture)})."
        : $"The video window's browser could not start (WebView2 {step}, 0x{hr.ToString("X8", CultureInfo.InvariantCulture)}).";
}
