using System.Globalization;

namespace NeonSidekick.Viewer;

/// <summary>The Browser window's own wording (2026-10-13, the bot checks): its title and why it could not open. Pinned.</summary>
public static class BrowserText
{
    public const string AppTitle = "NeonSidekick browser";

    /// <summary>What a title says while a bot check waits on the user.</summary>
    public const string SolvingLead = "Bot check";

    /// <summary>
    /// The window's title: the page's and the app's, <c>Example Domain · NeonSidekick browser</c>; while a bot check waits on the user
    /// <c>Bot check · Just a moment... · NeonSidekick browser</c>. Pinned.
    /// </summary>
    public static string Title(string? pageTitle, bool solving)
    {
        string page = string.IsNullOrWhiteSpace(pageTitle) ? "" : pageTitle.Trim() + " · ";
        return (solving ? SolvingLead + " · " : "") + page + AppTitle;
    }

    /// <summary>No WebView2 Runtime on the machine.</summary>
    public const string NoRuntime = "the Browser window needs the Microsoft Edge WebView2 Runtime, which is not installed (Windows 11 has it; on Windows 10 it is a free download from Microsoft)";

    /// <summary><see cref="WebViewNative.LoaderFileName"/> is missing beside the exe: a broken install, not the machine.</summary>
    public const string NoLoader = "the Browser window cannot start: WebView2Loader.dll is missing beside NeonSidekick.exe";

    /// <summary>A step of WebView2's chain failed; its profile held by another copy of the app is the likely one and named. Pinned.</summary>
    public static string Failed(string step, int hr) => hr == unchecked((int)0x8007139F)
        ? $"the Browser window's profile folder is in use by another copy of the app (WebView2 {step}, 0x{hr.ToString("X8", CultureInfo.InvariantCulture)})"
        : $"the Browser window's browser could not start (WebView2 {step}, 0x{hr.ToString("X8", CultureInfo.InvariantCulture)})";

    /// <summary>The window closed while a page it was asked for still waited (the app's exit, a failure). Pinned.</summary>
    public const string Closed = "the Browser window closed";

    /// <summary>The window's thread made no window in time, with no reason of its own (2026-10-20, inline until the code review). Pinned.</summary>
    public const string DidNotStart = "the Browser window did not start";

    /// <summary>Why a link the model opens is not put in the window now (later still on 2026-10-13, <see cref="IBrowserWindow.Unavailable"/>): it would end the check.</summary>
    public const string BotCheckWaiting = "a bot check is waiting in the Browser window";

    /// <summary>The bar's names (2026-10-13, the navigation bar), its tooltips' words. Pinned.</summary>
    public const string BackName = "Back";
    public const string ForwardName = "Forward";
    public const string ReloadName = "Reload";
    public const string StopName = "Stop";
    public const string HomeName = "Home";
    public const string HistoryName = "History";
    public const string LinksName = "Saved links";
    public const string GoName = "Go";
    public const string OpenName = "Open in your browser";

    /// <summary>The open button's name on a PDF the app opened from disk (2026-10-14, the PDF plan's gap 76). Pinned.</summary>
    public const string OpenFileName = "Open in its own program";
    public const string SecureName = "Secure";
    public const string NotSecureName = "Not secure";

    /// <summary>The log line for an address the box would not go to (a refused scheme, a path on this machine). Pinned.</summary>
    public static string Refused(string text) => $"the Browser window goes to web pages only, not {text}";

    /// <summary>A clear asked for while a bot check waits in the window (later on 2026-10-13): refused, the check's cookies wanted. Pinned.</summary>
    public const string ClearDuringCheck = "a bot check is waiting in the Browser window; solve it or give it up, then clear";

    /// <summary>A clear the runtime refused to start (no <c>ICoreWebView2Profile2</c>). Pinned.</summary>
    public const string ClearUnsupported = "this WebView2 Runtime cannot clear its data; update it (Windows Update does)";

    /// <summary>A clear that failed with its HRESULT. Pinned.</summary>
    public static string ClearFailed(int hr) => $"the Browser window's data was not cleared (WebView2 0x{hr.ToString("X8", CultureInfo.InvariantCulture)})";

    /// <summary>A page that did not load at all (no name, no answer): the detail of a <see cref="Web.BotCheckEnd.Refused"/> with no status. Pinned.</summary>
    public const string DidNotLoad = "the page did not load";

    // ── The page's live view (2026-10-17): why an input from the Web UI was refused (BrowserRemote.Check) ──

    /// <summary>A tap, hold or scroll whose place is off the picture.</summary>
    public const string RemoteOffView = "that place is off the window's page";

    /// <summary>A text input with nothing in it.</summary>
    public const string RemoteNoText = "there is no text to type";

    /// <summary>A text input past the most one sends.</summary>
    public static string RemoteTooLong(int most) => $"that text is too long: send at most {most.ToString(CultureInfo.InvariantCulture)} characters at a time";

    /// <summary>A key the page has no button for.</summary>
    public const string RemoteNoSuchKey = "that key can't be sent: Enter, Backspace and Tab can";

