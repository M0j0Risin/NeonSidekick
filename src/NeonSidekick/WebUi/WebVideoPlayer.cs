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
    /// <paramref name="claim"/> its Play here. <paramref name="end"/> ends the host's stream; the player calls it only for a stream it evicts
    /// or a reconnect replaces. The attachment's <c>hello</c> is written first; disposing it is the detach.
    /// </summary>
    WebVideoAttachment Attach(string page, string load, bool claim, IPAddress? remote, Action end);

    /// <summary>A page's report: <see cref="WebOutcome.Ok"/>, or <see cref="WebOutcome.Conflict"/> from a page that is not the player.</summary>
    WebOutcome Report(string page, string message, IPAddress? remote);
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
/// The Web UI's YouTube tab as a player (2026-10-16, <c>YouTube open links in</c> <c>webui-window</c>): an <see cref="IVideoPlayer"/> the
/// router (<see cref="RoutedVideoPlayer"/>) plays on, whose video runs in YouTube's player on a phone's or a browser's YouTube tab. Portable;
/// one lock, <see cref="Changed"/> and <see cref="TookOver"/> raised outside it; its time is the screen's <see cref="TimeProvider"/>.
///
/// <para><b>Pages, not streams.</b> A page is a browser tab, its id kept in <c>sessionStorage</c> and sent with its stream and its reports. A
/// stream with the same id and the same <c>load</c> nonce is a reconnect, which replaces the old one quietly (a half-open socket); the same id
/// with a new <c>load</c> and no stream open is a reload; and with another stream still open, a duplicated tab, given a fresh id in its hello.
/// The first page to attach plays (the owner); one attaching while another plays waits (its Play here claims, the old owner told <c>taken</c>,
/// both streams left open). The owner leaving is read after <see cref="DetachGrace"/>, so a reconnect is not a close: its video's snapshot goes
/// null, as a window's close, and its last place becomes the Resume offer; the longest-waiting page is promoted with an <c>owner</c> event. A
/// reload stops the video too: the owner's first report after it, <c>page</c>, is read as the close, and the offer is sent back to it.</para>
///
/// <para><b>A play</b> goes to the owner as a <c>load</c>, or is held (with no owner, or one in its grace) until a page attaches: the snapshot
/// then reads <see cref="VideoSnapshot.Held"/>, whether a page will see the nudge, and expires after <see cref="YouTubeText.HoldFor"/> (on the
/// screen's tick). Reports from the owner alone, applied as the windows apply theirs, <see cref="VideoLoadSettle"/> on each.</para>
/// </summary>
public sealed class WebVideoPlayer : IVideoPlayer, IVideoTakeover, IWebVideo, IDisposable
{
    /// <summary>How long a page's stream may be gone before its leaving counts: a reconnect within it changes nothing.</summary>
    public static readonly TimeSpan DetachGrace = TimeSpan.FromSeconds(5);

    /// <summary>The most YouTube streams at once; one more evicts the oldest waiting page's, never the owner's.</summary>
    public const int MaxStreams = 4;

    /// <summary>The most events a page may have waiting before it is dropped (its reconnect comes back as the same page).</summary>
    public const int Capacity = 256;

    /// <summary>The longest report a page may post (the host refuses a longer one with a 400).</summary>
    public const int MaxReportChars = 4096;

    /// <summary>How long a page waits for YouTube's script (<see cref="IWebVideo.ApiWaitMs"/>) by default.</summary>
    public const int DefaultApiWaitMs = 10_000;

    private const string Category = "WebUi";

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<bool> _nudge;
    private readonly Action<string>? _flash;
    private readonly List<PageStream> _streams = [];
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

    // Whether the owner's reports count: only after a load (or its own Resume), never after a close, so a stopped player's late report
    // never brings a closed video back.
    private bool _closed = true;

    /// <param name="time">The screen's clock: the grace's timer, the hold's age.</param>
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

