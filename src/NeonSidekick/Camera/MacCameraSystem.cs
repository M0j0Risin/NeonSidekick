using System.Globalization;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Camera.MacCameraNative;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Camera;

/// <summary>
/// The cameras through AVFoundation (2026-10-07, Stage 2: the camera on macOS; <see cref="MediaFoundationCameraSystem"/>'s twin).
/// <see cref="List"/> is an <c>AVCaptureDeviceDiscoverySession</c> over the built-in and external video types
/// (<see cref="MacCameraRules.DeviceTypes"/>: the FaceTime camera, a USB webcam, an iPhone as Continuity Camera), the suspended ones
/// last; nothing is opened, so listing never lights the LED. <see cref="Open"/> asks the permission first (the terminal's: macOS asks
/// once and remembers), then runs an <c>AVCaptureSession</c> with the device's input at the format nearest the target
/// (<see cref="CameraFormats.Pick"/> over each format's frame-rate ranges) and a video data output asking for 32BGRA; the frames come to a
/// runtime-made delegate on a serial dispatch queue (<see cref="MacCameraNative"/>). The format is set after the input is added and the
/// device stays locked for configuration until the session runs: set before, the session's preset put the StreamCam back at 1920x1080
/// (found probing). Each call runs in an autorelease pool of its own (the session's reader thread and the list's callers are plain
/// .NET threads). Excluded from coverage with the native layer; its decisions are <see cref="MacCameraRules"/>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacCameraSystem : ICameraSystem
{
    private const string Category = "Camera";

    /// <summary>How long an open waits for the user to answer macOS's question (the session's wait for a frame is 10 s).</summary>
    private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(8);

    public IReadOnlyList<CameraDevice> List() => Pooled(() => MacCameraRules.Order(Discover().Select(d => (d.Device, d.Suspended))));

    public ICameraStream Open(CameraDevice device, CameraSize target)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Pooled(() => OpenPooled(device, target));
    }

    /// <summary>
    /// <c>camera:avf</c> for the smoke: AVFoundation and its capture classes bound, the delegate class made and answering its selector,
    /// the permission's block made (never called: no question is asked), the status read, a 32BGRA pixel buffer made, locked and its
    /// rows copied by the stream's own copy, and the cameras listed without opening one (no LED). The detail line, or throws.
    /// </summary>
    internal static unsafe string Probe()
    {
        return Pooled(() =>
        {
            if (!Load())
            {
                throw new InvalidOperationException("AVFoundation did not load");
            }

            foreach (string name in (ReadOnlySpan<string>)["AVCaptureSession", "AVCaptureDeviceInput", "AVCaptureVideoDataOutput", "AVCaptureDeviceDiscoverySession"])
            {
                if (TryClass(name) == 0)
                {
                    throw new InvalidOperationException($"no class {name}");
                }
            }

            nint instance = Send(Send(DelegateClass, Sel("alloc")), Sel("init"));
            bool responds = SendBoolNint(instance, Sel("respondsToSelector:"), Sel("captureOutput:didOutputSampleBuffer:fromConnection:")) != 0;
            SendVoid(instance, Sel("release"));
            if (!responds)
            {
                throw new InvalidOperationException("the delegate does not answer its selector");
            }

            if (AccessBlock() == 0)
            {
                throw new InvalidOperationException("the permission's block was not made");
            }

            var access = MacCameraRules.Access(Authorization());

            nint buffer;
            if (CVPixelBufferCreate(0, 6, 2, PixelFormatBgra, 0, &buffer) != 0 || buffer == 0)
            {
                throw new InvalidOperationException("CVPixelBufferCreate failed");
            }

            int rowBytes;
            try
            {
                _ = CVPixelBufferLockBaseAddress(buffer, 0);
                try
                {
                    rowBytes = (int)CVPixelBufferGetBytesPerRow(buffer);
                    byte* rows = (byte*)CVPixelBufferGetBaseAddress(buffer);
                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 6 * 4; x++)
                        {
                            rows[(y * rowBytes) + x] = (byte)((y * 24) + x);
                        }
                    }
                }
                finally
                {
                    _ = CVPixelBufferUnlockBaseAddress(buffer, 0);
                }

                var frame = Stream.Copy(buffer);
                if (frame.Width != 6 || frame.Height != 2 || frame.Bgrx[24] != 24 || frame.Bgrx[47] != 47)
                {
                    throw new InvalidOperationException($"a pixel buffer's rows ({rowBytes.ToString(CultureInfo.InvariantCulture)} bytes each) did not copy back");
                }
            }
            finally
            {
                CVPixelBufferRelease(buffer);
            }

            var devices = Discover();
            return $"AVFoundation bound, the delegate answers, the permission's block made; access {access}; a 6x2 32BGRA buffer ({rowBytes.ToString(CultureInfo.InvariantCulture)} bytes a row) copied; "
                + (devices.Count == 0 ? "no camera listed" : $"{devices.Count.ToString(CultureInfo.InvariantCulture)} camera(s): {string.Join(", ", devices.Select(d => d.Device.Name + (d.Suspended ? " (suspended)" : "")))}");
        });
    }

    private static T Pooled<T>(Func<T> work)
    {
        nint pool = objc_autoreleasePoolPush();
        try
        {
            return work();
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The cameras of <see cref="MacCameraRules.DeviceTypes"/> in AVFoundation's order, each with whether it is suspended.</summary>
    private static List<(CameraDevice Device, bool Suspended)> Discover()
    {
        if (!Load())
        {
            throw new CameraException(CameraFailure.Unsupported, null);
        }

        nint discovery = TryClass("AVCaptureDeviceDiscoverySession");
        nint video = MediaTypeVideo;
        var types = MacCameraRules.DeviceTypes.Select(t => Constant(AVFoundationPath, t)).Where(t => t != 0).ToList();
        if (discovery == 0 || video == 0 || types.Count == 0)
        {
            throw new CameraException(CameraFailure.Unsupported, null);
        }

        return Quietly(() =>
        {
            nint array = Send(Class("NSMutableArray"), Sel("array"));
            foreach (nint type in types)
            {
                SendVoid(array, Sel("addObject:"), type);
            }

            nint session = SendNintNintLong(discovery, Sel("discoverySessionWithDeviceTypes:mediaType:position:"), array, video, 0);
            nint devices = session == 0 ? 0 : Send(session, Sel("devices"));
            long count = devices == 0 ? 0 : (long)Send(devices, Sel("count"));
            var list = new List<(CameraDevice, bool)>((int)count);
            for (long i = 0; i < count; i++)
            {
                nint device = SendIndex(devices, Sel("objectAtIndex:"), (nuint)i);
                string name = FromNSString(Send(device, Sel("localizedName")))?.Trim() is { Length: > 0 } named ? named : CameraText.UnnamedCamera;
                string id = FromNSString(Send(device, Sel("uniqueID"))) ?? name;
                list.Add((new CameraDevice(name, id), SendBool(device, Sel("isSuspended")) != 0));
            }

            return list;
        });
    }

    /// <summary>The permission settled: granted goes on; denied, restricted or unanswered throws the sentence that says so.</summary>
    private static void EnsureAccess()
    {
        var access = MacCameraRules.Access(Authorization());
        if (access == CameraAccess.NotDetermined)
        {
            DiagnosticLog.Info(Category, "Asking macOS for the camera permission.");
            _ = RequestAccess(AnswerWait);
            access = MacCameraRules.Access(Authorization());   // the status, not the block's word, decides
        }

        switch (access)
        {
            case CameraAccess.Authorized:
                return;
            case CameraAccess.NotDetermined:
                throw new CameraException(CameraFailure.Asking, null);
            case CameraAccess.Restricted:
                throw new CameraException(CameraFailure.Restricted, null);
            case CameraAccess.Denied:
                throw new CameraException(CameraFailure.Blocked, null);
            default:
                throw new CameraException(CameraFailure.Unsupported, null);
        }
    }

    private static ICameraStream OpenPooled(CameraDevice device, CameraSize target)
    {
        if (!Load())
        {
            throw new CameraException(CameraFailure.Unsupported, null);
        }

        EnsureAccess();
        nint captureDevice = Send(TryClass("AVCaptureDevice"), Sel("deviceWithUniqueID:"), NSString(device.Id));
        if (captureDevice == 0 || SendBool(captureDevice, Sel("isConnected")) == 0)
        {
            throw new CameraException(CameraFailure.Unplugged, device.Name);
        }

        if (SendBool(captureDevice, Sel("isSuspended")) != 0)
        {
            throw new CameraException(CameraFailure.Suspended, device.Name);
        }

        string? deviceType = FromNSString(Send(captureDevice, Sel("deviceType")));
        bool continuity = SendBool(captureDevice, Sel("isContinuityCamera")) != 0;   // macOS 13 and later; this layer needs 14
        var stream = new Stream(device.Name, captureDevice, MacCameraRules.WarmupFor(deviceType, continuity));
        try
        {
            stream.Start(target);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One open camera. The delegate's queue puts each new pixel buffer in a one-buffer slot (retained, the one it replaces let go):
    /// the latest wins, and holding one never starves AVFoundation's pool. <see cref="Read"/> waits for a buffer newer than the last
    /// it took and takes it out of the slot (its own now, so the queue can never swap it away mid-copy), copies it when asked, and lets
    /// it go. A read that waits gives up its lock every half second to ask whether the camera went away (unplugged, the lid closed,
    /// the session stopped): never on time alone, since the built-in camera's first frame takes 1.75 s; a camera that only stops
    /// sending is the session's <see cref="CameraFailure.NoFrames"/>.
    /// </summary>
    private sealed class Stream : ICameraStream, ISink
    {
        private static readonly TimeSpan HealthEvery = TimeSpan.FromMilliseconds(500);

        private readonly object _gate = new();
        private readonly string _name;
        private readonly nint _device;
        private nint _session;
        private nint _input;
        private nint _output;
        private nint _delegate;
        private nint _queue;
        private nint _latest;
        private long _arrived;
        private long _taken;
        private bool _interrupted;
        private bool _locked;
        private bool _disposed;
        private bool _closing;

        public Stream(string name, nint device, CameraWarmup warmup)
        {
            _name = name;
            _device = Send(device, Sel("retain"));
            Warmup = warmup;
        }

        public CameraFormat Native { get; private set; } = new(default, 0, "");

        public CameraWarmup Warmup { get; }

        public unsafe void Start(CameraSize target)
        {
            var (format, range, native) = ChooseFormat(target);
            _session = Send(Send(TryClass("AVCaptureSession"), Sel("alloc")), Sel("init"));
            SendVoid(_session, Sel("beginConfiguration"));

            nint error = 0;
            nint input = SendNintPtr(TryClass("AVCaptureDeviceInput"), Sel("deviceInputWithDevice:error:"), _device, &error);
            if (input == 0)
            {
                throw Failure(error, "deviceInputWithDevice");
            }

            _input = Send(input, Sel("retain"));
            if (SendBoolNint(_session, Sel("canAddInput:"), _input) == 0)
            {
                throw new CameraException(CameraFailure.InUse, "the session cannot take the camera's input");
            }

            SendVoid(_session, Sel("addInput:"), _input);

            error = 0;
            if (SendBoolPtr(_device, Sel("lockForConfiguration:"), &error) == 0)
            {
                throw Failure(error, "lockForConfiguration");
            }

            _locked = true;
            SendVoid(_device, Sel("setActiveFormat:"), format);
            double minFps = SendDouble(range, Sel("minFrameRate"));
            double maxFps = SendDouble(range, Sel("maxFrameRate"));
            var duration = MacCameraRules.DurationFor(minFps, maxFps) switch
            {
                FrameDuration.RangeShortest => SendTime(range, Sel("minFrameDuration")),
                FrameDuration.RangeLongest => SendTime(range, Sel("maxFrameDuration")),
                _ => new CMTime { Value = 1, Timescale = 30, Flags = 1 },
            };
            SendVoidTime(_device, Sel("setActiveVideoMinFrameDuration:"), duration);

            _output = Send(Send(TryClass("AVCaptureVideoDataOutput"), Sel("alloc")), Sel("init"));
            nint key = Constant("/System/Library/Frameworks/CoreVideo.framework/CoreVideo", "kCVPixelBufferPixelFormatTypeKey");
            nint bgra = SendUInt32Number(Class("NSNumber"), Sel("numberWithUnsignedInt:"), PixelFormatBgra);
            SendVoid(_output, Sel("setVideoSettings:"), Send(Class("NSDictionary"), Sel("dictionaryWithObject:forKey:"), bgra, key));
            SendVoidBool(_output, Sel("setAlwaysDiscardsLateVideoFrames:"), 1);
            _queue = dispatch_queue_create("NeonSidekick.Camera", 0);
            _delegate = NewDelegate(this);
            SendVoid(_output, Sel("setSampleBufferDelegate:queue:"), _delegate, _queue);
            if (SendBoolNint(_session, Sel("canAddOutput:"), _output) == 0)
            {
                throw new CameraException(CameraFailure.Failed, "the session cannot take a video output");
            }

            SendVoid(_session, Sel("addOutput:"), _output);
            SendVoid(_session, Sel("commitConfiguration"));
            SendVoid(_session, Sel("startRunning"));   // blocks until running (112–650 ms measured)
            Unlock();
            if (SendBool(_session, Sel("isRunning")) == 0)
            {
                throw new CameraException(CameraFailure.Failed, "the capture session did not start");
            }

            Native = native;
            DiagnosticLog.Info(Category, $"Opened {_name} at {CameraFormats.Describe(native)}, delivering 32BGRA.");
        }

        /// <summary>The device's format and frame-rate range nearest <paramref name="target"/>, as AVFoundation's own objects.</summary>
        private (nint Format, nint Range, CameraFormat Native) ChooseFormat(CameraSize target)
        {
            var candidates = new List<(CameraFormat Format, nint Device, nint Range)>();
            nint formats = Send(_device, Sel("formats"));
            long count = (long)Send(formats, Sel("count"));
            for (long i = 0; i < count; i++)
            {
                nint format = SendIndex(formats, Sel("objectAtIndex:"), (nuint)i);
                nint description = Send(format, Sel("formatDescription"));
                var size = CMVideoFormatDescriptionGetDimensions(description);
                string subtype = MacCameraRules.FourCc(CMFormatDescriptionGetMediaSubType(description));
                nint ranges = Send(format, Sel("videoSupportedFrameRateRanges"));
                long rangeCount = (long)Send(ranges, Sel("count"));
                for (long r = 0; r < rangeCount; r++)
                {
                    nint range = SendIndex(ranges, Sel("objectAtIndex:"), (nuint)r);
                    double fps = MacCameraRules.FpsIn(SendDouble(range, Sel("minFrameRate")), SendDouble(range, Sel("maxFrameRate")));
                    candidates.Add((new CameraFormat(new CameraSize(size.Width, size.Height), fps, subtype), format, range));
                }
            }

            var chosen = CameraFormats.Pick(candidates.Select(c => c.Format).ToList(), target)
                ?? throw new CameraException(CameraFailure.Failed, "the camera offers no video format");
            var match = candidates.First(c => ReferenceEquals(c.Format, chosen));
            return (match.Device, match.Range, chosen);
        }

        private static CameraException Failure(nint error, string call)
        {
            var (message, code) = Error(error);
            return new CameraException(MacCameraRules.FailureOf(code), $"{message ?? "no error"} ({code.ToString(CultureInfo.InvariantCulture)}) from {call}");
        }

        private void Unlock()
        {
            if (_locked)
            {
                _locked = false;
                SendVoid(_device, Sel("unlockForConfiguration"));
            }
        }

        /// <summary>On the delegate's queue: the buffer kept as the latest, the one it replaces let go, the reader woken.</summary>
        public void Arrived(nint sampleBuffer)
        {
            nint image = CMSampleBufferGetImageBuffer(sampleBuffer);
            if (image == 0)
            {
                return;
            }

            _ = CVPixelBufferRetain(image);
            nint replaced;
            lock (_gate)
            {
                if (_disposed)
                {
                    replaced = image;
                }
                else
                {
                    replaced = _latest;
                    _latest = image;
                    _arrived++;
                    Monitor.PulseAll(_gate);
                }
            }

            if (replaced != 0)
            {
                CVPixelBufferRelease(replaced);
            }
        }

        public CameraFrame? Read(bool copy)
        {
            while (true)
            {
                nint buffer = 0;
                lock (_gate)
                {
                    if (_interrupted)
                    {
                        return null;
                    }

                    if (_arrived > _taken)
                    {
                        buffer = _latest;
                        _latest = 0;
                        _taken = _arrived;
                    }
                    else if (Monitor.Wait(_gate, HealthEvery))
                    {
                        continue;
                    }
                }

                if (buffer == 0)
                {
                    CheckHealth();
                    continue;
                }

                try
                {
                    return copy ? Copy(buffer) : new CameraFrame((int)CVPixelBufferGetWidth(buffer), (int)CVPixelBufferGetHeight(buffer), [], 0, default);
                }
                finally
                {
                    CVPixelBufferRelease(buffer);
                }
            }
        }

        /// <summary>Throws when the camera went away while no frame came: unplugged, its lid closed, or the session stopped.</summary>
        private void CheckHealth()
        {
            Pooled(() =>
            {
                if (SendBool(_device, Sel("isConnected")) == 0)
                {
                    throw new CameraException(CameraFailure.Unplugged, _name);
                }

                if (SendBool(_device, Sel("isSuspended")) != 0)
                {
                    throw new CameraException(CameraFailure.Suspended, _name);
                }

                if (SendBool(_session, Sel("isRunning")) == 0)
                {
                    throw new CameraException(CameraFailure.Unplugged, "the capture session stopped");
                }

                return 0;
            });
        }

        /// <summary>A 32BGRA pixel buffer's rows, padding and all, packed top-down (<see cref="CameraPixels.CopyTopDown"/>).</summary>
        internal static unsafe CameraFrame Copy(nint buffer)
        {
            if (CVPixelBufferGetPixelFormatType(buffer) != PixelFormatBgra || CVPixelBufferIsPlanar(buffer) != 0)
            {
                throw new CameraException(CameraFailure.Failed, $"the camera delivered {MacCameraRules.FourCc(CVPixelBufferGetPixelFormatType(buffer))}, not 32BGRA");
            }

            if (CVPixelBufferLockBaseAddress(buffer, LockReadOnly) != 0)
            {
                throw new CameraException(CameraFailure.Failed, "CVPixelBufferLockBaseAddress failed");
            }

            try
            {
                int width = (int)CVPixelBufferGetWidth(buffer);
                int height = (int)CVPixelBufferGetHeight(buffer);
                int rowBytes = (int)CVPixelBufferGetBytesPerRow(buffer);
                var rows = new ReadOnlySpan<byte>(CVPixelBufferGetBaseAddress(buffer), checked(rowBytes * height));
                var pixels = new byte[width * height * CameraPixels.BytesPerPixel];
                CameraPixels.CopyTopDown(rows, 0, rowBytes, width, height, pixels);
                return new CameraFrame(width, height, pixels, 0, default);
            }
            finally
            {
                _ = CVPixelBufferUnlockBaseAddress(buffer, LockReadOnly);
            }
        }

        public void Interrupt()
        {
            lock (_gate)
            {
                _interrupted = true;
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>
        /// Stopped and let go in the order that keeps a late callback safe: the session stopped (synchronous: 58–260 ms measured, the
        /// LED off with it), the delegate detached, the queue fenced so a callback in flight has finished, and only then the sink
        /// forgotten and the objects and the held buffer released.
        /// </summary>
        public void Dispose()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            Interrupt();
            Pooled(() =>
            {
                if (_session != 0 && SendBool(_session, Sel("isRunning")) != 0)
                {
                    SendVoid(_session, Sel("stopRunning"));
                }

                Unlock();
                if (_output != 0)
                {
                    SendVoid(_output, Sel("setSampleBufferDelegate:queue:"), 0, 0);
                }

                if (_queue != 0)
                {
                    Fence(_queue);
                }

                lock (_gate)
                {
                    _disposed = true;
                }

                if (_delegate != 0)
                {
                    Forget(_delegate);
                }

                foreach (nint owned in (ReadOnlySpan<nint>)[_output, _input, _session, _delegate, _device])
                {
                    if (owned != 0)
                    {
                        SendVoid(owned, Sel("release"));
                    }
                }

                if (_queue != 0)
                {
                    dispatch_release(_queue);
                }

                nint latest;
                lock (_gate)
                {
                    latest = _latest;
                    _latest = 0;
                }

                if (latest != 0)
                {
                    CVPixelBufferRelease(latest);
                }

                return 0;
            });
            _output = _input = _session = _delegate = _queue = 0;
            DiagnosticLog.Debug(Category, "Closed the camera.");
        }

        public override string ToString() => _name;
    }
}
