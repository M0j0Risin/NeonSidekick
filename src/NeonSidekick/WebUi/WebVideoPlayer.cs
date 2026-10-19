using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.WebUi;

/// <summary>The YouTube tab's side the host talks to (2026-10-16, <see cref="WebVideoPlayer"/>): a page's stream attached, a page's report.</summary>
public interface IWebVideo
{
    /// <summary>How long a page waits for YouTube's player script before it reports a failure (a test's backend shortens it).</summary>
    int ApiWaitMs { get; }

    /// <summary>
    /// A page's stream attached: <paramref name="page"/> its id from <c>sessionStorage</c>, <paramref name="load"/> its script's nonce,
    /// <paramref name="claim"/> its Play here's id (a fresh one per tap; <c>1</c> from the first script; null for none), <paramref name="visible"/>
    /// whether it was in view as it opened. <paramref name="end"/> ends the host's stream; the player calls it only for a stream it evicts or a
    /// reconnect replaces. The attachment's <c>hello</c> is written first; disposing it is the detach.
    /// </summary>
    WebVideoAttachment Attach(string page, string load, string? claim, bool visible, IPAddress? remote, Action end);

    /// <summary>A page's report: <see cref="WebOutcome.Ok"/>, or <see cref="WebOutcome.Conflict"/> from a page that is not the player.</summary>
    WebOutcome Report(string page, string message, IPAddress? remote);

    /// <summary>
    /// A page came into view or left it (later on 2026-10-16): <see cref="WebOutcome.Ok"/> from any attached page, <see cref="WebOutcome.Conflict"/>
    /// from one with no open stream.
    /// </summary>
    WebOutcome Seen(string page, bool visible, IPAddress? remote);
}

/// <summary>One page's stream as the player holds it: the hello to write first, the page's own events, and the detach.</summary>
public sealed class WebVideoAttachment : IDisposable
{
    private readonly Action<WebVideoAttachment> _detach;
    private int _disposed;

    internal WebVideoAttachment(WebVideoHello hello, ChannelReader<WebEvent> events, Action<WebVideoAttachment> detach)
    {
        Hello = hello;
        Events = events;
        _detach = detach;
    }

    public WebVideoHello Hello { get; }

    public ChannelReader<WebEvent> Events { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _detach(this);
        }
    }
}

/// <summary>
/// The Web UI's YouTube tab as a player (2026-10-16, <c>YouTube open links in</c> <c>webui-window</c>, and the tab's own saved list): an
/// <see cref="IVideoPlayer"/> the router (<see cref="RoutedVideoPlayer"/>) plays on, whose video runs in YouTube's player on a phone's or a
/// browser's YouTube tab. Portable; one lock, <see cref="Changed"/> and <see cref="TookOver"/> raised outside it; its time is the screen's
/// <see cref="TimeProvider"/>.
///
/// <para><b>Pages, not streams.</b> A page is a browser tab, its id kept in <c>sessionStorage</c> and sent with its stream and its reports. A
/// stream with the same id and the same <c>load</c> nonce is a reconnect, which replaces the old one quietly (a half-open socket); the same id
/// with a new <c>load</c> and no stream open is a reload; and with another stream still open, a duplicated tab, given a fresh id in its hello.
/// Each page says whether it is in view (<c>visible=</c> on its stream, then <see cref="Seen"/> on every change and every reconnect), and the
/// player keeps when it last came into view; an owner with no open stream counts as hidden.</para>
///
/// <para><b>Who plays</b> (later on 2026-10-16, the user's call, after a play went to a hidden tab that had merely reconnected first, and a
/// Play here dropped the video): while nothing plays, the player follows the tab shown most recently; a play goes to the tab shown most
/// recently; a tab that is playing keeps the player until a new play or a Play here. So the first page to attach owns; an idle owner (nothing
/// open, or only a held request) yields to a page attaching in view or coming into view, and hands over to the visible page seen most recently
/// when it hides; <see cref="Play"/> moves the player to the visible page seen most recently, or, with none in view, holds the request (a hidden
/// owner's other video closed first); a playing owner is never moved on visibility alone. Play here (<c>claim</c>) takes the player and the
/// video with it: the old owner is told <c>taken</c> (its page stops), the video's place becomes the Resume offer, and the claimer's hello says
/// <c>play</c>. Each tap's claim carries a fresh id; the same id again (the browser's own reconnect to the same URL) is a plain reconnect. The
/// owner leaving is read after <see cref="DetachGrace"/>, so a reconnect is not a close: its video's snapshot goes null, as a window's close,
/// and its last place becomes the Resume offer; the visible page seen most recently (else the longest waiting) is promoted with an
/// <c>owner</c> event. A reload stops the video too: the owner's first report after it, <c>page</c>, is read as the close and the offer sent
/// back, unless its <c>load</c> nonce is the script a video was just given to (a held request loaded as it attached).</para>
///
/// <para><b>A held request</b> loads only into a visible owner. Its snapshot reads <see cref="VideoSnapshot.Held"/> (whether a page will see
/// the nudge; whether YouTube tabs are open but none in view) and expires after <see cref="YouTubeText.HoldFor"/> on the screen's tick. Only a
/// hold nudges the other pages to the tab. Reports from the owner alone, applied as the windows apply theirs, <see cref="VideoLoadSettle"/> on
/// each. Every decision writes a Debug line (a page by its first four characters and its address).</para>
/// </summary>
public sealed class WebVideoPlayer : IVideoPlayer, IVideoTakeover, IWebVideo, IDisposable
{
    /// <summary>How long a page's stream may be gone before its leaving counts: a reconnect within it changes nothing.</summary>
    public static readonly TimeSpan DetachGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a page's last honored claim id is kept once its last stream went (later on 2026-10-16): long past the browser's own reconnect
    /// (about 3 s), whose repeat it must recognize, so a blip after a tap never claims again.
    /// </summary>
    public static readonly TimeSpan ClaimMemory = TimeSpan.FromMinutes(1);

