using System.Globalization;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Camera;

/// <summary>
/// The cameras through Media Foundation (2026-10-02): <see cref="List"/> is <c>MFEnumDeviceSources</c> over the video-capture
/// sources, <see cref="Open"/> activates one, puts a source reader on it with video processing on (the reader adds the MJPEG
/// decoder and the colour converter a camera needs), runs the camera at the native format nearest the target
/// (<see cref="CameraFormats.Pick"/>) and asks the reader for RGB32. Every call happens on the caller's thread, which must be
/// MTA or uninitialised (the session's reader thread is the latter, and joins the MTA here); nothing is activated by a list,
/// so listing never lights the camera's LED.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationCameraSystem : ICameraSystem
{
    private const string Category = "Camera";

    public IReadOnlyList<CameraDevice> List()
    {
        using var apartment = Apartment.Enter();
        var (activates, count) = Enumerate();
        try
        {
            var devices = new List<CameraDevice>((int)count);
            for (uint i = 0; i < count; i++)
            {
                nint activate = ActivateAt(activates, i);
                string name = CameraNative.GetString(activate, CameraNative.DevSourceFriendlyName) ?? CameraText.UnnamedCamera;
                string id = CameraNative.GetString(activate, CameraNative.DevSourceVidcapSymbolicLink) ?? name;
                devices.Add(new CameraDevice(name.Trim(), id));
            }

            return devices;
        }
        finally
        {
            FreeActivates(activates, count);
        }
    }

    public ICameraStream Open(CameraDevice device, CameraSize target)
    {
        ArgumentNullException.ThrowIfNull(device);
        var apartment = Apartment.Enter();
        nint activate = 0;
        nint source = 0;
        nint reader = 0;
        try
        {
            var (activates, count) = Enumerate();
            try
            {
                for (uint i = 0; i < count && activate == 0; i++)
                {
                    nint candidate = ActivateAt(activates, i);
                    if (string.Equals(CameraNative.GetString(candidate, CameraNative.DevSourceVidcapSymbolicLink), device.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        activate = candidate;
                        _ = AddRef(activate);
                    }
                }
            }
            finally
            {
                FreeActivates(activates, count);
            }

            if (activate == 0)
            {
                throw new CameraException(CameraFailure.Unplugged, device.Name);
            }

            Check(CameraNative.ActivateObject(activate, CameraNative.IidMediaSource, out source), "ActivateObject");
            Check(CameraNative.MFCreateAttributes(out nint attributes, 1), "MFCreateAttributes");
            try
            {
                Check(CameraNative.SetUint32(attributes, CameraNative.ReaderEnableVideoProcessing, 1), "SetUINT32");
                Check(CameraNative.MFCreateSourceReaderFromMediaSource(source, attributes, out reader), "MFCreateSourceReaderFromMediaSource");
            }
            finally
            {
                CameraNative.Release(attributes);
            }

            _ = CameraNative.SetStreamSelection(reader, CameraNative.AllStreams, false);
            Check(CameraNative.SetStreamSelection(reader, CameraNative.FirstVideoStream, true), "SetStreamSelection");
            var native = ChooseNative(reader, target);
            var stream = new Stream(apartment, activate, source, reader, native);
            stream.ReadCurrentType();
            DiagnosticLog.Info(Category, $"Opened {device.Name} at {CameraFormats.Describe(native)}, delivering {stream.Width}x{stream.Height} RGB32.");
            return stream;
        }
        catch
        {
            CameraNative.Release(reader);
            if (source != 0)
            {
                _ = CameraNative.ShutdownSource(source);
                CameraNative.Release(source);
            }

            if (activate != 0)
            {
                _ = CameraNative.ShutdownObject(activate);
                CameraNative.Release(activate);
            }

            apartment.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The smoke's proof of the slots that need no camera (<c>camera:mf</c>): an attribute store's GUID and UINT64 round trip
    /// on a media type; a memory buffer's length, added to a sample, made contiguous and locked; a bottom-up RGB32 2-D buffer
    /// locked for its negative pitch; the device list read without activating anything (no LED). The detail line, or throws.
    /// </summary>
    internal static unsafe string ProbeSlots()
    {
        using var apartment = Apartment.Enter();
        Check(CameraNative.MFCreateMediaType(out nint type), "MFCreateMediaType");
        try
        {
            Check(CameraNative.SetGuid(type, CameraNative.MtSubtype, CameraNative.VideoFormatRgb32), "SetGUID");
            Check(CameraNative.GetGuid(type, CameraNative.MtSubtype, out Guid subtype), "GetGUID");
            var size = new CameraSize(1280, 720);
            Check(CameraNative.SetUint64(type, CameraNative.MtFrameSize, CameraFormats.PackSize(size)), "SetUINT64");
            Check(CameraNative.GetUint64(type, CameraNative.MtFrameSize, out ulong packed), "GetUINT64");
            Check(CameraNative.SetUint32(type, CameraNative.MtDefaultStride, unchecked((uint)-5120)), "SetUINT32");
            Check(CameraNative.GetUint32(type, CameraNative.MtDefaultStride, out uint stride), "GetUINT32");
            if (subtype != CameraNative.VideoFormatRgb32 || CameraFormats.UnpackSize(packed) != size || unchecked((int)stride) != -5120)
            {
                throw new InvalidOperationException("an attribute did not read back");
            }
        }
        finally
        {
            CameraNative.Release(type);
        }

        Check(CameraNative.MFCreateSample(out nint sample), "MFCreateSample");
        Check(CameraNative.MFCreateMemoryBuffer(64, out nint memory), "MFCreateMemoryBuffer");
        try
        {
            Check(CameraNative.SetCurrentLength(memory, 48), "SetCurrentLength");
            Check(CameraNative.AddBuffer(sample, memory), "AddBuffer");
            Check(CameraNative.ConvertToContiguousBuffer(sample, out nint contiguous), "ConvertToContiguousBuffer");
            try
            {
                Check(CameraNative.Lock(contiguous, out byte* data, out uint max, out uint length), "Lock");
                _ = CameraNative.Unlock(contiguous);
                if (data is null || length != 48 || max < 48)
                {
                    throw new InvalidOperationException($"the locked buffer reads {length} of {max}");
                }
            }
            finally
            {
                CameraNative.Release(contiguous);
            }
        }
        finally
        {
            CameraNative.Release(memory);
            CameraNative.Release(sample);
        }

        int pitch;
        Check(CameraNative.MFCreate2DMediaBuffer(16, 4, CameraNative.FourCcRgb32, 1, out nint planar), "MFCreate2DMediaBuffer");
        try
        {
            Check(CameraNative.QueryInterface(planar, CameraNative.IidMf2DBuffer, out nint buffer2D), "QueryInterface(IMF2DBuffer)");
            try
            {
                Check(CameraNative.Lock2D(buffer2D, out _, out pitch), "Lock2D");
                _ = CameraNative.Unlock2D(buffer2D);
                if (pitch != -16 * CameraPixels.BytesPerPixel)
                {
                    throw new InvalidOperationException($"a bottom-up buffer's pitch reads {pitch}");
                }
            }
            finally
            {
                CameraNative.Release(buffer2D);
            }
        }
        finally
        {
            CameraNative.Release(planar);
        }

        var (activates, count) = Enumerate();
        var names = new List<string>((int)count);
        try
        {
            for (uint i = 0; i < count; i++)
            {
                names.Add(CameraNative.GetString(ActivateAt(activates, i), CameraNative.DevSourceFriendlyName) ?? CameraText.UnnamedCamera);
            }
        }
        finally
        {
            FreeActivates(activates, count);
        }

        return $"attributes, sample, buffers and a bottom-up 2-D lock (pitch {pitch.ToString(CultureInfo.InvariantCulture)}) read back; "
            + (count == 0 ? "no camera listed" : $"{count.ToString(CultureInfo.InvariantCulture)} camera(s): {string.Join(", ", names)}");
    }

    /// <summary>Sets the camera to its format nearest <paramref name="target"/>, then asks the reader for RGB32.</summary>
    private static CameraFormat ChooseNative(nint reader, CameraSize target)
    {
        var formats = new List<(CameraFormat Format, uint Index)>();
        for (uint index = 0; ; index++)
        {
            int hr = CameraNative.GetNativeMediaType(reader, CameraNative.FirstVideoStream, index, out nint type);
            if (hr == CameraNative.MfENoMoreTypes)
            {
                break;
            }

            Check(hr, "GetNativeMediaType");
            try
            {
                _ = CameraNative.GetUint64(type, CameraNative.MtFrameSize, out ulong size);
                _ = CameraNative.GetUint64(type, CameraNative.MtFrameRate, out ulong rate);
                _ = CameraNative.GetGuid(type, CameraNative.MtSubtype, out Guid subtype);
                formats.Add((new CameraFormat(CameraFormats.UnpackSize(size), CameraFormats.UnpackRate(rate), CameraFormats.SubtypeName(subtype)), index));
            }
            finally
            {
                CameraNative.Release(type);
            }
        }

        var chosen = CameraFormats.Pick(formats.Select(f => f.Format).ToList(), target)
            ?? throw new CameraException(CameraFailure.Failed, "the camera offers no video format");
        uint chosenIndex = formats.First(f => ReferenceEquals(f.Format, chosen)).Index;
        Check(CameraNative.GetNativeMediaType(reader, CameraNative.FirstVideoStream, chosenIndex, out nint nativeType), "GetNativeMediaType");
        try
        {
            Check(CameraNative.SetCurrentMediaType(reader, CameraNative.FirstVideoStream, nativeType), "SetCurrentMediaType(native)");
        }
        finally
        {
            CameraNative.Release(nativeType);
        }

        Check(CameraNative.MFCreateMediaType(out nint rgb), "MFCreateMediaType");
        try
        {
            Check(CameraNative.SetGuid(rgb, CameraNative.MtMajorType, CameraNative.MediaTypeVideo), "SetGUID");
            Check(CameraNative.SetGuid(rgb, CameraNative.MtSubtype, CameraNative.VideoFormatRgb32), "SetGUID");
            Check(CameraNative.SetCurrentMediaType(reader, CameraNative.FirstVideoStream, rgb), "SetCurrentMediaType(RGB32)");
        }
        finally
        {
            CameraNative.Release(rgb);
        }

        return chosen;
    }

    /// <summary>The video-capture sources: the <c>IMFActivate*</c> array (COM memory) and its length.</summary>
    private static (nint Activates, uint Count) Enumerate()
    {
        Check(CameraNative.MFCreateAttributes(out nint attributes, 1), "MFCreateAttributes");
        try
        {
            Check(CameraNative.SetGuid(attributes, CameraNative.DevSourceType, CameraNative.DevSourceTypeVidcap), "SetGUID");
            Check(CameraNative.MFEnumDeviceSources(attributes, out nint activates, out uint count), "MFEnumDeviceSources");
            return (activates, count);
        }
        finally
        {
            CameraNative.Release(attributes);
        }
    }

    private static unsafe nint ActivateAt(nint activates, uint index) => ((nint*)activates)[index];

    private static unsafe void FreeActivates(nint activates, uint count)
    {
        if (activates == 0)
        {
            return;
        }

        for (uint i = 0; i < count; i++)
        {
            CameraNative.Release(((nint*)activates)[i]);
        }

        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(activates);
    }

    private static unsafe uint AddRef(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[1])(unknown);

    /// <summary>Throws the <see cref="CameraException"/> an HRESULT stands for (<see cref="CameraText.FailureOf"/>).</summary>
    internal static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new CameraException(CameraText.FailureOf(hr), CameraText.Hresult(hr) + " from " + call);
        }
    }

    /// <summary>
    /// The thread's COM apartment and Media Foundation's startup, undone in reverse on dispose. A thread already in the STA
    /// (<c>RPC_E_CHANGED_MODE</c>) still works for the free-threaded MF objects, so that is not a failure; a missing
    /// <c>mfplat.dll</c> is <see cref="CameraFailure.NoMediaFoundation"/>.
    /// </summary>
    private sealed class Apartment : IDisposable
    {
        private bool _uninitialize;
        private bool _shutdown;

        public static Apartment Enter()
        {
            var apartment = new Apartment();
            int co = CameraNative.CoInitializeEx(0, CameraNative.CoinitMultithreaded);
            apartment._uninitialize = co >= 0;
            if (co < 0 && co != CameraNative.RpcEChangedMode)
            {
                throw new CameraException(CameraFailure.Failed, CameraText.Hresult(co) + " from CoInitializeEx");
            }

            try
            {
                Check(CameraNative.MFStartup(CameraNative.MfVersion, CameraNative.MfStartupLite), "MFStartup");
                apartment._shutdown = true;
            }
            catch (DllNotFoundException e)
            {
                apartment.Dispose();
                throw new CameraException(CameraFailure.NoMediaFoundation, null, e);
            }
            catch
            {
                apartment.Dispose();
                throw;
            }

            return apartment;
        }

        public void Dispose()
        {
            if (_shutdown)
            {
                _shutdown = false;
                _ = CameraNative.MFShutdown();
            }

            if (_uninitialize)
            {
                _uninitialize = false;
                CameraNative.CoUninitialize();
            }
        }
    }

    /// <summary>One open camera: the activate, its media source, the reader and the thread's apartment, released in that order's reverse.</summary>
    private sealed class Stream : ICameraStream
    {
        private readonly Apartment _apartment;
        private readonly nint _activate;
        private readonly nint _source;
        private readonly nint _reader;
        private int _interrupted;
        private bool _disposed;
        private int _stride;

        public Stream(Apartment apartment, nint activate, nint source, nint reader, CameraFormat native)
        {
            _apartment = apartment;
            _activate = activate;
            _source = source;
            _reader = reader;
            Native = native;
        }

        public CameraFormat Native { get; }

        public CameraWarmup Warmup => CameraWarmup.Webcam;

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>The size and default stride the reader delivers now (read again when the type changes mid-stream).</summary>
        public void ReadCurrentType()
        {
            Check(CameraNative.GetCurrentMediaType(_reader, CameraNative.FirstVideoStream, out nint type), "GetCurrentMediaType");
            try
            {
                Check(CameraNative.GetUint64(type, CameraNative.MtFrameSize, out ulong size), "GetUINT64(FRAME_SIZE)");
                var unpacked = CameraFormats.UnpackSize(size);
                Width = unpacked.Width;
                Height = unpacked.Height;
                _stride = CameraNative.GetUint32(type, CameraNative.MtDefaultStride, out uint stride) >= 0 ? unchecked((int)stride) : Width * CameraPixels.BytesPerPixel;
            }
            finally
            {
                CameraNative.Release(type);
            }
        }

        public CameraFrame? Read(bool copy)
        {
            while (Volatile.Read(ref _interrupted) == 0)
            {
                int hr = CameraNative.ReadSample(_reader, CameraNative.FirstVideoStream, out uint flags, out nint sample);
                if (Volatile.Read(ref _interrupted) != 0 || hr == CameraNative.MfEShutdown)
                {
                    CameraNative.Release(sample);
                    return null;
                }

                try
                {
                    Check(hr, "ReadSample");
                    if ((flags & CameraNative.ReaderFlagError) != 0)
                    {
                        throw new CameraException(CameraFailure.Unplugged, "ReadSample reported a stream error");
                    }

                    if ((flags & CameraNative.ReaderFlagEndOfStream) != 0)
                    {
                        throw new CameraException(CameraFailure.Unplugged, "the stream ended");
                    }

                    if ((flags & CameraNative.ReaderFlagCurrentMediaTypeChanged) != 0)
                    {
                        ReadCurrentType();
                    }

                    if (sample == 0)
                    {
                        continue;
                    }

                    return copy ? Copy(sample) : new CameraFrame(Width, Height, [], 0, default);
                }
                finally
                {
                    CameraNative.Release(sample);
                }
            }

            return null;
        }

        private unsafe CameraFrame Copy(nint sample)
        {
            Check(CameraNative.ConvertToContiguousBuffer(sample, out nint buffer), "ConvertToContiguousBuffer");
            try
            {
                var pixels = new byte[Width * Height * CameraPixels.BytesPerPixel];
                if (CameraNative.QueryInterface(buffer, CameraNative.IidMf2DBuffer, out nint buffer2D) >= 0)
                {
                    try
                    {
                        Check(CameraNative.Lock2D(buffer2D, out byte* scanline0, out int pitch), "Lock2D");
                        try
                        {
                            CopyRows(scanline0, pitch, pixels);
                        }
                        finally
                        {
                            _ = CameraNative.Unlock2D(buffer2D);
                        }
                    }
                    finally
                    {
                        CameraNative.Release(buffer2D);
                    }
                }
                else
                {
                    Check(CameraNative.Lock(buffer, out byte* data, out _, out uint length), "Lock");
                    try
                    {
                        int pitch = _stride == 0 ? Width * CameraPixels.BytesPerPixel : _stride;
                        int rowSpan = Math.Abs(pitch);
                        if ((long)rowSpan * Height > length)
                        {
                            throw new CameraException(CameraFailure.Failed, "the frame buffer is shorter than its size says");
                        }

                        byte* first = pitch < 0 ? data + ((long)rowSpan * (Height - 1)) : data;
                        CopyRows(first, pitch, pixels);
                    }
                    finally
                    {
                        _ = CameraNative.Unlock(buffer);
                    }
                }

                return new CameraFrame(Width, Height, pixels, 0, default);
            }
            finally
            {
                CameraNative.Release(buffer);
            }
        }

        /// <summary>Rows from <paramref name="first"/> (row 0) at <paramref name="pitch"/> bytes apart into the packed array.</summary>
        private unsafe void CopyRows(byte* first, int pitch, byte[] pixels)
        {
            int rowSpan = Math.Abs(pitch);
            long total = (long)rowSpan * Height;
            byte* lowest = pitch < 0 ? first + ((long)pitch * (Height - 1)) : first;
            var span = new ReadOnlySpan<byte>(lowest, checked((int)total));
            int firstRow = pitch < 0 ? rowSpan * (Height - 1) : 0;
            CameraPixels.CopyTopDown(span, firstRow, pitch, Width, Height, pixels);
        }

        public void Interrupt()
        {
            if (Interlocked.Exchange(ref _interrupted, 1) == 0)
            {
                // Thread-safe by contract: a ReadSample blocked on the device returns MF_E_SHUTDOWN.
                _ = CameraNative.ShutdownSource(_source);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interrupt();
            CameraNative.Release(_reader);
            CameraNative.Release(_source);
            _ = CameraNative.ShutdownObject(_activate);
            CameraNative.Release(_activate);
            _apartment.Dispose();
            DiagnosticLog.Debug(Category, "Closed the camera.");
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");
    }
}
