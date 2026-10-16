using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Shopping;
using NeonSidekick.UI;

namespace NeonSidekick.WebUi;

/// <summary>
/// A message the page sent, waiting for the idle line: its text and the page's id for it; where it came from, and whether a line that reads as a
/// command goes as a message (later on 2026-10-14).
/// </summary>
public sealed record WebLine(string Text, string? Id, IPAddress? Remote = null, bool AsText = false);

/// <summary>
/// The app's side of the Web UI (2026-10-14), one per screen and outliving the server's restarts: the transcript as the pages see it
/// (<see cref="Feed"/>), the open asks (<see cref="Asks"/>), the pages' event fan-out (<see cref="Events"/>), the phone's messages waiting
/// for the idle line (<see cref="TryTakeLine"/>), the notices waiting for the screen (<see cref="TakeNotices"/>), and the state sampled
/// once a second while a page is connected. The host (<see cref="WebUiHost"/>) calls it on Kestrel threads; it never writes to the
/// transcript and never waits on the screen — a message or a notice is queued and <see cref="Nudge"/> wakes the idle line, which takes
/// them on its own task. The screen supplies what only it knows through the hooks given at construction.
/// </summary>
public sealed class WebUiSession : IWebUiBackend, IDisposable
{
    /// <summary>The most bytes one message may hold (UTF-8), under the request's own 64 KB cap.</summary>
    public const int MaxMessageBytes = 48 * 1024;

    /// <summary>The most messages waiting at once.</summary>
    public const int MaxWaiting = 20;

    /// <summary>How many recent message ids are remembered, so a phone's retry is not sent twice.</summary>
    public const int RememberedIds = 50;

    /// <summary>How often the state is sampled while a page is connected.</summary>
    public static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(1);

    private const string Category = "WebUi";

    private readonly Func<WebState> _state;
    private readonly Func<IPAddress?, WebReply> _stop;
    private readonly Func<ShoppingList?> _shopping;
    private readonly Action _shoppingChanged;
    private readonly ConcurrentQueue<WebLine> _lines = new();
    private readonly ConcurrentQueue<(string Text, bool Error)> _notices = new();
    private readonly ConcurrentQueue<string> _flashes = new();
    private int _flashing;
    private readonly LinkedList<string> _ids = new();
    private readonly object _idGate = new();
    private readonly ITimer _sampler;
    private readonly ITimer _statusTrail;
    private readonly TimeProvider _time;
    private readonly object _statusGate = new();
    private readonly WebThrottle _strip;
    private readonly WebThrottle _point;
    private string _lastStrip = "";
    private long _lastPoint;
    private string _lastState = "";
    private string _lastStatus = "";
    private long _statusAt;
    private bool _statusSent;
    private bool _statusTrailing;

    /// <summary>The fastest the status row is sent while its label changes (2026-10-15): a download's percent can change many times a second.</summary>
    public static readonly TimeSpan StatusThrottle = TimeSpan.FromMilliseconds(200);

