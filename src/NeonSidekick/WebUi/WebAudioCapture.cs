using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>What a page's chunk did (<see cref="WebAudioCapture.Deliver"/>).</summary>
internal enum WebChunkResult
{
    /// <summary>Taken, in order.</summary>
    Delivered,

    /// <summary>A chunk already taken (a retry whose first answer was lost): answered ok, not delivered twice.</summary>
    Repeat,

    /// <summary>A chunk past the next expected: refused, so the page resends what's missing.</summary>
    OutOfOrder,

    /// <summary>The listen has stopped: the page stops recording.</summary>
    Ended,
}

/// <summary>
/// A Web UI page's microphone as the voice pipeline sees it (2026-11-13, the webui-voice plan): the page posts 16 kHz mono 16-bit PCM in
/// numbered quarter-second chunks, and <see cref="Deliver"/> raises them, in order, from the posting Kestrel thread. No device: <see cref="Start"/>
/// only opens the gate, and chunks that came before it (the page starts sending as soon as its start is answered, the pipeline subscribes on the
/// pool) wait and go first.
///
/// <para><b>The PC's cadence.</b> Each chunk is raised as 50 ms buffers, the PC microphone's size: Silero analyzes the utterance once per
/// 300 ms of new audio, so whole chunks would hear the end of speech late. <b>No buffer after <see cref="Stop"/></b>: delivery and the stop
/// take one lock. <b>A stall:</b> 3 s with no chunk once one has come raises <see cref="Stalled"/> once, from a timer thread; the owner then
/// finishes the listen, so what came is transcribed.</para>
/// </summary>
internal sealed class WebAudioCapture : IAudioCapture
{
    /// <summary>The PC microphone's buffer: 50 ms of 16 kHz 16-bit mono.</summary>
    public const int SliceBytes = 1600;

    /// <summary>The most audio one chunk may hold: half a second.</summary>
    public const int MaxChunkBytes = 16_000;

    /// <summary>How long without a chunk, once one has come, is a stall.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(3);

    /// <summary>The most chunks held before <see cref="Start"/>: 10 s of audio.</summary>
    private const int MaxEarly = 40;

    private const string Category = "WebUi";

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _stallAfter;
    private readonly List<byte[]> _early = [];
    private ITimer? _stall;
    private long _next;
    private bool _started;
    private bool _stopped;
    private bool _stallRaised;
    private long _lastChunk;

    public WebAudioCapture(TimeProvider? time = null, TimeSpan? stallAfter = null)
    {
        _time = time ?? TimeProvider.System;
        _stallAfter = stallAfter ?? StallAfter;
    }

    public PcmFormat Format => PcmFormat.Whisper;

    public bool IsCapturing
    {
        get
        {
            lock (_gate)
            {
                return _started && !_stopped;
            }
        }
    }

    public event Action<byte[], int>? DataAvailable;

    /// <summary>3 s with no chunk once one has come. Raised once, from a timer thread.</summary>
    public event Action? Stalled;

    /// <summary>Raised once when the capture stops (the pipeline's end, a cancel, a dispose): the owner tells the page to stop recording.</summary>
    public event Action? Stopped;

    /// <summary>The chunks taken so far.</summary>
    public long Delivered
    {
        get
        {
            lock (_gate)
            {
                return _next;
            }
        }
    }

    /// <summary>
    /// Chunk <paramref name="seq"/> of the page's audio, already checked (even length, at most <see cref="MaxChunkBytes"/>). The next expected
    /// one is raised (or held until <see cref="Start"/>); an earlier one is a repeat; a later one is refused; after the stop, ended.
    /// </summary>
    public WebChunkResult Deliver(long seq, byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        lock (_gate)
        {
            if (_stopped)
            {
                return WebChunkResult.Ended;
            }

            if (seq < _next)
            {
                return WebChunkResult.Repeat;
            }

            if (seq > _next)
            {
                return WebChunkResult.OutOfOrder;
            }

            if (!_started)
            {
                if (_early.Count >= MaxEarly)
                {
                    return WebChunkResult.OutOfOrder;   // the pipeline never started: the page retries, then gives up
                }

                _early.Add(pcm);
                _next++;
                return WebChunkResult.Delivered;
            }

            _next++;
            Raise(pcm);
            ArmStall();
            return WebChunkResult.Delivered;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped)
            {
                return;
            }

            _started = true;
            foreach (var chunk in _early)
            {
                Raise(chunk);
            }

            if (_early.Count > 0)
            {
                ArmStall();
            }

            _early.Clear();
        }
    }

    public void Stop()
    {
        bool first;
        lock (_gate)
        {
            first = !_stopped;
            _stopped = true;
            _early.Clear();
            _stall?.Dispose();
            _stall = null;
        }

        if (first)
        {
            try
            {
                Stopped?.Invoke();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DiagnosticLog.Error(Category, "A Web UI listen's stop handler failed: " + e.Message, e);
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>Raises <paramref name="pcm"/> as 50 ms buffers. Under the lock.</summary>
    private void Raise(byte[] pcm)
    {
        if (DataAvailable is not { } handler)
        {
            return;
        }

        for (int offset = 0; offset < pcm.Length; offset += SliceBytes)
        {
            int count = Math.Min(SliceBytes, pcm.Length - offset);
            var slice = new byte[count];
            Buffer.BlockCopy(pcm, offset, slice, 0, count);
            handler(slice, count);
        }
    }

    /// <summary>(Re)starts the stall timer. Under the lock.</summary>
    private void ArmStall()
    {
        if (_stallRaised)
        {
            return;
        }

        _lastChunk = _time.GetTimestamp();
        if (_stall is null)
        {
            _stall = _time.CreateTimer(_ => OnStall(), null, _stallAfter, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _stall.Change(_stallAfter, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnStall()
    {
        try
        {
            lock (_gate)
            {
                if (_stopped || _stallRaised)
                {
                    return;
                }

                var since = _time.GetElapsedTime(_lastChunk);
                if (since < _stallAfter)
                {
                    _stall?.Change(_stallAfter - since, Timeout.InfiniteTimeSpan);   // a chunk came as the timer fired
                    return;
                }

                _stallRaised = true;
            }

            Stalled?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Error(Category, "A Web UI listen's stall check failed: " + e.Message, e);
        }
    }
}
