using System.Text.Json;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>What a page's progress report did (<see cref="WebAudioPlayback.Played"/>).</summary>
internal enum WebPlayedResult
{
    /// <summary>Taken (or below the last report for its clip, which moves nothing).</summary>
    Taken,

    /// <summary>A clip never made, or a negative offset: refused.</summary>
    Refused,

    /// <summary>A clip from before the last clear: ignored, so no byte is credited twice.</summary>
    Stale,
}

/// <summary>
/// A Web UI page's speaker as the speech path sees it (2026-11-13, the webui-voice plan): Kokoro's 24 kHz 16-bit mono PCM gathered into
/// clips, each announced to that page alone as <c>voice-clip {turn, n, bytes}</c> and fetched by the page, which plays them back to back
/// and reports how far it got. What the page has not reported played is <see cref="BufferedBytes"/>, so the speaker's tail and
/// <c>Completion</c> follow the phone as they follow the PC's device.
///
/// <para><b>Clips.</b> The first is cut at 0.4 s of audio (a quick start), the rest at 1 s, and whatever waits is cut 150 ms after the last
/// write (a sentence's end). A clip is kept until a report reaches a later one, or the speaker ends or is cleared; never dropped by the GET
/// that sent it, so a failed fetch's retry finds it again.</para>
///
/// <para><b>Never stuck.</b> The page gone (its stream past the grace, a reload, <c>gone</c>) counts everything played and drops later
/// audio. 5 s with clips owed and no report (an interrupted context), or more than a minute of audio no GET has asked for, raises
/// <see cref="GaveUp"/>: the owner stops the speaker. Both rules pause while the page listens (<see cref="Hold"/>): the page fetches the
/// clips but schedules none until its listen ends.</para>
///
/// <para><b>Never throws</b> from the <see cref="IAudioPlayback"/> members: a throwing playback would mark the shared synthesizer unavailable
/// and silence the PC too. Its own lock covers the pending buffer, the clips and the play head; events and <see cref="GaveUp"/> are raised
/// outside it.</para>
/// </summary>
internal sealed class WebAudioPlayback : IAudioPlayback, IPageAudio
{
    /// <summary>The first clip's size: 0.4 s of 24 kHz 16-bit mono.</summary>
    public const int FirstClipBytes = 19_200;

    /// <summary>Every later clip's size: 1 s.</summary>
    public const int ClipBytes = 48_000;

    /// <summary>How long after the last write what waits is cut into a clip.</summary>
    public static readonly TimeSpan FlushIdle = TimeSpan.FromMilliseconds(150);

    /// <summary>How long clips may be owed with no report before the speech stops.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(5);

    /// <summary>The most audio no GET has asked for: a minute.</summary>
    public const long MaxUnfetchedBytes = 60L * 48_000;

    private const string Category = "WebUi";

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Action<WebEvent> _publish;
    private readonly List<KeptClip> _clips = [];   // kept clips, oldest first
    private readonly ITimer _flush;
    private readonly ITimer _silent;
    private bool _silentArmed;     // the silent timer runs (under _gate): a new clip leaves it running, so it counts from the last report
    private byte[] _pending = new byte[ClipBytes];
    private int _pendingCount;
    private long _written;         // every byte taken by Write
    private long _clipped;         // bytes already cut into clips (made), including dropped ones
    private long _played;          // bytes the page has reported played, or counted so
    private long _nextClip;        // the next clip's number
    private long _firstLive;       // clips below this number came before the last clear
    private long _lastReport = -1; // the clip of the last report taken
    private long _lastReportBytes; // that report's place in its clip
    private long _unfetched;
    private bool _started;
    private bool _gone;
    private bool _held;
    private bool _gaveUp;
    private bool _disposed;

    private sealed class KeptClip(long n, long start, byte[] bytes)
    {
        public long N { get; } = n;

        public long Start { get; } = start;   // its first byte's place in the speaker's audio

        public byte[] Bytes { get; } = bytes;

        public bool Fetched { get; set; }
    }

    public WebAudioPlayback(string page, string turn, TimeProvider time, Action<WebEvent> publish)
    {
        ArgumentException.ThrowIfNullOrEmpty(page);
        ArgumentException.ThrowIfNullOrEmpty(turn);
        Page = page;
        Turn = turn;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _flush = _time.CreateTimer(_ => OnTimer(Flush), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _silent = _time.CreateTimer(_ => OnTimer(OnSilent), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The page the audio goes to.</summary>
    public string Page { get; }

    /// <summary>This speaker's id on the page: fresh per speaker, so one page can't name another's clips and a late stop names its own turn.</summary>
    public string Turn { get; }

    /// <summary>
    /// Raised once when the page stops taking the audio (5 s owed with no report, or the unfetched cap) with what happened, on a timer or
    /// the writing thread; the owner stops the speaker. Must not block.
    /// </summary>
    public Action<string>? GaveUp { get; set; }

    public PcmFormat Format => PcmFormat.Kokoro;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    public long BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _gone ? 0 : Math.Max(0, _written - _played);
            }
        }
    }