    /// <summary>The most YouTube streams at once; one more evicts the oldest hidden waiting page's (else the oldest waiting), never the owner's.</summary>
    public const int MaxStreams = 4;

    /// <summary>The most events a page may have waiting before it is dropped (its reconnect comes back as the same page).</summary>
    public const int Capacity = 256;

    /// <summary>The longest report a page may post (the host refuses a longer one with a 400).</summary>
    public const int MaxReportChars = 4096;

    /// <summary>How long a page waits for YouTube's script (<see cref="IWebVideo.ApiWaitMs"/>) by default.</summary>
    public const int DefaultApiWaitMs = 10_000;

    /// <summary>The claim the first script sent (<c>&amp;claim=1</c>): always a claim.</summary>
    public const string LegacyClaim = "1";

    private const string Category = "WebUi";

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<bool> _nudge;
    private readonly Action<string>? _flash;
    private readonly List<PageStream> _streams = [];
    private readonly Dictionary<string, ClaimSeen> _claims = new(StringComparer.Ordinal);
    private readonly VideoLoadSettle _settle = new();
    private VideoSnapshot? _snapshot;
    private long _version;
    private VideoRequest? _held;
    private DateTimeOffset _heldSince;
    private string? _owner;
    private WebVideoResume? _offer;
    private ITimer? _grace;
    private long _joined;
    private bool _disposed;

    // The load nonce of the script a video was last given to (later on 2026-10-16, Fix 3): that script's own first page report is no reload.
    private string? _loadedInto;

    // Whether the owner's reports count: only after a load (or its own Resume), never after a close, so a stopped player's late report
    // never brings a closed video back.
    private bool _closed = true;

    /// <param name="time">The screen's clock: the grace's timer, the hold's age, when a page came into view.</param>
    /// <param name="nudge">Asks the open pages to jump to the YouTube tab; true when one will see it soon (<c>WebUiSession.WatchNudge</c>).</param>
    /// <param name="flash">The PC's hint row (<c>WebUiSession.FlashPc</c>), for a Play here or a Resume tap; null for none.</param>
    /// <param name="apiWaitMs">How long a page waits for YouTube's script.</param>
    public WebVideoPlayer(TimeProvider? time = null, Func<bool>? nudge = null, Action<string>? flash = null, int apiWaitMs = DefaultApiWaitMs)
    {
        _time = time ?? TimeProvider.System;
        _nudge = nudge ?? (() => false);
        _flash = flash;
        ApiWaitMs = apiWaitMs;
    }

