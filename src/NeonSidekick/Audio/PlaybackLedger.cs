namespace NeonSidekick.Audio;

/// <summary>
/// The byte accounting behind <see cref="AudioQueuePlayback.BufferedBytes"/> (2026-10-07, sound on a Mac), kept pure so
/// it is covered on every OS. Bytes are <em>queued</em> (written, still in the managed queue), then <em>in flight</em> (in
/// one of the device buffers), then done when the queue hands that buffer back. The speech code waits for
/// <see cref="BufferedBytes"/> to reach zero, so the two rules that matter are WinMM's: a buffer goes in flight before its
/// bytes leave the queue (no dip to zero while audio is owed), and <see cref="Clear"/> makes it zero at once.
///
/// <para>Each dispatch gets a <em>ticket</em> (the clear count and the buffer's index), carried in the buffer's user data
/// to the output callback. <see cref="Complete"/> ignores a ticket from before the last <see cref="Clear"/>: AudioQueue
/// hands the flushed buffers back through the callback during <c>AudioQueueStop(immediate)</c> (the spike), and whether or
/// not a late one arrives, it can neither credit bytes a second time nor free a buffer that has since been reused. The
/// output callback takes only this object's lock, never the playback's, so a stop made under the playback's lock cannot
/// deadlock against it.</para>
/// </summary>
public sealed class PlaybackLedger
{
    private readonly object _gate = new();
    private readonly int[] _inFlight;
    private readonly long[] _tickets;
    private long _queuedBytes;
    private long _inFlightBytes;
    private long _clears;

    public PlaybackLedger(int buffers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buffers);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(buffers, 0xFFFF);
        _inFlight = new int[buffers];
        _tickets = new long[buffers];
    }

    /// <summary>Written and not yet handed back by the device: queued plus in flight.</summary>
    public long BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _queuedBytes + _inFlightBytes;
            }
        }
    }

    /// <summary>Bytes in the device's buffers.</summary>
    public long InFlightBytes
    {
        get
        {
            lock (_gate)
            {
                return _inFlightBytes;
            }
        }
    }

    /// <summary>Counts <paramref name="bytes"/> written into the managed queue.</summary>
    public void Queue(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _queuedBytes += bytes;
        }
    }

    /// <summary>A buffer the device does not hold, or -1 when all are in flight.</summary>
    public int FreeBuffer()
    {
        lock (_gate)
        {
            return Array.IndexOf(_inFlight, 0);
        }
    }

    /// <summary>
    /// Moves <paramref name="bytes"/> from the queue into buffer <paramref name="index"/> and returns its ticket for the
    /// callback. In flight first, then out of the queue, under one lock: no reader sees the bytes in neither.
    /// </summary>
    public long Dispatch(int index, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_gate)
        {
            if (_inFlight[index] != 0)
            {
                throw new InvalidOperationException("That buffer is still in flight.");
            }

            _inFlight[index] = bytes;
            _inFlightBytes += bytes;
            _queuedBytes = Math.Max(0, _queuedBytes - bytes);
            long ticket = (_clears << 16) | (uint)index;
            _tickets[index] = ticket;
            return ticket;
        }
    }

    /// <summary>
    /// The device handed back the buffer of <paramref name="ticket"/>: it is free and its bytes are done. False (and
    /// nothing changed) for a ticket from before the last <see cref="Clear"/>, or a buffer already handed back.
    /// </summary>
    public bool Complete(long ticket)
    {
        int index = (int)(ticket & 0xFFFF);
        lock (_gate)
        {
            if (index >= _inFlight.Length || _inFlight[index] == 0 || _tickets[index] != ticket)
            {
                return false;
            }

            _inFlightBytes -= _inFlight[index];
            _inFlight[index] = 0;
            return true;
        }
    }

    /// <summary>Takes back a dispatch the device refused (the enqueue failed): its bytes are dropped, the buffer free.</summary>
    public void Abandon(long ticket) => Complete(ticket);

    /// <summary>
    /// The device's buffers dropped, the managed queue kept: every buffer free, its tickets void, the queued bytes still
    /// counted. A stalled queue's way out (<see cref="AudioQueuePlayback.StallLimit"/>).
    /// </summary>
    public void DropInFlight()
    {
        lock (_gate)
        {
            _clears++;
            _inFlightBytes = 0;
            Array.Clear(_inFlight);
        }
    }

    /// <summary>Everything dropped: zero buffered, every buffer free, every outstanding ticket void.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _clears++;
            _queuedBytes = 0;
            _inFlightBytes = 0;
            Array.Clear(_inFlight);
        }
    }
}
