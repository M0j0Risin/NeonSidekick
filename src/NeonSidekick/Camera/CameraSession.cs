using NeonSidekick.Diagnostics;

namespace NeonSidekick.Camera;

/// <summary>Where the shared camera stands: closed, open and settling its exposure, delivering, or failed.</summary>
public enum CameraState
{
    Off,
    Warming,
    On,
    Faulted,
}

/// <summary>What the session opens: the saved device name ("" for the first camera) and the size asked for.</summary>
public sealed record CameraOptions(string DeviceName, CameraSize Target);

/// <summary>
/// The one camera stream everything shares (2026-10-02): the shutter pane, the live view, a botchat and watch mode each hold a
/// <see cref="CameraLease"/>, and the device is open while any lease is — one stream, never two opens of one camera. The first
/// lease opens it on a thread of its own (named <c>Camera</c>; Media Foundation's objects live and die there) that reads frames
/// continuously, so the auto exposure settles while the user frames the shot; a frame is copied out only while someone waits
/// for one or watches the live view. A frame is <em>settled</em> after the stream's <see cref="ICameraStream.Warmup"/> — frames
/// <em>and</em> time since the open (<see cref="CameraWarmup.Webcam"/>) — the first frames of a webcam are dark or tinted. When the last lease goes the
/// device stays open <see cref="Linger"/> longer, so a retake or the next bot turn does not warm up again; then it closes (the
/// camera's LED with it). A failure completes every waiter with the <see cref="CameraException"/>, and the next lease or wait
/// opens it again. Portable: the platform is behind <see cref="ICameraSystem"/>.
/// </summary>
public sealed class CameraSession : IDisposable
{
    /// <summary>How long the device stays open after the last lease goes.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(3);

    /// <summary>How long a wait for a frame lasts before it is <see cref="CameraFailure.NoFrames"/> (an open takes a second or two).</summary>
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a new open waits for the previous stream's thread to let the device go.</summary>
    private static readonly TimeSpan HandOver = TimeSpan.FromSeconds(5);

    private const string Category = "Camera";

    private readonly ICameraSystem? _system;
    private readonly Func<CameraOptions> _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<CameraLease> _leases = [];
    private readonly List<Waiter> _waiters = [];
    private readonly List<Action<CameraFrame>> _watchers = [];
    private Generation? _current;
    private Generation? _previous;
    private ITimer? _linger;
    private string? _notice;
    private bool _disposed;

    /// <param name="system">The platform's cameras; null where there are none (every use then fails with <see cref="CameraFailure.Unsupported"/>).</param>
    /// <param name="options">Read at each open: the saved device and resolution.</param>
    /// <param name="time">The clock for the warm-up, the linger and the timeouts.</param>
    public CameraSession(ICameraSystem? system, Func<CameraOptions> options, TimeProvider time)
    {
        _system = system;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Whether this platform has a camera layer at all.</summary>
    public bool Available => _system is not null;

    /// <summary>Raised on the camera's thread (or the caller's) whenever <see cref="State"/>, <see cref="Device"/> or the leases change. Handlers must not block.</summary>
    public event Action? Changed;

    public CameraState State { get; private set; }

    /// <summary>The last failure's sentence while <see cref="State"/> is <see cref="CameraState.Faulted"/>.</summary>
    public string? Fault { get; private set; }

    /// <summary>The device open now (or last open).</summary>
    public CameraDevice? Device { get; private set; }

    /// <summary>The camera's own format the open stream runs at.</summary>
    public CameraFormat? Format { get; private set; }

    /// <summary>The size frames arrive at now (the reader's output, which the format's size usually is).</summary>
    public CameraSize? FrameSize { get; private set; }

    /// <summary>True while the camera is open or opening: the toolbar's 📷 and the LED.</summary>
    public bool Live => State is CameraState.Warming or CameraState.On;

    /// <summary>The purposes holding the camera now, one entry per lease.</summary>
    public IReadOnlyList<string> Holders
    {
        get
        {
            lock (_gate)
            {
                return _leases.Select(l => l.Purpose).ToList();
            }
        }
    }

    /// <summary>The cameras Windows lists now. Throws <see cref="CameraException"/>.</summary>
    public IReadOnlyList<CameraDevice> List() => (_system ?? throw new CameraException(CameraFailure.Unsupported, null)).List();

    /// <summary>
    /// The device a saved name picks out of <paramref name="devices"/>: the one of that name (any case), else the first. False
    /// in <paramref name="fellBack"/> when the name was empty or found.
    /// </summary>
    public static CameraDevice? Choose(IReadOnlyList<CameraDevice> devices, string? name, out bool fellBack)
    {
        ArgumentNullException.ThrowIfNull(devices);
        fellBack = false;
        if (devices.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return devices[0];
        }

        var named = devices.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        fellBack = named is null;
        return named ?? devices[0];
    }

    /// <summary>The one-time notice of the last open, if it had one (the saved camera missing, the first used instead); cleared by the read.</summary>
    public string? TakeNotice()
    {
        lock (_gate)
        {
            string? notice = _notice;
            _notice = null;
            return notice;
        }
    }

    /// <summary>
    /// Holds the camera for <paramref name="purpose"/> (opening it when it is closed). <paramref name="revoked"/> runs when
    /// <see cref="Revoke"/> takes the lease away (<c>/camera off</c>). Throws <see cref="CameraException"/> where there is no
    /// camera layer; a device failure arrives at the next wait instead.
    /// </summary>
    public CameraLease Acquire(string purpose, Action? revoked = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (_system is null)
        {
            throw new CameraException(CameraFailure.Unsupported, null);
        }

        var lease = new CameraLease(this, purpose, revoked);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _leases.Add(lease);
            _linger?.Dispose();
            _linger = null;
            EnsureRunning();
        }

        Changed?.Invoke();
        return lease;
    }