    /// <param name="state">The state as the page's header shows it, read on a timer thread (reads only).</param>
    /// <param name="stop">The page's Stop: the screen ends what it may (its reply, not a model's load).</param>
    /// <param name="shopping">The screen's shopping list (it follows the profile), or null for none.</param>
    /// <param name="shoppingChanged">Tells the home bar's 🛒 a line changed (thread-safe).</param>
    /// <param name="time">The sampler's clock.</param>
    public WebUiSession(Func<WebState> state, Func<IPAddress?, WebReply> stop, Func<ShoppingList?> shopping, Action shoppingChanged, TimeProvider? time = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _stop = stop ?? throw new ArgumentNullException(nameof(stop));
        _shopping = shopping ?? throw new ArgumentNullException(nameof(shopping));
        _shoppingChanged = shoppingChanged ?? throw new ArgumentNullException(nameof(shoppingChanged));
        Feed.Changed += OnFeedChanged;
        Asks.Changed += PublishAsks;
        _time = time ?? TimeProvider.System;
        _sampler = _time.CreateTimer(_ => SampleState(), null, StateInterval, StateInterval);
        _statusTrail = _time.CreateTimer(_ => TrailingStatus(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _strip = new WebThrottle(_time, StatusThrottle, PublishStrip, e => DiagnosticLog.Debug(Category, "Web UI: the picture strip could not be read: " + e.Message));
        _point = new WebThrottle(_time, StatusThrottle, PublishPoint, e => DiagnosticLog.Debug(Category, "Web UI: the PC's picture could not be sent: " + e.Message));
    }

    /// <summary>The transcript as the pages see it.</summary>
    public TranscriptFeed Feed { get; } = new();

    /// <summary>The asks open on the pages.</summary>
    public WebAskBroker Asks { get; } = new();

    public WebEventHub Events { get; } = new();

    private static readonly string s_pictureRun = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public string PictureRun => s_pictureRun;

    private WebCompletionSnapshot? _completions;
    private readonly SemaphoreSlim _completing = new(1);
    private int _completionFailed;

    /// <summary>The dropdown's lists as the screen last built them (later on 2026-10-14), on its own task; null answers nothing.</summary>
    public WebCompletionSnapshot? Completions
    {
        get => Volatile.Read(ref _completions);
        set => Volatile.Write(ref _completions, value);
    }

    public WebCompletion Complete(WebCompleteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_completing.Wait(0))
        {
            return WebCompletion.Empty(request.Seq, truncated: true);   // one at a time: the page asks again as it types
        }

        try
        {
            return Completions?.Complete(request.Text ?? "", request.Cursor, request.Seq) ?? WebCompletion.Empty(request.Seq);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A list that failed is quiet (once a run in the log), and asked again next time.
            if (Interlocked.Exchange(ref _completionFailed, 1) == 0)
            {
                DiagnosticLog.Debug(Category, "Web UI: a completion list could not be read: " + e.Message);
            }

            return WebCompletion.Empty(request.Seq);
        }
        finally
        {
            _completing.Release();
        }
    }

    /// <summary>The settings a command from the page is checked against, read at each send (later on 2026-10-14); null refuses every command.</summary>
    public Func<WebCommandSettings>? CommandSettings { get; set; }

    /// <summary>The page's lines the screen holds for the idle line (a command sent under a botchat), listed as waiting on the page.</summary>
    public Func<IReadOnlyList<string>>? HeldForPc { get; set; }

    /// <summary>The screen's picture by id (under its own lock), asked only for an id the feed holds; null for none.</summary>
    public Func<int, WebPictureSource?>? PictureSource { get; set; }

    /// <summary>
    /// Picture <paramref name="id"/> while the transcript or the strip holds it (later on 2026-10-15: a strip picture whose transcript entry was
    /// trimmed or replayed away still loads in the drawer).
    /// </summary>
    public WebPictureSource? Picture(int id) => Feed.Holds(id) || (StripHolds?.Invoke(id) ?? false) ? PictureSource?.Invoke(id) : null;

    // ── The picture strip (later on 2026-10-15) ─────────────────────────────

    /// <summary>The strip's key now, cheap and with no file read (the setting, the strip's version, whether it is empty); null: no strip at all.</summary>
    public Func<string>? StripKey { get; set; }

    /// <summary>The strip as the page's drawer shows it, or null while <i>ComfyUI picture strip</i> is off or it is empty.</summary>
    public Func<WebStrip?>? StripView { get; set; }

    /// <summary>Whether the strip holds picture <paramref name="id"/> (thread-safe).</summary>
    public Func<int, bool>? StripHolds { get; set; }

    /// <summary>The PC's highlight and viewer moved to a strip picture, handed off the request's thread; false when the strip doesn't hold it or is off.</summary>
    public Func<int, bool>? StripSelect { get; set; }

    /// <summary>The screen's delete of a picture it holds (later on 2026-10-15): its own path for the id; null: no deletes.</summary>
    public Func<int, IPAddress?, WebReply>? PictureDelete { get; set; }

    public WebReply DeletePicture(WebPictureDelete request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (PictureDelete is not { } delete || !(Feed.Holds(request.Id) || (StripHolds?.Invoke(request.Id) ?? false)))
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.PictureNotHere);
        }

