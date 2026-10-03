namespace NeonSidekick.Viewer;

/// <summary>
/// The log window's words and <c>/log</c>'s (2026-10-02, the user's ask: <c>/log</c> opens a window that follows the log,
/// <c>/log --file</c> the <c>--log</c> file in the editor as <c>/log</c> did since 2026-09-22): the window's title, the line
/// it shows with nothing logged, and the command's notices and errors (in <c>ChatScreen</c> until then). Pinned.
/// </summary>
public static class LogViewText
{
    /// <summary>The window's title while it follows the log. Pinned.</summary>
    public const string Title = "NeonSidekick log";

    /// <summary>The window's title while the user has scrolled away from the newest line: following paused, and how to resume it. Pinned.</summary>
    public const string PausedTitle = "NeonSidekick log (paused: Ctrl+E follows)";

    /// <summary>The window's title for <paramref name="following"/>.</summary>
    public static string TitleFor(bool following) => following ? Title : PausedTitle;

    /// <summary>The line the window shows before anything is logged. Pinned.</summary>
    public const string Empty = "Nothing logged yet.";

    /// <summary><c>/log</c>'s switch for the <c>--log</c> file in the editor (2026-10-02, the user's word). Pinned.</summary>
    public const string FileSwitch = "--file";

    /// <summary><c>/log</c>'s notice once the window is open (or brought forward). Pinned.</summary>
    public static string WindowOpenedNotice => $"({App.NoticeGlyphs.Log}opened the log window; /log {FileSwitch} opens the --log file in your editor)";

    /// <summary>Ctrl+Alt+G's notice when it closes the open window (later on 2026-10-02, the user's ask: the chord toggles). Pinned.</summary>
    public static string WindowClosedNotice => $"({App.NoticeGlyphs.Log}closed the log window)";

    /// <summary><c>/log</c> where no window can be made (not Windows, or no log kept in this run). Pinned.</summary>
    public const string Unavailable = "The log window needs Windows; start the app with --log <path> and use /log --file to read the log in your editor.";

    /// <summary><c>/log</c> when the window could not be made. Pinned.</summary>
    public static string WindowFailedError(string detail) => $"Could not open the log window: {detail}";

    /// <summary><c>/log --file</c> without <c>--log</c>: there is no file to open. Pinned.</summary>
    public const string FileNeedsFlag = "/log --file needs the app started with --log <path>; /log opens the log window in any run.";

    /// <summary><c>/log</c> given something it does not take. Pinned.</summary>
    public static string UsageError(string args) => $"/log takes nothing or {FileSwitch}, not '{args}'.";

    /// <summary><c>/log --file</c>'s notice once the <c>--log</c> file is handed to the editor (2026-09-22). Pinned.</summary>
    public static string FileOpenedNotice(string path) => $"({App.NoticeGlyphs.Log}opened the log {path} in your editor)";

    /// <summary><c>/log --file</c> when the <c>--log</c> file is not there — it could not be opened at startup, or was deleted since. Pinned.</summary>
    public static string FileMissingError(string path) => $"The log file {path} does not exist; --log could not open it.";

    /// <summary><c>/log --file</c> when the editor launch fails. Pinned.</summary>
    public static string FileOpenFailedError(string detail) => $"Could not open the log file: {detail}";
}