    public int ApiWaitMs { get; }

    public VideoPlace Place => VideoPlace.WebTab;

    public event Action<VideoSnapshot?>? Changed;

    public event Action? TookOver;

    public VideoSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// Whether a video waits for a YouTube tab with none in view (none attached, or every one hidden): the pages' banner and jump
    /// (<c>WebState.Watch</c>, <c>WebPulse.Watch</c>).
    /// </summary>
    public bool Waiting
    {
        get
        {
            lock (_gate)
            {
                return _held is not null && !_streams.Any(s => s.Visible);
            }
        }
    }

    /// <summary>The page that plays, or null (tests).</summary>
    internal string? Owner
    {
        get
        {
            lock (_gate)
            {
                return _owner;
            }
        }
    }

    /// <summary>The streams attached now (tests).</summary>
    internal int Streams
    {
        get
        {
            lock (_gate)
            {
                return _streams.Count;
            }
        }
    }

    /// <summary>The Resume offer now (tests).</summary>
    internal WebVideoResume? Offer
    {
        get
        {
            lock (_gate)
            {
                return _offer;
            }
        }
    }

    /// <summary>Whether <paramref name="page"/>'s newest stream is in view, or null with none open (tests).</summary>
    internal bool? VisibleOf(string page)
    {
        lock (_gate)
        {
            return _streams.LastOrDefault(s => s.Page == page)?.Visible;
        }
    }

    /// <summary>Whether <paramref name="text"/> has the shape of a page id or a load nonce: 16 to 64 base64url characters.</summary>
    public static bool IsId(string? text) =>
        text is { Length: >= 16 and <= 64 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>A fresh page id: 16 random bytes, base64url.</summary>
    public static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── IVideoPlayer ──────────────────────────────────────────────────────

    /// <summary>
    /// A play: to the visible page seen most recently (the player moved there first when it is another), else held for one to come into view,
    /// the pages nudged then (Fix 5: only a hold nudges, after it is set, so the published state already says <see cref="Waiting"/>), and
    /// <see cref="Changed"/> raised once with the final snapshot.
    /// </summary>
    public void Play(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outbox = new Outbox();
        VideoSnapshot snapshot;
        bool held;
        DateTimeOffset since;
        lock (_gate)
        {
            _offer = null;
            since = _time.GetUtcNow();
            if (MostRecentVisible() is { } target)
            {
                held = false;
                if (target.Page != _owner)
                {
                    Transfer(target, outbox, WebUiText.YouTubeMovedLogLine(target.Remote), flash: null, attaching: false);
                }

                _held = null;
                snapshot = Load(target, request);
                outbox.Debug.Add(WebUiText.YouTubePlayDebug("loaded into", target.Page, target.Remote));
            }
            else
            {
                held = true;
                if (_snapshot is not null && _held is null && OwnerStream() is { } hidden)
                {
                    Enqueue(hidden, new WebEvent("close", () => "{}"));   // a hidden owner's other video never plays on under the new one's snapshot
                }

                _closed = true;
                _held = request;
                _heldSince = since;
                snapshot = _snapshot = VideoSnapshot.Opening(request) with
                {
                    Version = ++_version,
                    Place = VideoPlace.WebTab,
                    Held = new VideoHold(false, since, TabsHidden: _streams.Count > 0),
                };
                outbox.Debug.Add(WebUiText.YouTubePlayDebug(_streams.Count > 0 ? "held (no YouTube tab in view)" : "held (no YouTube tab open)", null, null));
            }
        }

        outbox.Flush(this, raise: false);
        if (held)
        {
            bool nudged = _nudge();   // outside the lock: it publishes on the Chat stream, reading Waiting
            lock (_gate)
            {
                if (ReferenceEquals(_held, request) && _snapshot is { Held: { } hold } current)
                {
                    snapshot = _snapshot = current with { Held = hold with { Nudged = nudged } };
                }
            }
        }

        Raise(snapshot);
    }

    public bool Send(VideoCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (_snapshot is null || _held is not null || OwnerStream() is not { } owner)
            {
                return false;
            }

            return Enqueue(owner, new WebEvent("cmd", () => VideoMessages.Command(command)));
        }
    }

