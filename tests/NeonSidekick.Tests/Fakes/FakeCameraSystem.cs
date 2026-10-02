using NeonSidekick.Camera;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A camera for the tests (2026-10-02): scripted devices, an open that fails on demand, frames made up — a solid colour by
/// default, <see cref="Paint"/> to draw them — delivered at once (a millisecond apart) or one per <see cref="Deliver"/> under
/// <see cref="Stepped"/>, a read failure after a number of frames, and counters of what was opened and closed. Its stream
/// settles at once unless <see cref="Warmup"/> says otherwise.
/// </summary>
public sealed class FakeCameraSystem : ICameraSystem
{
    private readonly SemaphoreSlim _steps = new(0);
    private int _opens;
    private int _closes;
    private int _reads;

    public List<CameraDevice> Devices { get; } = [new("Fake Cam", @"\\?\usb#fake-cam#1"), new("Other Cam", @"\\?\usb#other-cam#1")];

    public CameraException? ListFailure { get; set; }

    /// <summary>Thrown by <see cref="Open"/> (each open until set back to null).</summary>
    public CameraException? OpenFailure { get; set; }

    /// <summary>Thrown by a read once <see cref="FailAfterFrames"/> frames have been read.</summary>
    public CameraException? ReadFailure { get; set; }

    public int FailAfterFrames { get; set; }

    public CameraSize Size { get; set; } = new(64, 48);

    public CameraWarmup Warmup { get; set; } = CameraWarmup.None;

    /// <summary>Frames only on <see cref="Deliver"/>.</summary>
    public bool Stepped { get; set; }

    /// <summary>The pixels of frame n (from 1): a solid colour by default.</summary>
    public Func<long, CameraSize, byte[]> Paint { get; set; } = (_, size) => Solid(size, 30, 120, 200);

    public int Opens => Volatile.Read(ref _opens);

    public int Closes => Volatile.Read(ref _closes);

    public int Reads => Volatile.Read(ref _reads);

    /// <summary>The device the last open was for.</summary>
    public CameraDevice? Opened { get; private set; }

    /// <summary>The size the last open asked for.</summary>
    public CameraSize? Target { get; private set; }

    public IReadOnlyList<CameraDevice> List() => ListFailure is { } failure ? throw failure : Devices.ToList();

    public ICameraStream Open(CameraDevice device, CameraSize target)
    {
        if (OpenFailure is { } failure)
        {
            throw failure;
        }

        Opened = device;
        Target = target;
        _ = Interlocked.Increment(ref _opens);
        return new Stream(this);
    }

    /// <summary>Lets <paramref name="frames"/> more frames through under <see cref="Stepped"/>.</summary>
    public void Deliver(int frames = 1) => _steps.Release(frames);

    /// <summary>A solid BGRX picture.</summary>
    public static byte[] Solid(CameraSize size, byte r, byte g, byte b)
    {
        var pixels = new byte[size.Width * size.Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
        }

        return pixels;
    }

    private sealed class Stream(FakeCameraSystem owner) : ICameraStream
    {
        private readonly CancellationTokenSource _stop = new();
        private long _frame;
        private int _closed;

        public CameraFormat Native { get; } = new(owner.Size, 30, "NV12");

        public CameraWarmup Warmup => owner.Warmup;

        public CameraFrame? Read(bool copy)
        {
            try
            {
                if (owner.Stepped)
                {
                    owner._steps.Wait(_stop.Token);
                }
                else
                {
                    _stop.Token.WaitHandle.WaitOne(1);
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (_stop.IsCancellationRequested)
            {
                return null;
            }

            long n = Interlocked.Increment(ref _frame);
            _ = Interlocked.Increment(ref owner._reads);
            if (owner.ReadFailure is { } failure && n > owner.FailAfterFrames)
            {
                throw failure;
            }

            return new CameraFrame(owner.Size.Width, owner.Size.Height, copy ? owner.Paint(n, owner.Size) : [], 0, default);
        }

        public void Interrupt() => _stop.Cancel();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _stop.Cancel();
                _ = Interlocked.Increment(ref owner._closes);
            }
        }
    }
}