    // ── The Mac's window (2026-10-18, the Mac Browser plan: WebKit) ──

    /// <summary>No window can be had on this Mac: before macOS 14, or no window server (an SSH session).</summary>
    public const string MacUnavailable = "the Browser window needs macOS 14 or later and a desktop session";

    /// <summary>Another copy of the app holds the profile's WebKit store (its lock file, pass 22): Windows' 0x8007139F sentence, a Mac's words.</summary>
    public const string MacProfileInUse = "the Browser window's profile folder is in use by another copy of the app";

    /// <summary>The profile's store id could not be kept (<see cref="MacBrowserRules.StoreIdFileName"/>).</summary>
    public const string MacNoStore = "the Browser window's data store could not be set up in the profile folder";

    /// <summary>The web view could not be made.</summary>
    public const string MacFailed = "the Browser window's WebKit view could not start";

    /// <summary>A clear WebKit did not answer in time.</summary>
    public const string MacClearTimedOut = "the Browser window's data was not cleared: WebKit did not answer in time";

    /// <summary>A store's removal WebKit did not answer in time (<c>/profile delete</c>).</summary>
    public const string MacRemoveTimedOut = "WebKit did not answer in time";

    /// <summary>The profile's store id file is there but could not be read (2026-10-20, the code review's G6).</summary>
    public static string MacStoreIdUnreadable(string why) => $"the Browser window's store id could not be read: {why}";

    /// <summary>
    /// Why <c>/profile delete</c> keeps a Mac profile (2026-10-20, the code review's C5): its WebKit store, which lives outside the folder and
    /// is found only by the id in it, could not be removed. After <c>Could not delete the profile: </c>. Pinned.
    /// </summary>
    public static string MacStoreNotRemoved(string why) =>
        $"its Browser window data (a WebKit store kept outside the profile folder) could not be removed: {why.TrimEnd('.')}. The profile is kept; try again in a desktop session, after restarting the app if WebKit still holds it";

    /// <summary>The page's web content process ended twice: the page is gone.</summary>
    public const string ContentEnded = "the page's web content process ended";

    /// <summary>The error page's title and its lines (pass 17): WebKit draws none of its own. Pinned.</summary>
    public const string ErrorTitle = "This page did not load";

    /// <summary>The error page's line under the address: the reason, and that Reload tries again. Pinned.</summary>
    public static string ErrorLine(string reason) => $"{reason.TrimEnd('.')}. Reload (⌘R) to try again.";

    /// <summary>
    /// The error page's HTML: the title, the address and the reason, all escaped, no script, in the system's font and colours (light or dark).
    /// </summary>
    public static string ErrorPage(string url, string reason) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"color-scheme\" content=\"light dark\"><title>" + System.Net.WebUtility.HtmlEncode(ErrorTitle) + "</title>"
        + "<style>body{font:15px -apple-system,system-ui,sans-serif;max-width:640px;margin:12vh auto;padding:0 24px;line-height:1.5}"
        + "h1{font-size:22px;font-weight:600}code{word-break:break-all;opacity:.75}</style></head><body><h1>" + System.Net.WebUtility.HtmlEncode(ErrorTitle) + "</h1>"
        + "<p><code>" + System.Net.WebUtility.HtmlEncode(url) + "</code></p><p>" + System.Net.WebUtility.HtmlEncode(ErrorLine(reason)) + "</p></body></html>";

    /// <summary>The download button's name and its menu's words. Pinned.</summary>
    public const string DownloadsName = "Downloads";
    public const string NoDownloads = "No downloads";
    public const string DownloadOpen = "Open";
    public const string DownloadShow = "Show in Finder";
    public const string DownloadCancel = "Cancel download";
    public const string DownloadFailedWord = "failed";
    public const string DownloadCancelledWord = "cancelled";

    /// <summary>The find bar's placeholder and its answer for no match. Pinned.</summary>
    public const string FindPlaceholder = "Find on page";
    public const string FindNone = "No matches";

    /// <summary>A page's dialogs (sheets) and the HTTP sign-in sheet. Pinned.</summary>
    public const string DialogOk = "OK";
    public const string DialogCancel = "Cancel";
    public const string SignInButton = "Sign In";

    /// <summary>The line naming who asks in a page's dialog: its origin.</summary>
    public static string DialogFrom(string? origin) => string.IsNullOrEmpty(origin) ? "The page says:" : $"{origin} says:";

    /// <summary>The HTTP sign-in sheet's message for a host and its realm.</summary>
    public static string SignInMessage(string host, string? realm) =>
        string.IsNullOrWhiteSpace(realm) ? $"Sign in to {host}" : $"Sign in to {host} ({realm.Trim()})";

    public const string SignInUser = "User name";
    public const string SignInPassword = "Password";

    /// <summary>The padlock's tooltips on a Mac (Windows draws no tooltip there).</summary>
    public const string SecureTip = "Secure (https)";
    public const string NotSecureTip = "Not secure (http)";
}
