using System.Globalization;
using System.Net;

namespace NeonSidekick.WebUi;

/// <summary>
/// The Web UI's words (2026-10-14): the notices at the PC, the log lines, the sentences the pages get back, <c>/webui</c>'s pane. The
/// pages hold no wording of their own beyond their chrome: the asks' rows and the shopping list's headings are the PC's own constants.
/// </summary>
public static class WebUiText
{
    /// <summary>The glyph the Web UI's notices lead with: a phone (🌐 is the Web tools' toolbar button).</summary>
    public const string Glyph = "📱 ";

    // ── At the PC ─────────────────────────────────────────────────────────────

    /// <summary>The server is up: its address (never the token) and where the link is. Pinned.</summary>
    public static string ServingNotice(string address) =>
        "(" + Glyph + "Web UI on " + address + " — /webui shows the sign-in link)";

    /// <summary>The first time it serves the home network on Windows: the firewall may ask. Pinned.</summary>
    public const string FirewallNotice = "(" + Glyph + "Windows may ask to let NeonSidekick through its firewall: allow it on private networks for a phone to connect)";

    /// <summary>The server stopped. Pinned.</summary>
    public const string StoppedNotice = "(" + Glyph + "Web UI stopped)";

    /// <summary>It could not listen: the port is taken or Windows reserves it. Pinned.</summary>
    public static string PortFailedError(int port, string reason) =>
        "Web UI: port " + port.ToString(CultureInfo.InvariantCulture) + " could not be opened (" + reason.TrimEnd('.') + "). Another program may use it, or Windows reserves it "
        + "(netsh int ipv4 show excludedportrange protocol=tcp lists the reserved ranges); pick another in /settings › Client › Web UI.";

    /// <summary>A new token was made (none was saved, or this machine cannot read the saved one): browsers sign in again. Pinned.</summary>
    public const string TokenMintedNotice = "(" + Glyph + "a new Web UI sign-in token was made: open /webui's link again in each browser)";

    /// <summary>A browser signed in. Pinned.</summary>
    public static string SignedInNotice(IPAddress? remote) => "(" + Glyph + "a browser signed in to the Web UI from " + Where(remote) + ")";

    /// <summary>A message from the page stopped before the model answered: it went back to the page's draft. Pinned.</summary>
    public const string WithdrawnNotice = "(canceled — the message is back on the Web UI's page)";

    /// <summary>A pane answered from the phone, under its question. Pinned.</summary>
    public static string AnsweredNotice(string choice) => "(" + Glyph + "answered from the Web UI: " + choice + ")";

    /// <summary><c>/webui reset</c>'s question. Pinned.</summary>
    public const string ResetQuestion = "Make a new Web UI sign-in token? Every browser signed in now is signed out.";

    /// <summary><c>/webui reset</c> done. Pinned.</summary>
    public const string ResetNotice = "(" + Glyph + "a new Web UI sign-in token: every browser is signed out; /webui shows the new link)";

    /// <summary>Headless has no Web UI in this version. Pinned.</summary>
    public const string NotHeadlessError = "/webui: the Web UI is not hosted headless.";

    /// <summary>A build without the server (the screen tests). Pinned.</summary>
    public const string NotAvailableError = "/webui: the Web UI is not available in this build.";

    /// <summary><c>/webui</c>'s grammar. Pinned.</summary>
    public const string UsageError = "Usage: /webui [on|off|copy|reset]";

    /// <summary><c>/webui copy</c> with nothing serving. Pinned.</summary>
    public const string NoLinkError = "/webui copy: the Web UI is not serving, so there is no link (/webui on).";

    /// <summary><c>/webui copy</c> done. Pinned.</summary>
    public const string CopiedNotice = "(" + Glyph + "the sign-in link is on the clipboard: it holds the token, so paste it only where you trust)";

    /// <summary><c>/webui copy</c> failed. Pinned.</summary>
    public const string CopyFailedError = "/webui copy: the clipboard could not be written.";

    // ── /webui's pane ─────────────────────────────────────────────────────────

    /// <summary>The pane's title.</summary>
    public const string PaneTitle = Glyph + "Web UI";

    /// <summary>The pane's status lines: on or off, the port, the reach, then the links (or why there are none).</summary>
    public static IReadOnlyList<string> PaneLines(bool on, bool serving, int port, WebUiAudience audience, IReadOnlyList<string> links)
    {
        var lines = new List<string>(StatusLines(on, serving, port, audience)) { "" };
        if (!serving)
        {
            return lines;
        }

        if (links.Count == 0)
        {
            lines.Add("No private network address is up on this machine: the home network cannot reach it.");
            return lines;
        }

        lines.Add("Open one of these in the browser (a phone on the same network). The link signs it in and leaves the address bar at once:");
        lines.AddRange(links.Select(link => "  " + link));
        lines.Add("");
        lines.Add("The link holds the token: sending it through a messaging service hands that service the token too. /webui reset makes a new one.");
        return lines;
    }