    /// <summary>The held request dropped, or the owner told to close; the Resume offer cleared (an explicit close means done). True when one was open.</summary>
    public bool Close() => Stop(clearOffer: true);

    /// <summary>
    /// Everything let go (the screen's tick once the tab is off, and the app's exit): the owner told to close, the held request and the offer
    /// dropped, the snapshot null.
    /// </summary>
    public void Forget() => Stop(clearOffer: true);

    /// <summary>
    /// A profile switch: the held request and the Resume offer were the last profile's and go; a video already playing plays on, as the PC
    /// window does across a switch.
    /// </summary>
    public void ForgetProfile()
    {
        bool raise;
        lock (_gate)
        {
            _offer = null;
            raise = _held is not null;
            if (raise)
            {
                _held = null;
                _snapshot = null;
            }
        }

        if (raise)
        {
            Raise(null);
        }
    }

    /// <summary>The screen's tick: a request held past <see cref="YouTubeText.HoldFor"/> is dropped.</summary>
    public void Tick()
    {
        bool raise = false;
        lock (_gate)
        {
            if (_held is not null && _time.GetUtcNow() - _heldSince >= YouTubeText.HoldFor)
            {
                _held = null;
                _snapshot = null;
                raise = true;
            }
        }

        if (raise)
        {
            DiagnosticLog.Info(Category, "Web UI: a video waited for a YouTube tab too long and was dropped.");
            Raise(null);
        }
    }

    // ── IWebVideo ─────────────────────────────────────────────────────────

    /// <summary>A stream attached in view, with Play here as the first script sent it (the tests' and the old callers' shape).</summary>
    public WebVideoAttachment Attach(string page, string load, bool claim, IPAddress? remote, Action end) =>
        Attach(page, load, claim ? LegacyClaim : null, visible: true, remote, end);

    public WebVideoAttachment Attach(string page, string load, string? claim, bool visible, IPAddress? remote, Action end)
    {
        ArgumentNullException.ThrowIfNull(end);
        var outbox = new Outbox();
        PageStream stream;
        WebVideoHello hello;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _time.GetUtcNow();
            PruneClaims(now);
            var same = _streams.Where(s => s.Page == page).ToList();
            DateTimeOffset seenBefore = DateTimeOffset.MinValue;
            if (same.FirstOrDefault(s => s.Load == load) is { } reconnect)
            {
                _streams.Remove(reconnect);   // a reconnect: the old stream (a half-open socket) goes quietly
                reconnect.Queue.Writer.TryComplete();
                outbox.Ends.Add(reconnect.End);
                seenBefore = reconnect.SeenAt;
            }
            else if (same.Count > 0)
            {
                page = NewId();   // a duplicated tab: sessionStorage copied, the other page still open
            }

            if (_owner == page)
            {
                CancelGrace();
            }

            // At the cap the oldest hidden waiting page goes, else the oldest waiting page; never the owner.
            if (_streams.Count >= MaxStreams && _streams.Where(s => s.Page != _owner).OrderBy(s => s.Visible ? 1 : 0).ThenBy(s => s.Joined).FirstOrDefault() is { } evicted)
            {
                _streams.Remove(evicted);
                evicted.Queue.Writer.TryComplete();
                outbox.Ends.Add(evicted.End);
                LastStreamGone(evicted.Page, now);
            }

            var queue = Channel.CreateBounded<WebEvent>(new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            stream = new PageStream(page, load, queue, end, ++_joined, remote)
            {
                Visible = visible,
                SeenAt = visible ? Max(now, seenBefore) : seenBefore,
            };
            _streams.Add(stream);
            if (_claims.TryGetValue(page, out var kept))
            {
                _claims[page] = kept with { GoneSince = null };
            }

            // A claim is honored once per id (Fix 4): the browser's own reconnect repeats the stream's first URL, claim and all.
            bool claims = false;
            if (claim is not null)
            {
                if (claim != LegacyClaim && _claims.TryGetValue(page, out var last) && last.Id == claim)
                {
                    outbox.Debug.Add(WebUiText.YouTubeClaimDebug(page, remote, honored: false));
                }
                else
                {
                    claims = true;
                    _claims[page] = new ClaimSeen(claim, null);
                }
            }

            bool carried = false;
            if (_owner is null)
            {
                _owner = page;
                outbox.Info.Add(WebUiText.YouTubeOwnerLogLine(remote));
            }
            else if (claims && _owner != page)
            {
                // Play here takes the video with it (Fix 2): where it was, less the rewind, offered to the claimer, whose hello says play.
                if (_snapshot is { } playing && _held is null && !_closed)
                {
                    _offer = OfferFrom(playing);
                    _snapshot = null;
                    _closed = true;
                    outbox.RaiseNull = true;
                    carried = _offer is not null;
                }

                Transfer(stream, outbox, WebUiText.YouTubeClaimLogLine(remote), WebUiText.YouTubeClaimFlash, attaching: true);
                outbox.Debug.Add(WebUiText.YouTubeClaimDebug(page, remote, honored: true));
            }
            else if (claims && _owner == page)
            {
                outbox.Debug.Add(WebUiText.YouTubeClaimDebug(page, remote, honored: true));
            }
            else if (visible && _owner != page && OwnerIdle())
            {
                Transfer(stream, outbox, WebUiText.YouTubeMovedLogLine(remote), flash: null, attaching: true);   // rule 1: before the held load
            }

            bool owner = _owner == page;
            hello = new WebVideoHello(page, WebUiText.VideoWords, owner, _offer, ApiWaitMs, Play: carried);
            outbox.Debug.Add(WebUiText.YouTubeAttachDebug(page, remote, visible, owner));
            if (owner && visible && _held is { } held)
            {
                _held = null;
                outbox.Raise = Load(stream, held);
                outbox.RaiseAny = true;
                outbox.Debug.Add(WebUiText.YouTubePlayDebug("held, loaded into", page, remote));
            }
        }

        outbox.Flush(this);
        return new WebVideoAttachment(hello, stream.Queue.Reader, a => Detach(stream));
    }

