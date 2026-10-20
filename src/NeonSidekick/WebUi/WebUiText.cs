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

    /// <summary>A shopping line ticked or unticked from the page, flashed on the PC's hint row (2026-10-15).</summary>
    public static string CheckedFlash(string line, bool on) => Glyph + (on ? "ticked: " : "unticked: ") + line;

    /// <summary>A pane answered from the phone, under its question. Pinned.</summary>
    public static string AnsweredNotice(string choice) => "(" + Glyph + "answered from the Web UI: " + choice + ")";

    /// <summary><c>/webui reset</c>'s question. Pinned.</summary>
    public const string ResetQuestion = "Make a new Web UI sign-in token? Every browser signed in will be signed out.";

    /// <summary><c>/webui reset</c> done. Pinned.</summary>
    public const string ResetNotice = "(" + Glyph + "a new Web UI sign-in token: every browser is signed out; /webui shows the new link)";

    /// <summary>Headless has no Web UI in this version. Pinned.</summary>
    public const string NotHeadlessError = "/webui: the Web UI is not hosted headless.";

    /// <summary>A build without the server (the screen tests). Pinned.</summary>
    public const string NotAvailableError = "/webui: the Web UI is not available in this build.";

    /// <summary><c>/webui</c>'s grammar. Pinned.</summary>
    public const string UsageError = "Usage: /webui [on|off|copy|reset|browser [--external]]";

    // ── Opening it on this PC (later on 2026-10-15, /webui browser and the pane's link) ──

    /// <summary><c>/webui browser</c>'s flag: the default browser, whatever <i>Web open links in</i> says.</summary>
    public const string ExternalFlag = "--external";

    /// <summary>The address <c>/webui browser</c> opens: one host, so one cookie, under either reach.</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary><c>/webui browser</c> with the Web UI off.</summary>
    public const string BrowserWebUiOff = "The Web UI is off: /webui on starts it.";

    /// <summary><c>/webui browser</c> while the server isn't serving (a port refused, still starting).</summary>
    public static string BrowserNotServing(int port) => "The Web UI isn't serving on port " + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " yet.";

    /// <summary>Where it went; never the link or the token.</summary>
    public static string BrowserOpened(bool window) => "📱 opened the Web UI in " + (window ? "the Browser window" : "the default browser");

    /// <summary>The audit line: where, never the link.</summary>
    public static string BrowserOpenedLogLine(bool window, bool once) => "Web UI opened on this PC in " + (window ? "the Browser window" : "the default browser") + (once ? " with a one-time link." : " with the sign-in link.");

    /// <summary>The <c>/webui</c> pane's copy button (later on 2026-10-15, the user's ask): the shown tab's link to the clipboard.</summary>
    public const char CopyKey = 'c';

    public const string CopyWord = "copy link";

    public const string CopyButton = "copy";

    /// <summary>The pane's hint under the link: a double-click opens it here.</summary>
    public const string LinkOpenHint = "Double-click the link to open it on this PC; c copies it.";

    /// <summary><c>/webui copy</c> with nothing serving. Pinned.</summary>
    public const string NoLinkError = "/webui copy: the Web UI is not serving, so there is no link (/webui on).";

    /// <summary><c>/webui copy</c> done. Pinned.</summary>
    public const string CopiedNotice = "(" + Glyph + "the sign-in link is on the clipboard: it holds the token, so paste it only where you trust)";

    /// <summary><c>/webui copy</c> failed. Pinned.</summary>
    public const string CopyFailedError = "/webui copy: the clipboard could not be written.";

    // ── /webui's pane ─────────────────────────────────────────────────────────

    /// <summary>The pane's title.</summary>
    public const string PaneTitle = Glyph + "Web UI";

    /// <summary>
    /// The pane's status lines: on or off, the port, the reach, then the links (or why there are none). Where there is no pane, the notices;
    /// Tailscale's link marked <see cref="TailscaleMark"/> and the lead naming the tailnet (later on 2026-10-14). Pinned.
    /// </summary>
    public static IReadOnlyList<string> PaneLines(bool on, bool serving, int port, WebUiAudience audience, IReadOnlyList<WebUiLink> links)
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

        lines.Add(links.Any(l => l.Tailscale)
            ? "Open one of these in the browser (a phone on the same network, or on your tailnet for the one marked Tailscale). The link signs it in and leaves the address bar at once:"
            : "Open one of these in the browser (a phone on the same network). The link signs it in and leaves the address bar at once:");
        lines.AddRange(links.Select(link => "  " + link.Url + (link.Tailscale ? TailscaleMark : "")));
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

    /// <summary>Above Tailscale's QR code (later on 2026-10-14): <see cref="ScanHint"/> without "on the same network", which a tailnet is not. Pinned.</summary>
    public const string TailscaleScanHint = "Scan with the phone's camera, or open the link below. It signs the browser in and leaves the address bar at once.";

    /// <summary>What heads Tailscale's tab (later on 2026-10-14, the user's ask and wording). Pinned.</summary>
    public const string TailscaleNote = "Tailscale: works from any device on your tailnet, at home or away.";

    /// <summary>After Tailscale's link where there is no pane (later on 2026-10-14). Pinned.</summary>
    public const string TailscaleMark = " (Tailscale)";

    /// <summary>
    /// An address tab's text above its QR code: <paramref name="status"/> (<see cref="StatusLines"/> joined), a blank line and the scan hint;
    /// Tailscale's headed by <see cref="TailscaleNote"/> and with <see cref="TailscaleScanHint"/> (later on 2026-10-14). Pinned.
    /// </summary>
    public static string LinkTabText(string status, bool tailscale) =>
        tailscale ? TailscaleNote + "\n" + status + "\n\n" + TailscaleScanHint + "\n" : status + "\n\n" + ScanHint + "\n";

    /// <summary>Under the link: what holding it means. Pinned.</summary>
    public const string TokenWarning = "The code and the link hold the token: anyone who sees them can sign in. /webui reset makes a new one.";

    /// <summary>The on/off button's key on <c>/webui</c>'s pane (2026-10-14, the user's ask: <c>📱 Web UI │ enabled │ lan │ …</c>).</summary>
    public const char EnabledKey = 'e';

    /// <summary>What the pane's hint calls the on/off button: <c>e = on/off</c>. Pinned.</summary>
    public const string EnabledWord = "on/off";

    /// <summary>The on/off button on <c>/webui</c>'s strip, first after the label: what <i>Web UI</i> is now, <c>enabled</c> or <c>disabled</c>. Pinned.</summary>
    public static string EnabledButton(bool on) => on ? "enabled" : "disabled";

    /// <summary>The reach button's key on <c>/webui</c>'s pane (2026-10-14, the user's ask): l, the button reading lan or local.</summary>
    public const char ReachKey = 'l';

    /// <summary>What the pane's hint calls the reach button: <c>l = lan/local</c>. Pinned.</summary>
    public const string ReachWord = "lan/local";

    /// <summary>The reset button's key on <c>/webui</c>'s pane (later on 2026-10-14, the user's ask: <c>/webui reset</c> as a button, its yes too).</summary>
    public const char ResetKey = 'r';

    /// <summary>What the pane's hint calls the reset button: <c>r = new token</c>. Pinned.</summary>
    public const string ResetWord = "new token";

    /// <summary>The reset button on <c>/webui</c>'s strip, after the reach: a new sign-in token, after a yes. Pinned.</summary>
    public const string ResetButton = "reset";

    /// <summary>The reach button on <c>/webui</c>'s strip, after the on/off one: <i>Web UI reach</i> now, <c>lan</c> or <c>local</c>. Pinned.</summary>
    public static string ReachButton(WebUiAudience audience) => WebUiReach.Name(audience);

    /// <summary>An address's tab title: the host the link names.</summary>
    public static string LinkTabTitle(string link) => Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Host : link;

    // ── To the page ───────────────────────────────────────────────────────────

    /// <summary>A pasted sign-in whose token does not match (2026-10-15). Never echoes it.</summary>
    public const string SignInWrong = "That isn't the sign-in link or token: copy it again from /webui at the PC.";

    /// <summary>An API call without the cookie.</summary>
    public const string SignInNeeded = "Sign in first: open the link /webui shows in the app.";

    /// <summary>An API of the Chat tab while <i>Web UI chat controls</i> is off (later on 2026-10-15); the page goes to <c>/</c>.</summary>
    public const string ChatTabOff = "The Chat tab is off: Web UI chat controls in /settings › Client › Web UI turns it on.";

    /// <summary>An API of the Home tab while <i>Web UI home controls</i> is off (later on 2026-10-15).</summary>
    public const string HomeTabOff = "The Home tab is off: Web UI home controls in /settings › Client › Web UI turns it on.";

    /// <summary>An API of the Shopping tab while <i>Web UI shopping controls</i> is off (later on 2026-10-15); bare <c>/shopping</c> from the page too.</summary>
    public const string ShoppingTabOff = "The Shopping tab is off: Web UI shopping controls in /settings › Client › Web UI turns it on.";

    /// <summary>
    /// An API of the YouTube tab while the tab is off (2026-10-16; <i>Web UI YouTube controls</i> since later that day, <i>YouTube open links
    /// in</i> <c>webui-window</c> until then), and <c>/youtube list</c> typed in Chat then.
    /// </summary>
    public const string YouTubeTabOff = "The YouTube tab is off: Web UI YouTube controls in /settings › Client › Web UI turns it on.";

    /// <summary>The sentence for a tab that is off; each tab named (2026-10-16: a newcomer never reads another tab's).</summary>
    public static string TabOff(WebTab tab) => tab switch
    {
        WebTab.Chat => ChatTabOff,
        WebTab.Home => HomeTabOff,
        WebTab.Shopping => ShoppingTabOff,
        WebTab.YouTube => YouTubeTabOff,
        _ => "",
    };

    /// <summary>A POST without JSON and the header.</summary>
    public const string PostRefused = "Refused: not from the Web UI's own page.";

    /// <summary>A body that did not read.</summary>
    public const string BadRequest = "The request did not read.";

    // ── Commands from the page (later on 2026-10-14) ──────────────────────────

    /// <summary>Any command while <i>Web UI commands</i> is off.</summary>
    public const string CommandsOff = "Commands from the Web UI are off: Web UI commands in /settings › Client › Web UI turns them on.";

    /// <summary>A command kept for the PC (<see cref="App.WebCommands.Never"/>).</summary>
    public static string PcOnly(string word) => word + " runs at the PC only.";

    /// <summary>A form that opens a window or a pane only the PC has, and the form that works here when there is one.</summary>
    public static string OpensPane(string word, string? works) =>
        word + " opens a pane or a window at the PC, so it runs there." + (works is null ? "" : " Here: " + works + ".");

    /// <summary><c>/loop</c> over a command the page refuses (later on 2026-10-15): that command's own reason.</summary>
    public static string LoopRefused(string reason) => "/loop: " + reason;

    /// <summary>A command whose tools are switched off.</summary>
    public static string Needs(string word, string setting) => word + " needs " + setting + " on (/tools).";

    /// <summary>A word the app does not know: the page offers to send the line as a message.</summary>
    public static string UnknownCommand(string word) => "Unknown command: " + word + ". Type / to see the list.";

    /// <summary>Bare <c>/shopping</c> and <c>/shopping add</c>: the page has a shopping list of its own.</summary>
    public const string UseShoppingPage = "The shopping list is this page's Shopping tab; /shopping add <items> adds from here.";

    /// <summary>The audit line of a command from the page: its word, the address and the verdict, never its words.</summary>
    public static string CommandLogLine(string word, IPAddress? remote, string verdict) =>
        "Web UI command " + word + " from " + Where(remote) + ": " + verdict + ".";

    /// <summary>A command the screen refused as it took the line (the settings changed meanwhile, or the server would be walked).</summary>
    public static string RefusedAtDispatch(string reason) => "From the Web UI: " + reason;

    /// <summary><c>/model &lt;id&gt;</c> or <c>/reasoning</c> from the page while the connect would scan for a server.</summary>
    public const string WouldScan = "the server would be looked for (none saved is in use): pick it at the PC.";

    /// <summary><c>/model &lt;id&gt;</c> from the page naming an embedded model not installed.</summary>
    public const string WouldInstall = "that embedded model is not installed: install it at the PC.";

    /// <summary><c>/tts on</c> or <c>/stt on</c> from the page while its models would be downloaded first (2026-10-19).</summary>
    public static string WouldDownload(string word) => word + " on would download its models first: turn it on at the PC.";

    /// <summary>A post past its rate limit (2026-10-15, <see cref="WebRateLimit"/>); the page does not try again by itself.</summary>
    public const string SlowDown = "Slow down: too many in a row. Try again in a moment.";

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

    /// <summary>After a picture's name on the page: its file is gone, or the server would not serve it (later on 2026-10-14).</summary>
    public const string PictureGone = "is no longer there.";

    /// <summary>After a picture's name on the page: its file is over what the server decodes for a thumbnail.</summary>
    public const string PictureTooLarge = "is too large to show here; tap to open the original.";

    /// <summary>The overlay's link to the picture's original.</summary>
    public const string OpenOriginal = "Open original";

    /// <summary>A pick card's way out (later on 2026-10-14): nothing chosen.</summary>
    public const string CancelChoice = "Cancel";

    /// <summary>An info card's close.</summary>
    public const string CloseChoice = "Close";

    /// <summary>The most a list card carries, in UTF-16 characters (about 256 KB).</summary>
    public const int MaxInfoChars = 256 * 1024;

    /// <summary>Ahead of a list card cut to <see cref="MaxInfoChars"/>: its first part left out.</summary>
    public const string InfoCutNote = "(the first part is left out: the whole list is at the PC)";

    /// <summary>A rewind from the page whose message carried pictures, PDFs or pasted blocks: they come back at the PC alone.</summary>
    public const string RewindAttachmentsNotice = "(the message's pictures, PDFs and pasted blocks come back on the PC's line only: rewind there to send them again)";

    /// <summary>The page's offer under an unknown command: send the line as a message instead.</summary>
    public const string SendAsMessage = "Send as a message";

    /// <summary>The page's offer under <c>//word</c>: the command meant, put on the draft (the line follows).</summary>
    public const string UseLine = "Use";

    /// <summary>The page's line when a command put a text on its draft (<c>/prompts &lt;n&gt;</c>, <c>/rewind</c>).</summary>
    public const string DraftFilled = "Put on the draft.";

    /// <summary>The page's undo of a filled draft.</summary>
    public const string Undo = "Undo";

    /// <summary>The status row's word while the PC works with no label to show (2026-10-15: no pane at the PC).</summary>
    public const string Working = "working";

    /// <summary>The ✕ inside the page's message box (later on 2026-10-15): its label.</summary>
    public const string ClearDraft = "Clear the draft";

    /// <summary>The page's line after the ✕, with <see cref="Undo"/>.</summary>
    public const string DraftCleared = "Draft cleared.";

    /// <summary>The floating ↕'s labels (later on 2026-10-15): every fold opened, or closed again.</summary>
    public const string ExpandAll = "Expand all";

    public const string CollapseAll = "Collapse all";

    /// <summary>The floating ⤒ and ⤓ (later on 2026-10-15).</summary>
    public const string ToTop = "To the top";

    public const string ToBottom = "To the bottom";

    /// <summary>The floating 🎞️ and the drawer's region (later on 2026-10-15).</summary>
    public const string StripButton = "Pictures";

    /// <summary>The drawer's head for one picture, and for more (<c>{0}</c> the count): two words, as the page can't decline a noun.</summary>
    public const string StripOne = "1 picture";

    public const string StripMany = "{0} pictures";

    /// <summary>The drawer's ✕.</summary>
    public const string CloseStrip = "Close the pictures";

    /// <summary>The overlay's tag while it follows new pictures, on the newest slot (later on 2026-10-15), and its label for a screen reader.</summary>
    public const string Live = "live";

    public const string LiveLabel = "following new pictures";

    /// <summary>A strip picture the page picked that the strip no longer holds, or with the strip off.</summary>
    public const string NoSuchPicture = "That picture is no longer in the strip.";

    // ── The page's saved prompts (later on 2026-10-15) ──

    /// <summary>What the page opens for bare <c>/prompts</c>.</summary>
    public const string OpenPrompts = "prompts";

    /// <summary>A Page line that reached the PC (never by the page's own path): what it is.</summary>
    public const string OpensOnThePage = "That opens on the page itself.";

    /// <summary>The 📣 button's label, and the sheet's (2026-10-16: its three tabs named; in their order since 2026-10-17, History before Memory).</summary>
    public const string PromptsButton = "Prompts, history and memory";

    /// <summary>The sheet's prompts tab with nothing saved (2026-10-16: the page saves them too).</summary>
    public const string PromptsNone = "No saved prompts yet. ⇪ saves the draft as one.";

    /// <summary>A history row's ➤, and a saved prompt's tap since 2026-10-16 (its label, before the prompt).</summary>
    public const string PromptsSend = "Send this prompt";

    /// <summary>A saved prompt's ✎ (2026-10-16, the user's ask, when a tap came to send): the prompt on the message line, ahead of what's there.</summary>
    public const string PromptsToDraft = "Put on the message line";

    /// <summary>A long list's filter.</summary>
    public const string Filter = "Filter";

    /// <summary>The sheet's ✕.</summary>
    public const string ClosePrompts = "Close the sheet";

    // ── The sheet's tabs, its ⇪ and its 🗑️ (2026-10-16, the user's ask) ──

    /// <summary>The sheet's three tabs.</summary>
    public const string PromptsTab = "📣 Prompts";

    public const string MemoryTab = "💾 Memory";

    public const string HistoryTab = "🕘 History";

    /// <summary>The memory tab with none saved.</summary>
    public const string MemoryNone = "No memories yet. ⇪ saves the draft as one.";

    /// <summary>The history tab with nothing in it.</summary>
    public const string HistoryNone = "No messages in the history yet.";

    /// <summary>The bar's ⇪ on the prompts and memory tabs.</summary>
    public const string SaveDraft = "⇪ save draft";

    /// <summary>⇪ with an empty draft, and a memory save of nothing.</summary>
    public const string DraftEmpty = "The draft is empty: type the text to save first.";

    /// <summary>A row's 🗑️, and its label once armed.</summary>
    public const string DeleteRow = "Delete";

    public const string DeleteRowArmed = "Delete?";

    /// <summary>The history tab's clear all, and its label once armed.</summary>
    public const string ClearAll = "🗑️ clear all";

    public const string ClearAllArmed = "Clear all?";

    /// <summary>A memory turned off, after its text.</summary>
    public const string MemoryOff = "off";

    /// <summary>The memory tab's line while <i>Memory mode</i> is off.</summary>
    public const string MemoryModeOff = "Memory is off: the model doesn't see these, and saving is refused (/memory read-write turns it on).";

    /// <summary>A delete of a saved prompt that is gone already.</summary>
    public const string PromptGone = "That prompt is no longer saved.";

    /// <summary>A delete of a memory that is gone already.</summary>
    public const string MemoryGone = "That memory is no longer saved.";

    /// <summary>A memory the store could not remove (the file could not be written).</summary>
    public const string MemoryNotRemoved = "Could not remove the memory; the log has the reason.";

    // ── The 💾 tab's edit and off/on (2026-10-18, the user's ask) ──

    /// <summary>A memory's text, the button that opens its editor (its name for a screen reader, before the text).</summary>
    public const string MemoryEdit = "Edit this memory";

    /// <summary>The editor's buttons.</summary>
    public const string MemoryEditSave = "Save";

    public const string MemoryEditCancel = "Cancel";

    /// <summary>The editor's off/on button, by the memory's state.</summary>
    public const string MemoryTurnOff = "Turn off";

    public const string MemoryTurnOn = "Turn on";

    /// <summary>An edit to nothing (the page's Save is off while the text is empty; a post that sends one).</summary>
    public const string MemoryEditEmpty = "A memory cannot be empty; delete it instead.";

    /// <summary>An off/on post that says neither.</summary>
    public const string MemoryToggleUnsaid = "Say whether to turn the memory off or on.";

    /// <summary>The PC's hint row after the page saved a prompt (its number at the PC) or deleted one.</summary>
    public static string PromptSavedFlash(int number) => "📱 saved prompt " + number.ToString(CultureInfo.InvariantCulture);

    public const string PromptDeletedFlash = "📱 deleted a saved prompt";

    /// <summary>The PC's hint row after the page saved a memory or deleted one.</summary>
    public const string MemorySavedFlash = "📱 saved a memory";

    public const string MemoryDeletedFlash = "📱 deleted a memory";

    /// <summary>The PC's hint row after the page edited a memory, or turned one off or on (2026-10-18).</summary>
    public const string MemoryEditedFlash = "📱 edited a memory";

    public const string MemoryDisabledFlash = "📱 turned a memory off";

    public const string MemoryEnabledFlash = "📱 turned a memory on";

    /// <summary>The PC's hint row after the page cleared the message history.</summary>
    public static string HistoryClearedFlash(int count) => "📱 cleared " + App.ChatScreen.HistoryMessages(count);

    /// <summary>The audit lines for the sheet's changes: what changed and from where, never a prompt's or a memory's text (the log is shareable).</summary>
    public static string PromptSavedLogLine(int number, IPAddress? remote) =>
        "Web UI saved prompt " + number.ToString(CultureInfo.InvariantCulture) + " from " + Where(remote) + ".";

    public static string PromptDeletedLogLine(IPAddress? remote) => "Web UI deleted a saved prompt from " + Where(remote) + ".";

    public static string MemorySavedLogLine(IPAddress? remote) => "Web UI saved a memory from " + Where(remote) + ".";

    public static string MemoryDeletedLogLine(IPAddress? remote) => "Web UI deleted a memory from " + Where(remote) + ".";

    public static string MemoryEditedLogLine(IPAddress? remote) => "Web UI edited a memory from " + Where(remote) + ".";

    public static string MemoryToggledLogLine(bool off, IPAddress? remote) => "Web UI turned a memory " + (off ? "off" : "on") + " from " + Where(remote) + ".";

    public static string HistoryClearedLogLine(int count, IPAddress? remote) =>
        "Web UI cleared the message history (" + App.ChatScreen.HistoryMessages(count) + ") from " + Where(remote) + ".";

    // ── The held queue (2026-10-17, the user's question: under Queue cancel mode hold the page's lines waited with nothing saying why) ──

    /// <summary>The banner over the page's waiting lines while they are held after a cancel.</summary>
    public const string QueueHeld = "Held after a cancel: these wait until a reply ends or you resume.";

    /// <summary>The banner's button.</summary>
    public const string Resume = "Resume";

    /// <summary>The page's Resume with nothing held (a reply ended meanwhile).</summary>
    public const string NotHeld = "Nothing is held.";

    /// <summary>The PC's hint row after the page's Resume.</summary>
    public const string QueueResumedFlash = "📱 resumed the queue";

    /// <summary>The audit line.</summary>
    public static string QueueResumedLogLine(IPAddress? remote) => "Web UI resumed the held queue from " + Where(remote) + ".";

    // ── The status row's 🧠 (2026-10-17, the user's ask: no sign on the page that a reflection held the server, and no way to stop it) ──

    /// <summary>The 🧠's label while a reflection runs, and once the first tap armed it.</summary>
    public const string LearnCancel = "A reflection is running: tap twice to cancel it";

    public const string LearnCancelArmed = "Tap again to cancel the reflection";

    /// <summary>The page's cancel with no reflection running (it ended between the status and the tap).</summary>
    public const string NoReflection = "No reflection is running.";

    /// <summary>The PC's hint row after the page canceled the reflection.</summary>
    public const string LearnCancelledFlash = "📱 canceled the reflection";

    /// <summary>The audit line.</summary>
    public static string LearnCancelledLogLine(IPAddress? remote) => "Web UI canceled the reflection from " + Where(remote) + ".";

    // ── The page's 🗑️ (later on 2026-10-15) ──

    /// <summary>The overlay's 🗑️, and its label once armed.</summary>
    public const string DeletePicture = "Delete";

    public const string DeleteArmed = "Tap again to delete";

    /// <summary>A delete of a picture neither the transcript nor the strip holds, or whose file is gone already.</summary>
    public const string PictureNotHere = "That picture is no longer here.";

    /// <summary>A delete of a picture with no file of its own (pasted, a camera watch frame).</summary>
    public const string PictureHasNoFile = "That picture has no file of its own to delete.";

    /// <summary>The page's note after a delete: where it went (the Trash on a Mac).</summary>
    public static string PictureDeleted(string name, Files.DeleteWay way) => way switch
    {
        Files.DeleteWay.Recycled => "Moved " + name + " to the " + Files.FileText.RecycleBinName + ".",
        Files.DeleteWay.DeletedNoBin => "Deleted " + name + " (no " + Files.FileText.RecycleBinName + " on that drive).",
        _ => "Deleted " + name + ".",
    };

    /// <summary>A delete the system refused; the picture stays.</summary>
    public static string PictureDeleteFailed(string name, string detail) => "Could not delete " + name + ": " + detail;

    /// <summary>A delete the PC did not finish within the request's wait.</summary>
    public static string StillDeleting(string name) => name + " is still being deleted at the PC.";

    /// <summary>The PC's hint row after the page deleted a picture.</summary>
    public static string PictureDeletedFlash(string name, Files.DeleteWay way) =>
        "📱 " + (way == Files.DeleteWay.Recycled ? "moved " + name + " to the " + Files.FileText.RecycleBinName : "deleted " + name);

    /// <summary>The audit line: the name alone, never the path.</summary>
    public static string PictureDeletedLogLine(string name, Files.DeleteWay way, IPAddress? remote) =>
        "Web UI deleted \"" + name + "\"" + (way == Files.DeleteWay.Recycled ? " (to the " + Files.FileText.RecycleBinName + ")" : "") + " from " + Where(remote) + ".";

    /// <summary>The page's own words, sent with every snapshot.</summary>
    public static readonly WebWords Words = new(PictureGone, PictureTooLarge, OpenOriginal, SendAsMessage, UseLine, DraftFilled, Undo, Working, CloseChoice,
        UI.TranscriptRenderer.NoReplyText, UI.ThinkingFoldText.LiveBare, EntryGone,
        ClearDraft, DraftCleared, ExpandAll, CollapseAll, ToTop, ToBottom, StripButton, StripOne, StripMany, CloseStrip, Live, LiveLabel, DeletePicture, DeleteArmed, PromptsButton, PromptsNone, PromptsSend, PromptsToDraft, Filter, ClosePrompts,
        PromptsTab, MemoryTab, HistoryTab, MemoryNone, HistoryNone, SaveDraft, DraftEmpty, DeleteRow, DeleteRowArmed, ClearAll, ClearAllArmed, MemoryOff,
        MemoryModeOff,
        BotCheckHint, BotCheckPicture, BotCheckZoom, BotCheckScrollUp, BotCheckScrollDown, BotCheckTypeHere, BotCheckSend, BotCheckEnter, BotCheckBackspace, BotCheckTab,
        BotCheckStalled, LearnCancel, LearnCancelArmed, Resume,
        MemoryEdit, MemoryEditSave, MemoryEditCancel, MemoryTurnOff, MemoryTurnOn);

    // ── A bot check's card (2026-10-17): the Browser window on the page while a check runs in it ──

    /// <summary>The line under the picture.</summary>
    public const string BotCheckHint = "This is the PC's Browser window. Tap to click; touch and hold to press and hold.";

    /// <summary>The picture's alternative text.</summary>
    public const string BotCheckPicture = "The PC's Browser window";

    /// <summary>The zoom button's name (fit, 2×, 3×).</summary>
    public const string BotCheckZoom = "Zoom";

    /// <summary>The scroll buttons' names.</summary>
    public const string BotCheckScrollUp = "Scroll up";
    public const string BotCheckScrollDown = "Scroll down";

    /// <summary>The text box's placeholder.</summary>
    public const string BotCheckTypeHere = "Type into the window";

    /// <summary>The text box's button.</summary>
    public const string BotCheckSend = "Type";

    /// <summary>The key buttons' names.</summary>
    public const string BotCheckEnter = "Enter";
    public const string BotCheckBackspace = "Backspace";
    public const string BotCheckTab = "Tab";

    /// <summary>Over the last picture while the window isn't drawing.</summary>
    public const string BotCheckStalled = "The window isn't drawing right now (is the PC asleep?). Give up, or wait and it may come back.";

    /// <summary>A bot check card's input or picture refused with Web UI bot check control off.</summary>
    public const string BotCheckControlOff = "Web UI bot check control is off: the page can't see or drive the Browser window.";

    /// <summary>A picture or input for a check that is over, or never was.</summary>
    public const string BotCheckGone = "That bot check is over.";

    /// <summary>The PC's hint row when a page first drives a check.</summary>
    public static string BotCheckDrivenFlash(string host) => "📱 the Web UI is driving the bot check on " + host;

    /// <summary>The audit line when a page first drives a check: the address, never what it types.</summary>
    public static string BotCheckDrivenLogLine(string host, IPAddress? remote) => "The Web UI at " + Where(remote) + " is driving the bot check on " + host + ".";

    /// <summary>A folded entry whose text the page asked for after the transcript let it go (2026-10-15).</summary>
    public const string EntryGone = "(no longer available)";

    /// <summary>The shopping page's store picker (2026-10-15).</summary>
    public const string StoreLabel = "Store";

    /// <summary>A store the page picked that is gone meanwhile (2026-10-15).</summary>
    public const string NoSuchStore = "That store is gone from the list.";

    /// <summary>A store picked on the page, flashed on the PC's hint row (2026-10-15).</summary>
    public static string StoreFlash(string name) => Glyph + "store: " + name;

    // ── The Shopping tab's list controls (2026-10-16) ────────────────────────

    /// <summary>
    /// The Shopping tab's words for the list's controls (2026-10-16, the user's ask), sent with the list: <see cref="Shopping.ShoppingText"/>'s
    /// button words where they exist, their double spaces (there for Windows Terminal's two-cell ☑ and ☐) collapsed, since a page would show the gap.
    /// </summary>
    public static WebShopWords ShopWords { get; } = new(
        AddPlaceholder: "Add items: 2 milk, eggs",
        Add: Collapsed(Shopping.ShoppingText.AddButton),
        Past: "🕘 past items",
        PastTitle: "Bought before",
        Filter: "Filter the past items",
        NoMatch: UI.MenuFilter.NoMatchLine("{0}"),
        Forget: "Forget",
        ForgetArmed: "Tap again to forget",
        TickAll: Collapsed(Shopping.ShoppingText.TickAllButton),
        UntickAll: Collapsed(Shopping.ShoppingText.UntickAllButton),
        Clear: Collapsed(Shopping.ShoppingText.ClearButton) + " ticked",
        ClearArmed: "Tap again to clear the ticked lines",
        Options: "{0}: options",
        Less: Collapsed(Shopping.ShoppingText.LessButton),
        More: Collapsed(Shopping.ShoppingText.MoreButton),
        Quantity: "Quantity",
        Rename: Collapsed(Shopping.ShoppingText.RenameButton),
        Save: "Save",
        Remove: "🗑️ remove",
        RemoveArmed: "Tap again to remove",
        Close: "Close",
        Gone: NoSuchItem);

    /// <summary>A button word with its runs of spaces made one.</summary>
    internal static string Collapsed(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The add box posted with no item in it (2026-10-16; the page keeps ⊕ disabled while it is blank).</summary>
    public const string NothingToAdd = "Type an item to add: commas between several, a quantity before or after (2 milk, eggs).";

    /// <summary>One add with more than <paramref name="max"/> items, refused whole (2026-10-16).</summary>
    public static string TooManyToAdd(int max) => "Too many at once: add at most " + max.ToString(CultureInfo.InvariantCulture) + " items in one go.";

    /// <summary>A past item gone meanwhile (2026-10-16): re-added already, forgotten, or put back on the list from the PC.</summary>
    public const string NoSuchPastItem = "That item is no longer among the past items.";

    /// <summary>Lines added from the page, flashed on the PC's hint row (2026-10-16): the first three, then how many more.</summary>
    public static string AddedFlash(IReadOnlyList<string> lines) => Glyph + "added: " + FirstFew(lines);

    /// <summary>A past item forgotten from the page, flashed on the PC's hint row (2026-10-16).</summary>
    public static string ForgotFlash(string name) => Glyph + "forgot: " + name;

    /// <summary>A line's quantity changed from the page, flashed on the PC's hint row (2026-10-16): the line as it now reads.</summary>
    public static string QuantityFlash(string line) => Glyph + "quantity: " + line;

    /// <summary>A line renamed from the page, flashed on the PC's hint row (2026-10-16).</summary>
    public static string RenamedFlash(string from, string to) => Glyph + "renamed: " + from + " → " + to;

    /// <summary>A line removed from the page, flashed on the PC's hint row (2026-10-16).</summary>
    public static string RemovedFlash(string line) => Glyph + "removed: " + line;

    /// <summary>Every line ticked or unticked from the page, flashed on the PC's hint row (2026-10-16).</summary>
    public static string CheckedAllFlash(int count, bool on) => Glyph + (on ? "ticked all: " : "unticked all: ") + Lines(count);

    /// <summary>The ticked lines cleared from the page, flashed on the PC's hint row (2026-10-16).</summary>
    public static string ClearedFlash(int count) => Glyph + "cleared the ticked: " + Lines(count);

    private static string FirstFew(IReadOnlyList<string> lines) =>
        string.Join(", ", lines.Take(3)) + (lines.Count > 3 ? " and " + (lines.Count - 3).ToString(CultureInfo.InvariantCulture) + " more" : "");

    private static string Lines(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " line" : " lines");

    // ── The Home tab (2026-10-15) ─────────────────────────────────────────────

    /// <summary>
    /// Every Home action while <i>Web UI home controls</i> is off: a page from before the switch (later on 2026-10-15, the tab itself goes with the
    /// setting, so the host answers its 404 first; this is the backstop's 403).
    /// </summary>
    public const string HomeControlsOff = HomeTabOff;

    /// <summary>A tile, an action or a scene gone or changed since the page read it.</summary>
    public const string HomeChanged = "That changed since the page read it: look again.";

    /// <summary>No URL or token for Home Assistant.</summary>
    public const string HomeNotSetUp = "Home Assistant isn't set up: /settings › Tools › Home › HomeAssistant.";

    /// <summary>The live feed opening.</summary>
    public const string HomeConnecting = "Connecting to Home Assistant…";

    /// <summary>The live feed lost, trying again.</summary>
    public static string HomeRetrying(string? error) => "Home Assistant can't be reached" + (string.IsNullOrWhiteSpace(error) ? "" : " (" + error.TrimEnd('.') + ")") + "; trying again.";

    /// <summary>The token refused.</summary>
    public const string HomeUnauthorized = "Home Assistant refused the token: set a new one at the PC.";

    /// <summary>A question for Assist over the cap.</summary>
    public static string HomeSayTooLong(int max) => "Too long: a question holds at most " + max.ToString(CultureInfo.InvariantCulture) + " characters.";

    /// <summary>A question while another is asked.</summary>
    public const string HomeSayBusy = "Home Assistant is still answering the last one.";

    /// <summary>The video remote with no window open.</summary>
    public const string NoVideo = "No video is open at the PC.";

    /// <summary><see cref="NoVideo"/> for where the router plays now (2026-10-16): the PC's words, or the YouTube tab's.</summary>
    public static string NoVideoIn(Viewer.VideoPlace place) => place == Viewer.VideoPlace.WebTab ? "No video is open in the YouTube tab." : NoVideo;

    /// <summary>Home's video card: where the video plays (2026-10-16), from its snapshot's place.</summary>
    public static string VideoWhere(Viewer.VideoPlace place) => place == Viewer.VideoPlace.WebTab ? "in the YouTube tab" : "on the PC";

    // ── The YouTube tab (2026-10-16) ─────────────────────────────────────────

    /// <summary>
    /// A player the page could not make, as the model and the status read it: YouTube's script never came (no internet, a blocker), or
    /// another reason. The page's own reason is never shown raw.
    /// </summary>
    public static string PlayerFailed(string? reason) => reason switch
    {
        "api" => "the Web UI's YouTube tab could not load YouTube's player (no internet on the phone, or a blocker stopped it).",
        _ => "the Web UI's YouTube tab could not start YouTube's player.",
    };

    /// <summary>The banner a page shows instead of leaving for a waiting video: a link to the tab.</summary>
    public const string WatchBanner = "▶ A video is waiting — Watch";

    /// <summary>The YouTube tab's words, made here as every word the pages show (<see cref="WebVideoWords"/>).</summary>
    public static WebVideoWords VideoWords { get; } = new(
        Waiting: "Playing on another page",
        PlayHere: "Play here",
        TapForSound: "Tap the video's speaker for sound",
        TapToStart: "Tap the video to start",
        Idle: "Nothing is playing here. Ask in Chat for a video, and it plays on this tab.",
        Loading: "Loading YouTube's player…",
        Failed: "YouTube's player didn't load: is this phone online?",
        Closed: "The video was closed.")
    {
        // The saved list under the player (later on 2026-10-16).
        Saved = new WebSavedWords(
            Filter: "Filter the saved videos",
            NoMatch: UI.MenuFilter.NoMatchLine("{0}"),
            Details: "Details and moves",
            MoveUp: "Move up",
            MoveDown: "Move down",
            MoveTop: "Move to the top",
            MoveBottom: "Move to the bottom",
            Delete: "Remove",
            DeleteArmed: "Tap again to remove",
            ClearAll: "Clear all",
            ClearArmed: "Tap again to clear all",
            SavePlaying: "☆ Save the playing video",
            Saved: "★ Saved",
            Looking: YouTube.YouTubeText.LookingUpTitles),
    };

    /// <summary>A saved list's play from a page whose stream isn't open (later on 2026-10-16): it reconnects by itself.</summary>
    public const string YouTubePageNotConnected = "This page isn't connected to the app right now; try again in a moment.";

    /// <summary>A saved video gone meanwhile (later on 2026-10-16): removed at the PC or on another page.</summary>
    public const string SavedVideoGone = "That video is no longer saved.";

    /// <summary>A move that is none of up, down, top, bottom (later on 2026-10-16).</summary>
    public const string SavedMoveUnknown = "A move is up, down, top or bottom.";

    /// <summary>The PC's hint row as the YouTube tab plays a saved video (later on 2026-10-16).</summary>
    public const string SavedPlayFlash = Glyph + "YouTube tab: playing a saved video";

    /// <summary>The PC's hint row as the YouTube tab removes a saved video.</summary>
    public const string SavedRemoveFlash = Glyph + "YouTube tab: removed a saved video";

    /// <summary>The PC's hint row as the YouTube tab clears the saved videos.</summary>
    public const string SavedClearFlash = Glyph + "YouTube tab: cleared the saved videos";

    /// <summary>The PC's hint row as the YouTube tab moves a saved video.</summary>
    public const string SavedMoveFlash = Glyph + "YouTube tab: moved a saved video";

    /// <summary>The PC's hint row as the YouTube tab saves the playing video.</summary>
    public const string SavedSaveFlash = Glyph + "YouTube tab: saved the playing video";

    /// <summary>A saved list's change from the page (audit, no titles): <paramref name="what"/> is <c>played</c>, <c>removed</c>, <c>cleared</c>, <c>moved</c> or <c>saved</c>.</summary>
    public static string SavedLogLine(string what, IPAddress? remote) => "Web UI: the YouTube tab " + what + " " + (what == "cleared" ? "the saved videos" : "a saved video") + " from " + Where(remote) + ".";

    /// <summary><c>/youtube list</c> typed in the page's Chat (later on 2026-10-16), or the bare <c>/youtube</c> (2026-10-18): the YouTube tab's saved list does it.</summary>
    public const string UseYouTubePage = "The saved videos (/youtube, /youtube list) are on the YouTube tab here: its saved list plays, moves and removes them.";

    /// <summary>
    /// The Resume button's words (2026-10-16): <c>Resume "Big Buck Bunny" at 3:12</c>; the id for a video with no title yet; a playlist's entry.
    /// The time from <see cref="YouTube.YouTubeIds.FormatTime"/>, so the phone and the PC's lines read alike.
    /// </summary>
    public static string ResumeLabel(string? title, string? videoId, string? playlistId, int? entry, double start)
    {
        string what = !string.IsNullOrWhiteSpace(title) ? "\"" + title + "\""
            : videoId is not null ? "video " + videoId
            : "playlist " + playlistId;
        string inList = playlistId is not null && entry is { } at ? " (entry " + (at + 1).ToString(CultureInfo.InvariantCulture) + ")" : "";
        string time = start > 0 ? " at " + YouTube.YouTubeIds.FormatTime(start) : " from the start";
        return "Resume " + what + inList + time;
    }

    /// <summary>The PC's hint row as a page takes playback with Play here.</summary>
    public const string YouTubeClaimFlash = Glyph + "YouTube tab: playing here now";

    /// <summary>The PC's hint row as a page's Resume tap starts the last video.</summary>
    public const string YouTubeResumeFlash = Glyph + "YouTube tab: resumed";

    /// <summary>A page took playback with Play here (audit).</summary>
    public static string YouTubeClaimLogLine(IPAddress? remote) => "Web UI: a YouTube tab took playback from " + Where(remote) + ".";

    /// <summary>A page's Resume tap (audit).</summary>
    public static string YouTubeResumeLogLine(IPAddress? remote) => "Web UI: a YouTube tab resumed the last video from " + Where(remote) + ".";

    /// <summary>A page became the YouTube tab's owner, first come or promoted (audit).</summary>
    public static string YouTubeOwnerLogLine(IPAddress? remote) => "Web UI: a YouTube tab became the player from " + Where(remote) + ".";

    /// <summary>The player moved to the tab in view, for its coming into view or a play (audit; later on 2026-10-16).</summary>
    public static string YouTubeMovedLogLine(IPAddress? remote) => "Web UI: the player moved to the YouTube tab in view from " + Where(remote) + ".";

    // ── The YouTube tab's Debug lines (later on 2026-10-16: the user's log held two lines for the whole muddle) ──────────────────────
    // A page by its first four characters and its address, never its whole id.

    private static string PageName(string page, IPAddress? remote) => "page " + (page.Length > 4 ? page[..4] : page) + " (" + Where(remote) + ")";

    /// <summary>A page's stream attached: in view or not, owner or waiting.</summary>
    public static string YouTubeAttachDebug(string page, IPAddress? remote, bool visible, bool owner) =>
        "YouTube tab: " + PageName(page, remote) + " attached " + (visible ? "in view" : "hidden") + ", " + (owner ? "the player" : "waiting") + ".";

    /// <summary>A page's stream detached.</summary>
    public static string YouTubeDetachDebug(string page, IPAddress? remote, bool owner) =>
        "YouTube tab: " + PageName(page, remote) + " detached" + (owner ? " (the player: its grace starts)" : "") + ".";

    /// <summary>A page came into view or left it.</summary>
    public static string YouTubeSeenDebug(string page, IPAddress? remote, bool visible, bool was) =>
        "YouTube tab: " + PageName(page, remote) + (visible ? " in view" : " hidden") + (visible == was ? " (unchanged)" : "") + ".";

    /// <summary>A play's fate: loaded, held, or loaded once a page showed (with the page), or held (none).</summary>
    public static string YouTubePlayDebug(string what, string? page, IPAddress? remote) =>
        "YouTube tab: a play " + what + (page is null ? "" : " " + PageName(page, remote)) + ".";

    /// <summary>The owner's <c>page</c> report: read as a reload, or ignored as the script the video was just given to.</summary>
    public static string YouTubePageReportDebug(string page, IPAddress? remote, bool acted) =>
        "YouTube tab: " + PageName(page, remote) + (acted ? " reloaded: its video closed" : " reported its script, the one just given the video: ignored") + ".";

    /// <summary>A claim honored, or ignored as the browser's reconnect repeating one.</summary>
    public static string YouTubeClaimDebug(string page, IPAddress? remote, bool honored) =>
        "YouTube tab: " + PageName(page, remote) + (honored ? " claimed the player" : " repeated a claim (a reconnect): ignored") + ".";

    /// <summary>YouTube's refusal on the tab, as the window logs its own.</summary>
    public static string YouTubeRefusedLogLine(string? id, int code) =>
        "YouTube refused video " + id + " in the player on the Web UI's YouTube tab (error " + code.ToString(CultureInfo.InvariantCulture) + ").";

    /// <summary>The model card's server for the embedded model.</summary>
    public const string EmbeddedServer = "embedded";

    /// <summary>The model card's server for a Docker container.</summary>
    public static string DockerServer(string container) => "Docker: " + container;

    /// <summary>The model card's server for the Claude Code server.</summary>
    public const string ClaudeCodeServer = "Claude Code";

    /// <summary>A room's lights on of all, on the Home tab's room box (2026-10-16): <c>3 of 5 on</c>.</summary>
    public static string RoomLightsOn(int on, int total) =>
        on.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) + " on";

    /// <summary>The Home tab's own words.</summary>
    public static readonly WebHomeWords HomeWords = new(
        HomeControlsOff, "Rooms", "Scenes", "All lights", "Actions", "Ask the house", "Ask", "Now playing", "The PC", "Model", "Yes", "No", CloseChoice, "loading", "busy", "window", "down", "up");

    /// <summary>A home action from the page, flashed on the PC's hint row: the device and the service.</summary>
    public static string HomeFlash(string name, string service) => Glyph + name + ": " + service.Replace('_', ' ');

    /// <summary>A question for Assist from the page, flashed on the PC's hint row (never its words).</summary>
    public const string HomeSayFlash = Glyph + "asked Home Assistant";

    /// <summary>The video remote from the page, flashed on the PC's hint row.</summary>
    public static string HomeVideoFlash(string action) => Glyph + "video: " + action;

    /// <summary>A home action from the page (audit): the device, the service, the address.</summary>
    public static string HomeLogLine(string name, string call, IPAddress? remote) => "Web UI called " + call + " on \"" + name + "\" from " + Where(remote) + ".";

    /// <summary>A question for Assist from the page (audit): its length alone, never its words.</summary>
    public static string HomeSayLogLine(int length, IPAddress? remote) =>
        "Web UI asked Home Assistant (" + length.ToString(CultureInfo.InvariantCulture) + " characters) from " + Where(remote) + ".";

    /// <summary>The video remote from the page (audit).</summary>
    public static string HomeVideoLogLine(string action, IPAddress? remote) => "Web UI told the video to " + action + " from " + Where(remote) + ".";

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

    /// <summary>A store picked from the phone (audit, 2026-10-15).</summary>
    public static string StoreLogLine(string store, IPAddress? remote) => "Web UI made \"" + store + "\" the shopping store from " + Where(remote) + ".";

    /// <summary>A shopping line ticked from the phone (audit).</summary>
    public static string CheckedLogLine(string line, bool on, IPAddress? remote) =>
        "Web UI " + (on ? "ticked" : "unticked") + " \"" + line + "\" from " + Where(remote) + ".";

    /// <summary>Lines added from the phone, typed or a past item again (audit, 2026-10-16): every line as it now reads.</summary>
    public static string AddedLogLine(IReadOnlyList<string> lines, IPAddress? remote) =>
        "Web UI added " + string.Join(", ", lines.Select(l => "\"" + l + "\"")) + " to the shopping list from " + Where(remote) + ".";

    /// <summary>A past item forgotten from the phone (audit, 2026-10-16).</summary>
    public static string ForgotLogLine(string name, IPAddress? remote) => "Web UI forgot the past item \"" + name + "\" from " + Where(remote) + ".";

    /// <summary>A line's quantity changed from the phone (audit, 2026-10-16): the line as it now reads.</summary>
    public static string QuantityLogLine(string line, IPAddress? remote) => "Web UI set the quantity: \"" + line + "\" from " + Where(remote) + ".";

    /// <summary>A line renamed from the phone (audit, 2026-10-16); a merge into another line reads the same.</summary>
    public static string RenamedLogLine(string from, string to, IPAddress? remote) =>
        "Web UI renamed \"" + from + "\" to \"" + to + "\" on the shopping list from " + Where(remote) + ".";

    /// <summary>A line removed from the phone (audit, 2026-10-16).</summary>
    public static string RemovedLogLine(string line, IPAddress? remote) => "Web UI removed \"" + line + "\" from the shopping list from " + Where(remote) + ".";

    /// <summary>Every line ticked or unticked from the phone (audit, 2026-10-16), only when some changed.</summary>
    public static string CheckedAllLogLine(int count, bool on, IPAddress? remote) =>
        "Web UI " + (on ? "ticked" : "unticked") + " every line (" + Lines(count) + ") from " + Where(remote) + ".";

    /// <summary>The ticked lines cleared from the phone (audit, 2026-10-16), only when some went.</summary>
    public static string ClearedLogLine(int count, IPAddress? remote) => "Web UI cleared the ticked lines (" + Lines(count) + ") from " + Where(remote) + ".";

    /// <summary>An address past a rate limit, once a minute at most (2026-10-15).</summary>
    public static string RateLimitedLogLine(IPAddress? remote, WebRateBucket bucket) =>
        "Web UI: too many " + bucket switch
        {
            WebRateBucket.Act => "taps",
            WebRateBucket.Say => "questions to Home Assistant",
            WebRateBucket.SignIn => "sign-in attempts",
            WebRateBucket.Report => "player reports",
            _ => "requests",
        } + " from " + Where(remote) + "; refused for now.";

    private static string Where(IPAddress? remote) => remote is null ? "an unknown address" : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();

    /// <summary>The app's version, as the pages compare it.</summary>
    public static string Version => typeof(WebUiText).Assembly.GetName().Version?.ToString() ?? "";
}
