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

    /// <summary>Off Windows: no window to play in.</summary>
    public const string Unavailable = "The video window needs Windows; open_url can open the video in the browser instead.";

    /// <summary>No WebView2 Runtime installed (it ships with Windows 11; Windows 10 may lack it).</summary>
    public const string NoRuntime = "The video window needs the Microsoft Edge WebView2 Runtime, which is not installed (Windows 11 has it; on Windows 10 it is a free download from Microsoft). open_url can open the video in the browser instead.";

    /// <summary><see cref="WebViewNative.LoaderFileName"/> is missing beside the exe: a broken install, not the machine.</summary>
    public const string NoLoader = "The video window cannot start: WebView2Loader.dll is missing beside NeonSidekick.exe. Reinstall the app; open_url can open the video in the browser meanwhile.";

    /// <summary>The window's title: the video's title and the app's, <c>Big Buck Bunny · NeonSidekick video</c>; the app's alone before the player knows it.</summary>
    public static string Title(string? videoTitle) =>
        string.IsNullOrWhiteSpace(videoTitle) ? AppTitle : videoTitle.Trim() + " · " + AppTitle;

    /// <summary>The not-a-video-id refusal of <see cref="VideoRequest"/>.</summary>
    public static string NotAnId(string? text) => $"\"{text}\" is not a YouTube video id (11 letters, digits, - or _).";

    /// <summary>A WebView2 step that failed, as the snapshot's <see cref="VideoSnapshot.Failure"/>: what it was and its HRESULT.</summary>
    public static string Failed(string step, int hr) => hr == unchecked((int)0x8007139F)
        ? $"The video window's browser could not start: its profile folder is in use by another copy of the app started with other options (WebView2 {step}, 0x{hr.ToString("X8", CultureInfo.InvariantCulture)})."
        : $"The video window's browser could not start (WebView2 {step}, 0x{hr.ToString("X8", CultureInfo.InvariantCulture)}).";
}
