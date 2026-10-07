using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Audio.AudioQueueNative;

namespace NeonSidekick.Audio;

/// <summary>
/// Speaker output over AudioToolbox's AudioQueue (2026-10-07, Stage 2 of the Mac port): <see cref="WinMmAudioPlayback"/>'s
/// shape on macOS. Callers push PCM into a managed queue; a pump thread copies it into four 100 ms queue buffers as they
/// come free and enqueues them; the queue's output callback (on AudioQueue's own thread: the queue has no run loop) hands
/// each one back. <see cref="PlaybackLedger"/> does the counting, so <see cref="BufferedBytes"/> is zero exactly when every
/// written byte has been handed back — or dropped by <see cref="ClearBuffer"/>.
///
/// <para><b>Locks.</b> The callback takes only the ledger's lock, never <c>_gate</c>. That is what lets
/// <see cref="ClearBuffer"/> hold <c>_gate</c> (keeping the pump out) while <c>AudioQueueStop(immediate)</c> runs the
/// callback for every flushed buffer before it returns. <see cref="Stop"/> joins the pump outside the lock, then stops and
/// disposes the queue synchronously; a synchronous dispose calls no callback, and the <see cref="GCHandle"/> the callback
/// resolves is freed only after it.</para>
///
/// <para><b>Idle.</b> A reply that ends normally never calls <see cref="Stop"/>: the speech session keeps one playback
/// across replies. A running queue with nothing enqueued keeps the output device awake (AirPods included), so after
/// <see cref="IdleStop"/> with nothing buffered the pump stops it, and the next enqueue starts it again. It waits that long
/// because the last buffer's callback comes while its tail is still in the hardware. A stop, never <c>AudioQueuePause</c>:
/// a paused queue started again threw new buffers away unplayed. When it starts again it
/// first compares the system's default output with the one it opened on, and rebuilds the queue if they differ, so the
/// next reply plays on headphones connected in between.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class AudioQueuePlayback : IAudioPlayback
{
    private const string Category = "AudioQueuePlayback";

    /// <summary>Buffers in flight, as WinMM's: four at 100 ms covers the gaps between synthesized sentences.</summary>
    private const int BufferCount = 4;

    /// <summary>How long nothing must be buffered before the pump stops the queue.</summary>
    public static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long buffers may sit in the queue with none handed back before the pump gives up on them (2026-10-07: once, in a
    /// forced run of the whole suite, a queue kept its last four buffers and <see cref="BufferedBytes"/> never reached
    /// zero; not reproduced since). The in-flight bytes are dropped, the managed queue kept and the queue restarted, so a
    /// reply's wait ends instead of running into the speech code's drain timeout. Buffers are 100 ms; a Bluetooth output
    /// waking up has taken under a second.
    /// </summary>
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(3);

    private readonly int _bufferBytes;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private readonly PlaybackLedger _ledger = new(BufferCount);

    private nint _queue;
    private nint[] _buffers = [];
    private uint _device;
    private GCHandle _self;
    private bool _queueRunning;
    private long _idleSince;
    private long _lastProgress;
    private int _headOffset;
    private AutoResetEvent? _wake;
    private Thread? _pump;
    private volatile bool _running;
    private bool _disposed;

    public AudioQueuePlayback(PcmFormat format, int bufferMilliseconds = 100)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0 || format.BitsPerSample is not (8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "AudioQueue playback needs a positive rate, 8 or 16 bits and at least one channel.");
        }

        if (bufferMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));
        }

        Format = format;
        _bufferBytes = Math.Max(format.BlockAlign, format.BytesFor(bufferMilliseconds));
    }

    public PcmFormat Format { get; }

    public bool IsPlaying => _running;

    public long BufferedBytes => _ledger.BufferedBytes;

    /// <summary>1 when Core Audio names a default output device, 0 when it names none (a Mac runner with no audio).</summary>
    public static int OutputDeviceCount() => DefaultDevice(DefaultOutputDevice) == UnknownObject ? 0 : 1;

    /// <summary>
    /// Creates and disposes an output queue at <paramref name="format"/> without starting it: the <c>--smoke</c> check that
    /// the published binary's imports and callback pointer bind. Returns the OSStatus (0 = created).
    /// </summary>
    public static int ProbeDefaultDevice(PcmFormat format)
    {
        var description = StreamDescription.Pcm(format);
        nint queue;
        int status = AudioQueueNewOutput(&description, &OnOutput, 0, 0, 0, 0, &queue);
        if (status == 0 && queue != 0)
        {
            AudioQueueDispose(queue, 1);
        }

        return status;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _self = GCHandle.Alloc(this);
            try
            {
                OpenQueue();
            }
            catch
            {
                _self.Free();
                throw;
            }

            _wake = new AutoResetEvent(false);
            _headOffset = 0;
            _running = true;
            _pump = new Thread(Pump)
            {
                IsBackground = true,
                Name = "NeonSidekick AudioQueue playback",
                Priority = ThreadPriority.AboveNormal,
            };
            _pump.Start();
        }
    }

    public void Write(byte[] pcm, int count)
    {
        if (pcm is null || count <= 0)
        {
            return;
        }

        count = Math.Min(count, pcm.Length);
        var copy = new byte[count];
        Buffer.BlockCopy(pcm, 0, copy, 0, count);

        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _pending.Enqueue(copy);
            _ledger.Queue(count);
        }

        _wake?.Set();
    }

    /// <summary>
    /// Drops everything queued and silences the device now. The ledger is cleared first, so the callbacks that
    /// <c>AudioQueueStop(immediate)</c> runs for the flushed buffers carry void tickets; <see cref="BufferedBytes"/> is
    /// zero on return, and the next write starts the queue again.
    /// </summary>
    public void ClearBuffer()
    {
        lock (_gate)
        {
            _pending.Clear();
            _headOffset = 0;
            _ledger.Clear();

            if (_queue != 0)
            {
                AudioQueueStop(_queue, 1);
                _queueRunning = false;
            }
        }
    }

    public void Stop()
    {
        Thread? pump;

        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            pump = _pump;
            _pump = null;
            _pending.Clear();
            _headOffset = 0;
            _ledger.Clear();
        }

        _wake?.Set();
        if (pump is not null && pump != Thread.CurrentThread)
        {
            pump.Join(TimeSpan.FromSeconds(2));
        }

        lock (_gate)
        {
            CloseQueue();
            _ledger.Clear();
            if (_self.IsAllocated)
            {
                _self.Free();
            }

            _wake?.Dispose();
            _wake = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    /// <summary>Creates the queue on the current default output and allocates its buffers. Caller holds the gate.</summary>
    private void OpenQueue()
    {
        var description = StreamDescription.Pcm(Format);
        nint queue;
        int status = AudioQueueNewOutput(&description, &OnOutput, GCHandle.ToIntPtr(_self), 0, 0, 0, &queue);
        if (status != 0 || queue == 0)
        {
            throw new InvalidOperationException(AudioQueueText.Failed("AudioQueueNewOutput", status));
        }

        var buffers = new nint[BufferCount];
        for (int i = 0; i < BufferCount; i++)
        {
            QueueBuffer* buffer;
            status = AudioQueueAllocateBuffer(queue, (uint)_bufferBytes, &buffer);
            if (status != 0)
            {
                AudioQueueDispose(queue, 1);
                throw new InvalidOperationException(AudioQueueText.Failed("AudioQueueAllocateBuffer", status));
            }

            buffers[i] = (nint)buffer;
        }

        _queue = queue;
        _buffers = buffers;
        _device = DefaultDevice(DefaultOutputDevice);
        _queueRunning = false;
        _idleSince = 0;
    }

    /// <summary>
    /// Stops and disposes the queue (and with it the buffers). No callback runs after it. The ledger is the caller's: a
    /// rebuild keeps what is queued counted, so <see cref="BufferedBytes"/> never reads zero in between. Caller holds the gate.
    /// </summary>
    private void CloseQueue()
    {
        if (_queue == 0)
        {
            return;
        }

        AudioQueueStop(_queue, 1);
        AudioQueueDispose(_queue, 1);
        _queue = 0;
        _buffers = [];
        _queueRunning = false;
    }

    /// <summary>
    /// Moves queued PCM into free buffers, starts the queue when it has something to play, and stops it once it has had
    /// nothing for <see cref="IdleStop"/>. The wait is bounded so a stop is noticed without the event.
    /// </summary>
    private void Pump()
    {
        while (_running)
        {
            try
            {
                _wake?.WaitOne(50);

                lock (_gate)
                {
                    if (!_running)
                    {
                        continue;
                    }

                    if (_queue == 0)
                    {
                        // A rebuild failed earlier: try again when there is something to play.
                        if (_pending.Count == 0 || !TryReopen())
                        {
                            continue;
                        }
                    }

                    if (!_queueRunning && _pending.Count > 0 && _ledger.InFlightBytes == 0 && DefaultDevice(DefaultOutputDevice) is var device && device != _device && device != UnknownObject)
                    {
                        // The default output changed while the queue stood idle (AirPods connected between replies): a
                        // queue opened on the old default would keep playing there.
                        DiagnosticLog.Info(Category, AudioQueueText.OutputChanged(_device, device));
                        CloseQueue();
                        if (!TryReopen())
                        {
                            continue;
                        }
                    }

                    if (_queueRunning && _ledger.InFlightBytes > 0 && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastProgress)) >= StallLimit)
                    {
                        long dropped = _ledger.InFlightBytes;
                        DiagnosticLog.Warn(Category, AudioQueueText.Stalled(dropped, Stopwatch.GetElapsedTime(Volatile.Read(ref _lastProgress))));
                        _ledger.DropInFlight();
                        AudioQueueStop(_queue, 1);
                        _queueRunning = false;
                    }

                    bool enqueued = Feed();

                    if (enqueued && !_queueRunning)
                    {
                        Volatile.Write(ref _lastProgress, Stopwatch.GetTimestamp());
                        int status = AudioQueueStart(_queue, null);
                        if (status != 0)
                        {
                            // Nothing will play: drop it, so the speech code's wait for zero ends instead of timing out.
                            DiagnosticLog.Error(Category, AudioQueueText.Failed("AudioQueueStart", status));
                            AudioQueueStop(_queue, 1);
                            _pending.Clear();
                            _headOffset = 0;
                            _ledger.Clear();
                        }

                        _queueRunning = status == 0;
                    }

                    if (_queueRunning && _ledger.BufferedBytes == 0)
                    {
                        long now = Stopwatch.GetTimestamp();
                        if (_idleSince == 0)
                        {
                            _idleSince = now;
                        }
                        else if (Stopwatch.GetElapsedTime(_idleSince, now) >= IdleStop)
                        {
                            // Stopped, not paused (2026-10-07, the user's run: a timer's short alert after a reply was
                            // never heard). After AudioQueuePause and AudioQueueStart the queue handed 0.8 s of new
                            // buffers back in 32 ms without playing them, its timeline having run on through the pause;
                            // a stop starts the next one afresh. Nothing is buffered here, so a stop drops nothing.
                            AudioQueueStop(_queue, 1);
                            _queueRunning = false;
                            _idleSince = 0;
                        }
                    }
                    else
                    {
                        _idleSince = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error(Category, $"Playback pump failed: {ex.Message}", ex);
                return;
            }
        }
    }

    /// <summary>
    /// Opens a queue on the current default output. When it cannot, what was owed is dropped so the speech code's wait
    /// for zero ends, and the next write tries again. Caller holds the gate.
    /// </summary>
    private bool TryReopen()
    {
        try
        {
            OpenQueue();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            DiagnosticLog.Error(Category, ex.Message);
            _pending.Clear();
            _headOffset = 0;
            _ledger.Clear();
            return false;
        }
    }

    /// <summary>Fills every free buffer from the head of the queue. Caller holds the gate. True when anything was enqueued.</summary>
    private bool Feed()
    {
        bool enqueued = false;
        while (_pending.Count > 0)
        {
            int index = _ledger.FreeBuffer();
            if (index < 0)
            {
                break;
            }

            var chunk = _pending.Peek();
            int length = Math.Min(chunk.Length - _headOffset, _bufferBytes);
            var buffer = (QueueBuffer*)_buffers[index];
            Marshal.Copy(chunk, _headOffset, (nint)buffer->AudioData, length);
            buffer->AudioDataByteSize = (uint)length;
            long ticket = _ledger.Dispatch(index, length);
            buffer->UserData = (nint)ticket;

            // A chunk longer than one buffer stays at the head; the offset walks it.
            if (_headOffset + length >= chunk.Length)
            {
                _pending.Dequeue();
                _headOffset = 0;
            }
            else
            {
                _headOffset += length;
            }

            int status = AudioQueueEnqueueBuffer(_queue, buffer, 0, null);
            if (status != 0)
            {
                _ledger.Abandon(ticket);
                DiagnosticLog.Error(Category, AudioQueueText.Failed("AudioQueueEnqueueBuffer", status));
                break;
            }

            enqueued = true;
        }

        return enqueued;
    }

    /// <summary>
    /// The output callback, on AudioQueue's thread: the buffer is done. Only the ledger is touched (its own lock), then the
    /// pump is woken. Nothing may escape an <c>[UnmanagedCallersOnly]</c> method, so everything is caught.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOutput(nint userData, nint queue, QueueBuffer* buffer)
    {
        try
        {
            if (userData == 0 || GCHandle.FromIntPtr(userData).Target is not AudioQueuePlayback self)
            {
                return;
            }

            self._ledger.Complete(buffer->UserData);
            Volatile.Write(ref self._lastProgress, Stopwatch.GetTimestamp());
            self._wake?.Set();
        }
        catch (Exception)
        {
            // The ledger cannot throw on a ticket; a disposed event during Stop is the one thing that could, and a
            // stopping playback has nothing left to wake.
        }
    }
}