    /// <summary>The pane's first lines: on or off, the port, the reach.</summary>
    public static IReadOnlyList<string> StatusLines(bool on, bool serving, int port, WebUiAudience audience) =>
    [
        "Web UI: " + (on ? serving ? "on" : "on, not serving (see the notice above)" : "off (/webui on, or /settings › Client › Web UI)"),
        "Port: " + port.ToString(CultureInfo.InvariantCulture),
        "Reach: " + WebUiReach.Name(audience) + " — " + WebUiReach.Describe(WebUiReach.Name(audience)),
    ];

    /// <summary>Above an address's QR code (2026-10-14, the user's ask: no long address typed on a phone). Pinned.</summary>
    public const string ScanHint = "Scan with the phone's camera (on the same network), or open the link below. It signs the browser in and leaves the address bar at once.";

    /// <summary>Under the link: what holding it means. Pinned.</summary>
    public const string TokenWarning = "The code and the link hold the token: anyone who sees them can sign in. /webui reset makes a new one.";

    /// <summary>An address's tab title: the host the link names.</summary>
    public static string LinkTabTitle(string link) => Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Host : link;

    // ── To the page ───────────────────────────────────────────────────────────

    /// <summary>An API call without the cookie.</summary>
    public const string SignInNeeded = "Sign in first: open the link /webui shows in the app.";

    /// <summary>A POST without JSON and the header.</summary>
    public const string PostRefused = "Refused: not from the Web UI's own page.";

    /// <summary>A body that did not read.</summary>
    public const string BadRequest = "The request did not read.";

    /// <summary>A line naming a slash command. Pinned.</summary>
    public const string SlashRefused = "Slash commands run at the PC; the Web UI sends messages.";

    /// <summary>An empty message.</summary>
    public const string EmptyMessage = "Nothing to send.";

    /// <summary>A message over the cap. Pinned.</summary>
    public static string TooLong(int maxBytes) => "Too long: a message holds at most " + (maxBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB.";

    /// <summary>Too many messages waiting. Pinned.</summary>
    public static string QueueFull(int max) => "The queue is full: " + max.ToString(CultureInfo.InvariantCulture) + " messages wait already.";

    /// <summary>Stop with nothing to stop.</summary>
    public const string NothingToStop = "Nothing is running.";

    /// <summary>Stop while only a model loads.</summary>
    public const string LoadingNoStop = "The model is loading; that is stopped at the PC (Ctrl+C).";

    /// <summary>An answer to an ask already answered or gone.</summary>
    public const string AskGone = "That question was answered or withdrawn.";

    /// <summary>An answer that leaves a question unanswered. Pinned.</summary>
    public const string AskIncomplete = "Answer every question first.";

    /// <summary>A choice the ask does not have.</summary>
    public const string NoSuchChoice = "That is not one of the choices.";

    /// <summary>A shopping line gone meanwhile.</summary>
    public const string NoSuchItem = "That line is gone from the list.";

    /// <summary>A write the shopping list refused.</summary>
    public const string ShoppingWriteFailed = "The shopping list could not be written.";

    // ── The log ───────────────────────────────────────────────────────────────

    /// <summary>A browser signed in (audit). Pinned.</summary>
    public static string SignedInLogLine(IPAddress? remote) => "Web UI: a browser signed in from " + Where(remote) + ".";

    /// <summary>A sign-in with a wrong token. Pinned.</summary>
    public static string SignInFailedLogLine(IPAddress? remote) => "Web UI: a sign-in with a wrong or old token from " + Where(remote) + " was refused.";

    /// <summary>A browser at an address that is not private, under <c>lan</c>. Pinned.</summary>
    public static string RefusedRemoteLogLine(IPAddress remote) => "Web UI: refused a connection from " + remote + " (not a private address).";

    /// <summary>A request that failed.</summary>
    public static string RequestFailedLogLine(string path, Exception e) => "Web UI: " + path + " failed: " + e.Message;

    /// <summary>A pane answered from the phone (audit). Pinned.</summary>
    public static string AnsweredLogLine(string title, string choice, IPAddress? remote) =>
        "Web UI answered \"" + title + "\": " + choice + " from " + Where(remote) + ".";

    /// <summary>A shopping line ticked from the phone (audit).</summary>
    public static string CheckedLogLine(string line, bool on, IPAddress? remote) =>
        "Web UI " + (on ? "ticked" : "unticked") + " \"" + line + "\" from " + Where(remote) + ".";

    private static string Where(IPAddress? remote) => remote is null ? "an unknown address" : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();

    /// <summary>The app's version, as the pages compare it.</summary>
    public static string Version => typeof(WebUiText).Assembly.GetName().Version?.ToString() ?? "";
}
