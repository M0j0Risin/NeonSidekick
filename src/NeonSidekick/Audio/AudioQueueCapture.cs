using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Audio.AudioQueueNative;

namespace NeonSidekick.Audio;

/// <summary>
/// Microphone capture over AudioToolbox's AudioQueue (2026-10-07, Stage 2 of the Mac port): <see cref="WinMmAudioCapture"/>'s
/// shape on macOS. The queue is asked for the pipeline's own format (16 kHz, 16-bit mono) and converts from the device's
/// rate itself, so nothing is resampled here.
///
/// <para><b>Who raises <see cref="DataAvailable"/>.</b> Not the input callback: it runs on AudioQueue's thread, and a
/// subscriber that stops the capture from inside the event would then be stopping the queue from its own callback. The
/// callback only copies the buffer into a free slot of a small ring, re-enqueues it and wakes the pump; the pump raises the
/// event with the slot (the reused array), checking <c>_running</c> before each. <see cref="Stop"/> clears
/// <c>_running</c>, joins the pump outside the lock (unless it is the pump), then stops and disposes the queue
/// synchronously — so no event is raised after it returns, as the interface promises.</para>
///
/// <para><b>The permission.</b> macOS asks per terminal app and refuses silently: a refused queue records exact zeros.
/// <see cref="Start"/> refuses first when the status already says denied or restricted
/// (<see cref="MicrophoneAccess.Refusal"/>), and the pump's <see cref="SilenceWatch"/> turns a second of exact zeros into
/// one warning in the transcript, said once until it changes or the microphone is heard again (refused, restricted, or a microphone that is off — a MacBook's lid closed). While the
/// question is on screen <c>AudioQueueStart</c> waits for the answer (the spike: about a minute, the time it took to
/// click), and the zeros before it are not a verdict.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class AudioQueueCapture : IAudioCapture
{
    private const string Category = "AudioQueueCapture";

    /// <summary>Buffers the queue fills: four at 50 ms, as WinMM's.</summary>
    private const int BufferCount = 4;

    /// <summary>Copied buffers waiting for the pump: a second and a half at 50 ms before one is dropped.</summary>
    private const int SlotCount = 32;

    /// <summary>
    /// The silence verdict last warned about, process-wide: every listen and every wake re-arm starts a capture, and a
    /// closed lid would otherwise put the same warning in the transcript each turn. Cleared when a capture hears sound.
    /// </summary>
    private static string? s_warned;

    private readonly int _bufferBytes;
    private readonly object _gate = new();
    private readonly object _ringGate = new();
    private readonly Queue<(byte[] Slot, int Count)> _filled = new();
    private readonly Stack<byte[]> _free = new();

    private nint _queue;
    private GCHandle _self;
    private AutoResetEvent? _ready;
    private Thread? _pump;
    private volatile bool _running;
    private bool _overflowLogged;
    private bool _disposed;

    public AudioQueueCapture(PcmFormat format, int bufferMilliseconds = 50)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0 || format.BitsPerSample is not (8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "AudioQueue capture needs a positive rate, 8 or 16 bits and at least one channel.");
        }

        if (bufferMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));
        }

        Format = format;
        _bufferBytes = Math.Max(format.BlockAlign, format.BytesFor(bufferMilliseconds));
    }

    public PcmFormat Format { get; }

    public bool IsCapturing => _running;

    /// <inheritdoc/>
    public event Action<byte[], int>? DataAvailable;

    /// <summary>1 when Core Audio names a default input device, 0 when it names none. One is all the voice path needs to know.</summary>
    public static int InputDeviceCount() => DefaultDevice(AudioQueueNative.DefaultInputDevice) == UnknownObject ? 0 : 1;

    /// <summary>The sentence that refuses a recording before it starts (the permission denied or restricted), or null.</summary>
    public static string? Refusal() => MicrophoneAccess.Refusal(MicrophoneAuthorization(), MicrophoneText.Terminal);

    /// <summary>
    /// Creates and disposes an input queue at <paramref name="format"/> without starting it: the <c>--smoke</c> check. Never
    /// started, so it never brings up the permission question. Returns the OSStatus (0 = created).
    /// </summary>
    public static int ProbeDefaultDevice(PcmFormat format)
    {
        var description = StreamDescription.Pcm(format);
        nint queue;
        int status = AudioQueueNewInput(&description, &OnInput, 0, 0, 0, 0, &queue);
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

            if (Refusal() is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            _self = GCHandle.Alloc(this);
            var description = StreamDescription.Pcm(Format);
            nint queue;
            int status = AudioQueueNewInput(&description, &OnInput, GCHandle.ToIntPtr(_self), 0, 0, 0, &queue);
            if (status != 0 || queue == 0)
            {
                _self.Free();
                throw new InvalidOperationException(AudioQueueText.Failed("AudioQueueNewInput", status));
            }

            _queue = queue;
            lock (_ringGate)
            {
                _filled.Clear();
                _free.Clear();
                for (int i = 0; i < SlotCount; i++)
                {
                    _free.Push(new byte[_bufferBytes]);
                }
            }

            for (int i = 0; i < BufferCount; i++)
            {
                QueueBuffer* buffer;
                status = AudioQueueAllocateBuffer(queue, (uint)_bufferBytes, &buffer);
                if (status == 0)
                {
                    status = AudioQueueEnqueueBuffer(queue, buffer, 0, null);
                }

                if (status != 0)
                {
                    ReleaseQueue();
                    throw new InvalidOperationException(AudioQueueText.Failed("AudioQueueAllocateBuffer", status));
                }
            }

            _ready = new AutoResetEvent(false);
            _overflowLogged = false;
            _running = true;
            _pump = new Thread(Pump)
            {
                IsBackground = true,
                Name = "NeonSidekick AudioQueue capture",
                Priority = ThreadPriority.AboveNormal,
            };
            _pump.Start();

            status = AudioQueueStart(queue, null);
            if (status != 0)
            {
                _running = false;
                _ready.Set();
                ReleaseQueue();
                throw new InvalidOperationException(AudioQueueText.Failed("AudioQueueStart", status));
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
        }

        // Joined outside the lock: the pump raises DataAvailable, and a subscriber that reaches back into this class
        // would otherwise deadlock. A subscriber stopping from inside the event is the pump itself: no join, and the
        // pump sees _running false before it raises again.
        _ready?.Set();
        if (pump is not null && pump != Thread.CurrentThread)
        {
            pump.Join(TimeSpan.FromSeconds(2));
        }

        lock (_gate)
        {
            ReleaseQueue();
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

    /// <summary>Stops and disposes the queue (no callback after it), then frees the handle. Caller holds the gate.</summary>
    private void ReleaseQueue()
    {
        if (_queue != 0)
        {
            AudioQueueStop(_queue, 1);
            AudioQueueDispose(_queue, 1);
            _queue = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        lock (_ringGate)
        {
            _filled.Clear();
            _free.Clear();
        }

        _ready?.Dispose();
        _ready = null;
    }

    /// <summary>Raises <see cref="DataAvailable"/> for every copied buffer, in order, and watches for a silent microphone.</summary>
    private void Pump()
    {
        var watch = new SilenceWatch(Format);
        bool warned = false;
        var ready = _ready;

        while (_running)
        {
            try
            {
                ready?.WaitOne(50);

                while (_running)
                {
                    (byte[] Slot, int Count) item;
                    lock (_ringGate)
                    {
                        if (!_filled.TryDequeue(out item))
                        {
                            break;
                        }
                    }

                    try
                    {
                        DataAvailable?.Invoke(item.Slot, item.Count);
                    }
                    catch (Exception ex)
                    {
                        // A subscriber throwing here would otherwise end capture for the session, and the subscriber is
                        // the whole voice path.
                        DiagnosticLog.Error(Category, $"An audio subscriber threw: {ex.Message}", ex);
                    }

                    bool tripped = watch.Feed(item.Slot.AsSpan(0, item.Count));
                    if (tripped && !warned)
                    {
                        string? verdict = MicrophoneAccess.SilenceVerdict(MicrophoneAuthorization(), MicrophoneText.Terminal);
                        if (verdict is null)
                        {
                            watch.Reset();
                        }
                        else
                        {
                            warned = true;
                            if (Interlocked.Exchange(ref s_warned, verdict) != verdict)
                            {
                                DiagnosticLog.Warn(Category, verdict);
                            }
                        }
                    }
                    else if (watch.HeardSound && Volatile.Read(ref s_warned) is not null)
                    {
                        Volatile.Write(ref s_warned, null);
                    }

                    lock (_ringGate)
                    {
                        _free.Push(item.Slot);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error(Category, $"Capture pump failed: {ex.Message}", ex);
                return;
            }
        }
    }

    /// <summary>
    /// The input callback, on AudioQueue's thread: copy the buffer into a free slot, hand it back to the queue, wake the
    /// pump. Never raises the event itself (see the class remarks); nothing may escape it.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnInput(nint userData, nint queue, QueueBuffer* buffer, void* startTime, uint packets, void* descriptions)
    {
        try
        {
            if (userData == 0 || GCHandle.FromIntPtr(userData).Target is not AudioQueueCapture self || !self._running)
            {
                return;
            }

            int count = (int)Math.Min(buffer->AudioDataByteSize, (uint)self._bufferBytes);
            if (count > 0)
            {
                bool dropped = false;
                lock (self._ringGate)
                {
                    if (self._free.TryPop(out var slot))
                    {
                        new ReadOnlySpan<byte>(buffer->AudioData, count).CopyTo(slot);
                        self._filled.Enqueue((slot, count));
                    }
                    else
                    {
                        dropped = true;
                    }
                }

                if (dropped && !self._overflowLogged)
                {
                    self._overflowLogged = true;
                    DiagnosticLog.Warn(Category, AudioQueueText.CaptureOverflow);
                }

                self._ready?.Set();
            }

            if (self._running)
            {
                AudioQueueEnqueueBuffer(queue, buffer, 0, null);
            }
        }
        catch (Exception)
        {
            // A disposed event during Stop is the one thing that could throw here, and a stopping capture wants nothing.
        }
    }
}
