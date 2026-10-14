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

    /// <summary>The bar's names (2026-10-13, the navigation bar), its tooltips' words. Pinned.</summary>
    public const string BackName = "Back";
    public const string ForwardName = "Forward";
    public const string ReloadName = "Reload";
    public const string StopName = "Stop";
    public const string HomeName = "Home";
    public const string HistoryName = "History";
    public const string GoName = "Go";
    public const string OpenName = "Open in your browser";
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
}