    public WebOutcome Seen(string page, bool visible, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(page);
        var outbox = new Outbox();
        lock (_gate)
        {
            if (_streams.LastOrDefault(s => s.Page == page) is not { } stream)
            {
                return WebOutcome.Conflict;
            }

            bool was = stream.Visible;
            stream.Visible = visible;
            if (visible && !was)
            {
                stream.SeenAt = _time.GetUtcNow();
            }

            outbox.Debug.Add(WebUiText.YouTubeSeenDebug(page, remote, visible, was));
            if (visible)
            {
                // Coming into view only (2026-10-19, the CI-like runs): a page says it is in view again on every reconnect, and a page already
                // in view repeating it must not take the idle player back from another's Play here a moment before (the page told "taken" then
                // owned it again, "Nothing is playing here" where Play here belonged). Nor is it "shown more recently" for saying so twice.
                if (page != _owner && !was && OwnerIdle())
                {
                    Transfer(stream, outbox, WebUiText.YouTubeMovedLogLine(remote), flash: null, attaching: false);   // rule 2
                }

                if (page == _owner && _held is { } held)
                {
                    _held = null;
                    outbox.Raise = Load(stream, held);   // rule 2, and the unlocked phone
                    outbox.RaiseAny = true;
                    outbox.Debug.Add(WebUiText.YouTubePlayDebug("held, loaded into", page, remote));
                }
            }
            else if (page == _owner && OwnerIdle() && MostRecentVisible() is { } next)
            {
                Transfer(next, outbox, WebUiText.YouTubeMovedLogLine(next.Remote), flash: null, attaching: false);   // rule 3
                if (_held is { } held)
                {
                    _held = null;
                    outbox.Raise = Load(next, held);
                    outbox.RaiseAny = true;
                }
            }
        }

        outbox.Flush(this);
        return WebOutcome.Ok;
    }