    /// <summary>Takes away every lease of <paramref name="purpose"/> (each one's revoke callback runs); the number taken.</summary>
    public int Revoke(string purpose)
    {
        List<CameraLease> taken;
        lock (_gate)
        {
            taken = _leases.Where(l => l.Purpose == purpose).ToList();
        }

        foreach (var lease in taken)
        {
            lease.Dispose();
            lease.OnRevoked();
        }

        return taken.Count;
    }

    internal void Release(CameraLease lease)
    {
        lock (_gate)
        {
            if (!_leases.Remove(lease) || _leases.Count > 0 || _disposed)
            {
                return;
            }

            _linger?.Dispose();
            _linger = _time.CreateTimer(_ => CloseIfIdle(), null, Linger, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The next frame (a settled one when <paramref name="settled"/>), copied out for the caller. Needs a lease; reopens a
    /// failed or closed device. Throws <see cref="CameraException"/> on a failure or <see cref="FrameTimeout"/>.
    /// </summary>
    public async Task<CameraFrame> NextFrameAsync(bool settled, CancellationToken cancellationToken)
    {
        var waiter = new Waiter(settled);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leases.Count == 0)
            {
                throw new InvalidOperationException("A camera frame needs a lease.");
            }

            _waiters.Add(waiter);
            EnsureRunning();
        }

        try
        {
            return await waiter.Task.WaitAsync(FrameTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new CameraException(CameraFailure.NoFrames, null);
        }
        finally
        {
            lock (_gate)
            {
                _ = _waiters.Remove(waiter);
            }
        }
    }

    /// <summary>
    /// Every frame, as it arrives, to <paramref name="watcher"/> on the camera's thread (the live view; it must copy what it
    /// keeps and not block) until the result is disposed. Needs a lease of its own to keep the camera open.
    /// </summary>
    public IDisposable Watch(Action<CameraFrame> watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        lock (_gate)
        {
            _watchers.Add(watcher);
        }

        return new Unwatch(this, watcher);
    }

    public void Dispose()
    {
        Generation? current;
        List<Waiter> waiters;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _linger?.Dispose();
            _linger = null;
            _leases.Clear();
            _watchers.Clear();
            current = _current;
            _current = null;
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        current?.Stop();
        foreach (var waiter in waiters)
        {
            waiter.Fail(new CameraException(CameraFailure.NoFrames, null));
        }
    }

    private void CloseIfIdle()
    {
        Generation? closing;
        lock (_gate)
        {
            if (_leases.Count > 0 || _current is null)
            {
                return;
            }

            closing = _current;
            _current = null;
            _previous = closing;
            _linger?.Dispose();
            _linger = null;
        }

        closing.Stop();
    }

    /// <summary>Starts a reader thread when none runs. Under the gate.</summary>
    private void EnsureRunning()
    {
        if (_current is not null)
        {
            return;
        }

        var generation = new Generation(_previous);
        _current = generation;
        var thread = new Thread(() => Run(generation)) { IsBackground = true, Name = "Camera" };
        thread.Start();
    }

    private void Run(Generation generation)
    {
        generation.Previous?.Exited.Wait(HandOver);
        ICameraStream? stream = null;
        try
        {
            var devices = _system!.List();
            var options = _options();
            var device = Choose(devices, options.DeviceName, out bool fellBack) ?? throw new CameraException(CameraFailure.NoCamera, null);
            stream = _system.Open(device, options.Target);
            lock (_gate)
            {
                if (!ReferenceEquals(_current, generation))
                {
                    return;
                }

                generation.Stream = stream;
                Device = device;
                Format = stream.Native;
                FrameSize = null;
                State = CameraState.Warming;
                Fault = null;
                _notice = fellBack ? CameraText.FellBack(options.DeviceName, device.Name) : null;
            }

            Changed?.Invoke();
            Pump(generation, stream);
        }
        catch (CameraException e)
        {
            Failed(generation, e);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(generation, new CameraException(CameraFailure.Failed, e.Message, e));
        }
        finally
        {
            try
            {
                stream?.Dispose();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DiagnosticLog.Warn(Category, "Closing the camera failed.", e);
            }

            bool changed = false;
            lock (_gate)
            {
                if (ReferenceEquals(_current, generation))
                {
                    _current = null;
                }

                if (_current is null && State != CameraState.Faulted)
                {
                    changed = State != CameraState.Off;
                    State = CameraState.Off;
                }
            }

            generation.Exited.Set();
            if (changed)
            {
                Changed?.Invoke();
            }
        }
    }

    private void Pump(Generation generation, ICameraStream stream)
    {
        long sequence = 0;
        long openedAt = _time.GetTimestamp();
        var warmup = stream.Warmup;
        while (!generation.Stopped)
        {
            bool copy;
            lock (_gate)
            {
                copy = _waiters.Count > 0 || _watchers.Count > 0;
            }

            var read = stream.Read(copy);
            if (read is null)
            {
                return;
            }

            sequence++;
            var frame = read with { Sequence = sequence, At = _time.GetUtcNow() };
            bool settled = sequence >= warmup.Frames && _time.GetElapsedTime(openedAt) >= warmup.Time;
            bool changed = false;
            List<Waiter> done = [];
            Action<CameraFrame>[] watchers;
            lock (_gate)
            {
                if (!ReferenceEquals(_current, generation))
                {
                    return;
                }

                if (FrameSize != new CameraSize(frame.Width, frame.Height))
                {
                    FrameSize = new CameraSize(frame.Width, frame.Height);
                }

                if (settled && State == CameraState.Warming)
                {
                    State = CameraState.On;
                    changed = true;
                }

                if (frame.Bgrx.Length > 0)
                {
                    done.AddRange(_waiters.Where(w => settled || !w.Settled));
                    _ = _waiters.RemoveAll(done.Contains);
                }

                watchers = frame.Bgrx.Length > 0 ? [.. _watchers] : [];
            }

            if (changed)
            {
                Changed?.Invoke();
            }

            foreach (var waiter in done)
            {
                waiter.Complete(frame);
            }

            foreach (var watcher in watchers)
            {
                try
                {
                    watcher(frame);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    DiagnosticLog.Warn(Category, "A camera frame watcher failed.", e);
                }
            }
        }
    }

    private void Failed(Generation generation, CameraException error)
    {
        List<Waiter> waiters;
        lock (_gate)
        {
            if (!ReferenceEquals(_current, generation))
            {
                return;
            }

            _current = null;
            State = CameraState.Faulted;
            Fault = error.Message;
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        DiagnosticLog.Warn(Category, error.Message);
        foreach (var waiter in waiters)
        {
            waiter.Fail(error);
        }

        Changed?.Invoke();
    }

    private void Unsubscribe(Action<CameraFrame> watcher)
    {
        lock (_gate)
        {
            _ = _watchers.Remove(watcher);
        }
    }

    private sealed class Unwatch(CameraSession session, Action<CameraFrame> watcher) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                session.Unsubscribe(watcher);
            }
        }
    }

