using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Audio;

/// <summary>
/// The macOS sound imports (2026-10-07, Stage 2 of the Mac port): AudioToolbox's AudioQueue for
/// <see cref="AudioQueuePlayback"/> and <see cref="AudioQueueCapture"/>, Core Audio's
/// <c>AudioObjectGetPropertyData</c> for the default devices, and one Objective-C message for the
/// microphone permission (<c>AVCaptureDevice authorizationStatusForMediaType:</c>).
///
/// <para>The WinMM rule carried over: <b>nothing is marshalled by value.</b> Queues and buffers are
/// <see cref="nint"/> handles, the stream description and the property address are blittable structs
/// passed by pointer, and the callbacks are <c>[UnmanagedCallersOnly]</c> statics handed over as
/// function pointers with a <see cref="GCHandle"/> as their user data. All are system frameworks;
/// nothing ships beside the exe.</para>
///
/// <para>What the spike of 2026-10-07 settled on an M4 (a C program against the same calls), and the
/// two classes rely on: <c>AudioQueueStop(q, immediate: true)</c> and <c>AudioQueueReset</c> both hand
/// every flushed buffer back through the output callback <em>before they return</em>; the queue keeps
/// running after a reset; a running queue with nothing enqueued plays silence and resumes at the next
/// enqueue; a queue paused (<c>AudioQueuePause</c>) and started again throws new buffers away unplayed, so it is never
/// paused (found later that day: a stopped one starts afresh); <c>AudioQueueDispose(q, true)</c> calls no callback, so it is the fence after which none
/// can come. An input queue asked for 16 kHz, 16-bit mono converts from the device's rate itself
/// (31,567 B/s measured against 32,000), so the capture side needs no resampler.</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class AudioQueueNative
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary><c>kAudioFormatLinearPCM</c>, 'lpcm'.</summary>
    public const uint FormatLinearPcm = 0x6C70636D;

    /// <summary><c>kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked</c>.</summary>
    public const uint FlagsSignedPacked = 0x4 | 0x8;

    /// <summary><c>kAudioObjectSystemObject</c>.</summary>
    public const uint SystemObject = 1;

    /// <summary><c>kAudioObjectUnknown</c>: no device.</summary>
    public const uint UnknownObject = 0;

    /// <summary><c>kAudioHardwarePropertyDefaultOutputDevice</c>, 'dOut'.</summary>
    public const uint DefaultOutputDevice = 0x644F7574;

    /// <summary><c>kAudioHardwarePropertyDefaultInputDevice</c>, 'dIn '.</summary>
    public const uint DefaultInputDevice = 0x64496E20;

    /// <summary><c>kAudioObjectPropertyScopeGlobal</c>, 'glob'.</summary>
    public const uint ScopeGlobal = 0x676C6F62;

    /// <summary><c>kAudioObjectPropertyElementMain</c>.</summary>
    public const uint ElementMain = 0;

    /// <summary><c>AudioStreamBasicDescription</c>: 40 bytes, blittable.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;

        /// <summary>Interleaved signed little-endian integer PCM in <paramref name="format"/>.</summary>
        public static StreamDescription Pcm(PcmFormat format) => new()
        {
            SampleRate = format.SampleRate,
            FormatId = FormatLinearPcm,
            FormatFlags = FlagsSignedPacked,
            BytesPerPacket = (uint)format.BlockAlign,
            FramesPerPacket = 1,
            BytesPerFrame = (uint)format.BlockAlign,
            ChannelsPerFrame = (uint)format.Channels,
            BitsPerChannel = (uint)format.BitsPerSample,
        };
    }

    /// <summary><c>AudioQueueBuffer</c> as arm64 lays it out; only read and written through a pointer the queue owns.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public void* AudioData;
        public uint AudioDataByteSize;
        public nint UserData;
        public uint PacketDescriptionCapacity;
        public void* PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    /// <summary><c>AudioObjectPropertyAddress</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [LibraryImport(AudioToolbox)]
    public static partial int AudioQueueNewOutput(
        StreamDescription* format,
        delegate* unmanaged[Cdecl]<nint, nint, QueueBuffer*, void> callback,
        nint userData,
        nint runLoop,
        nint runLoopMode,
        uint flags,
        nint* queue);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioQueueNewInput(
        StreamDescription* format,
        delegate* unmanaged[Cdecl]<nint, nint, QueueBuffer*, void*, uint, void*, void> callback,
        nint userData,
        nint runLoop,
        nint runLoopMode,
        uint flags,
        nint* queue);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioQueueAllocateBuffer(nint queue, uint bytes, QueueBuffer** buffer);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioQueueEnqueueBuffer(nint queue, QueueBuffer* buffer, uint packetDescriptions, void* descriptions);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioQueueStart(nint queue, void* startTime);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioQueueStop(nint queue, byte immediate);

    [LibraryImport(AudioToolbox)]
    public static partial int AudioQueueDispose(nint queue, byte immediate);

    [LibraryImport(CoreAudio)]
    private static partial int AudioObjectGetPropertyData(uint objectId, PropertyAddress* address, uint qualifierSize, void* qualifier, uint* dataSize, void* data);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPointerArgument(nint receiver, nint selector, nint argument);

    /// <summary>The system's default output or input device (<see cref="DefaultOutputDevice"/>/<see cref="DefaultInputDevice"/>), or <see cref="UnknownObject"/> when there is none or the read fails.</summary>
    public static uint DefaultDevice(uint selector)
    {
        var address = new PropertyAddress { Selector = selector, Scope = ScopeGlobal, Element = ElementMain };
        uint device = UnknownObject;
        uint size = sizeof(uint);
        int status = AudioObjectGetPropertyData(SystemObject, &address, 0, null, &size, &device);
        return status == 0 ? device : UnknownObject;
    }

    /// <summary>
    /// <c>[AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio]</c>: 0 not determined, 1 restricted, 2 denied,
    /// 3 authorized (<see cref="MicrophoneAccess"/>). macOS answers for the app responsible for this process, the terminal
    /// (the spike read 0, then 3 once Terminal was allowed, from a child process). Null when AVFoundation cannot be reached.
    /// </summary>
    public static long? MicrophoneAuthorization()
    {
        if (!NativeLibrary.TryLoad(AVFoundation, out nint library)
            || !NativeLibrary.TryGetExport(library, "AVMediaTypeAudio", out nint export))
        {
            return null;
        }

        // AVMediaTypeAudio is an NSString * variable: the export is its address.
        nint mediaType = *(nint*)export;
        nint deviceClass = objc_getClass("AVCaptureDevice");
        if (mediaType == 0 || deviceClass == 0)
        {
            return null;
        }

        // NSInteger comes back in x0 as a pointer would; arm64 passes it as a plain C call does.
        return SendPointerArgument(deviceClass, sel_registerName("authorizationStatusForMediaType:"), mediaType);
    }
}
