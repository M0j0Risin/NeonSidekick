using System.Globalization;

namespace NeonSidekick.Web;

/// <summary>
/// The Browser window's bot-check wording (2026-10-13, <see cref="BotCheck"/>): the pane's question and its wait, what each ending
/// adds to a tool's refusal, and <c>/browser</c>'s lines. Pinned by <c>BotCheckTests</c>.
/// </summary>
public static class BotCheckText
{
    /// <summary>The ask pane's title (the camera allow page's, <c>CameraMenu.AllowAsync</c>).</summary>
    public const string AskTitle = "Bot check";

    /// <summary>The waiting pane's title.</summary>
    public const string WaitTitle = "Browser window";

    /// <summary>The waiting pane's one row.</summary>
    public const string GiveUpRow = "Give up";

    /// <summary>The waiting pane's hint.</summary>
    public const string WaitHint = "Enter / ESC = give up";

    /// <summary>What the tool was doing, for the pane: <c>reading</c>, <c>searching</c>, <c>downloading</c>, <c>printing</c>.</summary>
    public static string Doing(BotCheckUse use) => use switch
    {
        BotCheckUse.Search => "searching",
        BotCheckUse.Download => "downloading",
        BotCheckUse.Pdf => "printing",
        _ => "reading",
    };

    /// <summary>
    /// The ask pane's caption: <c>example.com wants a person to pass a bot check (HTTP 403 Forbidden) before reading
    /// https://example.com/page. Open it in the Browser window to solve it?</c> Pinned.
    /// </summary>
    public static string AskCaption(BotCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string why = request.Why.Length == 0 ? "" : " (" + request.Why + ")";
        return request.Url.Host + " wants a person to pass a bot check" + why + " before " + Doing(request.Use) + " " + request.Url.AbsoluteUri
            + ". Open it in the Browser window to solve it?";
    }

    /// <summary>The waiting pane's caption while the window is open. Pinned.</summary>
    public static string WaitCaption(BotCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return "Solve the check for " + request.Url.Host + " in the Browser window; it closes by itself once the page is through. ESC gives up.";
    }

    /// <summary>
    /// What an ending other than <see cref="BotCheckEnd.Cleared"/> adds to the tool's refusal, inside its parentheses, so the
    /// model knows a person was asked (<c>; the user declined to solve it in the Browser window</c>). Pinned.
    /// </summary>
    public static string For(BotCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.End switch
        {
            BotCheckEnd.Declined => "; the user declined to solve it in the Browser window",
            BotCheckEnd.GaveUp => "; the user gave up on it in the Browser window",
            BotCheckEnd.TimedOut => "; it was still a bot check after " + ((int)BotCheck.SolveTimeout.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes in the Browser window",
            BotCheckEnd.Refused => result.Status > 0
                ? "; the Browser window got HTTP " + result.Status.ToString(CultureInfo.InvariantCulture) + " too, with no check to solve"
                : "; the Browser window could not load it either",
            BotCheckEnd.Unavailable => "; the Browser window could not open" + (result.Detail.Length == 0 ? "" : ": " + result.Detail),
            _ => "",
        };
    }

    /// <summary>A download the window let through whose second try over HTTP was refused again: the clearance holds for the browser alone. Pinned.</summary>
    public const string ClearedButClientRefused = "; the check was solved in the Browser window, but the site refused the download again";

    /// <summary>The page left the network mode's reach after the check (a redirect to the LAN under <c>internet</c>, say). Pinned.</summary>
    public static string LeftTheReach(string finalUrl) => "; the Browser window ended on " + finalUrl + ", which Web browser network mode keeps off limits";

    /// <summary>What a PDF the window printed says it was made with (<c>convert_to_pdf</c>'s result). Pinned.</summary>
    public const string WindowName = "the Browser window";

    // ---- /browser ----

    /// <summary><c>/browser</c>'s usage line. Pinned.</summary>
    public const string BrowserUsage = "Usage: /browser [url] — opens the page (or Web browser home page) in the Browser window; its cookies go to the web tools when you close it. /browser save [url [name]], list [--clear], unsave <n|url> and /browser <n> keep its saved links; /browser clear history|all clears what it keeps";

    /// <summary>A <c>/browser</c> argument that is no web address. Pinned.</summary>
    public static string NotAUrl(string text) => $"'{text}' is not an http or https address. " + BrowserUsage;

    /// <summary>The words after <c>/browser clear</c> (later on 2026-10-13). Pinned.</summary>
    public const string ClearWord = "clear";
    public const string ClearHistoryWord = "history";
    public const string ClearAllWord = "all";

    /// <summary>A <c>/browser clear</c> without <c>history</c> or <c>all</c>. Pinned.</summary>
    public const string ClearUsage = "Usage: /browser clear history|all — history clears the pages the Browser window visited; all also clears its cookies and sign-ins";

    /// <summary>The question before <c>/browser clear all</c>: sign-ins go. Pinned.</summary>
    public const string ClearAllQuestion = "Clear everything the Browser window keeps for this profile — history, cookies and sign-ins? Your saved links stay.";

    /// <summary>The notices after a clear. Pinned.</summary>
    public const string ClearedHistory = "Cleared the Browser window's history.";
    public const string ClearedAll = "Cleared everything the Browser window kept: history, cookies and sign-ins. Your saved links stay.";

    /// <summary>A clear that could not be done, with why. Pinned.</summary>
    public static string ClearFailed(string why) => "The Browser window was not cleared: " + why + ".";

    /// <summary>The notice as the window opens. Pinned.</summary>
    public static string Opened(string url) => "Opened " + url + " in the Browser window; close it to hand its cookies to the web tools.";

    /// <summary>The notice once the user closed a <c>/browser</c> window: what went to the web tools. Pinned.</summary>
    public static string Closed(int cookies, IReadOnlyCollection<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        if (cookies == 0 || hosts.Count == 0)
        {
            return "The Browser window closed; no cookies to hand to the web tools.";
        }

        string count = cookies.ToString(CultureInfo.InvariantCulture);
        return "The Browser window closed; " + count + (cookies == 1 ? " cookie" : " cookies") + " for " + string.Join(", ", hosts) + " handed to the web tools.";
    }

    /// <summary>Headless has no window. Pinned.</summary>
    public const string BrowserNeedsScreen = "/browser needs the screen: the Browser window is the interactive app's.";

    /// <summary>
    /// No window off Windows (the macOS build leaves it out in v1). "/browser needs Windows: the Browser window is WebView2's." until
    /// 2026-10-15: no message tells a Mac user that something needs Windows (2026-10-11, the user's ask), so it says what still works. Pinned.
    /// </summary>
    public const string BrowserNotHere = "There is no Browser window here; /browser save, list and unsave still work.";
}