    /// <summary>Whether the page owes playback of a made clip (or audio waits to be cut): the page's ■ and wake lock follow it.</summary>
    public bool Owed => BufferedBytes > 0;

    /// <summary>The clips made so far, for the page's snapshot after a reconnect: it fetches those it hasn't from its last one on.</summary>
    public WebSpeaking Snapshot()
    {
        lock (_gate)
        {
            return new WebSpeaking(Turn, _nextClip);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
        }
    }

    public void Write(byte[] pcm, int count)
    {
        if (pcm is null || count <= 0)
        {
            return;
        }

        List<WebEvent>? events = null;
        string? gaveUp = null;
        lock (_gate)
        {
            if (!_started || _gone || _disposed)
            {
                return;
            }

            count = Math.Min(count, pcm.Length);
            _written += count;
            int offset = 0;
            while (offset < count)
            {
                int room = CutAt() - _pendingCount;
                int take = Math.Min(room, count - offset);
                EnsurePending(_pendingCount + take);
                Buffer.BlockCopy(pcm, offset, _pending, _pendingCount, take);
                _pendingCount += take;
                offset += take;
                if (_pendingCount >= CutAt())
                {
                    (events ??= []).Add(CutClip());
                }
            }

            gaveUp = CheckCap();
            _flush.Change(FlushIdle, Timeout.InfiniteTimeSpan);
        }

        Raise(events, gaveUp);
    }

    /// <summary>Drops what waits and every kept clip, tells the page to stop this turn (<c>voice-stop</c>), and reads 0 at once.</summary>
    public void ClearBuffer()
    {
        WebEvent? stop;
        lock (_gate)
        {
            stop = ClearLocked();
        }

        Raise(stop is null ? null : [stop], null);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            _flush.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The page's progress: clip <paramref name="clip"/> played to <paramref name="offset"/> samples (24 kHz frames), held to the clip's
    /// length. Earlier clips count played, and are dropped once a later one is reported. A report below the last moves nothing.
    /// </summary>
    public WebPlayedResult Played(long clip, long offset)
    {
        lock (_gate)
        {
            if (clip < _firstLive)
            {
                return WebPlayedResult.Stale;
            }

            if (offset < 0 || clip >= _nextClip || _disposed)
            {
                return WebPlayedResult.Refused;
            }

            var found = _clips.Find(c => c.N == clip);
            long length = found?.Bytes.Length ?? 0;
            // Clamped before doubling (2026-11-14, the review's #19): a huge offset doubled would wrap negative and read as nothing played.
            long bytes = offset >= (length + 1) / 2 ? length : offset * 2;
            if (clip < _lastReport || (clip == _lastReport && bytes <= _lastReportBytes))
            {
                ArmSilentLocked();
                return WebPlayedResult.Taken;   // a late report: nothing moves back
            }

            _lastReport = clip;
            _lastReportBytes = bytes;
            if (found is not null)
            {
                _played = Math.Max(_played, found.Start + bytes);
            }

            foreach (var passed in _clips)
            {
                if (passed.N < clip && !passed.Fetched)
                {
                    _unfetched -= passed.Bytes.Length;   // a clip the page skipped no longer counts toward the cap
                }
            }

            _clips.RemoveAll(c => c.N < clip);
            ArmSilentLocked();
            return WebPlayedResult.Taken;
        }
    }

    /// <summary>Clip <paramref name="n"/>'s bytes, kept until a report passes it; null when it isn't (another turn's, dropped, never made).</summary>
    public byte[]? Clip(long n)
    {
        lock (_gate)
        {
            var found = _clips.Find(c => c.N == n);
            if (found is null)
            {
                return null;
            }

            if (!found.Fetched)
            {
                found.Fetched = true;
                _unfetched -= found.Bytes.Length;
            }

            return found.Bytes;
        }
    }

    /// <summary>The page went (past the grace, a reload, its own <c>gone</c>): everything counts played, later audio is dropped.</summary>
    public void Gone()
    {
        lock (_gate)
        {
            if (_gone)
            {
                return;
            }

            _gone = true;
            _played = _written;
            _clips.Clear();
            _unfetched = 0;
            _pendingCount = 0;
            _flush.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            DisarmSilentLocked();
        }

        DiagnosticLog.Debug(Category, "Web UI voice on page " + Short(Page) + ": the page went; its audio counts heard.");
    }

    /// <summary>The page's listen running (<paramref name="on"/>) or ended: the silent rule and the cap pause meanwhile.</summary>
    public void Hold(bool on)
    {
        lock (_gate)
        {
            _held = on;
            if (on)
            {
                DisarmSilentLocked();
            }
            else
            {
                ArmSilentLocked();
            }
        }
    }

    /// <summary>Idempotent: tells the page to stop this turn when clips are still owed, and stops every timer.</summary>
    public void Dispose()
    {
        WebEvent? stop = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_gone && _written - _played > 0)
            {
                stop = ClearLocked();
            }

            _disposed = true;
            _started = false;
        }

