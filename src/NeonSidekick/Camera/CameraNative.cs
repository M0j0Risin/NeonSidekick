using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Camera;

/// <summary>
/// The native calls behind the camera (2026-10-02, the user's ask: a USB camera's pictures for the model): Windows' own Media
/// Foundation, four system libraries — <c>mfplat.dll</c> (startup, attribute stores, media types), <c>mf.dll</c> (the device
/// list), <c>mfreadwrite.dll</c> (the source reader that decodes and converts a camera's NV12/YUY2/MJPG to RGB32) and
/// <c>ole32.dll</c> (the reader thread's MTA). Every COM interface is called through its vtable with unmanaged function
/// pointers (the <c>PerfNative</c> DXGI shape): no <c>ComWrappers</c>, nothing for AOT to marshal. WinRT's MediaCapture was
/// the other road and was not taken: it needs a <c>-windows</c> target framework and CsWinRT; DirectShow is legacy and ffmpeg
/// would be a process-start site. Windows-only, like the other layers the portability note lists;
/// <see cref="MediaFoundationCameraSystem"/> is the guarded way in, and the published exe's smoke proves the slots that need no
/// camera (<c>camera:mf</c>) while <c>--camera-check</c> proves the rest on a real one. System libraries, so none joins
/// <c>SmokeChecks.RequiredNativeLibraries</c> (the <c>PrintNative</c> precedent).
///
/// <para>Slots from the SDK headers' declaration order: IUnknown 0–2; IMFAttributes 3–32; IMFActivate 33–35 and IMFMediaType
/// 33–37 and IMFSample 33–46 on top of it; IMFMediaEventGenerator 3–6 under IMFMediaSource 7–12; IMFSourceReader 3–12;
/// IMFMediaBuffer 3–7; IMF2DBuffer 3–8.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class CameraNative
{
    // ── HRESULTs (mferror.h, winerror.h) ─────────────────────────────────────

    internal const int SOk = 0;
    internal const int EAccessDenied = unchecked((int)0x80070005);
    internal const int ESharingViolation = unchecked((int)0x80070020);
    internal const int EDeviceNotConnected = unchecked((int)0x8007048F);
    internal const int EFileNotFound = unchecked((int)0x80070002);
    internal const int EGenFailure = unchecked((int)0x8007001F);
    internal const int ENoInterface = unchecked((int)0x80004002);
    internal const int RpcEChangedMode = unchecked((int)0x80010106);
    internal const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
    internal const int MfEShutdown = unchecked((int)0xC00D3E85);
    internal const int MfEHwMftFailedStartStreaming = unchecked((int)0xC00D3704);
    internal const int MfEVideoRecordingDeviceInvalidated = unchecked((int)0xC00DABE0);
    internal const int MfEVideoRecordingDevicePreempted = unchecked((int)0xC00DABE1);
    internal const int MfEAttributeNotFound = unchecked((int)0xC00D36E6);

    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary><c>MF_VERSION</c>: <c>MF_SDK_VERSION</c> 2 in the high word, <c>MF_API_VERSION</c> 0x70 in the low.</summary>
    internal const uint MfVersion = 0x00020070;

    /// <summary><c>MFSTARTUP_LITE</c>: no socket library, nothing a camera needs.</summary>
    internal const uint MfStartupLite = 1;

    internal const uint CoinitMultithreaded = 0;

    internal const uint FirstVideoStream = 0xFFFFFFFC;
    internal const uint AllStreams = 0xFFFFFFFE;

    internal const uint ReaderFlagError = 0x1;
    internal const uint ReaderFlagEndOfStream = 0x2;
    internal const uint ReaderFlagCurrentMediaTypeChanged = 0x20;
    internal const uint ReaderFlagStreamTick = 0x100;

    /// <summary><c>D3DFMT_X8R8G8B8</c>, RGB32's FourCC for <see cref="MFCreate2DMediaBuffer"/>.</summary>
    internal const uint FourCcRgb32 = 22;

    // ── GUIDs (mfapi.h, mfidl.h, mfreadwrite.h) ──────────────────────────────

    internal static readonly Guid DevSourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    internal static readonly Guid DevSourceTypeVidcap = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    internal static readonly Guid DevSourceFriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    internal static readonly Guid DevSourceVidcapSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    internal static readonly Guid ReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    internal static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    internal static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    internal static readonly Guid IidMediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    internal static readonly Guid IidMf2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

    // ── Slots ────────────────────────────────────────────────────────────────

    private const int QueryInterfaceSlot = 0;
    private const int ReleaseSlot = 2;
    private const int GetUint32Slot = 7;
    private const int GetUint64Slot = 8;
    private const int GetGuidSlot = 10;
    private const int GetAllocatedStringSlot = 13;
    private const int SetUint32Slot = 21;
    private const int SetUint64Slot = 22;
    private const int SetGuidSlot = 24;
    private const int ActivateObjectSlot = 33;
    private const int ShutdownObjectSlot = 34;
    private const int MediaSourceShutdownSlot = 12;
    private const int SetStreamSelectionSlot = 4;
    private const int GetNativeMediaTypeSlot = 5;
    private const int GetCurrentMediaTypeSlot = 6;
    private const int SetCurrentMediaTypeSlot = 7;
    private const int ReadSampleSlot = 9;
    private const int AddBufferSlot = 42;
    private const int ConvertToContiguousBufferSlot = 41;
    private const int BufferLockSlot = 3;
    private const int BufferUnlockSlot = 4;
    private const int BufferSetCurrentLengthSlot = 6;
    private const int Lock2DSlot = 3;
    private const int Unlock2DSlot = 4;

    // ── Imports ──────────────────────────────────────────────────────────────

    [LibraryImport("mfplat.dll")]
    internal static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateAttributes(out nint attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateMediaType(out nint mediaType);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateSample(out nint sample);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateMemoryBuffer(uint maxLength, out nint buffer);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreate2DMediaBuffer(uint width, uint height, uint fourCc, int bottomUp, out nint buffer);

    [LibraryImport("mf.dll")]
    internal static partial int MFEnumDeviceSources(nint attributes, out nint activates, out uint count);

    [LibraryImport("mfreadwrite.dll")]
    internal static partial int MFCreateSourceReaderFromMediaSource(nint mediaSource, nint attributes, out nint reader);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    // ── IUnknown ─────────────────────────────────────────────────────────────

    internal static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            _ = ((delegate* unmanaged<nint, uint>)Slot(unknown, ReleaseSlot))(unknown);
        }
    }

    internal static int QueryInterface(nint unknown, Guid iid, out nint found)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(unknown, QueryInterfaceSlot))(unknown, &iid, &result);
        found = hr >= 0 ? result : 0;
        return hr;
    }

    // ── IMFAttributes ────────────────────────────────────────────────────────

    internal static int GetUint32(nint attributes, Guid key, out uint value)
    {
        uint read;
        int hr = ((delegate* unmanaged<nint, Guid*, uint*, int>)Slot(attributes, GetUint32Slot))(attributes, &key, &read);
        value = hr >= 0 ? read : 0;
        return hr;
    }

    internal static int GetUint64(nint attributes, Guid key, out ulong value)
    {
        ulong read;
        int hr = ((delegate* unmanaged<nint, Guid*, ulong*, int>)Slot(attributes, GetUint64Slot))(attributes, &key, &read);
        value = hr >= 0 ? read : 0;
        return hr;
    }

    internal static int GetGuid(nint attributes, Guid key, out Guid value)
    {
        Guid read;
        int hr = ((delegate* unmanaged<nint, Guid*, Guid*, int>)Slot(attributes, GetGuidSlot))(attributes, &key, &read);
        value = hr >= 0 ? read : Guid.Empty;
        return hr;
    }

    /// <summary>A string attribute, or null when the store has none; the COM copy is freed here.</summary>
    internal static string? GetString(nint attributes, Guid key)
    {
        char* text;
        uint length;
        int hr = ((delegate* unmanaged<nint, Guid*, char**, uint*, int>)Slot(attributes, GetAllocatedStringSlot))(attributes, &key, &text, &length);
        if (hr < 0 || text is null)
        {
            return null;
        }

        try
        {
            return new string(text, 0, (int)length);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)text);
        }
    }

    internal static int SetUint32(nint attributes, Guid key, uint value) =>
        ((delegate* unmanaged<nint, Guid*, uint, int>)Slot(attributes, SetUint32Slot))(attributes, &key, value);

    internal static int SetUint64(nint attributes, Guid key, ulong value) =>
        ((delegate* unmanaged<nint, Guid*, ulong, int>)Slot(attributes, SetUint64Slot))(attributes, &key, value);

    internal static int SetGuid(nint attributes, Guid key, Guid value) =>
        ((delegate* unmanaged<nint, Guid*, Guid*, int>)Slot(attributes, SetGuidSlot))(attributes, &key, &value);

    // ── IMFActivate, IMFMediaSource ──────────────────────────────────────────

    internal static int ActivateObject(nint activate, Guid iid, out nint created)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(activate, ActivateObjectSlot))(activate, &iid, &result);
        created = hr >= 0 ? result : 0;
        return hr;
    }

    internal static int ShutdownObject(nint activate) =>
        ((delegate* unmanaged<nint, int>)Slot(activate, ShutdownObjectSlot))(activate);

    internal static int ShutdownSource(nint mediaSource) =>
        ((delegate* unmanaged<nint, int>)Slot(mediaSource, MediaSourceShutdownSlot))(mediaSource);

    // ── IMFSourceReader ──────────────────────────────────────────────────────

    internal static int SetStreamSelection(nint reader, uint stream, bool selected) =>
        ((delegate* unmanaged<nint, uint, int, int>)Slot(reader, SetStreamSelectionSlot))(reader, stream, selected ? 1 : 0);

    internal static int GetNativeMediaType(nint reader, uint stream, uint index, out nint mediaType)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, uint, uint, nint*, int>)Slot(reader, GetNativeMediaTypeSlot))(reader, stream, index, &result);
        mediaType = hr >= 0 ? result : 0;
        return hr;
    }

    internal static int GetCurrentMediaType(nint reader, uint stream, out nint mediaType)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, uint, nint*, int>)Slot(reader, GetCurrentMediaTypeSlot))(reader, stream, &result);
        mediaType = hr >= 0 ? result : 0;
        return hr;
    }

    internal static int SetCurrentMediaType(nint reader, uint stream, nint mediaType) =>
        ((delegate* unmanaged<nint, uint, uint*, nint, int>)Slot(reader, SetCurrentMediaTypeSlot))(reader, stream, null, mediaType);

    /// <summary>One synchronous read: blocks until a sample, a stream tick, an end or an error (or the source's shutdown).</summary>
    internal static int ReadSample(nint reader, uint stream, out uint flags, out nint sample)
    {
        uint actual;
        uint streamFlags;
        long timestamp;
        nint result = 0;
        int hr = ((delegate* unmanaged<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, ReadSampleSlot))(reader, stream, 0, &actual, &streamFlags, &timestamp, &result);
        flags = streamFlags;
        sample = hr >= 0 ? result : 0;
        return hr;
    }

    // ── IMFSample, IMFMediaBuffer, IMF2DBuffer ───────────────────────────────

    internal static int ConvertToContiguousBuffer(nint sample, out nint buffer)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, nint*, int>)Slot(sample, ConvertToContiguousBufferSlot))(sample, &result);
        buffer = hr >= 0 ? result : 0;
        return hr;
    }

    internal static int AddBuffer(nint sample, nint buffer) =>
        ((delegate* unmanaged<nint, nint, int>)Slot(sample, AddBufferSlot))(sample, buffer);

    internal static int Lock(nint buffer, out byte* data, out uint maxLength, out uint currentLength)
    {
        byte* bytes;
        uint max;
        uint current;
        int hr = ((delegate* unmanaged<nint, byte**, uint*, uint*, int>)Slot(buffer, BufferLockSlot))(buffer, &bytes, &max, &current);
        data = hr >= 0 ? bytes : null;
        maxLength = max;
        currentLength = current;
        return hr;
    }

    internal static int Unlock(nint buffer) =>
        ((delegate* unmanaged<nint, int>)Slot(buffer, BufferUnlockSlot))(buffer);

    internal static int SetCurrentLength(nint buffer, uint length) =>
        ((delegate* unmanaged<nint, uint, int>)Slot(buffer, BufferSetCurrentLengthSlot))(buffer, length);

    internal static int Lock2D(nint buffer2D, out byte* scanline0, out int pitch)
    {
        byte* first;
        int step;
        int hr = ((delegate* unmanaged<nint, byte**, int*, int>)Slot(buffer2D, Lock2DSlot))(buffer2D, &first, &step);
        scanline0 = hr >= 0 ? first : null;
        pitch = hr >= 0 ? step : 0;
        return hr;
    }

    internal static int Unlock2D(nint buffer2D) =>
        ((delegate* unmanaged<nint, int>)Slot(buffer2D, Unlock2DSlot))(buffer2D);

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
}