    /// <summary>Whether a video waits for a YouTube tab with none attached: the pages' banner and jump (<c>WebState.Watch</c>).</summary>
    public bool Waiting
    {
        get
        {
            lock (_gate)
            {
                return _held is not null && _streams.Count == 0;
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

    /// <summary>Whether <paramref name="text"/> has the shape of a page id or a load nonce: 16 to 64 base64url characters.</summary>
    public static bool IsId(string? text) =>
        text is { Length: >= 16 and <= 64 } && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>A fresh page id: 16 random bytes, base64url.</summary>
    public static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── IVideoPlayer ──────────────────────────────────────────────────────

    public void Play(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool nudged = _nudge();   // outside the lock: it publishes on the Chat stream
        VideoSnapshot snapshot;
        lock (_gate)
        {
            _offer = null;
            if (OwnerStream() is { } owner)
            {
                _held = null;
                snapshot = Load(owner, request);
            }
            else
            {
                _held = request;
                _heldSince = _time.GetUtcNow();
                snapshot = _snapshot = VideoSnapshot.Opening(request) with { Version = ++_version, Place = VideoPlace.WebTab, Held = new VideoHold(nudged, _heldSince) };
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
    /// Everything let go (the screen's tick once the setting is off or the Web UI isn't serving, and the app's exit): the owner told to close,
    /// the held request and the offer dropped, the snapshot null.
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

    public WebVideoAttachment Attach(string page, string load, bool claim, IPAddress? remote, Action end)
    {
        ArgumentNullException.ThrowIfNull(end);
        var ends = new List<Action>();
        var lines = new List<string>();
        string? flash = null;
        VideoSnapshot? raise = null;
        bool raiseAny = false;
        PageStream stream;
        WebVideoHello hello;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var same = _streams.Where(s => s.Page == page).ToList();
            if (same.FirstOrDefault(s => s.Load == load) is { } reconnect)
            {
                _streams.Remove(reconnect);   // a reconnect: the old stream (a half-open socket) goes quietly
                reconnect.Queue.Writer.TryComplete();
                ends.Add(reconnect.End);
            }
            else if (same.Count > 0)
            {
                page = NewId();   // a duplicated tab: sessionStorage copied, the other page still open
            }

            if (_owner == page)
            {
                CancelGrace();
            }

            // At the cap the oldest waiting page goes, never the owner.
            if (_streams.Count >= MaxStreams && _streams.Where(s => s.Page != _owner).OrderBy(s => s.Joined).FirstOrDefault() is { } evicted)
            {
                _streams.Remove(evicted);
                evicted.Queue.Writer.TryComplete();
                ends.Add(evicted.End);
            }

            var queue = Channel.CreateBounded<WebEvent>(new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            stream = new PageStream(page, load, queue, end, ++_joined, remote);
            _streams.Add(stream);

            if (_owner is null)
            {
                _owner = page;
                lines.Add(WebUiText.YouTubeOwnerLogLine(remote));
            }
            else if (claim && _owner != page)
            {
                if (OwnerStream() is { } old)
                {
                    Enqueue(old, new WebEvent("taken", () => "{}"));
                }

                _owner = page;
                CancelGrace();
                lines.Add(WebUiText.YouTubeClaimLogLine(remote));
                flash = WebUiText.YouTubeClaimFlash;
            }

            bool owner = _owner == page;
            hello = new WebVideoHello(page, WebUiText.VideoWords, owner, _offer, ApiWaitMs);
            if (owner && _held is { } held)
            {
                _held = null;
                raise = Load(stream, held);
                raiseAny = true;
            }
        }

        foreach (var gone in ends)
        {
            End(gone);
        }

        foreach (string line in lines)
        {
            DiagnosticLog.Info(Category, line);
        }

        if (flash is not null)
        {
            _flash?.Invoke(flash);
        }

        if (raiseAny)
        {
            Raise(raise);
        }

        return new WebVideoAttachment(hello, stream.Queue.Reader, a => Detach(stream));
    }

    public WebOutcome Report(string page, string message, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(page);
        VideoSnapshot? raise = null;
        bool raiseAny = false;
        bool tookOver = false;
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
                    // The owner's page loaded again (a reload, the user's or after an upgrade): its player went with it.
                    _closed = true;
                    if (_snapshot is { } playing && _held is null)
                    {
                        _offer = OfferFrom(playing);
                        _snapshot = null;
                        raiseAny = true;
                        if (_offer is { } offer && OwnerStream() is { } owner)
                        {
                            Enqueue(owner, new WebEvent("resume", () => JsonSerializer.Serialize(offer, WebUiJsonContext.Default.WebVideoResume)));
                        }
                    }

                    break;
                case VideoPageEvent.Resumed:
                    _offer = null;
                    _held = null;
                    _closed = false;
                    _settle.Loaded();   // the page loads the video itself
                    tookOver = true;
                    break;
                case VideoPageEvent.Failed:
                case VideoPageEvent.State:
                case VideoPageEvent.Error:
                    if (_closed || !_settle.Accept(kind, next))
                    {
                        break;   // the "paused, no duration" a load leaves behind
                    }

                    _version = next.Version;
                    raise = _snapshot = next with { Place = VideoPlace.WebTab, Held = null };
                    raiseAny = true;
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

        if (tookOver)
        {
            DiagnosticLog.Info(Category, WebUiText.YouTubeResumeLogLine(remote));
            _flash?.Invoke(WebUiText.YouTubeResumeFlash);
            TookOver?.Invoke();
        }

        if (raiseAny)
        {
            Raise(raise);
        }

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

    // A load to the owner: the settle armed, the snapshot opening on the tab.
    private VideoSnapshot Load(PageStream owner, VideoRequest request)
    {
        _settle.Loaded();
        _closed = false;
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

        _ = Task.Run(() => End(stream.End));
        return false;
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
        }
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

    // The grace ran out with the owner's page still gone: its video closes, its place is offered, the longest-waiting page plays next.
    private void OwnerLeft(string page)
    {
        bool raise = false;
        string? promoted = null;
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
                raise = true;
            }

            _owner = null;
            if (_streams.OrderBy(s => s.Joined).FirstOrDefault() is { } next)
            {
                _owner = next.Page;
                var owner = new WebVideoOwner(true, _offer);
                Enqueue(next, new WebEvent("owner", () => JsonSerializer.Serialize(owner, WebUiJsonContext.Default.WebVideoOwner)));
                promoted = WebUiText.YouTubeOwnerLogLine(next.Remote);
                if (_held is { } held)
                {
                    _held = null;
                    Load(next, held);
                    raise = true;
                }
            }
        }

        if (promoted is not null)
        {
            DiagnosticLog.Info(Category, promoted);
        }

        if (raise)
        {
            Raise(Snapshot);
        }
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

    /// <summary>One page's open stream.</summary>
    private sealed record PageStream(string Page, string Load, Channel<WebEvent> Queue, Action End, long Joined, IPAddress? Remote);
}