    /// <summary>
    /// A tap on the YouTube tab's saved list from a page (later on 2026-10-16): the page takes the player, as Play here does but never carrying
    /// the old video (the play that follows at once replaces it), and is marked in view now, since a tap means it is, so the play goes to it.
    /// <see cref="WebOutcome.Conflict"/> for a page with no open stream; the owner's own tap changes nothing else.
    /// </summary>
    public WebOutcome Claim(string page, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(page);
        var outbox = new Outbox();
        lock (_gate)
        {
            if (_streams.LastOrDefault(s => s.Page == page) is not { } stream)
            {
                return WebOutcome.Conflict;
            }

            stream.Visible = true;
            stream.SeenAt = _time.GetUtcNow();
            if (_owner != page)
            {
                Transfer(stream, outbox, WebUiText.YouTubeClaimLogLine(remote), WebUiText.YouTubeClaimFlash, attaching: false);
                outbox.Debug.Add(WebUiText.YouTubeClaimDebug(page, remote, honored: true));
            }
        }

        outbox.Flush(this);
        return WebOutcome.Ok;
    }

    public WebOutcome Report(string page, string message, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(page);
        var outbox = new Outbox();
        string? refused = null;
        lock (_gate)
        {
            if (_owner != page)
            {
                return WebOutcome.Conflict;
            }

            var current = _snapshot ?? new VideoSnapshot(VideoState.Opening, null, Version: _version);
            var (kind, next) = VideoMessages.Apply(current, message);
            switch (kind)
            {
                case VideoPageEvent.Page:
                    // The script given the video just now (a held request loaded as it attached, Fix 3): its first report is no reload.
                    if (VideoMessages.PageLoad(message) is { } nonce && nonce == _loadedInto)
                    {
                        outbox.Debug.Add(WebUiText.YouTubePageReportDebug(page, remote, acted: false));
                        break;
                    }

                    // The owner's page loaded again (a reload, the user's or after an upgrade): its player went with it.
                    _closed = true;
                    if (_snapshot is { } playing && _held is null)
                    {
                        _offer = OfferFrom(playing);
                        _snapshot = null;
                        outbox.RaiseAny = true;
                        if (_offer is { } offer && OwnerStream() is { } owner)
                        {
                            Enqueue(owner, new WebEvent("resume", () => JsonSerializer.Serialize(offer, WebUiJsonContext.Default.WebVideoResume)));
                        }
                    }

                    outbox.Debug.Add(WebUiText.YouTubePageReportDebug(page, remote, acted: true));
                    break;
                case VideoPageEvent.Resumed:
                    _offer = null;
                    _held = null;
                    _closed = false;
                    _settle.Loaded();   // the page loads the video itself
                    _loadedInto = OwnerStream()?.Load;
                    outbox.TookOver = true;
                    break;
                case VideoPageEvent.Failed:
                case VideoPageEvent.State:
                case VideoPageEvent.Error:
                    if (_closed || !_settle.Accept(kind, next))
                    {
                        break;   // the "paused, no duration" a load leaves behind
                    }

                    _version = next.Version;
                    outbox.Raise = _snapshot = next with { Place = VideoPlace.WebTab, Held = null };
                    outbox.RaiseAny = true;
                    if (kind == VideoPageEvent.Error && next.Error is { } code)
                    {
                        refused = WebUiText.YouTubeRefusedLogLine(next.VideoId, code);
                    }

                    break;
            }
        }

        if (refused is not null)
        {
            DiagnosticLog.Info("Video", refused);
        }

        if (outbox.TookOver)
        {
            DiagnosticLog.Info(Category, WebUiText.YouTubeResumeLogLine(remote));
            _flash?.Invoke(WebUiText.YouTubeResumeFlash);
            TookOver?.Invoke();
        }

        outbox.Flush(this);
        return WebOutcome.Ok;
    }

    public void Dispose()
    {
        List<PageStream> streams;
        lock (_gate)
        {
            _disposed = true;
            CancelGrace();
            streams = [.. _streams];
            _streams.Clear();
        }

        foreach (var stream in streams)
        {
            stream.Queue.Writer.TryComplete();
        }
    }

    // ── Under the lock ────────────────────────────────────────────────────

    // The owner's open stream, or null (no owner, or one in its grace).
    private PageStream? OwnerStream() => _owner is null ? null : _streams.LastOrDefault(s => s.Page == _owner);

    // Nothing open but perhaps a held request: the player may follow the tab in view.
    private bool OwnerIdle() => _snapshot is null || _held is not null;