        return delete(request.Id, remote);
    }

    public WebReply SelectStripPicture(WebStripSelect request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StripSelect?.Invoke(request.Id) == true ? WebReply.Ok : new WebReply(WebOutcome.NotFound, WebUiText.NoSuchPicture);
    }

    /// <summary>
    /// The strip changed (its <c>Changed</c>, on the thread that changed it) or the session's tick: the strip now, or, inside <see cref="StatusThrottle"/>
    /// of the last send, once more when the window ends. Deduped on its key, so the tick costs nothing while nothing changed. Never blocks.
    /// </summary>
    public void StripNudge()
    {
        if (Events.Count == 0 || StripKey is null)
        {
            return;
        }

        _strip.Nudge();
    }

    // ── The PC's point (later on 2026-10-15) ────────────────────────────────

    /// <summary>The picture the PC's own hand moved to last, or null before any (thread-safe, reads only).</summary>
    public Func<WebPoint?>? PointNow { get; set; }

    /// <summary>
    /// The PC pointed at a picture (its strip, its viewer's keys, its thumbnail browser, the viewer opened on one; any thread): the point to every
    /// page, at most one per <see cref="StatusThrottle"/> with a trailing send, so a held arrow key's last picture lands. Never blocks.
    /// </summary>
    public void PointNudge()
    {
        if (Events.Count == 0 || PointNow is null)
        {
            return;
        }

        _point.Nudge();
    }

    /// <summary>The point now, to every page, when it is newer than the one sent last.</summary>
    public void PublishPoint()
    {
        if (PointNow?.Invoke() is not { } point)
        {
            return;
        }

        long last = Interlocked.Read(ref _lastPoint);
        if (point.Seq <= last || Interlocked.CompareExchange(ref _lastPoint, point.Seq, last) != last)
        {
            return;
        }

        Events.Publish(new WebEvent("point", new Lazy<string>(JsonSerializer.Serialize(point, WebUiJsonContext.Default.WebPoint))));
    }

    /// <summary>The strip now, to every page, when its key differs from what they have; the pictures built only then.</summary>
    public void PublishStrip()
    {
        if (Events.Count == 0 || StripKey is null)
        {
            return;
        }

        string key = StripKey();
        if (key == Interlocked.Exchange(ref _lastStrip, key))
        {
            return;
        }

        var strip = StripView?.Invoke();
        Events.Publish(new WebEvent("strip", new Lazy<string>(JsonSerializer.Serialize(strip, WebUiJsonContext.Default.WebStrip))));
    }

    /// <summary>A picture as an entry carries it: its three addresses under this run's tag. Pure but for the tag.</summary>
    public WebPicture Describe(int id, string name, int width, int height, bool tooLarge, string? key = null, bool deletable = false)
    {
        string root = "/api/picture/" + s_pictureRun + "/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
        return new WebPicture(id, name, root + "thumb", root + "mid", root + "full", width, height, tooLarge, key, deletable);
    }

    /// <summary>
    /// The page's status row now and its key (2026-10-15, the screen's <c>WebStatusNow</c>, reads only): the key leaves out the running clocks,
    /// so a tick alone sends nothing. Null sends no row.
    /// </summary>
    public Func<(WebStatus Status, string Key)>? StatusNow { get; set; }

    /// <summary>
    /// The full <c>/usage</c> figures for the page's card (2026-10-15, reads only; the card's meter and tiles since 2026-10-16); null gives an
    /// empty card.
    /// </summary>
    public Func<WebUsageCard>? UsageNow { get; set; }

    public WebUsageCard Usage() => UsageNow?.Invoke() ?? new WebUsageCard(App.UsageText.Label, null, [], App.UsageText.NothingCounted);

    /// <summary>The profile's saved prompts in their order (later on 2026-10-15, reads only; whole since 2026-10-16, for their ids); null lists none.</summary>
    public Func<IReadOnlyList<NeonSidekick.Prompts.SavedPrompt>>? PromptsNow { get; set; }

    public WebPrompts Prompts()
    {
        IReadOnlyList<NeonSidekick.Prompts.SavedPrompt> prompts;
        try
        {
            prompts = PromptsNow?.Invoke() ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Debug(Category, "Web UI: the saved prompts could not be read: " + e.Message);
            prompts = [];
        }

        return new WebPrompts([.. prompts.Select((p, i) => new WebPrompt(i + 1, p.Text, p.Id))]);
    }

    /// <summary>The screen's save of the page's draft as a prompt (2026-10-16); null: no saves.</summary>
    public Func<string, IPAddress?, WebReply>? PromptSave { get; set; }

    /// <summary>The screen's delete of a saved prompt by its id (2026-10-16); null: no deletes.</summary>
    public Func<string, IPAddress?, WebReply>? PromptDelete { get; set; }

    public WebReply SavePrompt(WebTextBody request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PromptSave is { } save ? save(request.Text ?? "", remote) : new WebReply(WebOutcome.NotFound);
    }

    public WebReply DeletePrompt(WebPromptDelete request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PromptDelete is { } delete && !string.IsNullOrEmpty(request.Id) ? delete(request.Id, remote) : new WebReply(WebOutcome.NotFound, WebUiText.PromptGone);
    }

    /// <summary>The profile's memories as the sheet's 💾 tab shows them (2026-10-16, reads only); null lists none.</summary>
    public Func<WebMemories>? MemoriesNow { get; set; }

    public WebMemories Memories()
    {
        try
        {
            return MemoriesNow?.Invoke() ?? new WebMemories([], false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Debug(Category, "Web UI: the memories could not be read: " + e.Message);
            return new WebMemories([], false);
        }
    }

    /// <summary>The screen's save of the page's draft as a memory (2026-10-16, <c>/remember</c>'s); null: no saves.</summary>
    public Func<string, IPAddress?, WebReply>? MemorySave { get; set; }

    /// <summary>The screen's delete of a memory by its text (2026-10-16); null: no deletes.</summary>
    public Func<string, IPAddress?, WebReply>? MemoryDelete { get; set; }

    public WebReply SaveMemory(WebTextBody request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MemorySave is { } save ? save(request.Text ?? "", remote) : new WebReply(WebOutcome.NotFound);
    }

    public WebReply DeleteMemory(WebTextBody request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MemoryDelete is { } delete && !string.IsNullOrEmpty(request.Text) ? delete(request.Text, remote) : new WebReply(WebOutcome.NotFound, WebUiText.MemoryGone);
    }

    /// <summary>The message history as the page gets it (2026-10-16, reads only, thread-safe): oldest first, no drafts set aside, no paste tokens.</summary>
    public Func<IReadOnlyList<string>>? HistoryNow { get; set; }

    public WebHistory History() => new(HistoryNow?.Invoke() ?? []);

    /// <summary>The screen's wipe of the message history (2026-10-16, the 🕘 tab's clear all); null: none.</summary>
    public Func<IPAddress?, WebReply>? HistoryClear { get; set; }

    public WebReply ClearHistory(IPAddress? remote) => HistoryClear is { } clear ? clear(remote) : new WebReply(WebOutcome.NotFound);

    /// <summary>
    /// A line the page sent, told as <see cref="Send"/> accepts it (2026-10-16: queued, or bare <c>/prompts</c> opening the sheet): the screen
    /// remembers it in the message history, as Enter does a typed line — at once, not when the line runs, so a line sent under a reply is
    /// already there for the page's next ↑. Called on the request's thread; never for a refusal, an empty or too long line, a full queue or a
    /// retry already taken.
    /// </summary>
    public Action<string>? Accepted { get; set; }

    public WebEntry? Entry(long seq) => Feed.EntryOf(seq, WebUiMarkdown.ToHtml);

    /// <summary>Whether the pages wear the profile's theme (2026-10-15, <i>Web UI profile theme</i>, read per request); null: they do.</summary>
    public Func<bool>? ProfileTheme { get; set; }

    public string ThemeCss() => WebTheme.Css(UI.Theme.Current, ProfileTheme?.Invoke() ?? true);

    public string Manifest() => WebTheme.Manifest(UI.Theme.Current, ProfileTheme?.Invoke() ?? true);

    /// <summary>The Home tab's side (2026-10-15, the screen's); null in a build without one.</summary>
    public IWebHome? Home { get; set; }

    /// <summary>The screen's chores on the session's 1-second tick whether or not a page streams (2026-10-15: the Home tab's lease running out).</summary>
    public Action? Tick { get; set; }

    /// <summary>The YouTube tab's player (2026-10-16, the screen's), or null with none.</summary>
    public IWebVideo? Video { get; set; }

    /// <summary>
    /// What the Chat badge reads (2026-10-15): the state's busy and waiting, the asks open, the theme, the files' tag, and the tabs on; the
    /// YouTube tab and the banner's words while a video waits for it (2026-10-16), copied from the screen's state. Every pulse marks the read
    /// (<see cref="WatchNudge"/>): <c>/api/pulse</c> and Home's read both build theirs here, a 304 included.
    /// </summary>
    public WebPulse Pulse()
    {
        Interlocked.Exchange(ref _pulseRead, _time.GetTimestamp());
        var state = _state();
        return new WebPulse(state.Busy, Asks.OpenAsks().Count, state.WaitingAtPc, state.Theme, WebUiAssets.Current, state.ChatTab, state.HomeTab, state.ShoppingTab, state.YouTubeTab, state.Watch);
    }

    /// <summary>When a pulse was last built (a timestamp of the session's clock; <see cref="long.MinValue"/> for never: a clock may start at 0), read without a lock.</summary>
    private long _pulseRead = long.MinValue;

    /// <summary>How recent a pulse or Home read counts as a page that will see a nudge soon (2026-10-16).</summary>
    public static readonly TimeSpan PulseFresh = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A video waits for a YouTube tab (2026-10-16): the state goes out now with its <c>watch</c> words, and a <c>watch</c> event on the Chat
    /// stream, so an open Chat page jumps at once (Home and Shopping at their next poll). True when a page will see it soon: a Chat stream is
    /// connected, or a pulse or Home read came within <see cref="PulseFresh"/>.
    /// </summary>
    public bool WatchNudge()
    {
        PublishState();
        string words = WebUiText.WatchBanner;
        Events.Publish(new WebEvent("watch", () => JsonSerializer.Serialize(new WebTextBack(words), WebUiJsonContext.Default.WebTextBack)));
        long read = Interlocked.Read(ref _pulseRead);
        return Events.Count > 0 || (read != long.MinValue && _time.GetElapsedTime(read) < PulseFresh);
    }

    /// <summary>Which tabs are on, read per request (later on 2026-10-15, the screen's settings); null: every one.</summary>
    public Func<WebTabs>? TabsNow { get; set; }

    public WebTabs Tabs() => TabsNow?.Invoke() ?? WebTabs.All;

    /// <summary>Wakes the screen's idle line: a message or a notice is waiting (the screen's <c>SignalAlert</c>, and its act signal under a botchat).</summary>
    public Action? Nudge { get; set; }

    /// <summary>Whether a message waits.</summary>
    public bool LinesReady => !_lines.IsEmpty;

    /// <summary>Whether a notice waits.</summary>
    public bool NoticesReady => !_notices.IsEmpty;

    /// <summary>The messages waiting now, oldest first (the state's list): the queue's, then the screen's held ones.</summary>
    public IReadOnlyList<string> Waiting => [.. _lines.Select(l => l.Text), .. HeldForPc?.Invoke() ?? []];

    /// <summary>How many messages wait (the Status bar's Queued count).</summary>
    public int WaitingCount => _lines.Count;

    /// <summary>The next message for the idle line, oldest first.</summary>
    public bool TryTakeLine(out WebLine line) => _lines.TryDequeue(out line!);

    /// <summary>Every waiting message dropped and handed back to the pages' drafts (a conversation cleared); their count.</summary>
    public int DropLines()
    {
        int dropped = 0;
        while (_lines.TryDequeue(out var line))
        {
            dropped++;
            HandBack("dropped", line.Text);
        }

        if (dropped > 0)
        {
            PublishState();
        }

        return dropped;
    }

    /// <summary>A message the screen withdrew (stopped before the model answered): back into the page's draft.</summary>
    public void Withdrawn(string text) => HandBack("withdrawn", text);

    /// <summary>A line the screen held for the idle line, dropped with the conversation: back into the page's draft.</summary>
    public void Dropped(string text) => HandBack("dropped", text);

    private readonly List<string> _drafts = [];

    /// <summary>
    /// A text for the page's draft (later on 2026-10-14: <c>/prompts &lt;n&gt;</c>, <c>/rewind</c> from the page): sent to the pages connected
    /// now, or held until the next one connects, which takes it with its snapshot — delivered once either way.
    /// </summary>
    public void Draft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_drafts)
        {
            if (Events.Count == 0)
            {
                _drafts.Add(text);
                return;
            }
        }

        HandBack("draft", text);
    }

    private IReadOnlyList<string>? TakeDrafts()
    {
        lock (_drafts)
        {
            if (_drafts.Count == 0)
            {
                return null;
            }

            var taken = _drafts.ToList();
            _drafts.Clear();
            return taken;
        }
    }

    /// <summary>A notice (or, with <paramref name="error"/>, an error line) for the screen to print on its own task.</summary>
    public void Notice(string text, bool error = false)
    {
        _notices.Enqueue((text, error));
        Nudge?.Invoke();
    }

    /// <summary>
    /// Hands a line to the PC's hint row (2026-10-15: the screen's <c>ScreenPane.Flash</c>). Called on a pool thread, never a request's: the pane
    /// draws under its own lock while it writes to the console, and a stalled console (a mouse selection in the terminal) must not hold a phone.
    /// </summary>
    public Action<string>? FlashOnPc { get; set; }

    /// <summary>
    /// What the page did, shown at the PC for a moment (2026-10-15: a ticked line, a store, a light): queued and handed over by one drain on the
    /// thread pool, the newest of a burst only, so the request answers at once whatever the console does.
    /// </summary>
    public void FlashPc(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (FlashOnPc is null)
        {
            return;
        }

        _flashes.Enqueue(text);
        if (Interlocked.CompareExchange(ref _flashing, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => DrainFlashes());
        }
    }

    private void DrainFlashes()
    {
        while (true)
        {
            string? newest = null;
            while (_flashes.TryDequeue(out string? text))
            {
                newest = text;
            }

            if (newest is not null)
            {
                try
                {
                    FlashOnPc?.Invoke(newest);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    DiagnosticLog.Debug(Category, "Web UI: a flash at the PC failed: " + e.Message);
                }
            }

            Volatile.Write(ref _flashing, 0);
            if (_flashes.IsEmpty || Interlocked.CompareExchange(ref _flashing, 1, 0) != 0)
            {
                return;   // nothing came meanwhile, or another drain took it
            }
        }
    }

    /// <summary>The notices waiting, oldest first, taken.</summary>
    public IReadOnlyList<(string Text, bool Error)> TakeNotices()
    {
        var taken = new List<(string Text, bool Error)>();
        while (_notices.TryDequeue(out var notice))
        {
            taken.Add(notice);
        }

        return taken;
    }

    // ── The pages' calls (Kestrel threads) ────────────────────────────────────

    public WebSnapshot Snapshot()
    {
        // The connecting page's own copy: the other pages' last state stays what the sampler compares with.
        return new WebSnapshot(Feed.Snapshot(WebUiMarkdown.ToHtml, defer: true), Asks.OpenAsks().Select(View).ToList(), _state(), WebUiText.Words, TakeDrafts(), StatusNow?.Invoke().Status,
            StripView?.Invoke(), PointNow?.Invoke());
    }

    public void SignedIn(IPAddress? remote)
    {
        DiagnosticLog.Info(Category, WebUiText.SignedInLogLine(remote));
        Notice(WebUiText.SignedInNotice(remote));
    }

    public WebReply Send(WebSendRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        string text = Clean(request.Text ?? "");
        if (text.Trim().Length == 0)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.EmptyMessage);
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxMessageBytes)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.TooLong(MaxMessageBytes));
        }

        // A command (later on 2026-10-14): checked here, and again as the screen takes it (the settings may change in between).
        if (!request.AsText && SlashCommands.Parse(text) is var (command, args) && command != SlashCommand.None)
        {
            var verdict = WebCommands.Check(command, args, text, CommandSettings?.Invoke() ?? default);
            DiagnosticLog.Info(Category, WebUiText.CommandLogLine(WebCommands.Word(text), remote, verdict.Kind == WebCommandKind.Refused ? "refused" : verdict.Kind.ToString().ToLowerInvariant()));
            if (verdict.Kind == WebCommandKind.Page)
            {
                Accepted?.Invoke(text);
                return new WebReply(WebOutcome.Ok, Open: verdict.Open);   // later on 2026-10-15: the page opens its own sheet, nothing queued for the PC
            }

            if (!verdict.Allowed)
            {
                return new WebReply(WebOutcome.Refused, verdict.Reason, verdict.AsText, verdict.Use);
            }
        }

        if (!Remember(request.Id))
        {
            return WebReply.Ok;   // a retry of a message already taken
        }

        if (_lines.Count >= MaxWaiting)
        {
            Forget(request.Id);
            return new WebReply(WebOutcome.TooMany, WebUiText.QueueFull(MaxWaiting));
        }

        _lines.Enqueue(new WebLine(text, request.Id, remote, request.AsText));
        Accepted?.Invoke(text);
        PublishState();
        Nudge?.Invoke();
        return WebReply.Ok;
    }

    public WebReply Stop(IPAddress? remote) => _stop(remote);

    public WebReply Answer(WebAnswerRequest request, IPAddress? remote) => Asks.Answer(request, remote);

    public WebShopping Shopping() => _shopping() is { } list ? WebShoppingView.Of(list) : new WebShopping("", ShoppingText.Unavailable, [], ShoppingText.TickedHeading, [], ShoppingText.GuessMark, WebUiAssets.Current);

    public WebReply Check(WebCheckRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_shopping() is not { } list)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchItem);
        }

        var reply = WebShoppingView.Check(list, request.Id, request.On, out string? line);
        if (reply.Outcome == WebOutcome.Ok && line is not null)
        {
            DiagnosticLog.Info(Category, WebUiText.CheckedLogLine(line, request.On, remote));
            _shoppingChanged();
            FlashPc(WebUiText.CheckedFlash(line, request.On));
        }

        return reply;
    }

    public WebReply UseStore(WebStoreRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_shopping() is not { } list)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchStore);
        }

        var reply = WebShoppingView.UseStore(list, request.Id, out string? store);
        if (reply.Outcome == WebOutcome.Ok && store is not null)
        {
            DiagnosticLog.Info(Category, WebUiText.StoreLogLine(store, remote));
            _shoppingChanged();
            FlashPc(WebUiText.StoreFlash(store));
        }

        return reply;
    }

    // ── Publishing ────────────────────────────────────────────────────────────

    /// <summary>The state now, to every page, when it differs from what they have (the sampler's tick; a message queued).</summary>
    public void PublishState()
    {
        if (Events.Count == 0)
        {
            return;
        }

        string json = Json(_state());
        if (json == Interlocked.Exchange(ref _lastState, json))
        {
            return;
        }

        Events.Publish(new WebEvent("state", new Lazy<string>(json)));
    }

    /// <summary>
    /// The PC's spinner changed (2026-10-15, the pane's <c>BusyChanged</c>, on the thread that changed it): the row now, or, inside
    /// <see cref="StatusThrottle"/> of the last one, once more when the window ends, so the last label always lands. Never blocks.
    /// </summary>
    public void StatusNudge()
    {
        if (Events.Count == 0 || StatusNow is null)
        {
            return;
        }

        long now = _time.GetTimestamp();
        lock (_statusGate)
        {
            if (_statusTrailing)
            {
                return;   // the trailing send reads the newest
            }

            var since = _time.GetElapsedTime(_statusAt, now);
            if (_statusSent && since < StatusThrottle)
            {
                _statusTrailing = true;
                _statusTrail.Change(StatusThrottle - since, Timeout.InfiniteTimeSpan);
                return;
            }

            _statusAt = now;
            _statusSent = true;
        }

        PublishStatus();
    }

    private void TrailingStatus()
    {
        lock (_statusGate)
        {
            _statusTrailing = false;
            _statusAt = _time.GetTimestamp();
        }

        try
        {
            PublishStatus();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(Category, "Web UI: the status could not be read: " + e.Message);
        }
    }

    /// <summary>The status row now, to every page, when its key differs from what they have (the sampler's tick, a nudge).</summary>
    public void PublishStatus()
    {
        if (Events.Count == 0 || StatusNow is null)
        {
            return;
        }

        var (status, key) = StatusNow();
        if (key == Interlocked.Exchange(ref _lastStatus, key))
        {
            return;
        }

        Events.Publish(new WebEvent("status", new Lazy<string>(JsonSerializer.Serialize(status, WebUiJsonContext.Default.WebStatus))));
    }

    private void SampleState()
    {
        try
        {
            Tick?.Invoke();
            PublishState();
            PublishStatus();
            // A flip of ComfyUI picture strip changes nothing in the strip itself: the tick reads its key (free when nothing changed).
            PublishStrip();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(Category, "Web UI: the state could not be read: " + e.Message);
        }
    }

    private static string Json(WebState state) => JsonSerializer.Serialize(state, WebUiJsonContext.Default.WebState);

    private void OnFeedChanged(FeedChange change)
    {
        switch (change)
        {
            case FeedChange.Appended appended:
                Events.Publish(new WebEvent("append", () => JsonSerializer.Serialize(appended.Entry, WebUiJsonContext.Default.WebEntry)));
                break;
            case FeedChange.Grew grew:
                Events.Publish(new WebEvent("delta", () => JsonSerializer.Serialize(new WebDelta(grew.Seq, grew.At, grew.Text), WebUiJsonContext.Default.WebDelta)));
                break;
            case FeedChange.Ended ended:
                // A reply's Markdown as HTML; a thinking block its summary, a run or an output their end alone (2026-10-15).
                Events.Publish(new WebEvent("end", () => JsonSerializer.Serialize(
                    new WebEnd(ended.Seq, ended.Kind == FeedKind.Assistant ? WebUiMarkdown.ToHtml(ended.Markdown) : null, ended.Summary), WebUiJsonContext.Default.WebEnd)));
                break;
            case FeedChange.Replaced replaced:
                Events.Publish(new WebEvent("replace", () => JsonSerializer.Serialize(new WebReplaced(replaced.Seq, replaced.Text, replaced.Failed), WebUiJsonContext.Default.WebReplaced)));
                break;
            case FeedChange.Cleared:
                Events.Publish(new WebEvent("reset", () => "{}"));
                break;
            case FeedChange.Removed removed:
                Events.Publish(new WebEvent("remove", () => JsonSerializer.Serialize(new WebRemoved(removed.Seq), WebUiJsonContext.Default.WebRemoved)));
                break;
        }
    }

    private void PublishAsks()
    {
        var asks = Asks.OpenAsks();
        Events.Publish(new WebEvent("asks", () => JsonSerializer.Serialize(new WebAsks(asks.Select(View).ToList()), WebUiJsonContext.Default.WebAsks)));
    }

    private void HandBack(string name, string text) =>
        Events.Publish(new WebEvent(name, () => JsonSerializer.Serialize(new WebTextBack(text), WebUiJsonContext.Default.WebTextBack)));

    private static WebAskView View(WebAskHandle handle)
    {
        var spec = handle.Spec;
        return new WebAskView(handle.Id, spec.Kind, spec.Title, spec.Body, spec.Lines, spec.Markdown is { } markdown ? WebUiMarkdown.ToHtml(markdown) : null,
            spec.Choices, spec.TextChoice, spec.Questions, spec.SubmitLabel, spec.Items, spec.Current);
    }

    // ── Message ids ───────────────────────────────────────────────────────────

    /// <summary>The paste stand-ins the PC's draft uses, taken out: a web text never names one of the PC's pastes.</summary>
    public static string Clean(string text) => text.Any(PasteBlocks.IsToken) ? new string(text.Where(c => !PasteBlocks.IsToken(c)).ToArray()) : text;

    private bool Remember(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return true;
        }

        lock (_idGate)
        {
            if (_ids.Contains(id))
            {
                return false;
            }

            _ids.AddLast(id);
            if (_ids.Count > RememberedIds)
            {
                _ids.RemoveFirst();
            }

            return true;
        }
    }

    private void Forget(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        lock (_idGate)
        {
            _ids.Remove(id);
        }
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _statusTrail.Dispose();
        _strip.Dispose();
        _point.Dispose();
        _completing.Dispose();
    }
}
