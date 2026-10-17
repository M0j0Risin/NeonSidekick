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
            UsageNow = () => WebUsageView.Of(_session.Usage, _session.ContextLength),
            // The Home tab (2026-10-15): its side, and its lease's end checked on the session's 1-second tick.
            Home = new HomeSide(this),
            // The YouTube tab's player (2026-10-16), and its chores on the same tick: a hold's expiry, a video stopped once the tab is off.
            Video = _webVideo,
            Tick = () => { HomeWebTick(); WebVideoTick(); },
            // The pages wear the profile's theme (2026-10-15), per request.
            ProfileTheme = () => _effective().WebUiProfileTheme,
            // Each tab only while its controls are on (later on 2026-10-15), per request.
            TabsNow = WebTabsNow,
            // The PC's picture strip in the page's drawer (later on 2026-10-15).
            StripKey = WebStripKey,
            StripView = WebStripNow,
            StripHolds = _pictureStrip.Holds,
            StripSelect = WebStripSelect,
            // The picture the PC's own hand moved to (later on 2026-10-15): the page's open view follows it.
            PointNow = PcPointNow,
            // The overlay's 🗑️ (later on 2026-10-15): the screen's own path for the id, File deletes to Recycle bin read now.
            PictureDelete = WebDeletePicture,
            // The page's saved prompts sheet (later on 2026-10-15): the loaded profile's, read at each opening; with their ids since 2026-10-16.
            PromptsNow = () => SavedPromptsNow().List(),
            // The sheet's ⇪ and 🗑️, its 💾 and 🕘 tabs, and ↑/↓ on the page (2026-10-16): ChatScreen.WebLists.cs.
            PromptSave = WebSavePrompt,
            PromptDelete = WebDeletePrompt,
            MemoriesNow = WebMemoriesNow,
            MemorySave = WebSaveMemory,
            MemoryDelete = WebDeleteMemory,
            HistoryNow = WebHistoryNow,
            HistoryClear = WebClearHistory,
            // The status row's 🧠 (2026-10-17): the reflection canceled from the page, as Ctrl+L does.
            LearnCancel = WebCancelLearn,
            // The queue held after a cancel (2026-10-17): the page's lines from before it wait for the hold to lift, its Resume lifts it.
            CancelHoldNow = () => _queue.Held,
            QueueResume = WebResumeQueue,
            // The YouTube tab's saved list (later on 2026-10-16): ChatScreen.WebSaved.cs; none without a web player.
            SavedVideosNow = () => _webVideo is null ? null : WebSavedNow(),
            SavedPlay = WebPlaySaved,
            SavedRemove = WebRemoveSaved,
            SavedClear = WebClearSaved,
            SavedMove = WebMoveSaved,
            SavedSave = WebSaveCurrent,
            // A bot check card's picture of the Browser window and its input (2026-10-17): ChatScreen.BotCheckWeb.cs.
            BotCheckFrame = WebBotCheckFrameAsync,
            BotCheckInput = WebBotCheckInputAsync,
            // A line the page sent joins the message history as the page sends it, as Enter does a typed one (the history's lock allows it).
            Accepted = text => _input.Remember(text),
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
    /// <remarks>
    /// Also while a turn the page started runs (2026-10-17, the user's question: a turn's panes run on the watcher, which reads as the PC's, so
    /// the camera's PC-only shutter went unseen): a page line sent (<see cref="_sentFromWeb"/>) or a page command dispatching
    /// (<see cref="_webDispatching"/>). A pane the user opens at the PC under such a turn trips it too (a Debug line; only tests listen). Its blind
    /// spot, kept: "no ask open" is any ask, so a PC-only pane opened while an unrelated ask waits on the page goes unrecorded.
    /// </remarks>
    private void PaneOpening(string title)
    {
        bool fromWeb = FromWebNow || (_turnRunning && (_sentFromWeb || _webDispatching));
        if (fromWeb && _webUi.Asks.OpenAsks().Count == 0)
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
        // Its file's key and whether the page may delete it (later on 2026-10-15): a picture with bytes only has neither.
        string? key = source.FullPath is { } full ? WebPictureKey.Of(full) : null;
        return _webUi.Describe(id, source.Name, source.Width, source.Height, tooLarge, key, deletable: source.FullPath is not null && !source.WebOnly);
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
            return id >= 0 && id < _pictures.Count && !_webDeleted.Contains(id) && _pictures[id] is var source
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

    // ── The page's 🗑️ (later on 2026-10-15, the user's ask) ──────────────────
    // A picture deleted from the page's full view, as the PC's viewer deletes one: the id's own registered file (never a path from the page), to
    // the Recycle bin under File deletes to Recycle bin, else directly. The deleted file's every id stops being served (the server checks a file
    // before its cache, but a picture registered with its bytes too would still draw), the strip drops it, the PC's hint row flashes, and an
    // audit line names the picture alone. The viewer and the thumbnail browser see it leave through their watchers, which make no point.

    /// <summary>How long a page's delete waits for the PC: the bin's call is quick, its cap is not.</summary>
    internal static TimeSpan WebDeleteWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The ids whose file the page deleted (under <c>_pictures</c>' lock): no longer served.</summary>
    private readonly HashSet<int> _webDeleted = [];

    private WebReply WebDeletePicture(int id, IPAddress? remote)
    {
        string? path;
        string name;
        lock (_pictures)
        {
            if (id < 0 || id >= _pictures.Count || _webDeleted.Contains(id))
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.PictureNotHere);
            }

            var source = _pictures[id];
            path = source.WebOnly ? null : source.FullPath;
            name = source.Name;
        }

        if (path is null)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.PictureHasNoFile);
        }

        if (!File.Exists(path))
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.PictureNotHere);
        }

        var bin = _effective().FileDeletesToRecycleBin ? _recycleBin : null;
        var work = Task.Run(() => Files.FileDeletes.Remove(path, isDirectory: false, isLink: false, bin));
        try
        {
            if (!work.Wait(WebDeleteWait))
            {
                return new WebReply(WebOutcome.Refused, WebUiText.StillDeleting(name));
            }
        }
        catch (AggregateException e) when (e.InnerException is IOException or UnauthorizedAccessException)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.PictureDeleteFailed(name, e.InnerException.Message));
        }

        var way = work.Result;
        ForgetDeletedFile(path);
        _webUi.FlashPc(WebUiText.PictureDeletedFlash(name, way));
        DiagnosticLog.Info(WebUiCategory, WebUiText.PictureDeletedLogLine(name, way, remote));
        return new WebReply(WebOutcome.Ok, WebUiText.PictureDeleted(name, way));
    }

    /// <summary>Every id registered for <paramref name="path"/> no longer served, and the strip without them.</summary>
    private void ForgetDeletedFile(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ids = new HashSet<int>();
        lock (_pictures)
        {
            for (int i = 0; i < _pictures.Count; i++)
            {
                if (_pictures[i].FullPath is { } full && string.Equals(full, path, comparison))
                {
                    ids.Add(i);
                    _webDeleted.Add(i);
                }
            }
        }

        foreach (int id in ids)
        {
            _stripWebPictures.TryRemove(id, out _);
        }

        if (_pictureStrip.Remove(ids.Contains))
        {
            _pane.RedrawStrip();
        }
    }

    // ── The PC's point (later on 2026-10-15, the user's ask) ──────────────────
    // The page's selection follows the PC's as the PC's follows the page's: a picture the PC's own hand moves to (a click or an arrow on the strip,
    // the viewer's keys, a pick in the thumbnail browser, the viewer opened on a picture) is a point; the page's open full view follows it by the
    // file's key. The page's own pick (SelectStripFromWeb) is no point, so the page never chases its own echo. The sequence lives as long as the
    // screen, never reset: a page holding a higher one would otherwise ignore every point until the counter passed it.

    private readonly object _pcPointGate = new();
    private WebPoint? _pcPoint;
    private long _pcPointSeq;

    /// <summary>The PC pointed at <paramref name="fullPath"/> (any thread): the point recorded and the pages nudged. Null points at nothing.</summary>
    private void PointAt(string? fullPath)
    {
        if (fullPath is null)
        {
            return;
        }

        string key;
        try
        {
            key = WebPictureKey.Of(fullPath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        lock (_pcPointGate)
        {
            _pcPoint = new WebPoint(key, ++_pcPointSeq);
        }

        _webUi.PointNudge();
    }

    /// <summary>The strip picture <paramref name="id"/>'s file, pointed at; nothing for an id with none.</summary>
    private void PointAtPicture(int id)
    {
        string? path;
        lock (_pictures)
        {
            path = id >= 0 && id < _pictures.Count ? _pictures[id].FullPath : null;
        }

        PointAt(path);
    }

    private WebPoint? PcPointNow()
    {
        lock (_pcPointGate)
        {
            return _pcPoint;
        }
    }

    /// <summary>For the tests: the PC's last point.</summary>
    internal WebPoint? PcPointForTests => PcPointNow();

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
        _webUiReconciling = ReconcileWebUiQuietlyAsync(wanted);
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
            // Queued for the screen's task (later on 2026-10-15): the /webui pane mints here from the watcher under a reply too.
            _webUi.Notice(WebUiText.TokenMintedNotice);
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
            effective.WebUiShoppingControls,
            // The YouTube tab (2026-10-16), and the banner's words while a video waits for it.
            YouTubeTab: WebTabsNow().YouTube,
            Watch: _webVideo?.Waiting == true ? WebUiText.WatchBanner : null,
            // The held queue's banner (2026-10-17): while the hold stands and something waits behind it, the PC's queue or the page's held lines.
            QueueHeld: _queue.Held && (_queue.Count > 0 || _webUi.CancelHeldCount > 0) ? WebUiText.QueueHeld : null);
    }

    /// <summary>
    /// The Web UI's tabs on now (later on 2026-10-15, the user's call: a tab exists only while its controls are on): <i>Web UI chat controls</i>,
    /// <i>home controls</i>, <i>shopping controls</i>, and (2026-10-16) <i>YouTube controls</i> where there is a web player, whatever
    /// <i>YouTube open links in</i> says. Any thread.
    /// </summary>
    private WebTabs WebTabsNow()
    {
        var effective = _effective();
        return new WebTabs(effective.WebUiChatControls, effective.WebUiHomeControls, effective.WebUiShoppingControls,
            YouTube: _webVideo is not null && effective.WebUiYouTubeControls);
    }

    /// <summary>
    /// The YouTube tab's chores on the session's 1-second tick (2026-10-16): a request held too long dropped, and the tab's video stopped
    /// (its place saved) once the tab is off (<i>Web UI YouTube controls</i> off, or the Web UI not serving). Not on <i>YouTube open links in</i>
    /// alone: the tab plays its saved list under <c>youtube-window</c> too, and a play in the video window closes the tab's through the router's
    /// switch. A playing PC window is left alone the other way round, until the next play.
    /// </summary>
    private void WebVideoTick()
    {
        if (_webVideo is not { } web)
        {
            return;
        }

        web.Tick();
        if (!_effective().WebUiYouTubeControls || !_webUiController.Serving)
        {
            web.Forget();
        }
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
        // The reflection's 🧠 (2026-10-17, the user's ask): the PC's strip shows it while one runs, so the page does too.
        bool learning = _session.IsLearning;
        var status = new WebStatus(
            busy?.Label,
            busy is { } running ? (long)running.Elapsed.TotalMilliseconds : null,
            step ? (long)busy!.Value.Step.TotalMilliseconds : null,
            parts is null ? null : new WebUsage(parts.Tokens, parts.Percent, parts.Speed),
            learning);
        string key = (busy is { } r ? r.Label + "\n" + r.Since.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + (step ? r.StageSince : 0).ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
            + "\n" + parts?.Joined + (learning ? "\n🧠" : "");
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

    /// <summary>
    /// The status row's 🧠, tapped twice (2026-10-17, the user's ask: a reflection could hold the server with no sign on the page): Ctrl+L's
    /// cancel (<see cref="CancelLearnNow"/>, safe on a Kestrel thread), the audit line, the PC's hint row. The canceled notice reaches the page
    /// through the transcript. Conflict when nothing runs or waits paused.
    /// </summary>
    private WebReply WebCancelLearn(IPAddress? remote)
    {
        if (!CancelLearnNow())
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.NoReflection);
        }

        DiagnosticLog.Info(WebUiCategory, WebUiText.LearnCancelledLogLine(remote));
        _webUi.FlashPc(WebUiText.LearnCancelledFlash);
        return WebReply.Ok;
    }

    /// <summary>
    /// The held queue's Resume (2026-10-17, the user's question: under Queue cancel mode hold the page could never release it): the hold lifted
    /// as a reply ending on its own lifts it, the idle line woken to send the next, the audit line, the PC's hint row; nothing in the transcript.
    /// Conflict when nothing is held. <c>MessageQueue.Held</c> is lock-guarded, so a Kestrel thread may clear it.
    /// </summary>
    private WebReply WebResumeQueue(IPAddress? remote)
    {
        if (!_queue.Held)
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.NotHeld);
        }

        _queue.Held = false;
        DiagnosticLog.Info(WebUiCategory, WebUiText.QueueResumedLogLine(remote));
        _webUi.FlashPc(WebUiText.QueueResumedFlash);
        _webUi.PublishState();
        WebNudge();
        return WebReply.Ok;
    }

    // ── The page's messages ───────────────────────────────────────────────

    /// <summary>The next message from the page as the idle line sends it: its text alone, never through the PC's paste store.</summary>
    private SubmittedLine? TakeWebLine()
    {
        // A hold after a cancel (Queue cancel mode hold) keeps the lines that waited behind the reply, never one sent since: the page's next
        // message goes as the PC's typed one does (2026-10-17, the user's question); the session sets the held ones apart.
        if (!_webUi.TryTakeLine(out var line))
        {
            return null;
        }

        return WebLine(line.Text, line.AsText, line.Remote);
    }

    /// <summary>
    /// A page's line from its text alone (2026-10-16, out of <see cref="TakeWebLine"/> for a saved prompt the page's <c>/prompts &lt;n&gt;</c>
    /// sends): never through the PC's paste store, its label one row, routed back to the page when withdrawn.
    /// </summary>
    private static SubmittedLine WebLine(string text, bool asText, System.Net.IPAddress? remote)
    {
        string label = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        return new SubmittedLine(text, text.TrimEnd(), [], label) { FromWeb = true, AsText = asText, Remote = remote };
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
        new("browser", "open the Web UI on this PC"),
    ];

    /// <summary><c>/webui browser</c>'s flag's note for the completion list.</summary>
    public const string ExternalNote = "in the default browser, whatever Web open links in says";

    /// <summary>
    /// <c>/webui [on|off|copy|reset]</c> (2026-10-14): bare, the pane with the state and the sign-in links (an info pane, so the token stays
    /// out of the transcript; printed as notices where there is no pane); <c>on</c>/<c>off</c> the switch, reconciled at once; <c>copy</c>
    /// the first link to the clipboard; <c>reset</c> a new token after a yes.
    /// </summary>
    private async Task WebUiCommandAsync(string args, CancellationToken cancellationToken, INoticeSink? flow = null)
    {
        var sink = flow ?? _transcript;
        if (!_webUiController.Available)
        {
            sink.Error(WebUiText.NotAvailableError);
            return;
        }

        if (WebUiBrowserArgs(args) is { } external)
        {
            await OpenWebUiHereAsync(WebUiText.LoopbackHost, external, sink).ConfigureAwait(false);
            return;
        }

        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                await ShowWebUiAsync(_transcript, onWatcher: false, cancellationToken).ConfigureAwait(false);
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

                sink.Notice(_copy(link.Url) ? WebUiText.CopiedNotice : WebUiText.CopyFailedError);
                return;
            case "reset":
                if (await ResetWebUiTokenAsync(_transcript, cancellationToken).ConfigureAwait(false))
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
    /// A double-click on <paramref name="line"/> of an address's tab: when the line is the link (or, wrapped, a piece of it), that address opened on
    /// this PC, behind the pane, which stays open.
    /// </summary>
    /// <summary>For the tests: the flow sink, a notice from any thread.</summary>
    internal INoticeSink FlowForTests => _flow;

    internal void OpenLinkLine(string url, string line, INoticeSink sink)
    {
        string piece = line.Trim();
        if (piece.Length < 8 || !url.Contains(piece, StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return;
        }

        _ = OpenWebUiQuietlyAsync(parsed.Host.Trim('[', ']'), sink);
    }

    private async Task OpenWebUiQuietlyAsync(string host, INoticeSink sink)
    {
        try
        {
            await OpenWebUiHereAsync(host, external: false, sink).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(WebUiCategory, "Web UI: could not open it on this PC: " + e.Message);
            sink.Error(e.Message);
        }
    }

    /// <summary><c>/webui browser [--external]</c>'s arguments: whether <c>--external</c> is given; null for any other form. Pure.</summary>
    public static bool? WebUiBrowserArgs(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var words = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !string.Equals(words[0], "browser", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return words.Length switch
        {
            1 => false,
            2 when string.Equals(words[1], WebUiText.ExternalFlag, StringComparison.OrdinalIgnoreCase) => true,
            _ => null,
        };
    }

    /// <summary>The server's reconcile in flight, if any: <c>/webui browser</c> just after <c>/webui on</c> waits for it.</summary>
    private Task? _webUiReconciling;

    /// <summary>How long <c>/webui browser</c> waits for a server still starting.</summary>
    internal static TimeSpan BrowserServerWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The Web UI opened on this PC at <paramref name="host"/> (later on 2026-10-15, the user's ask): <c>/webui browser</c> (127.0.0.1) and a
    /// double-click on the pane's link (its tab's host). <i>Web open links in</i> <c>browser-window</c> (and not <paramref name="external"/>) opens
    /// the app's Browser window with the usual sign-in link, or a one-time link under <i>Web browser privacy</i> <c>keep-history</c>, whose kept
    /// history would hold the token; a window that can't take it now, and <c>default-browser</c>, go to the default browser, signed in as
    /// <i>Web browser sign in mode</i> says. The notice and the log name where, never the link; the window hands none of its cookies over.
    /// </summary>
    private async Task OpenWebUiHereAsync(string host, bool external, INoticeSink sink)
    {
        var effective = _effective();
        if (!effective.WebUi)
        {
            sink.Error(WebUiText.BrowserWebUiOff);
            return;
        }

        if (!_webUiController.Serving && _webUiReconciling is { IsCompleted: false } pending)
        {
            try
            {
                await pending.WaitAsync(BrowserServerWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still starting: said below.
            }
        }

        if (!_webUiController.Serving)
        {
            sink.Error(WebUiText.BrowserNotServing(effective.WebUiPort));
            return;
        }

        bool window = !external && Web.OpenLinksIn.Resolve(effective) == Web.OpenLinksInKind.BrowserWindow && _browserWindow is { } browser && browser.Unavailable() is null;
        bool once = window
            ? Viewer.BrowserPrivacy.Resolve(effective) == Viewer.BrowserPrivacyKind.KeepHistory
            : Web.WebSignInMode.Resolve(effective) == Web.WebSignInModeKind.OneTimeLink;
        string? link = once ? _webUiController.OneTimeLink(host) : WebUiAuth.SignInLink(host, _webUiController.Port, WebUiTokenNow(mint: false));
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var url))
        {
            sink.Error(WebUiText.BrowserNotServing(_webUiController.Port));
            return;
        }

        if (window)
        {
            OpenInBrowser(url, sink: null);   // its own line would name the link; the notice below names where
        }
        else
        {
            _web.OpenUrl(link);
        }

        sink.Notice(WebUiText.BrowserOpened(window));
        DiagnosticLog.Info(WebUiCategory, WebUiText.BrowserOpenedLogLine(window, once));
    }

    /// <summary>
    /// <c>/webui reset</c> and the pane's reset button (later on 2026-10-14): after a yes, a new token and its notice (true); on a no, kept (false).
    /// The caller reconciles, so the server signs in with the new one.
    /// </summary>
    private async Task<bool> ResetWebUiTokenAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        if (!await ConfirmAsync(WebUiText.ResetQuestion, cancellationToken).ConfigureAwait(false))
        {
            sink.Notice(KeptNotice);
            return false;
        }

        MintWebUiToken(announce: false);
        sink.Notice(WebUiText.ResetNotice);
        return true;
    }

    /// <summary>
    /// <c>/webui</c>'s pane over a reply (later on 2026-10-15, the user's ask: it waited for the reply's end), on the watcher: as at the idle line,
    /// its buttons acting at once (the server restarted under the reply; the user's pick), its lines through <paramref name="sink"/> (posted to the
    /// turn task) and the server's own notices left to the turn's act drain. Without a server in this build, the idle line's error, through the sink.
    /// </summary>
    private async Task ShowWebUiMidTurnAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        if (!_webUiController.Available)
        {
            sink.Error(WebUiText.NotAvailableError);
            return;
        }

        await ShowWebUiAsync(sink, onWatcher: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/webui</c>'s pane: on or off, the port, the reach and a tab per address with its QR code and link (the token in them, never in the
    /// transcript but without a pane). Three buttons lead the strip (2026-10-14, the user's ask, <see cref="InfoAction"/>): <c>enabled</c>/<c>disabled</c>
    /// switches <i>Web UI</i>, <c>lan</c>/<c>local</c> <i>Web UI reach</i>, and <c>reset</c> makes a new token after a yes (<c>/webui reset</c>);
    /// pressed, the setting is saved, the server starts, stops or restarts on it, and the pane opens again on what changed. Its lines go through
    /// <paramref name="sink"/>; <paramref name="onWatcher"/> (over a reply, later on 2026-10-15) leaves the server's queued notices to the turn
    /// task's act drain, the transcript's one writer under a reply.
    /// </summary>
    private async Task ShowWebUiAsync(INoticeSink sink, bool onWatcher, CancellationToken cancellationToken)
    {
        int shownTab = 0;
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
                    sink.Notice(line);
                }

                return;
            }

            // The pane's buttons ahead of the address tabs (2026-10-14, the user's ask): 📱 Web UI │ enabled │ lan │ reset │ 192.168.1.217 · …
            InfoAction[] buttons =
            [
                new(WebUiText.EnabledButton(effective.WebUi), WebUiText.EnabledKey, WebUiText.EnabledWord) { Lit = effective.WebUi },
                new(WebUiText.ReachButton(audience), WebUiText.ReachKey, WebUiText.ReachWord),
                new(WebUiText.ResetButton, WebUiText.ResetKey, WebUiText.ResetWord),
                .. (links.Count == 0 ? Array.Empty<InfoAction>() : new[] { new InfoAction(WebUiText.CopyButton, WebUiText.CopyKey, WebUiText.CopyWord) }),
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
                // A double-click on the link opens it on this PC (later on 2026-10-15, the user's ask), as /webui browser does, at that tab's host.
                tabs = [.. links.Select(link => new InfoTab(WebUiText.LinkTabTitle(link.Url), () => new Spectre.Console.Rows(
                    new Spectre.Console.Text(WebUiText.LinkTabText(status, link.Tailscale)),
                    new QrView(QrCode.Encode(link.Url)),
                    new Spectre.Console.Text("\n" + link.Url + "\n\n" + WebUiText.TokenWarning + "\n" + WebUiText.LinkOpenHint)))
                {
                    LineDoubleClicked = text => OpenLinkLine(link.Url, text, sink),
                })];
            }

            await _info.ShowAsync(WebUiText.PaneTitle, tabs, shownTab, cancellationToken, buttons).ConfigureAwait(false);
            shownTab = _info.LastTab;
            switch (_info.Pressed?.Key)
            {
                case WebUiText.CopyKey:
                    // The shown tab's link (later on 2026-10-15, the user's ask), as /webui copy's; the pane opens again on the same tab.
                    if (shownTab < links.Count)
                    {
                        sink.Notice(_copy(links[shownTab].Url) ? WebUiText.CopiedNotice : WebUiText.CopyFailedError);
                    }

                    continue;
                case WebUiText.EnabledKey:
                    bool on = !effective.WebUi;
                    _settings.Update(d => d.WebUi = on);
                    sink.Notice(SettingsMenu.SavedNotice(SettingsField.WebUi, _settings.Current, _settings.ProfileDirectory));
                    break;
                case WebUiText.ReachKey:
                    string next = WebUiReach.Name(audience == WebUiAudience.Local ? WebUiAudience.LocalAreaNetwork : WebUiAudience.Local);
                    _settings.Update(d => d.WebUiReach = next);
                    sink.Notice(SettingsMenu.SavedNotice(SettingsField.WebUiReach, _settings.Current, _settings.ProfileDirectory));
                    break;
                case WebUiText.ResetKey:
                    // A no keeps the token and the server as they are; the pane opens again either way.
                    if (!await ResetWebUiTokenAsync(sink, cancellationToken).ConfigureAwait(false))
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
            if (!onWatcher)
            {
                PrintWebNotices();
            }
        }
    }

    /// <summary>For the tests: the session the fake server drives.</summary>
    internal WebUiSession WebUiSessionForTests => _webUi;

    /// <summary>For the tests: the PC's ↑ history, which the page's lines join as the page sends them (2026-10-16).</summary>
    internal IReadOnlyList<string> InputHistoryForTests => _input.History;

    /// <summary>For the tests: a line added to the ↑ history as a spoken one is (2026-10-16: a paste's token, which a test can't type).</summary>
    internal void RememberForTests(string line) => _input.Remember(line);
}