    // The visible stream that came into view last (the owner's too); null with none in view.
    private PageStream? MostRecentVisible() => _streams.Where(s => s.Visible).OrderByDescending(s => s.SeenAt).ThenByDescending(s => s.Joined).FirstOrDefault();

    // The player moved to `to` (the one place it moves, but for the first owner): taken to the old owner's stream, the grace cancelled, owner
    // to `to` unless it is the stream being attached (its hello says so), the caller's audit line and flash queued.
    private void Transfer(PageStream to, Outbox outbox, string logLine, string? flash, bool attaching)
    {
        if (OwnerStream() is { } old && old.Page != to.Page)
        {
            Enqueue(old, new WebEvent("taken", () => "{}"));
        }

        _owner = to.Page;
        CancelGrace();
        if (!attaching)
        {
            var owner = new WebVideoOwner(true, _offer);
            Enqueue(to, new WebEvent("owner", () => JsonSerializer.Serialize(owner, WebUiJsonContext.Default.WebVideoOwner)));
        }

        outbox.Info.Add(logLine);
        outbox.Flash ??= flash;
    }

    // A load to a page's stream: the settle armed, the snapshot opening on the tab, the stream's script remembered (Fix 3).
    private VideoSnapshot Load(PageStream owner, VideoRequest request)
    {
        _settle.Loaded();
        _closed = false;
        _loadedInto = owner.Load;
        Enqueue(owner, new WebEvent("load", () => VideoMessages.Load(request)));
        return _snapshot = VideoSnapshot.Opening(request) with { Version = ++_version, Place = VideoPlace.WebTab };
    }

    // An event for one page; a page whose queue is full is dropped (its reconnect comes back as the same page).
    private bool Enqueue(PageStream stream, WebEvent item)
    {
        if (stream.Queue.Writer.TryWrite(item))
        {
            return true;
        }

        _streams.Remove(stream);
        stream.Queue.Writer.TryComplete();
        if (stream.Page == _owner)
        {
            StartGrace(stream.Page);
        }

        LastStreamGone(stream.Page, _time.GetUtcNow());
        _ = Task.Run(() => End(stream.End));
        return false;
    }

    // A page's last stream went: its claim id is kept a while yet (ClaimMemory), past the browser's reconnect.
    private void LastStreamGone(string page, DateTimeOffset now)
    {
        if (!_streams.Any(s => s.Page == page) && _claims.TryGetValue(page, out var seen))
        {
            _claims[page] = seen with { GoneSince = now };
        }
    }

    private void PruneClaims(DateTimeOffset now)
    {
        foreach (var (page, seen) in _claims.ToList())
        {
            if (seen.GoneSince is { } gone && now - gone >= ClaimMemory)
            {
                _claims.Remove(page);
            }
        }
    }

    private bool Stop(bool clearOffer)
    {
        bool was;
        lock (_gate)
        {
            was = _snapshot is not null || _held is not null;
            _held = null;
            if (clearOffer)
            {
                _offer = null;
            }

            if (_snapshot is not null && OwnerStream() is { } owner)
            {
                Enqueue(owner, new WebEvent("close", () => "{}"));
            }

            _snapshot = null;
            _closed = true;
        }

        if (was)
        {
            Raise(null);
        }

        return was;
    }

    private void Detach(PageStream stream)
    {
        string? line = null;
        lock (_gate)
        {
            if (!_streams.Remove(stream))
            {
                return;   // replaced by a reconnect, or evicted: nothing of the page's changes here
            }

            stream.Queue.Writer.TryComplete();
            if (stream.Page == _owner && !_streams.Any(s => s.Page == stream.Page))
            {
                StartGrace(stream.Page);
            }

            LastStreamGone(stream.Page, _time.GetUtcNow());
            line = WebUiText.YouTubeDetachDebug(stream.Page, stream.Remote, stream.Page == _owner);
        }

        DiagnosticLog.Debug(Category, line);
    }

