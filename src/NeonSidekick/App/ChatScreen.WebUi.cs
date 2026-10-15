using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── The Web UI (2026-10-14, the user's ask) ─────────────────────────────────
// A browser front end the app hosts for the loaded profile: the chat as it happens, a draft line of its own, the panes that ask
// answered there or here (the first answer wins), and the shopping list. The server (WebUi/WebUiHost) only turns HTTP into calls on
// the session (WebUi/WebUiSession), which outlives its restarts; this part feeds the session and takes from it on the screen's own
// tasks — a message from the page is sent from the idle line, a notice printed there or by the turn's act drain, never from a
// Kestrel thread.

internal sealed partial class ChatScreen
{
    private const string WebUiCategory = "WebUi";

    /// <summary>The app's side of the Web UI: the feed, the asks, the page's messages and notices waiting.</summary>
    private readonly WebUiSession _webUi;

    /// <summary>Starts, stops and restarts the server as the settings say.</summary>
    private readonly WebUiController _webUiController;

    /// <summary>The token as the profile stores it (encrypted) and as the server compares it; read again only when the stored value changes.</summary>
    private string? _webUiTokenStored;
    private string _webUiTokenPlain = "";

    /// <summary>The state last handed to the controller: the idle line's backstop asks again only when the settings say otherwise.</summary>
    private WebUiWanted? _webUiAsked;

    /// <summary>
    /// The page's Stop asked for the turn's end (2026-10-14): before the model's first event the turn ends withdrawn, as ESC's would
    /// (<see cref="TurnEndNotice"/> reads it as <see cref="Interrupt.Cancel"/> at its call); cleared at each turn's start and end.
    /// </summary>
    private volatile bool _webStopRequested;

    /// <summary>The line sent last came from the page (2026-10-14): withdrawn, it goes back to the page's draft, never the PC's row.</summary>
    private bool _sentFromWeb;

    /// <summary>Builds the session and the controller, and wires the feed (from the constructor, before the first draw).</summary>
    private (WebUiSession Session, WebUiController Controller) CreateWebUi(Func<IWebUiBackend, IWebUiHost>? webUiHost)
    {
        var session = new WebUiSession(WebStateNow, WebStop, () => _shopping, HomeShoppingChanged, _time)
        {
            Nudge = WebNudge,
            CommandSettings = WebCommandSettingsNow,
            HeldForPc = HeldWebLines,
            // What the page did, on the hint row for a moment (2026-10-15): handed over on a pool thread, never a request's.
            FlashOnPc = text => _pane.Flash(text),
            // The page's status row (2026-10-15): the PC's spinner and usage, read without waiting on the pane's lock.
            StatusNow = WebStatusNow,
            UsageLines = () => UsageText.Lines(_session.Usage, _session.ContextLength),
            // The Home tab (2026-10-15): its side, and its lease's end checked on the session's 1-second tick.
            Home = new HomeSide(this),
            Tick = HomeWebTick,
            // The pages wear the profile's theme (2026-10-15), per request.
            ProfileTheme = () => _effective().WebUiProfileTheme,
            // Each tab only while its controls are on (later on 2026-10-15), per request.
            TabsNow = WebTabsNow,
            // The PC's picture strip in the page's drawer (later on 2026-10-15).
            StripKey = WebStripKey,
            StripView = WebStripNow,
            StripHolds = _pictureStrip.Holds,
            StripSelect = WebStripSelect,
        };
        return (session, new WebUiController(webUiHost, session));
    }

    /// <summary>The transcript recorded for the pages from the start: the renderer's lines and the sent lines.</summary>
    private void WireWebUiFeed()
    {
        _transcript.Feed = _webUi.Feed;
        // The page folds a tool run as the pane does (2026-10-15): Tool collapse count, read as each run opens.
        _webUi.Feed.ToolKeep = () => _effective().ToolCollapseCount;
        _transcript.WebPicture = WebPictureOf;
        // The PC's spinner on the page's status row (2026-10-15): every start, rename and end, sent at once (throttled).
        _pane.BusyChanged += _webUi.StatusNudge;
        _webUi.PictureSource = WebPictureSourceOf;
        _webUi.Feed.PictureReleased += ForgetWebPicture;
        // The strip's every change to the page's drawer (later on 2026-10-15), throttled and deduped by the session.
        _pictureStrip.Changed += _webUi.StripNudge;
        _pane.Committed = text => _webUi.Feed.Add(FeedKind.User, text);
    }

    /// <summary>
    /// A pane opening (later on 2026-10-14): one a command from the page opened with no ask on the page leaves the page waiting on the PC — the
    /// safety net <see cref="WaitingAtPc"/> names it on the page; the log says so, and the tests fail on it (<see cref="WebPaneWithoutAskForTests"/>).
    /// </summary>
    private void PaneOpening(string title)
    {
        if (FromWebNow && _webUi.Asks.OpenAsks().Count == 0)
        {
            DiagnosticLog.Debug(WebUiCategory, "Web UI: a command from the page opened \"" + title + "\" at the PC with nothing on the page.");
            WebPaneWithoutAskForTests?.Invoke(title);
        }
    }

    /// <summary>For the tests: told a pane's title when a command from the page opens one with no ask on the page.</summary>
    internal Action<string>? WebPaneWithoutAskForTests { get; set; }

    // ── The page's pictures (later on 2026-10-14) ───────────────────────────

    /// <summary>A registered picture as the feed's entry carries it: its addresses, its name and size, and whether its file is over the server's decode.</summary>
    private WebPicture? WebPictureOf(int id)
    {
        PictureSource? source;
        lock (_pictures)
        {
            source = id >= 0 && id < _pictures.Count ? _pictures[id] : null;
        }

        if (source is null)
        {
            return null;
        }

        // An <img> cannot read a status code, so a file over the thumbnail's decode is marked when the entry is made.
        bool tooLarge = source.Bytes is null && source.FullPath is { } path && FileLength(path) > PictureServer.MaxDecodeBytes;
        return _webUi.Describe(id, source.Name, source.Width, source.Height, tooLarge);
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>A registered picture for the page's server (a Kestrel thread; the session asks only for an id the feed holds).</summary>
    private WebPictureSource? WebPictureSourceOf(int id)
    {
        lock (_pictures)
        {
            return id >= 0 && id < _pictures.Count && _pictures[id] is var source
                ? new WebPictureSource(source.Name, source.FullPath, source.Bytes, source.MediaType)
                : null;
        }
    }

    /// <summary>
    /// The page's transcript let go of picture <paramref name="id"/> (a trim, a new conversation, a rewind): a picture only the page had keeps no
    /// bytes after it, so the registered list stays small however many rewinds replay a picture-heavy session. One the PC drew keeps them.
    /// </summary>
    private void ForgetWebPicture(int id)
    {
        lock (_pictures)
        {
            if (id >= 0 && id < _pictures.Count && _pictures[id] is { WebOnly: true, Bytes: not null } source)
            {
                _pictures[id] = source with { Bytes = null };
            }
        }
    }

    // ── The page's picture strip (later on 2026-10-15) ──────────────────────

    /// <summary>Each strip picture's addresses, built once: an id's describe never changes, so a highlight move re-sends with no file read.</summary>
    private readonly ConcurrentDictionary<int, WebPicture> _stripWebPictures = new();

    private int _webStripPick = -1;
    private int _webStripPicking;

    /// <summary>The strip's key: the setting, its version and whether it holds any, read with no file access (the session's tick reads it each second).</summary>
    private string WebStripKey() =>
        (_effective().ComfyPictureStrip ? "on" : "off") + "|" + _pictureStrip.Version.ToString(CultureInfo.InvariantCulture) + "|" + (_pictureStrip.Count > 0 ? "some" : "none");

    /// <summary>
    /// The strip as the page's drawer shows it (any thread): its pictures newest first and the PC's highlight, or null while <i>ComfyUI picture strip</i>
    /// is off or it holds none. The PC's × is the PC's own: the page's drawer follows the setting alone.
    /// </summary>
    private WebStrip? WebStripNow()
    {
        var (ids, selected) = _pictureStrip.Snapshot();
        if (ids.Count == 0)
        {
            _stripWebPictures.Clear();
            return null;
        }

        if (!_effective().ComfyPictureStrip)
        {
            return null;
        }

        var pictures = new List<WebPicture>(ids.Count);
        foreach (int id in ids)
        {
            if (_stripWebPictures.TryGetValue(id, out var known))
            {
                pictures.Add(known);
            }
            else if (WebPictureOf(id) is { } made)
            {
                _stripWebPictures[id] = made;
                pictures.Add(made);
            }
        }

        if (_stripWebPictures.Count > ids.Count * 2)
        {
            var held = ids.ToHashSet();
            foreach (int gone in _stripWebPictures.Keys.Where(k => !held.Contains(k)).ToList())
            {
                _stripWebPictures.TryRemove(gone, out _);
            }
        }

        return pictures.Count == 0 ? null : new WebStrip(pictures, selected);
    }

    /// <summary>
    /// A strip picture picked on the page (a Kestrel thread): checked here, then handed to a pool thread, the latest pick winning, so a request never
    /// waits on the pane or a viewer. False while <i>ComfyUI picture strip</i> is off or the strip no longer holds it.
    /// </summary>
    private bool WebStripSelect(int id)
    {
        if (!_effective().ComfyPictureStrip || !_pictureStrip.Holds(id))
        {
            return false;
        }

        Volatile.Write(ref _webStripPick, id);
        if (Interlocked.Exchange(ref _webStripPicking, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => DrainWebStripPicks());
        }

        return true;
    }

    private void DrainWebStripPicks()
    {
        while (true)
        {
            int id = Interlocked.Exchange(ref _webStripPick, -1);
            if (id >= 0)
            {
                try
                {
                    SelectStripFromWeb(id);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    DiagnosticLog.Warn(WebUiCategory, "Web UI: the strip's pick did not reach the PC: " + e.Message);
                }
            }

            Volatile.Write(ref _webStripPicking, 0);
            if (Volatile.Read(ref _webStripPick) < 0 || Interlocked.Exchange(ref _webStripPicking, 1) != 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The page's pick on the PC (a pool thread): the tile highlighted as a click on the strip highlights it, and an open viewer and thumbnail
    /// browser moved to it — always, unlike the PC's own click, which moves nothing on the tile already highlighted: a viewer opened since follows too.
    /// </summary>
    private void SelectStripFromWeb(int id)
    {
        if (_pictureStrip.Highlight(entry => entry == id))
        {
            _pane.RedrawStrip();
        }

        FollowInViewer();
    }

    /// <summary>For the tests: the bytes picture <paramref name="id"/> keeps, or null.</summary>
    internal byte[]? PictureBytesForTests(int id)
    {
        lock (_pictures)
        {
            return id >= 0 && id < _pictures.Count ? _pictures[id].Bytes : null;
        }
    }

    /// <summary>A message or a notice waits: the idle read ends, and a turn's (or a botchat's) wait wakes to drain the notices.</summary>
    private void WebNudge()
    {
        SignalAlert();
        Volatile.Read(ref _actSignal).TrySetResult();
    }

    // ── The server's life ─────────────────────────────────────────────────

    /// <summary>
    /// The server as the settings say, now (the screen's start, a settings change, a profile switch, <c>/webui</c>) or at the idle line's
    /// backstop: started, stopped, restarted or left. A token is minted here, on the screen's task, when the profile has none it can
    /// read. The start itself runs behind the line (it is not awaited), its notices queued for the line.
    /// </summary>
    private void ReconcileWebUi()
    {
        if (!_webUiController.Available)
        {
            return;
        }

        var wanted = WebUiWantedNow();
        if (wanted == _webUiAsked)
        {
            return;
        }

        _webUiAsked = wanted;
        _ = ReconcileWebUiQuietlyAsync(wanted);
    }

    private async Task ReconcileWebUiQuietlyAsync(WebUiWanted wanted)
    {
        try
        {
            await _webUiController.ReconcileAsync(wanted).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Error(WebUiCategory, "Web UI: the server could not be started or stopped: " + e.Message, e);
            _webUi.Notice("Web UI: " + e.Message, error: true);
        }
    }

    /// <summary>What the server should be doing for the settings in force.</summary>
    private WebUiWanted WebUiWantedNow()
    {
        var effective = _effective();
        if (!effective.WebUi)
        {
            return WebUiWanted.Off;
        }

        int port = Math.Clamp(effective.WebUiPort, AppSettingsData.MinWebUiPort, AppSettingsData.MaxWebUiPort);
        return new WebUiWanted(true, port, WebUiReach.Resolve(effective), WebUiTokenNow(mint: true));
    }

    /// <summary>
    /// The profile's sign-in token, plain: decrypted once per stored value; with <paramref name="mint"/>, a new one made and saved when there
    /// is none this machine can read (a fresh profile, or a profile.json copied from another machine — then the notice says the browsers
    /// sign in again). Never empty when minted.
    /// </summary>
    private string WebUiTokenNow(bool mint)
    {
        string stored = _settings.Current.WebUiToken ?? "";
        if (stored != _webUiTokenStored)
        {
            _webUiTokenStored = stored;
            _webUiTokenPlain = SettingsSecrets.Reveal(stored) ?? "";
        }

        if (!mint || WebUiAuth.IsWellFormed(_webUiTokenPlain))
        {
            return _webUiTokenPlain;
        }

        return MintWebUiToken(announce: stored.Trim().Length > 0);
    }

    /// <summary>A new token, saved encrypted; every browser signed in with the old one is signed out.</summary>
    private string MintWebUiToken(bool announce)
    {
        string fresh = WebUiAuth.NewToken();
        string saved = SettingsSecrets.Protect(fresh, out string? error);
        if (error is not null)
        {
            DiagnosticLog.Warn(WebUiCategory, "Web UI: the sign-in token stays unencrypted: " + error + ".");
        }

        _settings.Update(d => d.WebUiToken = saved);
        _webUiTokenStored = _settings.Current.WebUiToken;
        _webUiTokenPlain = fresh;
        if (announce)
        {
            _transcript.Notice(WebUiText.TokenMintedNotice);
        }

        return fresh;
    }

    /// <summary>The queued notices, on the screen's task: the idle line's top and the turn's act drain.</summary>
    private void PrintWebNotices()
    {
        foreach (var (text, error) in _webUi.TakeNotices())
        {
            if (error)
            {
                _transcript.Error(text);
            }
            else
            {
                _transcript.Notice(text);
            }
        }
    }

    // ── The page's state and Stop ─────────────────────────────────────────

    /// <summary>The page's header and composer, read on the sampler's timer thread (reads only).</summary>
    private WebState WebStateNow()
    {
        var effective = _effective();
        var (working, loading) = ScopesNow();
        return new WebState(
            _settings.ProfileName,
            effective.ModelAvatar ?? "",
            effective.UserAvatar ?? "",
            ModelLabel(_session.Endpoint?.ModelId),
            working,
            loading,
            _plan.Active,
            _sessionTitle?.Title ?? "",
            _webUi.Waiting,
            WaitingAtPc(working),
            WebUiText.Version,
            WebUiAssets.Current,
            // The theme's name (2026-10-15): a /theme at the PC re-links the page's sheet; empty while the pages keep their own look.
            effective.WebUiProfileTheme ? Theme.Current.Name : "",
            // The tabs on (later on 2026-10-15): the page hides the others' links, and leaves a tab that went off.
            effective.WebUiChatControls,
            effective.WebUiHomeControls,
            effective.WebUiShoppingControls);
    }

    /// <summary>
    /// The Web UI's tabs on now (later on 2026-10-15, the user's call: a tab exists only while its controls are on): <i>Web UI chat controls</i>,
    /// <i>home controls</i>, <i>shopping controls</i>. Any thread.
    /// </summary>
    private WebTabs WebTabsNow()
    {
        var effective = _effective();
        return new WebTabs(effective.WebUiChatControls, effective.WebUiHomeControls, effective.WebUiShoppingControls);
    }

    /// <summary>
    /// The page's status row (2026-10-15), read on a timer or a request's thread (reads only, no lock of the pane's): the PC's spinner label and its
    /// clocks (the step's only while the PC shows it), and the usage the PC's Status bar shows — the busy row's figures while a spinner runs (none
    /// during a /botchat turn), the idle row's otherwise, whatever the Status bar's Usage item says. The key leaves out the running clocks.
    /// </summary>
    private (WebStatus Status, string Key) WebStatusNow()
    {
        var busy = _pane.BusyNow();
        bool step = busy is not null && _turnRunning && _toolStage;
        var parts = busy is null
            ? UsageText.HintParts(_session.Usage, _session.ContextLength)
            : _botTurnRunning ? null
            : UsageText.BusyHintParts(_session.Usage, _session.ContextLength, MidTurnUsageMode.TryParse(_effective().LlmMidTurnUsage, out var mode) ? mode : MidTurnUsage.LastKnown, _session.Meter.Read());
        var status = new WebStatus(
            busy?.Label,
            busy is { } running ? (long)running.Elapsed.TotalMilliseconds : null,
            step ? (long)busy!.Value.Step.TotalMilliseconds : null,
            parts is null ? null : new WebUsage(parts.Tokens, parts.Percent, parts.Speed));
        string key = (busy is { } r ? r.Label + "\n" + r.Since.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + (step ? r.StageSince : 0).ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
            + "\n" + parts?.Joined;
        return (status, key);
    }

    /// <summary>
    /// What the PC waits on that the page cannot answer (2026-10-14): the open pane's title while a turn runs or a message from the page
    /// waits, and no ask is open on the page (an ask open there is the page's to answer).
    /// </summary>
    private string? WaitingAtPc(bool working)
    {
        if (!(working || _webUi.WaitingCount > 0 || _webDispatching) || _webUi.Asks.OpenAsks().Count > 0)
        {
            return null;
        }

        return _menuPane.OpenTitle is { Length: > 0 } title ? title : null;
    }

    /// <summary>
    /// The page's Stop (a Kestrel thread): speech stopped and the model's work cancelled — a reply, a compact, a botchat, an advisor,
    /// <c>/imagine</c>, <c>/test</c>, <c>/loop</c>'s wait — never the user's own editor and never a model's load (Ctrl+C's at the PC).
    /// Before the reply's first event it withdraws the message, as ESC does.
    /// </summary>
    private WebReply WebStop(IPAddress? remote)
    {
        var (working, loading) = ScopesNow();
        if (!working)
        {
            return new WebReply(WebOutcome.Conflict, loading ? WebUiText.LoadingNoStop : WebUiText.NothingToStop);
        }

        _webStopRequested = true;
        DiagnosticLog.Info(WebUiCategory, "Web UI: Stop from " + (remote?.ToString() ?? "an unknown address") + ".");
        _ = Task.Run(() =>
        {
            _speech.Stop();
            CancelScopes(all: false);
        });
        return WebReply.Ok;
    }

    // ── The page's messages ───────────────────────────────────────────────

    /// <summary>The next message from the page as the idle line sends it: its text alone, never through the PC's paste store.</summary>
    private SubmittedLine? TakeWebLine()
    {
        if (_queue.Held || !_webUi.TryTakeLine(out var line))
        {
            return null;
        }

        string label = line.Text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        return new SubmittedLine(line.Text, line.Text.TrimEnd(), [], label) { FromWeb = true, AsText = line.AsText, Remote = line.Remote };
    }

    /// <summary>A message from the page, withdrawn before the model answered: back to the page's draft.</summary>
    private void WithdrawWebLine(string text) => _webUi.Withdrawn(text);

    // ── /webui ─────────────────────────────────────────────────────────────

    /// <summary>The words <c>/webui</c> takes, with their notes for the completion list.</summary>
    public static readonly IReadOnlyList<CompletionItem> WebUiWords =
    [
        new("on", "start the Web UI for this profile"),
        new("off", "stop it"),
        new("copy", "copy the sign-in link to the clipboard"),
        new("reset", "a new sign-in token: every browser signs in again"),
    ];

    /// <summary>
    /// <c>/webui [on|off|copy|reset]</c> (2026-10-14): bare, the pane with the state and the sign-in links (an info pane, so the token stays
    /// out of the transcript; printed as notices where there is no pane); <c>on</c>/<c>off</c> the switch, reconciled at once; <c>copy</c>
    /// the first link to the clipboard; <c>reset</c> a new token after a yes.
    /// </summary>
    private async Task WebUiCommandAsync(string args, CancellationToken cancellationToken)
    {
        if (!_webUiController.Available)
        {
            _transcript.Error(WebUiText.NotAvailableError);
            return;
        }

        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                await ShowWebUiAsync(cancellationToken).ConfigureAwait(false);
                return;
            case "on":
            case "off":
                bool on = args.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                _settings.Update(d => d.WebUi = on);
                _transcript.Notice(SettingsMenu.SavedNotice(SettingsField.WebUi, _settings.Current, _settings.ProfileDirectory));
                ReconcileWebUi();
                return;
            case "copy":
                if (_webUiController.Links(WebUiTokenNow(mint: false)) is not [var link, ..])
                {
                    _transcript.Error(WebUiText.NoLinkError);
                    return;
                }

                _transcript.Notice(_copy(link.Url) ? WebUiText.CopiedNotice : WebUiText.CopyFailedError);
                return;
            case "reset":
                if (await ResetWebUiTokenAsync(cancellationToken).ConfigureAwait(false))
                {
                    ReconcileWebUi();
                }

                return;
            default:
                _transcript.Error(WebUiText.UsageError);
                return;
        }
    }

    /// <summary>
    /// <c>/webui reset</c> and the pane's reset button (later on 2026-10-14): after a yes, a new token and its notice (true); on a no, kept (false).
    /// The caller reconciles, so the server signs in with the new one.
    /// </summary>
    private async Task<bool> ResetWebUiTokenAsync(CancellationToken cancellationToken)
    {
        if (!await ConfirmAsync(WebUiText.ResetQuestion, cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(KeptNotice);
            return false;
        }

        MintWebUiToken(announce: false);
        _transcript.Notice(WebUiText.ResetNotice);
        return true;
    }

    /// <summary>
    /// <c>/webui</c>'s pane: on or off, the port, the reach and a tab per address with its QR code and link (the token in them, never in the
    /// transcript but without a pane). Three buttons lead the strip (2026-10-14, the user's ask, <see cref="InfoAction"/>): <c>enabled</c>/<c>disabled</c>
    /// switches <i>Web UI</i>, <c>lan</c>/<c>local</c> <i>Web UI reach</i>, and <c>reset</c> makes a new token after a yes (<c>/webui reset</c>);
    /// pressed, the setting is saved, the server starts, stops or restarts on it, and the pane opens again on what changed.
    /// </summary>
    private async Task ShowWebUiAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var effective = _effective();
            var audience = WebUiReach.Resolve(effective);
            var links = _webUiController.Links(WebUiTokenNow(mint: false));
            var lines = WebUiText.PaneLines(effective.WebUi, _webUiController.Serving, _webUiController.Serving ? _webUiController.Port : effective.WebUiPort, audience, links);
            if (!_pane.Enabled)
            {
                foreach (string line in lines.Where(l => l.Length > 0))
                {
                    _transcript.Notice(line);
                }

                return;
            }

            // The pane's buttons ahead of the address tabs (2026-10-14, the user's ask): 📱 Web UI │ enabled │ lan │ reset │ 192.168.1.217 · …
            InfoAction[] buttons =
            [
                new(WebUiText.EnabledButton(effective.WebUi), WebUiText.EnabledKey, WebUiText.EnabledWord) { Lit = effective.WebUi },
                new(WebUiText.ReachButton(audience), WebUiText.ReachKey, WebUiText.ReachWord),
                new(WebUiText.ResetButton, WebUiText.ResetKey, WebUiText.ResetWord),
            ];
            IReadOnlyList<InfoTab> tabs;
            if (links.Count == 0)
            {
                string text = string.Join("\n", lines);
                tabs = [new InfoTab(WebUiText.PaneTitle, () => new Spectre.Console.Text(text))];
            }
            else
            {
                // One tab per address (2026-10-14, the user's ask): its QR code for the phone's camera, then the link itself; Tailscale's
                // headed by its note (later that day, the user's ask).
                string status = string.Join("\n", WebUiText.StatusLines(effective.WebUi, true, _webUiController.Port, audience));
                tabs = [.. links.Select(link => new InfoTab(WebUiText.LinkTabTitle(link.Url), () => new Spectre.Console.Rows(
                    new Spectre.Console.Text(WebUiText.LinkTabText(status, link.Tailscale)),
                    new QrView(QrCode.Encode(link.Url)),
                    new Spectre.Console.Text("\n" + link.Url + "\n\n" + WebUiText.TokenWarning))))];
            }

            await _info.ShowAsync(WebUiText.PaneTitle, tabs, 0, cancellationToken, buttons).ConfigureAwait(false);
            switch (_info.Pressed?.Key)
            {
                case WebUiText.EnabledKey:
                    bool on = !effective.WebUi;
                    _settings.Update(d => d.WebUi = on);
                    _transcript.Notice(SettingsMenu.SavedNotice(SettingsField.WebUi, _settings.Current, _settings.ProfileDirectory));
                    break;
                case WebUiText.ReachKey:
                    string next = WebUiReach.Name(audience == WebUiAudience.Local ? WebUiAudience.LocalAreaNetwork : WebUiAudience.Local);
                    _settings.Update(d => d.WebUiReach = next);
                    _transcript.Notice(SettingsMenu.SavedNotice(SettingsField.WebUiReach, _settings.Current, _settings.ProfileDirectory));
                    break;
                case WebUiText.ResetKey:
                    // A no keeps the token and the server as they are; the pane opens again either way.
                    if (!await ResetWebUiTokenAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    break;
                default:
                    return;
            }

            // The start, stop or restart before the pane opens again, so its tabs are the addresses served now.
            var wanted = WebUiWantedNow();
            _webUiAsked = wanted;
            await ReconcileWebUiQuietlyAsync(wanted).ConfigureAwait(false);
            PrintWebNotices();
        }
    }

    /// <summary>For the tests: the session the fake server drives.</summary>
    internal WebUiSession WebUiSessionForTests => _webUi;

    /// <summary>For the tests: the PC's ↑ history, which the page's lines never join.</summary>
    internal IReadOnlyList<string> InputHistoryForTests => _input.History;
}