        _flush.Dispose();
        _silent.Dispose();
        Raise(stop is null ? null : [stop], null);
    }

    // ── Under the lock ──────────────────────────────────────────────────────

    private int CutAt() => _nextClip == _firstLive ? FirstClipBytes : ClipBytes;

    private void EnsurePending(int size)
    {
        if (_pending.Length < size)
        {
            Array.Resize(ref _pending, Math.Max(size, _pending.Length * 2));
        }
    }

    private WebEvent CutClip()
    {
        var bytes = _pending.AsSpan(0, _pendingCount).ToArray();
        var clip = new KeptClip(_nextClip++, _clipped, bytes);
        _clipped += bytes.Length;
        _pendingCount = 0;
        _clips.Add(clip);
        _unfetched += bytes.Length;
        if (!_silentArmed)
        {
            // Armed only when it isn't (2026-11-14, the review's #6): a clip made while the page reports nothing must not push the
            // deadline back, or a steady reply would never be found silent. A report (Played) or a listen's end re-arms it.
            ArmSilentLocked();
        }

        var announced = new WebVoiceClip(Turn, clip.N, bytes.Length);
        return new WebEvent("voice-clip", () => JsonSerializer.Serialize(announced, WebUiJsonContext.Default.WebVoiceClip)) { Page = Page };
    }

    private WebEvent? ClearLocked()
    {
        bool owed = _written - _played > 0 || _clips.Count > 0;
        _pendingCount = 0;
        _clips.Clear();
        _unfetched = 0;
        _played = _written;
        _clipped = _written;
        _firstLive = _nextClip;
        _flush.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        DisarmSilentLocked();
        if (!owed || _gone)
        {
            return null;
        }

        var stopped = new WebVoiceStopped(Turn);
        return new WebEvent("voice-stop", () => JsonSerializer.Serialize(stopped, WebUiJsonContext.Default.WebVoiceStopped)) { Page = Page };
    }

    private void ArmSilentLocked()
    {
        if (_held || _gone || _disposed || _gaveUp || _clips.Count == 0 || _played >= _clipped)
        {
            DisarmSilentLocked();
            return;
        }

        _silent.Change(SilentAfter, Timeout.InfiniteTimeSpan);
        _silentArmed = true;
    }

    /// <summary>Stops the silent timer and says so, so the next clip arms it again (every disarm goes through here).</summary>
    private void DisarmSilentLocked()
    {
        _silent.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _silentArmed = false;
    }

    private string? CheckCap()
    {
        if (_held || _gaveUp || _unfetched <= MaxUnfetchedBytes)
        {
            return null;
        }

        _gaveUp = true;
        return "more than a minute of audio was never fetched";
    }

    // ── Timers and raising ──────────────────────────────────────────────────

    private void Flush()
    {
        List<WebEvent>? events = null;
        lock (_gate)
        {
            if (_disposed || _gone || _pendingCount == 0)
            {
                return;
            }

            events = [CutClip()];
        }

        Raise(events, null);
    }

    private void OnSilent()
    {
        lock (_gate)
        {
            _silentArmed = false;   // it fired: a later clip arms it again, whatever this fire decides
            if (_held || _gone || _disposed || _gaveUp || _played >= _clipped)
            {
                return;
            }

            _gaveUp = true;
        }

        Raise(null, "no progress for 5 s with audio owed");
    }

    private void OnTimer(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Error(Category, "A Web UI voice timer failed: " + e.Message, e);
        }
    }

    private void Raise(List<WebEvent>? events, string? gaveUp)
    {
        if (events is not null)
        {
            foreach (var item in events)
            {
                try
                {
                    _publish(item);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    DiagnosticLog.Error(Category, "A Web UI voice event failed: " + e.Message, e);
                }
            }
        }

        if (gaveUp is not null)
        {
            DiagnosticLog.Info(Category, "Web UI voice on page " + Short(Page) + " stopped: " + gaveUp + ".");
            try
            {
                GaveUp?.Invoke(gaveUp);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DiagnosticLog.Error(Category, "A Web UI voice stop failed: " + e.Message, e);
            }
        }
    }

    private static string Short(string page) => page.Length <= 6 ? page : page[..6];
}