    private void StartGrace(string page)
    {
        CancelGrace();
        if (!_disposed)
        {
            _grace = _time.CreateTimer(_ => OwnerLeft(page), null, DetachGrace, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelGrace()
    {
        _grace?.Dispose();
        _grace = null;
    }

    // The grace ran out with the owner's page still gone: its video closes, its place is offered, the visible page seen most recently plays
    // next (else the longest waiting), a held request loaded only into a visible one.
    private void OwnerLeft(string page)
    {
        var outbox = new Outbox();
        lock (_gate)
        {
            if (_disposed || _owner != page || _streams.Any(s => s.Page == page))
            {
                return;
            }

            _grace?.Dispose();
            _grace = null;
            if (_snapshot is { } last && _held is null)
            {
                _offer = OfferFrom(last) ?? _offer;
                _snapshot = null;
                _closed = true;
                outbox.RaiseAny = true;
            }

            _owner = null;
            if ((MostRecentVisible() ?? _streams.OrderBy(s => s.Joined).FirstOrDefault()) is { } next)
            {
                Transfer(next, outbox, WebUiText.YouTubeOwnerLogLine(next.Remote), flash: null, attaching: false);
                if (next.Visible && _held is { } held)
                {
                    _held = null;
                    Load(next, held);
                    outbox.RaiseAny = true;
                }
            }

            outbox.Raise = _snapshot;
        }

        outbox.Flush(this);
    }

    /// <summary>
    /// The Resume offer for <paramref name="last"/> (pure): its video or playlist where it was left, by the saved videos' own rules — the
    /// place less <see cref="YouTubeLibrary.ResumeRewind"/>, from the start under <see cref="YouTubeLibrary.ResumeMinimum"/> — or none for a
    /// video seen to its end, refused, failed or held.
    /// </summary>
    public static WebVideoResume? OfferFrom(VideoSnapshot last)
    {
        ArgumentNullException.ThrowIfNull(last);
        if (last.Held is not null || last.Error is not null || last.State is VideoState.Failed or VideoState.Ended
            || (last.VideoId is null && last.PlaylistId is null) || YouTubeLibrary.Finished(last.Position, last.Duration))
        {
            return null;
        }

        double start = last.Position < YouTubeLibrary.ResumeMinimum ? 0 : last.Position - YouTubeLibrary.ResumeRewind;
        return new WebVideoResume(last.VideoId, last.PlaylistId, last.PlaylistEntry, start,
            WebUiText.ResumeLabel(last.Title, last.VideoId, last.PlaylistId, last.PlaylistEntry, start));
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static void End(Action end)
    {
        try
        {
            end();
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // the stream ended meanwhile
        }
    }

    private void Raise(VideoSnapshot? snapshot)
    {
        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Error(Category, "A video listener failed.", e);
        }
    }

    /// <summary>One page's open stream: whether it is in view, and when it last came into view (both under the lock).</summary>
    private sealed record PageStream(string Page, string Load, Channel<WebEvent> Queue, Action End, long Joined, IPAddress? Remote)
    {
        public bool Visible { get; set; } = true;

        public DateTimeOffset SeenAt { get; set; }
    }

    /// <summary>A page's last honored claim id, and since when its last stream is gone (null while one is open).</summary>
    private sealed record ClaimSeen(string Id, DateTimeOffset? GoneSince);

    /// <summary>What a call decided under the lock, done after it: the audit and Debug lines, a flash, streams to end, what to raise.</summary>
    private sealed class Outbox
    {
        public List<string> Info { get; } = [];

        public List<string> Debug { get; } = [];

        public List<Action> Ends { get; } = [];

        public string? Flash { get; set; }

        public bool RaiseNull { get; set; }

        public bool RaiseAny { get; set; }

        public VideoSnapshot? Raise { get; set; }

        public bool TookOver { get; set; }

        public void Flush(WebVideoPlayer player, bool raise = true)
        {
            foreach (var gone in Ends)
            {
                End(gone);
            }

            foreach (string line in Info)
            {
                DiagnosticLog.Info(Category, line);
            }

            foreach (string line in Debug)
            {
                DiagnosticLog.Debug(Category, line);
            }

            if (Flash is not null)
            {
                player._flash?.Invoke(Flash);
            }

            if (RaiseNull)
            {
                player.Raise(null);   // a claim carried the video: the router's listeners see its close (the resume saves its place)
            }

            if (raise && RaiseAny)
            {
                player.Raise(Raise);
            }
        }
    }
}