    /// <summary>
    /// One open of the device: its thread's stop flag, the stream to interrupt, and the signal its thread has let go. A stop
    /// only sets the flag: a running camera returns a frame every few dozen milliseconds and the thread sees it then. Shutting
    /// the source down from here does not wake a blocked synchronous <c>ReadSample</c> (found on the MX Brio, 2026-10-02: the
    /// read never returned), so <see cref="ICameraStream.Interrupt"/> is only the last resort, for a camera that stopped sending
    /// for <see cref="StallGrace"/>.
    /// </summary>
    private sealed class Generation(Generation? previous)
    {
        private static readonly TimeSpan StallGrace = TimeSpan.FromSeconds(2);

        private volatile bool _stopped;

        public Generation? Previous { get; } = previous;

        public ManualResetEventSlim Exited { get; } = new();

        public ICameraStream? Stream { get; set; }

        public bool Stopped => _stopped;

        public void Stop()
        {
            _stopped = true;
            _ = ThreadPool.QueueUserWorkItem(static generation =>
            {
                if (!generation.Exited.Wait(StallGrace))
                {
                    generation.Stream?.Interrupt();
                }
            }, this, preferLocal: false);
        }
    }

    private sealed class Waiter(bool settled)
    {
        private readonly TaskCompletionSource<CameraFrame> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Settled { get; } = settled;

        public Task<CameraFrame> Task => _source.Task;

        public void Complete(CameraFrame frame) => _source.TrySetResult(frame);

        public void Fail(Exception error) => _source.TrySetException(error);
    }
}

/// <summary>A hold on the shared camera (<see cref="CameraSession.Acquire"/>); disposing it lets the camera close after the linger.</summary>
public sealed class CameraLease : IDisposable
{
    private readonly CameraSession _session;
    private readonly Action? _revoked;
    private int _released;

    internal CameraLease(CameraSession session, string purpose, Action? revoked)
    {
        _session = session;
        Purpose = purpose;
        _revoked = revoked;
    }

    public string Purpose { get; }

    public bool Released => Volatile.Read(ref _released) != 0;

    /// <summary>The next frame through this lease's session (<see cref="CameraSession.NextFrameAsync"/>).</summary>
    public Task<CameraFrame> NextFrameAsync(bool settled, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Released, this);
        return _session.NextFrameAsync(settled, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _session.Release(this);
        }
    }

    internal void OnRevoked() => _revoked?.Invoke();
}
