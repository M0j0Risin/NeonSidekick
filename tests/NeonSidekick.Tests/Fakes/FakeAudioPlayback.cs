using NeonSidekick.Audio;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// An <see cref="IAudioPlayback"/> that records what it is told. Audio "plays" instantly:
/// <see cref="BufferedBytes"/> is zero unless <see cref="HoldBytes"/> is set, in which case
/// everything written stays buffered until <see cref="ClearBuffer"/>, <see cref="Stop"/> or
/// <see cref="Release"/>.
/// </summary>
public sealed class FakeAudioPlayback : IAudioPlayback
{
    private readonly object _gate = new();
    private long _buffered;

    public PcmFormat Format { get; init; } = PcmFormat.Kokoro;

    public bool HoldBytes { get; set; }

    public bool ThrowOnStart { get; set; }

    public List<byte[]> Writes { get; } = new();

    public long WrittenBytes { get; private set; }

    public int Started { get; private set; }

    public int Stopped { get; private set; }

    public int Cleared { get; private set; }

    public bool Disposed { get; private set; }

    public bool IsPlaying { get; private set; }

    public long BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return HoldBytes ? _buffered : 0;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("waveOutOpen failed with MMSYSERR 6.");
            }

            if (IsPlaying)
            {
                return;
            }

            Started++;
            IsPlaying = true;
        }
    }

    public void Write(byte[] pcm, int count)
    {
        lock (_gate)
        {
            if (!IsPlaying || count <= 0)
            {
                return;
            }

            var copy = new byte[count];
            Buffer.BlockCopy(pcm, 0, copy, 0, count);
            Writes.Add(copy);
            WrittenBytes += count;
            _buffered += count;
        }
    }

    public void ClearBuffer()
    {
        lock (_gate)
        {
            Cleared++;
            _buffered = 0;
        }
    }

    /// <summary>Lets held bytes "finish playing" without counting as an interruption.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _buffered = 0;
        }
    }

    /// <summary>Lets the next <paramref name="bytes"/> held bytes "finish playing": the play head moves that far (2026-09-17, the /read position).</summary>
    public void Release(long bytes)
    {
        lock (_gate)
        {
            _buffered = Math.Max(0, _buffered - bytes);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsPlaying)
            {
                return;
            }

            Stopped++;
            IsPlaying = false;
            _buffered = 0;
        }
    }

    public void Dispose()
    {
        Stop();
        Disposed = true;
    }
}
