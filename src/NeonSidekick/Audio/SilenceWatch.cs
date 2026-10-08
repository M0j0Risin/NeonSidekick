namespace NeonSidekick.Audio;

/// <summary>
/// Spots a microphone that sends nothing but exact zeros (2026-10-07, sound on a Mac): what macOS delivers when the
/// terminal's microphone permission is refused, while its question is still on screen, and from a MacBook's own
/// microphone with the lid closed. A real microphone in a quiet room never does this — its noise floor moves the low
/// bits — so <see cref="Window"/> of unbroken zero samples is a verdict, not a guess about loudness. Pure: the capture
/// feeds it each buffer and reads the status only when it trips (<see cref="MicrophoneAccess.SilenceVerdict"/>).
/// </summary>
public sealed class SilenceWatch
{
    /// <summary>How long the zeros must run. A second, the brief's floor; well past any start-up ramp seen on the M4.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly long _threshold;
    private long _zeroBytes;
    private bool _tripped;

    public SilenceWatch(PcmFormat format) : this(format, Window)
    {
    }

    public SilenceWatch(PcmFormat format, TimeSpan window)
    {
        _threshold = Math.Max(format.BlockAlign, format.BytesFor((int)window.TotalMilliseconds));
    }

    /// <summary>Whether it has tripped since the last <see cref="Reset"/>.</summary>
    public bool Tripped => _tripped;

    /// <summary>Whether any non-zero byte has come since the last <see cref="Reset"/>: the microphone is live.</summary>
    public bool HeardSound { get; private set; }

    /// <summary>
    /// Takes one buffer. True exactly once, on the buffer that completes the window of zeros; a single non-zero byte
    /// starts the count over (any sample width: a zero sample is zero bytes).
    /// </summary>
    public bool Feed(ReadOnlySpan<byte> pcm)
    {
        int lastNonZero = pcm.LastIndexOfAnyExcept((byte)0);
        if (lastNonZero >= 0)
        {
            HeardSound = true;
        }

        if (_tripped)
        {
            return false;
        }

        if (lastNonZero >= 0)
        {
            _zeroBytes = pcm.Length - lastNonZero - 1;
        }
        else
        {
            _zeroBytes += pcm.Length;
        }

        if (_zeroBytes >= _threshold)
        {
            _tripped = true;
            return true;
        }

        return false;
    }

    /// <summary>Starts over: a new recording, or the permission question still open when it tripped.</summary>
    public void Reset()
    {
        _zeroBytes = 0;
        _tripped = false;
        HeardSound = false;
    }
}
